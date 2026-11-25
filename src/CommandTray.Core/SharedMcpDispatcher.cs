using System.Text.Json.Nodes;
using static CommandTray.Core.ClientFeatures;

namespace CommandTray.Core;

public sealed record ClientMcpTool(string Name, string Description, JsonObject InputSchema,
    Func<JsonObject, CancellationToken, ValueTask<JsonNode?>> Invoke);

public sealed class SharedMcpDispatcher
{
    private const string Protocol = "2025-11-25";
    private readonly Dictionary<string, ClientMcpTool> _tools = new(StringComparer.Ordinal);
    public IReadOnlyCollection<ClientMcpTool> Tools => _tools.Values;

    public SharedMcpDispatcher(IClientBackend backend, IEnumerable<ClientMcpTool>? extraTools = null)
    {
        Add(None, "workspace_read", "Read the spaces, component data, layout and SSH host metadata.");
        Add(None, "space_create", "Create a space.", "name");
        Add(None, "space_rename", "Rename a space.", "spaceId,name");
        Add(None, "space_delete", "Delete a space.", "spaceId");
        Add(None, "space_activate", "Select a space in this application.", "spaceId");
        Add(None, "item_save", "Create or update a component using the workspace document format.", "spaceId,item:object");
        Add(None, "item_remove", "Remove a component from a space.", "spaceId,itemId");
        Add(Layout, "item_move", "Move or resize a component.", "spaceId,itemId,x:integer,y:integer,width:integer,height:integer");
        Add(None, "relay_status", "Read the local relay and workspace synchronization status.");
        Add(None, "relay_pair", "Pair this device with a self-hosted relay.", "server,deviceName,pairingCode");
        Add(None, "relay_unpair", "Unpair this device.");
        Add(None, "relay_devices", "List devices paired with the relay.");
        Add(None, "sync_push", "Synchronize local changes with the relay.");
        Add(None, "sync_pull", "Pull relay changes into the local workspace.");
        Add(None, "sync_resolve", "Resolve the saved conflict with local or remote.", "choice");
        Add(None, "files_list", "List shared files or folders.");
        Add(None, "file_upload", "Upload a selected local file.", "path", "name,folder");
        Add(None, "file_download", "Download a shared file to a selected destination.", "id,path");
        Add(None, "file_delete", "Delete a shared file.", "id");
        Add(None, "clipboard_read", "Read the last synchronized clipboard text.");
        Add(None, "clipboard_send", "Send clipboard text to the relay.", "text");
        Add(None, "clipboard_receive", "Copy the relay clipboard to this device.");
        Add(None, "clipboard_local_read", "Read text from the system clipboard.");
        Add(None, "clipboard_local_write", "Copy text to the system clipboard.", "text");
        Add(ClipboardHistory, "clipboard_history", "Read local clipboard history.");
        Add(None, "settings_read", "Read local application settings.");
        var desktopSettings = (backend.Features & Layout) != 0 ? ",sidebarWidth:integer,sidebarCollapsed:boolean" : "";
        Add(None, "settings_save", "Update local application settings.", "", "language,runAtLogin:boolean,mcpEnabled:boolean" + desktopSettings);
        Add(Commands, "commands_list", "List locally managed commands.");
        Add(Commands, "command_save", "Create or update a command profile.", "name,command", "id,workingDirectory");
        Add(Commands, "command_start", "Start a saved command.", "id");
        Add(Commands, "command_stop", "Stop a saved command.", "id");
        Add(Commands, "command_restart", "Restart a saved command.", "id");
        Add(Commands, "command_delete", "Delete a saved command.", "id");
        Add(Commands, "command_log", "Read the log of a saved command.", "id");
        Add(LocalFiles, "local_list", "List a local directory.", "path");
        Add(LocalFiles, "local_preview", "Preview a local file.", "path");
        Add(LocalFiles, "local_copy", "Copy a local file or directory to a destination.", "source,destination");
        Add(LocalFiles, "local_mkdir", "Create a local directory.", "path");
        Add(Terminals, "terminal_open", "Open a terminal component.", "itemId", "shell,workingDirectory,hostId");
        Add(Terminals, "terminal_write", "Send text to a running local terminal.", "itemId,text");
        Add(Terminals, "terminal_read", "Read output from a running local terminal.", "itemId");
        Add(Terminals, "terminal_resize", "Update a terminal's character grid.", "itemId,columns:integer,rows:integer");
        Add(Terminals, "terminal_close", "Close a running terminal.", "itemId");
        Add(Capture, "capture_list", "List local captures.");
        Add(Capture, "capture_sources", "List display IDs, the last screenshot region and optional DirectShow audio devices.", "", "includeAudio:boolean");
        Add(Capture, "capture_take", "Capture the desktop, primary display, active window, last region, display ID, or supplied rectangle.", "",
            "x:integer,y:integer,width:integer,height:integer,mode,delay:integer,hideWindow:boolean,copy:boolean");
        Add(Capture, "capture_select", "Ask the user to select a region for capture.", "", "hideWindow:boolean");
        Add(Capture, "capture_pin", "Pin a screenshot.", "path");
        Add(Capture, "capture_open", "Open a captured file.", "path");
        Add(Capture, "capture_annotate", "Annotate a captured image.", "path");
        Add(Capture, "capture_record_state", "Read the current recording state, elapsed time and last result.");
        Add(Capture, "capture_record_start", "Start recording the primary display, desktop, a display ID, or a region.", "",
            "frameRate:integer,mode,x:integer,y:integer,width:integer,height:integer,cursor:boolean,audio,format,delay:integer,hideWindow:boolean");
        Add(Capture, "capture_record_stop", "Stop the local screen recorder.");
        Add(Capture, "capture_record_discard", "Stop and discard the active recording.");
        Add(Capture, "capture_ocr", "Read text from a screenshot.", "path", "language");
        Add(Pet, "pet_set", "Show or hide the desktop pet.", "visible:boolean");
        Add(Pet, "notify", "Show a desktop notification linked to a component.", "message", "spaceId,itemId");
        Add(Ssh, "ssh_save", "Create or update SSH host metadata.", "name,address,username,port:integer", "id,keyId");
        Add(Ssh, "ssh_delete", "Delete SSH host metadata.", "id");
        Add(Ssh, "ssh_import", "Import and encrypt an authorized local SSH identity.", "path,name,passphrase");
        Add(Ssh, "ssh_keys", "List encrypted SSH identity names and IDs.");
        Add(Ssh, "ssh_key_delete", "Remove an encrypted SSH identity.", "id");

        if (extraTools is not null)
            foreach (var tool in extraTools) _tools[tool.Name] = tool;

        void Add(ClientFeatures feature, string name, string description, string required = "", string optional = "")
        {
            if (feature != None && (backend.Features & feature) != feature) return;
            var schema = Schema(required, optional);
            _tools.Add(name, new(name, description, schema,
                (arguments, cancellationToken) => backend.ExecuteAsync(name, arguments, cancellationToken)));
        }
    }

