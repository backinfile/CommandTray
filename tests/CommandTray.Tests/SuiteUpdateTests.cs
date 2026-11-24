using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using CommandTray.Sync;

namespace CommandTray.Tests;

public sealed class SuiteUpdateTests
{
    [Fact]
    public async Task MirrorsCannotReplaceSignedMetadataOrSubstituteThePackage()
    {
        var root = Directory.CreateTempSubdirectory("commandtray-suite-update-");
        try
        {
            using var trusted = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var untrusted = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            byte[] package = "signed update package"u8.ToArray();
            byte[] corrupt = "wrong update payload!"u8.ToArray();
            byte[] correctManifest = Manifest(trusted, package, "1.2.0");
            byte[] fakeManifest = Manifest(untrusted, corrupt, "9.0.0");
            using var http = new HttpClient(new DownloadHandler(correctManifest, fakeManifest, package, corrupt));
            var updater = new SuiteUpdates(root.FullName, "windows-portable", new Version(1, 0, 0),
                trusted.ExportSubjectPublicKeyInfoPem(), http,
                ["", "https://first.example/", "https://second.example/"]);

            await updater.CheckAsync();

            Assert.Equal("ready", (string?)updater.Snapshot()["state"]);
            Assert.Equal("1.2.0", (string?)updater.Snapshot()["availableVersion"]);
            Assert.Equal("second.example", (string?)updater.Snapshot()["source"]);
            var ready = await updater.ReadyDirectoryAsync();
            Assert.NotNull(ready);
            Assert.Equal(package, await File.ReadAllBytesAsync(SuiteUpdates.PackagePath(ready)));
            await File.WriteAllBytesAsync(SuiteUpdates.PackagePath(ready), corrupt);
            Assert.Null(await updater.ReadyDirectoryAsync());
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task CancelStopsTheActualDownloadAndLeavesNoStagedUpdate()
    {
        var root = Directory.CreateTempSubdirectory("commandtray-suite-update-");
        try
        {
            using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            byte[] package = "signed update package"u8.ToArray();
            using var handler = new PendingDownloadHandler(Manifest(signer, package, "1.2.0"));
            using var http = new HttpClient(handler);
            var updater = new SuiteUpdates(root.FullName, "windows-portable", new Version(1, 0, 0),
                signer.ExportSubjectPublicKeyInfoPem(), http, [""]);

            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var check = updater.CheckAsync(lifetime.Token);
            await handler.Started.Task.WaitAsync(lifetime.Token);
            Assert.Equal("GitHub", (string?)updater.Snapshot()["source"]);
            Assert.Equal(package.LongLength, (long?)updater.Snapshot()["totalBytes"]);

            await updater.ExecuteAsync("update_cancel", new(), (_, _) => Task.CompletedTask, lifetime.Token);
            await check;

            Assert.Equal("cancelled", (string?)updater.Snapshot()["state"]);
            Assert.Null(await updater.ReadyDirectoryAsync());
            Assert.Empty(root.EnumerateFiles("*", SearchOption.AllDirectories));
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task AValidOlderReleaseDoesNotDownloadOrBecomeReady()
    {
        var root = Directory.CreateTempSubdirectory("commandtray-suite-update-");
        try
        {
            using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            byte[] package = "older package"u8.ToArray();
            using var handler = new OlderReleaseHandler(Manifest(signer, package, "1.0.0"));
            using var http = new HttpClient(handler);
            var updater = new SuiteUpdates(root.FullName, "windows-portable", new Version(1, 2, 0),
                signer.ExportSubjectPublicKeyInfoPem(), http, [""]);

            await updater.CheckAsync();

            Assert.Equal("current", (string?)updater.Snapshot()["state"]);
            Assert.Null(await updater.ReadyDirectoryAsync());
            Assert.Equal(1, handler.Requests);
        }
        finally { root.Delete(recursive: true); }
    }

    private static byte[] Manifest(ECDsa key, byte[] bytes, string version)
    {
        var asset = new
        {
            name = $"CommandTray-Suite-{version}-win-x64.zip",
            size = bytes.LongLength,
            sha256 = Convert.ToHexString(SHA256.HashData(bytes))
        };
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = 1, channel = "suite", version, assets = new Dictionary<string, object> { ["windows-portable"] = asset }
        });
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            payload, signature = key.SignData(payload, HashAlgorithmName.SHA256)
        });
    }

    private sealed class DownloadHandler(byte[] manifest, byte[] fake, byte[] package, byte[] corrupt) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Assert.Null(request.Headers.Authorization);
            if (uri.Host is "github.com" or "raw.githubusercontent.com")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var bytes = (uri.Host, uri.AbsolutePath.EndsWith("latest.json", StringComparison.Ordinal)) switch
            {
                ("first.example", true) => fake,
                ("first.example", false) => corrupt,
                ("second.example", true) => manifest,
                ("second.example", false) => package,
                _ => throw new InvalidOperationException(uri.AbsoluteUri)
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }

    private sealed class OlderReleaseHandler(byte[] manifest) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ++Requests;
            Assert.EndsWith("/latest.json", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(manifest) });
        }
    }

    private sealed class PendingDownloadHandler(byte[] manifest) : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/latest.json", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(manifest) };
            Started.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("The test download should have been cancelled.");
        }
    }
}
