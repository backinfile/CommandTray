using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CommandTray.Sync;

public sealed record SuiteUpdateAsset(Version Version, string Platform, string FileName, long Size, string Sha256);

public sealed class SuiteUpdates
{
    private const string Repository = "backinfile/CommandTray";
    private const string ManifestUrl = "https://raw.githubusercontent.com/" + Repository + "/updates/suite/latest.json";
    private const int ManifestLimit = 64 * 1024;
    private static readonly string[] DefaultSources = ["", "https://ghfast.top/", "https://gh-proxy.com/"];
    private static readonly HttpClient Http = new(new HttpClientHandler { UseCookies = false })
        { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim _check = new(1, 1);
    private readonly HttpClient _http;
    private readonly string[] _sources;
    private readonly string _publicKey;
    private readonly string _root;
    private readonly string _platform;
    private readonly Version _current;
    private readonly string _disabled;
    private readonly object _downloadGate = new();
    private CancellationTokenSource? _downloadCancellation;
    private Status _status = new("idle");

    private sealed record Status(string State, Version? Available = null, string? Detail = null,
        string? Source = null, long Downloaded = 0, long? Total = null);

    public SuiteUpdates(string dataDirectory, string platform, Version current)
        : this(dataDirectory, platform, current, PublicKey(), Http, DefaultSources) { }

    public SuiteUpdates(string dataDirectory, string platform, Version current, string publicKey, HttpClient http, string[] sources)
    {
        _root = Path.GetFullPath(Path.Combine(dataDirectory, "updates", platform));
        _disabled = Path.Combine(dataDirectory, "updates", "automatic-disabled");
        _platform = platform;
        _current = current;
        _publicKey = publicKey;
        _http = http;
        _sources = sources;
    }

    public bool Enabled => !File.Exists(_disabled);
    public static Version AssemblyVersion(Assembly assembly) => ParseVersion(
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]) ?? new(0, 0, 0);

