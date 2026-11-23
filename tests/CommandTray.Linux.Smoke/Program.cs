using CommandTray.Linux;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;

[assembly: SupportedOSPlatform("linux")]

if (!OperatingSystem.IsLinux() || args.Length != 1)
    throw new ArgumentException("Run on Linux with the path to the published commandtray binary.");

var home = Directory.CreateTempSubdirectory("ct-ui-").FullName;
Environment.SetEnvironmentVariable("COMMANDTRAY_HOME", home);
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
var cancellation = deadline.Token;
var daemonStart = new ProcessStartInfo(Path.GetFullPath(args[0])) { UseShellExecute = false };
daemonStart.ArgumentList.Add("daemon");
using var daemon = Process.Start(daemonStart) ?? throw new InvalidOperationException("Failed to start the test daemon.");
try
{
    await WaitUntilAsync(() => Task.FromResult(File.Exists(Path.Combine(home, "run", "control.sock"))));
    var clipboard = "";
    await using var backend = new LinuxClientBackend(_ => ValueTask.FromResult<string?>(clipboard), (value, _) =>
    {
        clipboard = value;
        return ValueTask.CompletedTask;
    });
    ValueTask<JsonNode?> Call(string op, JsonObject? values = null) => backend.ExecuteAsync(op, values ?? new JsonObject(), cancellation);
    static JsonObject Id(string key, string value) => new() { [key] = value };

    var space = (await Call("space_create", Id("name", "Linux smoke")))!["id"]!.GetValue<string>();
    await Call("item_save", new JsonObject
    {
        ["spaceId"] = space,
        ["item"] = new JsonObject { ["id"] = "terminal", ["title"] = "Local shell", ["kind"] = "terminal", ["x"] = 0, ["y"] = 0,
            ["width"] = 400, ["height"] = 240, ["data"] = new JsonObject { ["workingDirectory"] = home } }
    });
    await Call("item_move", new JsonObject { ["spaceId"] = space, ["itemId"] = "terminal", ["x"] = 20, ["y"] = 10,
        ["width"] = 600, ["height"] = 300 });
    var workspace = await Call("workspace_read");
    Check(workspace?["spaces"]?[0]?["items"]?[0]?["width"]?.GetValue<int>() == 600, "The resized layout was not persisted.");

    var directory = Path.Combine(home, "files");
    var source = Path.Combine(directory, "source.txt");
    var target = Path.Combine(directory, "copy.txt");
    await Call("local_mkdir", Id("path", directory));
    await File.WriteAllTextAsync(source, "Linux test file", cancellation);
    await Call("local_copy", new JsonObject { ["source"] = source, ["destination"] = target });
    Check((await Call("local_preview", Id("path", target)))?.GetValue<string>() == "Linux test file", "The copied file differs.");
    Check((await Call("local_list", Id("path", directory)))?["entries"] is JsonArray { Count: 2 }, "The Linux folder listing differs.");

    await Call("terminal_open", new JsonObject { ["itemId"] = "terminal", ["workingDirectory"] = home, ["shell"] = "/bin/sh" });
    await Call("terminal_resize", new JsonObject { ["itemId"] = "terminal", ["columns"] = 100, ["rows"] = 36 });
    await Call("terminal_write", new JsonObject { ["itemId"] = "terminal", ["text"] = "stty size\n" });
    await WaitUntilAsync(async () => (await Call("terminal_read", Id("itemId", "terminal")))?.GetValue<string>().Contains("36 100") == true);
    await Call("terminal_close", Id("itemId", "terminal"));

    await Call("clipboard_local_write", Id("text", "normal text"));
    await Call("clipboard_local_write", new JsonObject { ["text"] = "sensitive value", ["sensitive"] = true });
    Check((await Call("clipboard_local_read"))?["text"]?.GetValue<string>() == "sensitive value", "The clipboard adapter did not read the copied text.");
    Check(await Call("clipboard_history") is JsonArray { Count: 1 } history && history[0]?.GetValue<string>() == "normal text",
        "Clipboard history retained sensitive data or omitted the ordinary entry.");

    var command = (await Call("command_save", new JsonObject { ["name"] = "Smoke command", ["command"] = "printf shared-backend",
        ["workingDirectory"] = home }))!["id"]!.GetValue<string>();
    await Call("command_start", Id("id", command));
    await WaitUntilAsync(async () => (await Call("command_log", Id("id", command)))?.GetValue<string>().Contains("shared-backend") == true);
    workspace = await Call("workspace_read");
    Check(workspace?["commands"]?[0]?["id"]?.GetValue<string>() == command, "The command profile is absent from the synchronized document.");
    workspace!["commands"]![0]!["command"] = "printf synchronized-profile";
    var updatedDocument = Path.Combine(home, "updated-workspace.json");
    await File.WriteAllTextAsync(updatedDocument, workspace.ToJsonString(), cancellation);
    var import = new ProcessStartInfo(daemonStart.FileName) { UseShellExecute = false, RedirectStandardOutput = true };
    foreach (var argument in new[] { "workspace", "load", updatedDocument }) import.ArgumentList.Add(argument);
    using (var load = Process.Start(import) ?? throw new InvalidOperationException("Could not import the workspace fixture."))
    {
        _ = await load.StandardOutput.ReadToEndAsync(cancellation);
        await load.WaitForExitAsync(cancellation);
        Check(load.ExitCode == 0, "The CLI did not import the workspace document.");
    }
    Check((await Call("commands_list"))?[0]?["command"]?.GetValue<string>() == "printf synchronized-profile",
        "The dashboard did not expose the updated portable command.");
    Check((await Call("command_log", Id("id", command)))?.GetValue<string>().Contains("synchronized-profile") != true,
        "Updating the workspace unexpectedly ran a command.");
    await Call("command_restart", Id("id", command));
    await WaitUntilAsync(async () => (await Call("command_log", Id("id", command)))?.GetValue<string>().Contains("synchronized-profile") == true);

    int port;
    using (var reserve = new TcpListener(IPAddress.Loopback, 0))
    {
        reserve.Start();
        port = ((IPEndPoint)reserve.LocalEndpoint).Port;
    }
    await File.WriteAllTextAsync(Path.Combine(home, "settings.json"), $$"""{"mcpPort":{{port}}}""", cancellation);
    await Call("settings_save", new JsonObject { ["mcpEnabled"] = true });
    var configuration = await Call("mcp_config");
    using var mcp = new HttpClient { BaseAddress = new Uri(configuration!["url"]!.GetValue<string>()) };
    mcp.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    mcp.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
    using (var unauthorized = await mcp.PostAsync("", RpcRequest(), cancellation))
        Check(unauthorized.StatusCode == HttpStatusCode.Unauthorized, "Unauthenticated MCP access was accepted.");
    mcp.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse(configuration["headers"]!["Authorization"]!.GetValue<string>());
    using (var authorized = await mcp.PostAsync("", RpcRequest(), cancellation))
    {
        authorized.EnsureSuccessStatusCode();
        var response = await authorized.Content.ReadAsStringAsync(cancellation);
        Check(response.Contains("Linux smoke") && response.Contains("structuredContent"), "The canonical MCP workspace alias did not return the workspace.");
    }
    using (var stopped = await mcp.PostAsync("", new StringContent("""
        {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"settings_save","arguments":{"mcpEnabled":false}}}
        """, Encoding.UTF8, "application/json"), cancellation))
    {
        stopped.EnsureSuccessStatusCode();
        var response = await stopped.Content.ReadAsStringAsync(cancellation);
        Check(response.Contains("structuredContent") && response.Contains("\"mcpEnabled\":false"), "MCP failed to confirm its setting change before shutdown.");
    }
    Console.WriteLine("Linux desktop backend smoke passed.");
}
finally
{
    if (!daemon.HasExited)
    {
        using var stop = Process.Start("/bin/kill", $"-TERM {daemon.Id}");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try { await daemon.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { daemon.Kill(entireProcessTree: true); }
        await daemon.WaitForExitAsync();
    }
    Directory.Delete(home, recursive: true);
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static StringContent RpcRequest() => new("""
    {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"workspace_read","arguments":{}}}
    """, Encoding.UTF8, "application/json");

async Task WaitUntilAsync(Func<Task<bool>> condition)
{
    while (!await condition()) await Task.Delay(75, cancellation);
}