    public async ValueTask<JsonObject?> DispatchAsync(JsonObject request, CancellationToken cancellationToken = default)
    {
        var id = request["id"]?.DeepClone();
        if (request.Text("jsonrpc") != "2.0" || request.Text("method") is not { Length: > 0 } method)
            return Error(id, -32600, "Invalid JSON-RPC request.");
        if (!request.ContainsKey("id")) return null;
        var parameters = request.Object("params");

        switch (method)
        {
            case "initialize":
            {
                var requested = parameters.Text("protocolVersion");
                return Result(id, new JsonObject
                {
                    ["protocolVersion"] = requested is "2025-03-26" or "2025-06-18" ? requested : Protocol,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "CommandTray", ["version"] = "1.0.0" }
                });
            }
            case "ping":
                return Result(id, []);
            case "tools/list":
                return Result(id, new JsonObject { ["tools"] = new JsonArray(_tools.Values.Select(tool =>
                    (JsonNode?)new JsonObject { ["name"] = tool.Name, ["description"] = tool.Description,
                        ["inputSchema"] = tool.InputSchema.DeepClone() }).ToArray()) });
            case "tools/call":
            {
                if (!_tools.TryGetValue(parameters.Text("name"), out var tool))
                    return Error(id, -32602, "This tool is unavailable.");
                var arguments = parameters.Object("arguments");
                if (!Valid(tool.InputSchema, arguments)) return Error(id, -32602, "Invalid tool arguments.");
                try
                {
                    var result = await tool.Invoke(arguments, cancellationToken);
                    var structured = result as JsonObject ?? new JsonObject { ["value"] = result?.DeepClone() };
                    return Result(id, new JsonObject { ["content"] = Content(result?.ToJsonString() ?? "{}"),
                        ["structuredContent"] = structured.DeepClone() });
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                {
                    return Result(id, new JsonObject { ["content"] = Content(error.Message), ["isError"] = true });
                }
            }
            default:
                return Error(id, -32601, "Method not found.");
        }
    }

    private static JsonObject Schema(string required, string optional)
    {
        var properties = new JsonObject();
        var keys = new JsonArray();
        Add(required, true);
        Add(optional, false);
        return new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = keys };

        void Add(string fields, bool mandatory)
        {
            foreach (var field in fields.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = field.Split(':', 2);
                properties[pair[0]] = new JsonObject { ["type"] = pair.Length > 1 ? pair[1] : "string" };
                if (mandatory) keys.Add(pair[0]);
            }
        }
    }

    private static bool Valid(JsonObject schema, JsonObject arguments)
    {
        if (schema.Array("required").Any(key => key is not null && !arguments.ContainsKey(key.GetValue<string>())))
            return false;
        foreach (var field in schema.Object("properties"))
        {
            if (!arguments.TryGetPropertyValue(field.Key, out var value)) continue;
            bool valid = field.Value.Text("type") switch
            {
                "string" => value is JsonValue text && text.TryGetValue<string>(out _),
                "integer" => value is JsonValue number && number.TryGetValue<long>(out _),
                "boolean" => value is JsonValue flag && flag.TryGetValue<bool>(out _),
                "object" => value is JsonObject,
                "array" => value is JsonArray,
                _ => true
            };
            if (!valid) return false;
        }
        return true;
    }

    private static JsonArray Content(string text) => new(new JsonObject { ["type"] = "text", ["text"] = text });

    private static JsonObject Result(JsonNode? id, JsonObject value) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = value };

    private static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}
