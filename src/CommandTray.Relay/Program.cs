using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using CommandTray.Relay;
using CommandTray.Sync;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;

var directory = RelayDirectory.FromEnvironment();
var admin = new RelayAdmin(directory);

if (args is ["mcp", "--stdio"])
{
    var mcp = Host.CreateApplicationBuilder();
    mcp.Logging.ClearProviders();
    mcp.Services.AddMcpServer().WithStdioServerTransport().WithTools(admin);
    await mcp.Build().RunAsync();
    return;
}

if (args is ["admin", .. var operation])
{
    object result = operation switch
    {
        ["status"] => admin.Status(),
        ["devices"] => admin.Devices(),
        ["revoke", var id] => await admin.Revoke(id, CancellationToken.None),
        ["backups"] => admin.Backups(),
        ["backup"] => admin.Backup(),
        ["restore", var id] => admin.Restore(id),
        _ => throw new ArgumentException("Use admin status, devices, revoke <device-id>, backups, backup, or restore <backup-id>.")
    };
    Console.WriteLine(JsonSerializer.Serialize(result, RelayData.Json));
    return;
}

var pairing = Environment.GetEnvironmentVariable("COMMANDTRAY_PAIRING_CODE");
if (pairing is not { Length: >= 16 and <= 512 })
    throw new InvalidOperationException("Set COMMANDTRAY_PAIRING_CODE to a unique value of at least 16 characters.");
var pairingHash = SHA256.HashData(Encoding.UTF8.GetBytes(pairing));
var relayVersion = RelayClientVersion.Current;
var configuredVersions = Environment.GetEnvironmentVariable("COMMANDTRAY_RELAY_SUPPORTED_CLIENT_VERSIONS");
var supportedClientVersions = string.IsNullOrWhiteSpace(configuredVersions)
    ? [relayVersion]
    : configuredVersions.Split(',', StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToArray();
if (supportedClientVersions.Any(version => !Version.TryParse(version, out var parsed) ||
    parsed.Build < 0 || parsed.Revision >= 0 || parsed.ToString(3) != version))
    throw new InvalidOperationException("Set COMMANDTRAY_RELAY_SUPPORTED_CLIENT_VERSIONS to comma-separated X.Y.Z versions.");
var supportedVersions = supportedClientVersions.ToHashSet(StringComparer.Ordinal);
var enableWeb = bool.TryParse(Environment.GetEnvironmentVariable("COMMANDTRAY_RELAY_WEB"), out var web) && web;
if (enableWeb && !supportedVersions.Contains(relayVersion))
    throw new InvalidOperationException("Allow the relay's own version when its built-in Web client is enabled.");
var versionError = $"Update CommandTray to a version supported by this relay: {string.Join(", ", supportedClientVersions)}.";

var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
    builder.WebHost.UseUrls("http://127.0.0.1:58630");
builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = RelayData.FileLimit);
builder.Services.AddSingleton<RelayStore>(_ => new(directory));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("pair", limiter =>
    {
        limiter.PermitLimit = 20;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
});

await using var app = builder.Build();
_ = app.Services.GetRequiredService<RelayStore>();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/v1"))
    {
        var versions = context.Request.Headers[RelayClientVersion.Header];
        if (versions.Count != 1 || versions[0] is not { } clientVersion || !supportedVersions.Contains(clientVersion))
        {
            context.Response.Headers.CacheControl = "no-store";
            await Results.Json(new { code = "unsupported_client_version", error = versionError, supportedClientVersions },
                statusCode: StatusCodes.Status426UpgradeRequired).ExecuteAsync(context);
            return;
        }
    }
    var requestLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (!context.Request.Path.Equals("/v1/files") && requestLimit is { IsReadOnly: false })
        requestLimit.MaxRequestBodySize = RelayData.WorkspaceLimit;
    await next(context);
});
app.UseRateLimiter();
if (enableWeb) RelayWeb.Map(app, relayVersion);
app.MapGet("/healthz", () => Results.Ok(new { ready = true, protocol = 1, relayVersion, supportedClientVersions }));
app.MapPost("/v1/devices/pair", async (PairInput input, RelayStore store, CancellationToken cancellation) =>
{
    string name = input.DeviceName?.Trim() ?? "";
    if (name.Length is < 1 or > 120 || name.Any(char.IsControl))
        return Results.BadRequest(new { error = "A device name between 1 and 120 characters is required." });
    var supplied = input.PairingCode;
    if (supplied is not { Length: >= 16 and <= 512 } ||
        !CryptographicOperations.FixedTimeEquals(pairingHash, SHA256.HashData(Encoding.UTF8.GetBytes(supplied))))
        return Results.Json(new { error = "The pairing code is invalid." }, statusCode: StatusCodes.Status401Unauthorized);
    try
    {
        var paired = await store.PairAsync(name, cancellation);
        return Results.Ok(new { deviceId = paired.Device.Id, token = paired.Token });
    }
    catch (InvalidDataException error)
    {
        return Results.Json(new { error = error.Message }, statusCode: StatusCodes.Status409Conflict);
    }
}).RequireRateLimiting("pair");

