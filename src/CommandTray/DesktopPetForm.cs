using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using CommandTray.Services;

namespace CommandTray;

internal sealed class DesktopPetForm : Form
{
    private enum Pose { Idle, Walk, Sleep, Wave }

    // Four poses in reading order in the Chestnut atlas. Keep their authored proportions.
    private static readonly Rectangle[] Sprites =
    [
        Rectangle.FromLTRB(98, 83, 500, 542), Rectangle.FromLTRB(650, 121, 1208, 534),
        Rectangle.FromLTRB(50, 816, 565, 1130), Rectangle.FromLTRB(696, 696, 1156, 1175)
    ];
    private static readonly int[] Sizes = [80, 100, 125];
    private static readonly Rectangle Bubble = new(4, 3, 222, 49);
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 55 };
    private readonly ContextMenuStrip _menu = new();
    private readonly Bitmap _atlas;
    private readonly Action _showWorkspace;
    private Pose _pose;
    private bool _roaming = true;
    private bool _hover;
    private bool _dragged;
    private Point? _dragStart;
    private int _direction = -1;
    private int _size = 1;
    private int _walksToRest = 3;
    private int _bob;
    private long _nextPoseAt;
    private long _noticeUntil;
    private string? _notice;
    private Action? _openNotice;

    public DesktopPetForm(Action showWorkspace)
    {
        _showWorkspace = showWorkspace;
        using var stream = typeof(DesktopPetForm).Assembly.GetManifestResourceStream("CommandTray.Assets.DesktopPetChestnut.png")!;
        using var atlas = new Bitmap(stream);
        _atlas = new Bitmap(atlas);

        FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        var desktop = Screen.FromPoint(Cursor.Position).WorkingArea;
        Size = new Size(Scale(230), Scale(199));
        Location = Clamp(new Point(desktop.Right - Width - 12, desktop.Bottom - Height - 5), desktop);
        BuildMenu();
        _timer.Tick += (_, _) => Tick(Environment.TickCount64);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int layered = 0x00080000, toolWindow = 0x00000080, noActivate = 0x08000000;
            var parameters = base.CreateParams;
            parameters.ExStyle |= layered | toolWindow | noActivate;
            return parameters;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        var desktop = Screen.FromRectangle(Bounds).WorkingArea;
        Size = new Size(Scale(230), Scale(199));
        Location = Clamp(new Point(desktop.Right - Width - Scale(12), desktop.Bottom - Height - Scale(5)), desktop);
        SetIdle(Environment.TickCount64);
        _timer.Start();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ResizePet();
    }

    public void Notice(string message, Action? openSource)
    {
        var now = Environment.TickCount64;
        _notice = message;
        _openNotice = openSource;
        _noticeUntil = now + 4500;
        SetPose(Pose.Wave, _noticeUntil);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        if (_pose == Pose.Walk) SetIdle(Environment.TickCount64);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _dragStart = e.Location;
        _dragged = false;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragStart is not { } start) return;
        var cursor = Cursor.Position;
        var next = new Point(cursor.X - start.X, cursor.Y - start.Y);
        if (!_dragged && Math.Abs(next.X - Left) + Math.Abs(next.Y - Top) < Scale(5)) return;
        _dragged = true;
        Location = next;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || _dragStart is null) return;
        _dragStart = null;
        Capture = false;
        if (_dragged)
        {
            _roaming = false;
            Location = Clamp(Location, Screen.FromRectangle(Bounds).WorkingArea);
            SetIdle(Environment.TickCount64);
        }
        else if (_notice != null && Scale(Bubble).Contains(e.Location))
        {
            var open = _openNotice ?? _showWorkspace;
            ClearNotice();
            SetIdle(Environment.TickCount64);
            open();
        }
        else Greet();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left && !_dragged && !(_notice != null && Scale(Bubble).Contains(e.Location)))
            _showWorkspace();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (Capture || _dragStart is null) return;
        _dragStart = null;
        if (_dragged) Location = Clamp(Location, Screen.FromRectangle(Bounds).WorkingArea);
    }

    private void BuildMenu()
    {
        var walk = _menu.Items.Add(Localizer.T("Walk around", "自动走动"), null, (_, _) =>
        {
            _roaming = !_roaming;
            SetIdle(Environment.TickCount64);
        });
        var sleep = _menu.Items.Add("", null, (_, _) =>
        {
            if (_pose == Pose.Sleep) SetIdle(Environment.TickCount64);
            else SetPose(Pose.Sleep, long.MaxValue);
        });
        _menu.Items.Add(Localizer.T("Say hello", "打招呼"), null, (_, _) => Greet());
        var size = new ToolStripMenuItem(Localizer.T("Size", "大小"));
        for (int i = 0; i < Sizes.Length; i++)
        {
            int index = i;
            size.DropDownItems.Add($"{Sizes[i]}%", null, (_, _) =>
            {
                _size = index;
                ResizePet();
            });
        }
        _menu.Items.Add(size);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Localizer.T("Open workspace", "打开工作区"), null, (_, _) => _showWorkspace());
        _menu.Items.Add(Localizer.T("Hide desktop pet", "隐藏桌宠"), null, (_, _) => Close());
        _menu.Opening += (_, _) =>
        {
            ((ToolStripMenuItem)walk).Checked = _roaming;
            sleep.Text = _pose == Pose.Sleep ? Localizer.T("Wake up", "叫醒") : Localizer.T("Sleep", "睡觉");
            for (int i = 0; i < size.DropDownItems.Count; i++)
                ((ToolStripMenuItem)size.DropDownItems[i]).Checked = i == _size;
        };
        ContextMenuStrip = _menu;
    }

    private void Tick(long now)
    {
        if (_notice != null)
        {
            if (now < _noticeUntil) return;
            ClearNotice();
            SetIdle(now);
        }
        if (_dragStart.HasValue || _hover || _menu.Visible) return;
        if (now >= _nextPoseAt)
        {
            if (_pose != Pose.Idle) SetIdle(now);
            else if (!_roaming || --_walksToRest <= 0)
            {
                _walksToRest = Random.Shared.Next(2, 5);
                SetPose(Pose.Sleep, _roaming ? now + Random.Shared.Next(13000, 26000) : long.MaxValue);
            }
            else
            {
                _direction = Random.Shared.Next(2) == 0 ? -1 : 1;
                SetPose(Pose.Walk, now + Random.Shared.Next(1800, 3800));
            }
        }
        if (_pose != Pose.Walk) return;

        var desktop = Screen.FromRectangle(Bounds).WorkingArea;
        var next = Clamp(new Point(Left + _direction * Scale(3), Top), desktop);
        if (next.X == Left)
        {
            var previous = _direction;
            _direction = -_direction;
            next = Clamp(new Point(Left + _direction * Scale(3), Top), desktop);
            if (next.X == Left) _direction = previous;
            else Present();
        }
        Location = next;
        int bob = (now / 150) % 2 == 0 ? Scale(2) : 0;
        if (_bob == bob) return;
        _bob = bob;
        Present();
    }

    private void Greet() => SetPose(Pose.Wave, Environment.TickCount64 + 1250);

    private void SetIdle(long now) => SetPose(Pose.Idle,
        now + (_roaming ? Random.Shared.Next(4500, 10000) : 30000));

    private void SetPose(Pose pose, long until)
    {
        _pose = pose;
        _nextPoseAt = until;
        _bob = 0;
        Present();
    }

    private void ClearNotice()
    {
        _notice = null;
        _openNotice = null;
    }

    private void ResizePet()
    {
        var desktop = Screen.FromRectangle(Bounds).WorkingArea;
        var anchor = new Point(Left + Width / 2, Bottom);
        Size = new Size(Scale(230), Scale(199));
        Location = Clamp(new Point(anchor.X - Width / 2, anchor.Y - Height), desktop);
        // Capture is still held when the drag crosses to a different DPI.
        if (_dragStart.HasValue) _dragStart = PointToClient(Cursor.Position);
        Present();
    }

    private int Scale(int value) => (int)Math.Round(value * DeviceDpi * Sizes[_size] / 9600d);

    private Rectangle Scale(Rectangle rect) => new(Scale(rect.X), Scale(rect.Y), Scale(rect.Width), Scale(rect.Height));

    private Point Clamp(Point point, Rectangle desktop) =>
        new(Math.Clamp(point.X, desktop.Left, Math.Max(desktop.Left, desktop.Right - Width)),
            Math.Clamp(point.Y, desktop.Top, Math.Max(desktop.Top, desktop.Bottom - Height)));

    private void DrawFrame(Bitmap frame)
    {
        using var graphics = Graphics.FromImage(frame);
        graphics.Clear(Color.Transparent);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var source = Sprites[(int)_pose];
        int width = Scale((int)Math.Round(source.Width * .27));
        int height = Scale((int)Math.Round(source.Height * .27));
        int left = (Width - width) / 2, top = Height - Scale(4) - height - _bob;
        if (_pose == Pose.Walk && _direction > 0)
            graphics.DrawImage(_atlas, [new Point(left + width, top), new Point(left, top), new Point(left + width, top + height)],
                source, GraphicsUnit.Pixel);
        else graphics.DrawImage(_atlas, new Rectangle(left, top, width, height), source, GraphicsUnit.Pixel);
        if (_notice is null) return;

        var box = Scale(Bubble);
        using var outline = UiTheme.RoundedRectangle(box, Scale(9));
        using var surface = new SolidBrush(UiTheme.Surface);
        using var border = new Pen(UiTheme.Separator);
        using var text = new SolidBrush(UiTheme.Text);
        using var font = new Font(UiTheme.Small.FontFamily, Scale(12), GraphicsUnit.Pixel);
        using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, LineAlignment = StringAlignment.Center };
        graphics.FillPath(surface, outline);
        graphics.DrawPath(border, outline);
        graphics.DrawString(_notice, font, text, Rectangle.Inflate(box, -Scale(9), -Scale(5)), format);
    }

    private void Present()
    {
        if (!IsHandleCreated || Width <= 0 || Height <= 0) return;
        var size = Size;
        var bitmap = CreateLayerBitmap(size, out var pixels);
        if (bitmap == IntPtr.Zero) throw new Win32Exception();
        var device = IntPtr.Zero;
        var previous = IntPtr.Zero;
        try
        {
            using (var frame = new Bitmap(size.Width, size.Height, size.Width * 4, PixelFormat.Format32bppPArgb, pixels))
                DrawFrame(frame);
            device = CreateCompatibleDC(IntPtr.Zero);
            if (device == IntPtr.Zero) throw new Win32Exception();
            previous = SelectObject(device, bitmap);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1)) throw new Win32Exception();
            var origin = Point.Empty;
            var blend = new Blend { Opacity = 255, AlphaFormat = 1 };
            const int perPixelAlpha = 2;
            if (!UpdateLayeredWindow(Handle, IntPtr.Zero, IntPtr.Zero, ref size, device, ref origin, 0, ref blend, perPixelAlpha))
                throw new Win32Exception();
        }
        finally
        {
            if (previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(device, previous);
            if (device != IntPtr.Zero) DeleteDC(device);
            DeleteObject(bitmap);
        }
    }

    private static IntPtr CreateLayerBitmap(Size size, out IntPtr pixels)
    {
        // Win32 alpha blending reads premultiplied BGRA directly from this top-down bitmap.
        var info = new BitmapInfoHeader
        {
            HeaderSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            Width = size.Width,
            Height = -size.Height,
            Planes = 1,
            BitsPerPixel = 32
        };
        return CreateDIBSection(IntPtr.Zero, ref info, 0, out pixels, IntPtr.Zero, 0);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _menu.Dispose();
            _atlas.Dispose();
        }
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blend
    {
        public byte Operation, Flags, Opacity, AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint HeaderSize;
        public int Width, Height;
        public ushort Planes, BitsPerPixel;
        public uint Compression, ImageSize;
        public int PixelsPerMeterX, PixelsPerMeterY;
        public uint ColorsUsed, ImportantColors;
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr device, ref BitmapInfoHeader info, uint usage,
        out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr device);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr device, IntPtr handle);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr device);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr destination, IntPtr point, ref Size size,
        IntPtr source, ref Point origin, int colorKey, ref Blend blend, int flags);
}
