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
                "check" => "M5,12 L10,17 L19,7",
                "minus" => "M4,12 H20 M4,12.1 H20",
                "pointer" => "M4,3 L11,20 L13,13 L20,11 Z",
                "hand" => "M7,13 V6 A2,2 0 0 1 11,6 V11 V4 A2,2 0 0 1 15,4 V11 V6 A2,2 0 0 1 19,6 V15 A6,6 0 0 1 13,21 H12 C10,21 8,20 7,18 L4,14 A2,2 0 0 1 7,12 L8,14",
                "pen" => "M4,20 L5,14 L16,3 L21,8 L10,19 Z M14,5 L19,10",
                "highlighter" => "M7,13 L16,4 L20,8 L11,17 H7 Z M7,16 L4,19 H12",
                "line" => "M5,19 L19,5",
                "arrow" => "M5,19 L19,5 M12,5 H19 V12",
                "rectangle" => "M4,6 H20 V18 H4 Z",
                "ellipse" => "M21,12 A9,6 0 1 1 3,12 A9,6 0 1 1 21,12",
                "diamond" => "M12,3 L21,12 L12,21 L3,12 Z",
                "text" => "M5,5 H19 M12,5 V20 M8,20 H16",
                "eraser" => "M3,14 L12,4 L20,11 L12,20 H8 Z M8,9 L16,16 M11,20 H21",
                "undo" => "M8,5 L3,10 L8,15 M4,10 H15 A5,5 0 0 1 20,15 V19",
                "redo" => "M16,5 L21,10 L16,15 M20,10 H9 A5,5 0 0 0 4,15 V19",
                "pin" => "M8,4 L19,10 L15,12 L14,17 L11,14 L5,20 L8,13 L5,10 L8,9 Z",
                "history" => "M4,8 A8,8 0 1 1 4,16 M4,3 V9 H10 M12,7 V12 L16,14",
                "sync" => "M20,7 V3 M20,7 H16 M20,7 A8,8 0 0 0 5,9 M4,17 V21 M4,17 H8 M4,17 A8,8 0 0 0 19,15",
                "more" => "M5,11 V13 M12,11 V13 M19,11 V13",
                "up" => "M12,20 V4 M6,10 L12,4 L18,10",
                "download" => "M12,3 V15 M6,9 L12,15 L18,9 M4,17 V21 H20 V17",
                "send" => "M3,10 L21,3 L15,21 L11,13 Z M11,13 L21,3",
                "copy" => "M8,8 H20 V20 H8 Z M15,4 H4 V15",
                "edit" => "M4,20 L5,15 L16,4 L20,8 L9,19 Z M14,6 L18,10",
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
                "fit" => "M4,9 V4 H9 M15,4 H20 V9 M20,15 V20 H15 M9,20 H4 V15 M9,9 H15 V15 H9 Z",
                "front" => "M12,20 V9 M7,14 L12,9 L17,14 M5,5 H19",
                "back" => "M12,4 V15 M7,10 L12,15 L17,10 M5,19 H19",
                "trash" => "M4,6 H20 M9,6 V3 H15 V6 M6,6 L7,20 H17 L18,6",
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