    public JsonObject Snapshot()
    {
        var status = Volatile.Read(ref _status);
        return new JsonObject
        {
            ["currentVersion"] = _current.ToString(3), ["availableVersion"] = status.Available?.ToString(3),
            ["state"] = status.State, ["detail"] = status.Detail, ["enabled"] = Enabled,
            ["source"] = status.Source, ["downloadedBytes"] = status.Downloaded, ["totalBytes"] = status.Total
        };
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled) File.Delete(_disabled);
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_disabled)!);
            File.WriteAllText(_disabled, "");
        }
    }

    public async Task RunAsync(CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(12), cancellation);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(4));
            do
            {
                if (Enabled) await CheckAsync(cancellation);
            }
            while (await timer.WaitForNextTickAsync(cancellation));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    public async Task CheckAsync(CancellationToken cancellation = default)
    {
        await _check.WaitAsync(cancellation);
        SuiteUpdateAsset? ready = null;
        try
        {
            Publish("checking");
            ready = ReadReadyAsset(_root, _platform, _current, _publicKey);
            if (ready is not null && !await VerifyFileAsync(PackagePath(_root), ready, cancellation)) ready = null;
            SuiteUpdateAsset? best = null;
            byte[]? manifest = null;
            foreach (var source in _sources)
            {
                try
                {
                    var bytes = await ReadManifestAsync(source, cancellation);
                    var offer = VerifyManifest(bytes, _platform, _publicKey);
                    if (best is null || offer.Version > best.Version) (best, manifest) = (offer, bytes);
                    if (source.Length == 0) break;
                }
                catch (Exception error) when (IsSourceFailure(error, cancellation)) { }
            }
            if (ready is not null && (best is null || ready.Version >= best.Version))
            {
                Publish("ready", ready.Version);
                return;
            }
            if (best is null) { Publish("unreachable"); return; }
            if (best.Version <= _current) { Publish("current"); return; }

            Directory.CreateDirectory(_root);
            var temporary = Path.Combine(_root, "partial-" + Guid.NewGuid().ToString("N"));
            using var transfer = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            lock (_downloadGate) _downloadCancellation = transfer;
            try
            {
                foreach (var source in _sources)
                {
                    var displaySource = source.Length == 0 ? "GitHub" : new Uri(source).Host;
                    try
                    {
                        Publish("downloading", best.Version, source: displaySource, total: best.Size);
                        await DownloadAsync(source, best, temporary, transfer.Token, bytes =>
                            Publish("downloading", best.Version, source: displaySource, downloaded: bytes, total: best.Size));
                        transfer.Token.ThrowIfCancellationRequested();
                        File.Move(temporary, PackagePath(_root), overwrite: true);
                        WriteManifest(_root, manifest!);
                        Publish("ready", best.Version, source: displaySource);
                        return;
                    }
                    catch (Exception error) when (IsSourceFailure(error, transfer.Token)) { File.Delete(temporary); }
                }
                Publish(ready is null ? "downloadFailed" : "ready", (ready ?? best).Version);
            }
            catch (OperationCanceledException) when (transfer.IsCancellationRequested && !cancellation.IsCancellationRequested)
            {
                Publish(ready is null ? "cancelled" : "ready", (ready ?? best).Version);
            }
            finally
            {
                lock (_downloadGate) _downloadCancellation = null;
                File.Delete(temporary);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Publish(ready is null ? "idle" : "ready", ready?.Version);
            throw;
        }
        catch (IOException error) { Publish("failed", detail: error.Message); }
        catch (UnauthorizedAccessException error) { Publish("failed", detail: error.Message); }
        finally { _check.Release(); }
    }

    public async Task<string?> ReadyDirectoryAsync(CancellationToken cancellation = default)
    {
        if (!await _check.WaitAsync(0, cancellation)) return null;
        try
        {
            var asset = ReadReadyAsset(_root, _platform, _current, _publicKey);
            return asset is not null && await VerifyFileAsync(PackagePath(_root), asset, cancellation) ? _root : null;
        }
        finally { _check.Release(); }
    }

    public async ValueTask<JsonNode?> ExecuteAsync(string operation, JsonObject args,
        Func<string, CancellationToken, Task> install, CancellationToken cancellation)
    {
        switch (operation)
        {
            case "update_status": break;
            case "update_check": await CheckAsync(cancellation); break;
            case "update_cancel":
                lock (_downloadGate) _downloadCancellation?.Cancel();
                break;
            case "update_set": SetEnabled(args["enabled"]!.GetValue<bool>()); break;
            case "update_install":
                var ready = await ReadyDirectoryAsync(cancellation)
                    ?? throw new InvalidOperationException("There is no verified update ready to install.");
                await install(ready, cancellation);
                break;
            default: throw new NotSupportedException(operation);
        }
        return Snapshot();
    }

    public static string PackagePath(string directory) => Path.Combine(directory, "update.package");

    public static SuiteUpdateAsset? ReadReadyAsset(string directory, string platform, Version current) =>
        ReadReadyAsset(directory, platform, current, PublicKey());

    private static SuiteUpdateAsset? ReadReadyAsset(string directory, string platform, Version current, string key)
    {
        var file = Path.Combine(directory, "latest.json");
        if (!File.Exists(file)) return null;
        try
        {
            if (new FileInfo(file).Length > ManifestLimit) return null;
            var asset = VerifyManifest(File.ReadAllBytes(file), platform, key);
            return asset.Version > current ? asset : null;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or CryptographicException
            or FormatException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    public static async Task<bool> VerifyFileAsync(string path, SuiteUpdateAsset asset, CancellationToken cancellation = default)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != asset.Size) return false;
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation)).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    public static SuiteUpdateAsset VerifyManifest(byte[] envelope, string platform, string publicKey)
    {
        using var wire = JsonDocument.Parse(envelope);
        var payload = wire.RootElement.GetProperty("payload").GetBytesFromBase64();
        var signature = wire.RootElement.GetProperty("signature").GetBytesFromBase64();
        using var signer = ECDsa.Create();
        signer.ImportFromPem(publicKey);
        if (!signer.VerifyData(payload, signature, HashAlgorithmName.SHA256))
            throw new CryptographicException("Update manifest signature was rejected.");
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (root.GetProperty("schema").GetInt32() != 1 || root.GetProperty("channel").GetString() != "suite" ||
            ParseVersion(root.GetProperty("version").GetString()) is not { } version)
            throw new InvalidDataException("Invalid Suite update metadata.");
        var asset = root.GetProperty("assets").GetProperty(platform);
        var name = asset.GetProperty("name").GetString();
        long size = asset.GetProperty("size").GetInt64();
        var hash = asset.GetProperty("sha256").GetString();
        if (name != AssetName(platform, version) || size is <= 0 or > 1_073_741_824 ||
            hash is not { Length: 64 } || !hash.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid Suite update asset.");
        return new(version, platform, name, size, hash);
    }

    private static string AssetName(string platform, Version version) => platform switch
    {
        "windows-setup" => $"CommandTray-Suite-{version}-win-x64-setup.exe",
        "windows-portable" => $"CommandTray-Suite-{version}-win-x64.zip",
        "android" => $"CommandTray-{version}-android-release.apk",
        "linux-x64" or "linux-arm64" or "linux-x64-desktop" or "linux-arm64-desktop" =>
            $"CommandTray-{version}-{platform}.tar.gz",
        _ => throw new InvalidDataException("Unsupported Suite update platform.")
    };

    private async Task<byte[]> ReadManifestAsync(string source, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        using var request = new HttpRequestMessage(HttpMethod.Get, source + ManifestUrl);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var data = new MemoryStream();
        await CopyBoundedAsync(stream, data, ManifestLimit, timeout.Token);
        return data.ToArray();
    }

    private async Task DownloadAsync(string source, SuiteUpdateAsset asset, string destination, CancellationToken cancellation,
        Action<long> progress)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        var origin = $"https://github.com/{Repository}/releases/download/suite-v{asset.Version}/{asset.FileName}";
        using var request = new HttpRequestMessage(HttpMethod.Get, source + origin);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        timeout.CancelAfter(TimeSpan.FromMinutes(8));
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length != asset.Size)
            throw new InvalidDataException("Update size does not match.");
        await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            await CopyBoundedAsync(input, output, asset.Size, timeout.Token, progress);
        if (!await VerifyFileAsync(destination, asset, timeout.Token))
            throw new CryptographicException("Update checksum was rejected.");
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, long limit, CancellationToken cancellation,
        Action<long>? progress = null)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        long lastReport = Environment.TickCount64;
        for (int read; (read = await input.ReadAsync(buffer, cancellation)) != 0;)
        {
            total += read;
            if (total > limit) throw new InvalidDataException("Update exceeds the signed size limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellation);
            if (progress is not null && (total == limit || Environment.TickCount64 - lastReport >= 100))
            {
                progress(total);
                lastReport = Environment.TickCount64;
            }
        }
    }

    private static void WriteManifest(string directory, byte[] bytes)
    {
        var temp = Path.Combine(directory, "manifest-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, Path.Combine(directory, "latest.json"), overwrite: true);
        }
        finally { File.Delete(temp); }
    }

    private static bool IsSourceFailure(Exception error, CancellationToken cancellation) =>
        error is HttpRequestException or IOException or InvalidDataException or TimeoutException or CryptographicException or JsonException
            or FormatException or KeyNotFoundException or InvalidOperationException ||
        error is OperationCanceledException && !cancellation.IsCancellationRequested;

    private static Version? ParseVersion(string? value) => Version.TryParse(value, out var version) &&
        version.Build >= 0 && version.Revision == -1 && version.ToString(3) == value ? version : null;

    private static string PublicKey()
    {
        using var stream = typeof(SuiteUpdates).Assembly.GetManifestResourceStream("CommandTray.SuiteUpdatePublic.pem")!;
        using var input = new StreamReader(stream);
        return input.ReadToEnd();
    }

    private void Publish(string state, Version? available = null, string? detail = null,
        string? source = null, long downloaded = 0, long? total = null) =>
        Volatile.Write(ref _status, new(state, available, detail, source, downloaded, total));
}
