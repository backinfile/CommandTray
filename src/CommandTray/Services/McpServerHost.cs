using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CommandTray.Services;

public sealed class McpServerHost : IAsyncDisposable
{
    public const int Port = 58629;
    public const string Endpoint = "http://127.0.0.1:58629/mcp";

    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly int _port;
    private WebApplication? _application;

    public McpServerHost(int port = Port) => _port = port;

    public bool IsRunning => _application is not null;
    public string CurrentEndpoint => $"http://127.0.0.1:{_port}/mcp";

    public async Task StartAsync(CommandTrayApi api, CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (_application is not null) return;

            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(McpServerHost).Assembly.GetName().Name,
                Args = []
            });
            builder.Logging.ClearProviders();
            builder.Configuration["AllowedHosts"] = "127.0.0.1";
            builder.WebHost.UseUrls($"http://127.0.0.1:{_port}");
            builder.Services
                .AddMcpServer()
                .WithHttpTransport(options => options.Stateless = true)
                .WithTools(new CommandTrayMcpTools(api));

            var application = builder.Build();
            application.MapMcp("/mcp");
            try { await application.StartAsync(cancellationToken); }
            catch
            {
                await application.DisposeAsync();
                throw;
            }
            _application = application;
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (_application is not { } application) return;
            _application = null;
            try { await application.StopAsync(cancellationToken); }
            finally { await application.DisposeAsync(); }
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lifecycle.Dispose();
    }
}
