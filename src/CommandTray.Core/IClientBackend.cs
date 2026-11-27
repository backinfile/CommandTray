using System.Text.Json.Nodes;

namespace CommandTray.Core;

[Flags]
public enum ClientFeatures
{
    None = 0,
    LocalFiles = 1,
    Terminals = 2,
    Commands = 4,
    Layout = 8,
    Capture = 16,
    Pet = 32,
    Ssh = 64,
    ClipboardHistory = 128,
    Updates = 256,
    ConfigurationFiles = 512
}

public enum CaptureShortcut { Region, ActiveWindow, ToggleRecording }

public interface IClientBackend
{
    ClientFeatures Features { get; }

    ValueTask<JsonNode?> ExecuteAsync(
        string operation, JsonObject arguments, CancellationToken cancellationToken = default);

    ValueTask<JsonNode?> ExecuteAsync(string operation, JsonObject arguments,
        IProgress<long>? transferProgress, CancellationToken cancellationToken) =>
        ExecuteAsync(operation, arguments, cancellationToken);
}
