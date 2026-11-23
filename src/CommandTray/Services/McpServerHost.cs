using CommandTray.Core;
using ModelContextProtocol.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CommandTray.Services;

public sealed class McpServerHost : IAsyncDisposable
{
    public const int Port = 58631;
    public static string Endpoint => $"http://127.0.0.1:{AppPaths.McpPort}/mcp";

    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly int _port;
    private WebApplication? _application;

    public McpServerHost(int? port = null) => _port = port ?? AppPaths.McpPort;

    public bool IsRunning => _application is not null;
    public string CurrentEndpoint => $"http://127.0.0.1:{_port}/mcp";

    public Task StartAsync(CommandTrayApi api, CancellationToken cancellationToken = default) =>
        StartCoreAsync(api, null, cancellationToken);

    internal Task StartDesktopAsync(CommandTrayApi api, DesktopRuntime desktop, CancellationToken cancellationToken = default) =>
        StartCoreAsync(api, desktop, cancellationToken);

    private async Task StartCoreAsync(CommandTrayApi api, DesktopRuntime? desktop, CancellationToken cancellationToken)
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
            var mcp = builder.Services.AddMcpServer()
                .WithHttpTransport(options => options.Stateless = true)
                .WithTools(new CommandTrayMcpTools(api));

            if (desktop is not null) mcp.WithTools(new DesktopMcpTools(desktop));
            if (desktop?.Backend is { } backend)
            {
                var shared = new SharedMcpDispatcher(backend);
                mcp.WithTools(shared.Tools.Select(tool => (McpServerTool)new SharedDesktopMcpTool(shared, tool)));
            }
            var application = builder.Build();
            application.Use(async (context, next) =>
            {
                if (context.Request.Headers.TryGetValue("Origin", out var origin) &&
                    origin.ToString() != $"http://127.0.0.1:{_port}" && origin.ToString() != $"http://localhost:{_port}")
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
                if (desktop is not null && !Authorized(context.Request.Headers.Authorization.ToString(), desktop.McpCredential))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.Headers.WWWAuthenticate = "Bearer";
                    return;
                }
                await next(context);
            });
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

    private static bool Authorized(string header, string token) =>
        header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header[7..]), Encoding.UTF8.GetBytes(token));

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
