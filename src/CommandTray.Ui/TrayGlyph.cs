using IconPath = Avalonia.Controls.Shapes.Path;
using Avalonia.Layout;
using Avalonia.Media;

namespace CommandTray.Ui;

internal static class TrayGlyph
{
    public static IconPath Icon(string name, double size = 16)
    {
        var path = new IconPath
        {
            Data = Geometry.Parse(name switch
            {
                "spaces" => "M3,4 H21 V20 H3 Z M3,9 H21 M10,9 V20",
                "files" => "M3,6 H10 L12,8 H21 V19 H3 Z",
                "folder-open" => "M3,6 H10 L12,8 H21 V11 M3,6 V19 H18 L22,11 H7 L3,19",
                "file" => "M5,3 H14 L19,8 V21 H5 Z M14,3 V8 H19 M8,12 H16 M8,16 H16",
                "clipboard" => "M8,6 H5 V21 H19 V6 H16 M8,4 H16 V8 H8 Z M9,12 H15 M9,16 H15",
                "settings" => "M9,3 H15 L16,6 L19,7 L21,10 L19,12 L20,15 L18,18 L15,18 L13,21 H10 L8,18 L5,18 L3,15 L4,12 L3,9 L5,6 L8,6 Z M15,12 A3,3 0 1 1 9,12 A3,3 0 1 1 15,12",
                "terminal" => "M3,4 H21 V20 H3 Z M6,9 L10,12 L6,15 M12,15 H17",
                "capture" => "M8,5 L10,3 H14 L16,5 H21 V20 H3 V5 Z M16,12 A4,4 0 1 1 8,12 A4,4 0 1 1 16,12",
                "ssh" => "M14,8 A5,5 0 1 1 4,8 A5,5 0 1 1 14,8 M13,12 L21,20 M17,16 L19,14",
                "plus" => "M12,4 V20 M4,12 H20",
                "sync" => "M20,7 V3 M20,7 H16 M20,7 A8,8 0 0 0 5,9 M4,17 V21 M4,17 H8 M4,17 A8,8 0 0 0 19,15",
                "more" => "M5,11 V13 M12,11 V13 M19,11 V13",
                "up" => "M12,20 V4 M6,10 L12,4 L18,10",
                "download" => "M12,3 V15 M6,9 L12,15 L18,9 M4,17 V21 H20 V17",
                "send" => "M3,10 L21,3 L15,21 L11,13 Z M11,13 L21,3",
                "copy" => "M8,8 H20 V20 H8 Z M15,4 H4 V15",
                "note" => "M4,4 H20 V14 L14,20 H4 Z M14,20 V14 H20 M8,9 H16",
                "tasks" => "M3,7 L5,9 L8,5 M12,7 H21 M3,16 L5,18 L8,14 M12,16 H21",
                "board" => "M3,4 H21 V20 H3 Z M5,16 L9,9 L13,15 L18,9",
                "play" => "M8,4 L19,12 L8,20 Z",
                "stop" => "M6,6 H18 V18 H6 Z",
                "chevron" => "M7,9 L12,14 L17,9",
                "search" => "M15,10 A5,5 0 1 1 5,10 A5,5 0 1 1 15,10 M14,14 L20,20",
                "close" => "M6,6 L18,18 M18,6 L6,18",
                "next" => "M9,6 L15,12 L9,18",
                "grip" => "M9,20 L20,9 M14,20 L20,14",
                "rail-collapse" => "M3,4 H21 V20 H3 Z M9,4 V20 M17,9 L14,12 L17,15",
                "rail-expand" => "M3,4 H21 V20 H3 Z M9,4 V20 M14,9 L17,12 L14,15",
                "maximize" => "M4,9 V4 H9 M15,4 H20 V9 M20,15 V20 H15 M9,20 H4 V15",
                "restore" => "M4,9 H9 V4 M15,4 V9 H20 M20,15 H15 V20 M9,20 V15 H4",
                _ => "M4,7 H10 L13,10 H20 V18 H4 Z M8,14 H16"
            }),
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        path.Classes.Add("glyph");
        return path;
    }
}
