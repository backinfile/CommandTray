using System.Net.Sockets;
using CommandTray.Models;
using CommandTray.Services;

namespace CommandTray.Tests;

[CollectionDefinition("Port58628", DisableParallelization = true)]
public sealed class Port58628Collection;

[Collection("Port58628")]
public sealed class ProcessSupervisorTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"CommandTray-http-{Guid.NewGuid():N}");
    private ProcessSupervisor? _supervisor;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _supervisor = new ProcessSupervisor(Path.Combine(_directory, "logs"));
        return Task.CompletedTask;
    }

    [Fact]
    public async Task StartsAndStopsOwnedPythonHttpServerOn58628()
    {
        Assert.False(await CanConnectAsync(58628));
        var service = CreateService();

        await _supervisor!.StartAsync(service);
        try { await WaitUntilAsync(() => CanConnectAsync(58628), TimeSpan.FromSeconds(10)); }
        catch
        {
            var snapshot = _supervisor.GetSnapshot(service);
            var logPath = _supervisor.GetLogPath(service);
            var log = File.Exists(logPath) ? await ReadSharedAsync(logPath) : "<no log>";
            throw new Xunit.Sdk.XunitException($"Server did not listen. State={snapshot.State}, Error={snapshot.Error}\n{log}");
        }
        Assert.Equal(ServiceState.Running, _supervisor.GetSnapshot(service).State);
        Assert.True(_supervisor.GetSnapshot(service).ProcessId > 0);

        using var client = new HttpClient();
        var response = await client.GetAsync("http://127.0.0.1:58628/");
        Assert.True(response.IsSuccessStatusCode);

        await _supervisor.StopAsync(service);
        await WaitUntilAsync(async () => !await CanConnectAsync(58628), TimeSpan.FromSeconds(10));
        Assert.Equal(ServiceState.Stopped, _supervisor.GetSnapshot(service).State);
        Assert.Contains("Started PID", await ReadSharedAsync(_supervisor.GetLogPath(service)));
    }

    [Fact]
    public async Task ActiveApplicationExitStopsAllOwnedBackgroundProcesses()
    {
        Assert.False(await CanConnectAsync(58628));
        var service = CreateService();
        await _supervisor!.StartAsync(service);
        await WaitUntilAsync(() => CanConnectAsync(58628), TimeSpan.FromSeconds(10));

        await _supervisor.StopAllAsync([service]);

        await WaitUntilAsync(async () => !await CanConnectAsync(58628), TimeSpan.FromSeconds(10));
        Assert.Equal(ServiceState.Stopped, _supervisor.GetSnapshot(service).State);
    }

    [Fact]
    public async Task MissingWorkingDirectoryFailsWithoutStartingAnything()
    {
        var service = CreateService();
        service.WorkingDirectory = Path.Combine(_directory, "missing");
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => _supervisor!.StartAsync(service));
        Assert.Equal(ServiceState.Faulted, _supervisor!.GetSnapshot(service).State);
    }

    private ServiceDefinition CreateService() => new()
    {
        Name = "Python one-line server",
        Command = "python -c \"from http.server import ThreadingHTTPServer,SimpleHTTPRequestHandler; ThreadingHTTPServer(('127.0.0.1',58628),SimpleHTTPRequestHandler).serve_forever()\"",
        WorkingDirectory = _directory,
        OpenUrl = "http://127.0.0.1:58628/",
        StopTimeoutSeconds = 1
    };

    private static async Task<bool> CanConnectAsync(int port)
    {
        using var client = new TcpClient();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            await client.ConnectAsync("127.0.0.1", port, timeout.Token);
            return true;
        }
        catch { return false; }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }
        Assert.Fail("Condition was not met before the timeout.");
    }

    private static async Task<string> ReadSharedAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    public async Task DisposeAsync()
    {
        if (_supervisor is not null) await _supervisor.DisposeAsync();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