var endpoints = app.MapGroup("/v1");
endpoints.AddEndpointFilter((context, next) =>
{
    var request = context.HttpContext.Request;
    var store = context.HttpContext.RequestServices.GetRequiredService<RelayStore>();
    if (!AuthenticationHeaderValue.TryParse(request.Headers.Authorization, out var header) ||
        !header.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) ||
        store.Authenticate(header.Parameter) is not { } device)
        return ValueTask.FromResult<object?>(Results.Unauthorized());
    context.HttpContext.Items[typeof(DeviceRecord)] = device;
    return next(context);
});

endpoints.MapGet("/devices", (RelayStore store) => Results.Ok(new
{
    devices = store.State.Devices.Select(device => new RelayDeviceView(device.Id, device.Name, device.PairedAt))
}));
endpoints.MapDelete("/devices/{id}", async (string id, HttpContext context, RelayStore store, CancellationToken cancellation) =>
{
    if (CurrentDevice(context).Id != id) return Results.StatusCode(StatusCodes.Status403Forbidden);
    return await store.DeleteDeviceAsync(id, cancellation) ? Results.NoContent() : Results.NotFound();
});

endpoints.MapGet("/workspace", (RelayStore store) => Results.Ok(store.State.Workspace));
endpoints.MapGet("/workspace/changes", (long after, RelayStore store) => after < 0
    ? Results.BadRequest(new { error = "Expected a nonnegative workspace revision." })
    : Results.Ok(store.WorkspaceChanges(after)));
endpoints.MapPatch("/workspace/changes", async (WorkspaceDeltaInput input, RelayStore store,
    HttpContext context, CancellationToken cancellation) =>
{
    if (input.BaseRevision < 0 || !RelayData.IsId(input.OperationId) || input.Changes is not { Length: > 0 and <= 4096 })
        return Results.BadRequest(new { error = "Expected a base revision, operation ID and workspace changes." });
    try
    {
        var write = await store.ApplyWorkspaceAsync(input, CurrentDevice(context).Id, cancellation);
        return write.Accepted ? Results.Ok(write.Value) : Results.Conflict(write.Value);
    }
    catch (InvalidDataException error) { return Results.BadRequest(new { error = error.Message }); }
});
endpoints.MapPut("/workspace", async (WorkspaceInput input, RelayStore store, CancellationToken cancellation) =>
{
    if (input.BaseRevision < 0 || !RelayData.IsWorkspace(input.Document))
        return Results.BadRequest(new { error = "Expected a base revision and a schemaVersion 1 workspace document with a spaces array." });
    var result = await store.PutWorkspaceAsync(input, cancellation);
    return result.Accepted ? Results.Ok(result.Value) : Results.Conflict(result.Value);
});

endpoints.MapGet("/clipboard", (RelayStore store) => Results.Ok(store.State.Clipboard));
endpoints.MapGet("/clipboard/history", (RelayStore store) => Results.Ok(new { entries = store.ReadClipboardHistory() }));
endpoints.MapPut("/clipboard", async (ClipboardInput input, HttpContext context, RelayStore store, CancellationToken cancellation) =>
{
    if (input.Text is not { Length: <= RelayData.ClipboardTextLimit })
        return Results.BadRequest(new { error = $"Clipboard text must be at most {RelayData.ClipboardTextLimit:N0} characters." });
    return Results.Ok(await store.PutClipboardAsync(input.Text, CurrentDevice(context), cancellation));
});

endpoints.MapGet("/files", (RelayStore store) =>
{
    var state = store.State;
    var names = state.Devices.ToDictionary(device => device.Id, device => device.Name, StringComparer.Ordinal);
    return Results.Ok(new { files = state.Files.Select(file => new
    {
        file.Id, file.Name, file.Folder, file.Size, file.Sha256, file.ModifiedAt, file.DeviceId,
        deviceName = names.GetValueOrDefault(file.DeviceId)
    }) });
});
endpoints.MapPost("/files", async (string name, string? folder, HttpContext context, RelayStore store, CancellationToken cancellation) =>
{
    folder ??= "";
    if (!RelayData.IsFileName(name) || !RelayData.IsFolder(folder))
        return Results.BadRequest(new { error = "Use a plain file name and a slash-delimited relative folder." });
    if (context.Request.ContentLength > RelayData.FileLimit)
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    try
    {
        var file = await store.UploadAsync(name, folder, context.Request.Body, CurrentDevice(context).Id, cancellation);
        return Results.Created("files/" + file.Id, file);
    }
    catch (FileLimitException)
    {
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    }
});
endpoints.MapGet("/files/{id}", (string id, RelayStore store) =>
{
    var file = store.OpenFile(id);
    return file is null ? Results.NotFound() : Results.File(file.Value.Data, "application/octet-stream", file.Value.Entry.Name,
        lastModified: file.Value.Entry.ModifiedAt, enableRangeProcessing: true);
});
endpoints.MapDelete("/files/{id}", async (string id, RelayStore store, CancellationToken cancellation) =>
    await store.DeleteFileAsync(id, cancellation) ? Results.NoContent() : Results.NotFound());

await app.RunAsync();

static DeviceRecord CurrentDevice(HttpContext context) => (DeviceRecord)context.Items[typeof(DeviceRecord)]!;
