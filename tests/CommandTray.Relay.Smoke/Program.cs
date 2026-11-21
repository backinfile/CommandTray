using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommandTray.Sync;

if (args.Length != 1 || !File.Exists(args[0]))
    throw new ArgumentException("Pass the full path to a built CommandTray.Relay.dll.");

string relay = Path.GetFullPath(args[0]);
string runData = Path.Combine(Path.GetFullPath("artifacts/relay-smoke"), Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(runData);
string pairingCode = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));

var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
int port = ((IPEndPoint)listener.LocalEndpoint).Port;
listener.Stop();
var uri = new Uri($"http://127.0.0.1:{port}");
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

try
{
    string backupId;
    const string recordId = "smoke-ssh-key";
    const string vaultPassphrase = "generated smoke fixture only";
    byte[] key = Encoding.UTF8.GetBytes("Test fixture. Not a private key.");
    var encrypted = SecretEnvelope.Encrypt(key, vaultPassphrase, recordId);
    Assert(encrypted.Decrypt(vaultPassphrase, recordId).SequenceEqual(key), "Vault did not restore the original bytes.");
    try
    {
        encrypted.Decrypt(vaultPassphrase, "a-different-record");
        throw new InvalidOperationException("The vault accepted a secret from another record.");
    }
    catch (System.Security.Cryptography.CryptographicException) { }

    var serialized = JsonSerializer.SerializeToNode(encrypted, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    var document = new JsonObject
    {
        ["schemaVersion"] = 1,
        ["activeSpaceId"] = "development",
        ["spaces"] = new JsonArray(new JsonObject
        {
            ["id"] = "development",
            ["name"] = "Development",
            ["items"] = new JsonArray(new JsonObject
            {
                ["id"] = "release-note",
                ["kind"] = "note",
                ["data"] = new JsonObject { ["text"] = "release checklist" },
            }),
        }),
        ["sshKeyEnvelopes"] = new JsonArray(new JsonObject
        {
            ["id"] = recordId,
            ["name"] = "Smoke key",
            ["envelope"] = serialized
        })
    };

    PairingResult windows;
    PairingResult linux;
    string fileId;
    byte[] payload = [1, 3, 7, 255];

    using (var server = await StartRelayAsync())
    {
        var unauthenticated = await http.GetAsync(new Uri(uri, "/v1/files"));
        Assert(unauthenticated.StatusCode == HttpStatusCode.Unauthorized, "Shared files were accessible before pairing.");

        var pairing = new RelayClient(uri, http: http);
        windows = await pairing.PairAsync("Smoke Windows", pairingCode);
        linux = await pairing.PairAsync("Smoke Linux", pairingCode);
        Assert(windows.Token != linux.Token, "Pairing reused a token.");
        var first = new RelayClient(uri, windows.Token, http);
        var second = new RelayClient(uri, linux.Token, http);

        var pushed = await first.PutWorkspaceAsync(0, document);
        Assert(pushed["revision"]!.GetValue<long>() == 1, "Workspace revision did not advance.");
        var pulled = await second.GetWorkspaceAsync();
        Assert(JsonNode.DeepEquals(pulled["document"], document), "Another device did not receive the workspace.");
        var retry = await first.PutWorkspaceAsync(0, document);
        Assert(retry["revision"]!.GetValue<long>() == 1, "Retry did not reuse the committed workspace revision.");
        var conflicting = document.DeepClone();
        conflicting["activeSpaceId"] = "offline";
        try
        {
            await second.PutWorkspaceAsync(0, conflicting);
            throw new InvalidOperationException("A stale update overwrote the workspace.");
        }
        catch (RelayConflictException conflict)
        {
            Assert(JsonNode.DeepEquals(conflict.Current["document"], document), "Conflict did not return the server version.");
        }

        await first.PutClipboardAsync("跨设备剪贴板");
        var clipboard = await second.GetClipboardAsync();
        Assert((string?)clipboard["text"] == "跨设备剪贴板", "Clipboard did not cross devices.");

        var upload = await second.UploadAsync("release.bin", new MemoryStream(payload), "releases/next");
        fileId = (string)upload["id"]!;
        var again = await second.UploadAsync("release.bin", new MemoryStream(payload), "releases/next");
        Assert((string?)again["id"] == fileId, "Upload retries created duplicates.");
        Assert((await first.ListFilesAsync()).Count == 1, "Shared file listing differs across devices.");
        using var downloaded = new MemoryStream();
        await first.DownloadAsync(fileId, downloaded);
        Assert(downloaded.ToArray().SequenceEqual(payload), "Downloaded bytes differed.");

        string duringRun = await AdminAsync(expectedExitCode: false, "backup");
        Assert(duringRun.Contains("Stop the relay", StringComparison.OrdinalIgnoreCase), "A running relay accepted an offline backup.");
        await StopAsync(server);
    }

    backupId = (string)JsonNode.Parse(await AdminAsync(true, "backup"))!["id"]!;
    using (var server = await StartRelayAsync())
    {
        var first = new RelayClient(uri, windows.Token, http);
        var second = new RelayClient(uri, linux.Token, http);
        await second.DeleteDeviceAsync(linux.DeviceId);
        try
        {
            await second.GetClipboardAsync();
            throw new InvalidOperationException("A revoked device retained access.");
        }
        catch (RelayException error) when (error.StatusCode == HttpStatusCode.Unauthorized) { }

        await first.DeleteFileAsync(fileId);
        await StopAsync(server);
    }
    await AdminAsync(true, "restore", backupId);
    using (var server = await StartRelayAsync())
    {
        var second = new RelayClient(uri, linux.Token, http);
        using var downloaded = new MemoryStream();
        await second.DownloadAsync(fileId, downloaded);
        Assert(downloaded.ToArray().SequenceEqual(payload), "Restored backup lost the shared bytes.");
        Assert(JsonNode.DeepEquals((await second.GetWorkspaceAsync())["document"], document), "Backup lost workspace documents.");
        await StopAsync(server);
    }

    await VerifyAdminMcpAsync();
    Console.WriteLine("Relay smoke passed: pairing, conflict, clipboard, shared bytes, revocation, backup/restore, encrypted secret and MCP.");
}
finally
{
    var expectedParent = Path.GetFullPath("artifacts/relay-smoke");
    if (Path.GetDirectoryName(runData) == expectedParent && Directory.Exists(runData))
        Directory.Delete(runData, recursive: true);
}

Process Launch(params string[] arguments)
{
    var start = new ProcessStartInfo("dotnet")
    {
        UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
        RedirectStandardError = true, CreateNoWindow = true
    };
    start.ArgumentList.Add(relay);
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    start.Environment["COMMANDTRAY_RELAY_DATA"] = runData;
    start.Environment["COMMANDTRAY_PAIRING_CODE"] = pairingCode;
    start.Environment["ASPNETCORE_URLS"] = uri.AbsoluteUri.TrimEnd('/');
    return Process.Start(start) ?? throw new InvalidOperationException("The relay process did not start.");
}

async Task<Process> StartRelayAsync()
{
    var process = Launch();
    _ = process.StandardOutput.ReadToEndAsync();
    var errors = process.StandardError.ReadToEndAsync();
    try
    {
        for (int retry = 0; retry < 50 && !process.HasExited; retry++)
        {
            try
            {
                using var response = await http.GetAsync(new Uri(uri, "/healthz"));
                if (response.IsSuccessStatusCode) return process;
            }
            catch (HttpRequestException) { }
            await Task.Delay(100);
        }
        await StopAsync(process);
        throw new InvalidOperationException("Relay failed its startup probe. " + await errors);
    }
    catch
    {
        await StopAsync(process);
        process.Dispose();
        throw;
    }
}

async Task<string> AdminAsync(bool expectedExitCode, params string[] operation)
{
    using var process = Launch(["admin", .. operation]);
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
    Assert((process.ExitCode == 0) == expectedExitCode, "Unexpected admin exit code. " + await error);
    return expectedExitCode ? await output : await error;
}

async Task VerifyAdminMcpAsync()
{
    using var process = Launch("mcp", "--stdio");
    _ = process.StandardError.ReadToEndAsync();
    try
    {
        var result = await Send(1, "initialize", new
        {
            protocolVersion = "2025-11-25",
            capabilities = new { },
            clientInfo = new { name = "commandtray-relay-smoke", version = "1" }
        });
        Assert(result["result"]?["serverInfo"] != null, "MCP initialize did not return server info.");
        await process.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        await process.StandardInput.FlushAsync();
        var tools = await Send(2, "tools/list", new { });
        Assert(tools["result"]?["tools"]?.AsArray()?.Any(tool => (string?)tool?["name"] == "backup_relay") == true,
            "Backup admin tool was missing.");
        var called = await Send(3, "tools/call", new { name = "get_relay_status", arguments = new { } });
        Assert(called["result"] is JsonObject && (bool?)called["result"]?["isError"] != true,
            "Relay admin status call failed: " + called.ToJsonString());
    }
    finally { await StopAsync(process); }

    async Task<JsonNode> Send(int id, string method, object parameters)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
        await process.StandardInput.FlushAsync();
        while (true)
        {
            string? line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8));
            if (line == null) throw new InvalidOperationException("MCP transport closed.");
            var message = JsonNode.Parse(line)!;
            if (message["id"]?.ToJsonString() == id.ToString()) return message;
        }
    }
}

static async Task StopAsync(Process process)
{
    if (!process.HasExited) process.Kill(entireProcessTree: true);
    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
}
static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
