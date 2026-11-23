using System.Text.Json;
using System.Text.Json.Nodes;
using CommandTray.Core;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CommandTray.Services;

internal sealed class SharedDesktopMcpTool(SharedMcpDispatcher dispatcher, ClientMcpTool tool) : McpServerTool
{
    public override IReadOnlyList<object> Metadata { get; } = [];

    public override Tool ProtocolTool { get; } = new()
    {
        Name = tool.Name,
        Description = tool.Description,
        InputSchema = JsonSerializer.SerializeToElement(tool.InputSchema)
    };

    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        var rpc = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = tool.Name,
                ["arguments"] = JsonSerializer.SerializeToNode(request.Params?.Arguments)
            }
        };
        var response = await dispatcher.DispatchAsync(rpc, cancellationToken);
        if (response?["result"] is { } result &&
            result.Deserialize<CallToolResult>(McpJsonUtilities.DefaultOptions) is { } call)
            return call;

        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = (response?["error"]).Text("message", "Invalid tool arguments.") }]
        };
    }
}
