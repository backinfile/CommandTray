using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommandTray.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CommandTray.Linux;

internal sealed class LinuxSharedMcpTool(SharedMcpDispatcher dispatcher, ClientMcpTool tool) : McpServerTool
{
    public override Tool ProtocolTool { get; } = new()
    {
        Name = tool.Name, Description = tool.Description, InputSchema = JsonSerializer.SerializeToElement(tool.InputSchema)
    };

    public override IReadOnlyList<object> Metadata => [];

    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        var response = await dispatcher.DispatchAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool.Name, ["arguments"] = JsonSerializer.SerializeToNode(request.Params?.Arguments) }
        }, cancellationToken);
        return response?["result"]?.Deserialize<CallToolResult>(ModelContextProtocol.McpJsonUtilities.DefaultOptions)
            ?? new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = response?["error"]?["message"]?.GetValue<string>() ?? "Tool call failed." }] };
    }

    public static IEnumerable<McpServerTool> CreateAliases(LinuxClientBackend backend)
    {
        var dispatcher = new SharedMcpDispatcher(backend);
        foreach (var tool in dispatcher.Tools)
        {
            // These existing SDK tools own their names and response contracts.
            if (tool.Name is "sync_push" or "sync_pull" or "sync_resolve" or "local_list" or "local_preview" or "local_copy" or "local_mkdir") continue;
            if (!backend.HasDesktopClipboard && tool.Name is "clipboard_local_read" or "clipboard_local_write") continue;
            if (backend.HasDesktopClipboard && tool.Name == "clipboard_history") continue;
            yield return new LinuxSharedMcpTool(dispatcher, tool);
        }
    }
}

internal sealed class LinuxFilesMcpTools(LinuxClientBackend backend)
{
    [McpServerTool(Name = "local_list", ReadOnly = true, UseStructuredContent = true)]
    [Description("List this Linux device's directory entries at an absolute path.")]
    public async Task<LinuxToolResult> List(string path, CancellationToken cancellationToken)
        => new(await backend.ExecuteAsync("local_list", new JsonObject { ["path"] = path }, cancellationToken));

    [McpServerTool(Name = "local_preview", ReadOnly = true, UseStructuredContent = true)]
    [Description("Read a short preview of a local UTF-8 text file.")]
    public async Task<LinuxToolResult> Preview(string path, CancellationToken cancellationToken)
        => new(await backend.ExecuteAsync("local_preview", new JsonObject { ["path"] = path }, cancellationToken));

    [McpServerTool(Name = "local_copy", UseStructuredContent = true)]
    [Description("Copy a local file to a new path, preserving any existing destination.")]
    public async Task<LinuxToolResult> Copy(string source, string destination, CancellationToken cancellationToken)
        => new(await backend.ExecuteAsync("local_copy", new JsonObject { ["source"] = source, ["destination"] = destination }, cancellationToken));

    [McpServerTool(Name = "local_mkdir", UseStructuredContent = true)]
    [Description("Create a local directory at an absolute path.")]
    public async Task<LinuxToolResult> MakeDirectory(string path, CancellationToken cancellationToken)
        => new(await backend.ExecuteAsync("local_mkdir", new JsonObject { ["path"] = path }, cancellationToken));
}

internal sealed class LinuxClipboardMcpTools(LinuxClientBackend backend)
{
    [McpServerTool(Name = "read_local_clipboard", ReadOnly = true, UseStructuredContent = true)]
    [Description("Get text from the connected desktop clipboard.")]
    public async Task<LinuxToolResult> Read(CancellationToken cancellationToken)
        => new(await backend.ExecuteAsync("clipboard_local_read", new JsonObject(), cancellationToken));

    [McpServerTool(Name = "write_local_clipboard", UseStructuredContent = true)]
    [Description("Set text on the connected desktop clipboard.")]
    public async Task<LinuxToolResult> Write(string text, CancellationToken cancellationToken)
        => new(await backend.ExecuteAsync("clipboard_local_write", new JsonObject { ["text"] = text }, cancellationToken));

    [McpServerTool(Name = "clipboard_history", ReadOnly = true, UseStructuredContent = true)]
    [Description("List the recent text values accessed through CommandTray's clipboard actions.")]
    public async Task<LinuxToolResult> History(CancellationToken cancellationToken)
        => new(await backend.ExecuteAsync("clipboard_history", new JsonObject(), cancellationToken));
}
