using System.Drawing.Imaging;
using CommandTray.Services;

namespace CommandTray;

internal sealed class CaptureSelectionForm : Form
{
    [Flags]
    private enum Grip
    {
        None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8, Move = 16, Draw = 32,
        TopLeft = Top | Left, TopRight = Top | Right, BottomLeft = Bottom | Left, BottomRight = Bottom | Right
    }

    private const int HandleSize = 8;
    private static readonly Color Accent = Color.FromArgb(141, 184, 255);
    private readonly Bitmap _snapshot;
    private readonly Rectangle _desktop;
    private readonly FlowLayoutPanel _actions;
    private Rectangle _selection;
    private Rectangle _original;
    private Point _anchor;
    private Grip _drag;
    private bool _moved;

    public CaptureSelectionForm(Rectangle desktop, Bitmap snapshot)
    {
        _desktop = desktop;
        _snapshot = snapshot;
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.None;
        Bounds = desktop;
        DoubleBuffered = true;
        ShowInTaskbar = false;
        TopMost = true;
        Cursor = Cursors.Cross;
        Text = Localizer.T("Select a region", "选择区域");
        var confirm = new Button { AutoSize = true, Text = Localizer.T("Confirm", "确认"), ForeColor = Color.FromArgb(17, 31, 50), BackColor = Accent };
        var cancel = new Button { AutoSize = true, Text = Localizer.T("Cancel", "取消"), ForeColor = Color.White, BackColor = Color.FromArgb(39, 53, 67) };
        confirm.Click += (_, _) => ConfirmRegion();
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        AcceptButton = confirm;
        _actions = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Padding = new Padding(3),
            BackColor = Color.FromArgb(21, 28, 37), Cursor = Cursors.Default, Visible = false
        };
        _actions.Controls.Add(confirm);
        _actions.Controls.Add(cancel);
        Controls.Add(_actions);
        Shown += (_, _) => Activate();
    }

    internal Rectangle DesktopSelection => new(_desktop.X + _selection.X, _desktop.Y + _selection.Y, _selection.Width, _selection.Height);

    internal Bitmap Crop() => _snapshot.Clone(_selection, PixelFormat.Format32bppArgb);

    protected override bool ProcessCmdKey(ref Message msg, Keys keys)
    {
        var key = keys & Keys.KeyCode;
        if (key == Keys.Escape) { DialogResult = DialogResult.Cancel; return true; }
        if (key == Keys.Enter && !_selection.IsEmpty) { ConfirmRegion(); return true; }
        if (_drag == Grip.None && !_selection.IsEmpty && key is Keys.Left or Keys.Right or Keys.Up or Keys.Down)
        {
            int step = keys.HasFlag(Keys.Shift) ? 10 : 1;
            var dx = key == Keys.Left ? -step : key == Keys.Right ? step : 0;
            var dy = key == Keys.Up ? -step : key == Keys.Down ? step : 0;
            MoveSelection(_selection, dx, dy);
            UpdateActions();
            Invalidate();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keys);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Right) { DialogResult = DialogResult.Cancel; return; }
        if (e.Button != MouseButtons.Left) return;
        _drag = HitTest(e.Location);
        if (_drag == Grip.None) _drag = Grip.Draw;
        _original = _selection;
        _anchor = Inside(e.Location);
        _moved = false;
        _actions.Visible = false;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag != Grip.None)
        {
            var point = Inside(e.Location);
            if (_moved || point != _anchor) { _moved = true; Adjust(point); Invalidate(); }
        }
        else
        {
            Cursor = HitTest(e.Location) switch
            {
                Grip.Left or Grip.Right => Cursors.SizeWE,
                Grip.Top or Grip.Bottom => Cursors.SizeNS,
                Grip.TopLeft or Grip.BottomRight => Cursors.SizeNWSE,
                Grip.TopRight or Grip.BottomLeft => Cursors.SizeNESW,
                Grip.Move => Cursors.SizeAll,
                _ => Cursors.Cross
            };
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || _drag == Grip.None) return;
        var point = Inside(e.Location);
        if (_moved || point != _anchor) Adjust(point);
        _drag = Grip.None;
        Capture = false;
        UpdateActions();
        Invalidate();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left && _selection.Contains(e.Location)) ConfirmRegion();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (Capture || _drag == Grip.None) return;
        _drag = Grip.None;
        _selection = _original;
        UpdateActions();
        Invalidate();
    }

    private void Adjust(Point point)
    {
        int dx = point.X - _anchor.X, dy = point.Y - _anchor.Y;
        if (_drag == Grip.Draw)
        {
            _selection = Rectangle.FromLTRB(Math.Min(_anchor.X, point.X), Math.Min(_anchor.Y, point.Y),
                Math.Max(_anchor.X, point.X), Math.Max(_anchor.Y, point.Y));
            if (_selection.Width == 0 || _selection.Height == 0) _selection = Rectangle.Empty;
        }
        else if (_drag == Grip.Move) MoveSelection(_original, dx, dy);
        else
        {
            int left = _drag.HasFlag(Grip.Left) ? Math.Clamp(_original.Left + dx, 0, _original.Right - 1) : _original.Left;
            int right = _drag.HasFlag(Grip.Right) ? Math.Clamp(_original.Right + dx, _original.Left + 1, ClientSize.Width) : _original.Right;
            int top = _drag.HasFlag(Grip.Top) ? Math.Clamp(_original.Top + dy, 0, _original.Bottom - 1) : _original.Top;
            int bottom = _drag.HasFlag(Grip.Bottom) ? Math.Clamp(_original.Bottom + dy, _original.Top + 1, ClientSize.Height) : _original.Bottom;
            _selection = Rectangle.FromLTRB(left, top, right, bottom);
        }
    }

    private Point Inside(Point p) => new(Math.Clamp(p.X, 0, ClientSize.Width), Math.Clamp(p.Y, 0, ClientSize.Height));

    private void MoveSelection(Rectangle area, int dx, int dy) =>
        _selection = new Rectangle(Math.Clamp(area.X + dx, 0, ClientSize.Width - area.Width),
            Math.Clamp(area.Y + dy, 0, ClientSize.Height - area.Height), area.Width, area.Height);

    private Grip HitTest(Point point)
    {
        if (_selection.IsEmpty || !Rectangle.Inflate(_selection, HandleSize, HandleSize).Contains(point)) return Grip.None;
        var grip = Grip.None;
        if (Math.Abs(point.X - _selection.Left) <= HandleSize) grip |= Grip.Left;
        else if (Math.Abs(point.X - _selection.Right) <= HandleSize) grip |= Grip.Right;
        if (Math.Abs(point.Y - _selection.Top) <= HandleSize) grip |= Grip.Top;
        else if (Math.Abs(point.Y - _selection.Bottom) <= HandleSize) grip |= Grip.Bottom;
        return grip == Grip.None ? Grip.Move : grip;
    }

    private void UpdateActions()
    {
        if (_selection.IsEmpty) { _actions.Visible = false; return; }
        _actions.Visible = true;
        var size = _actions.GetPreferredSize(Size.Empty);
        var center = new Point(_desktop.X + _selection.X + _selection.Width / 2, _desktop.Y + _selection.Y + _selection.Height / 2);
        var monitor = Screen.FromPoint(center).Bounds;
        monitor.Offset(-_desktop.X, -_desktop.Y);
        var bounds = Rectangle.Intersect(ClientRectangle, monitor);
        if (bounds.Width < size.Width || bounds.Height < size.Height) bounds = ClientRectangle;
        var top = _selection.Bottom + size.Height + 8 <= bounds.Bottom ? _selection.Bottom + 8 : _selection.Top - size.Height - 8;
        _actions.Location = new Point(Math.Clamp(_selection.Right - size.Width, bounds.Left, Math.Max(bounds.Left, bounds.Right - size.Width)),
            Math.Clamp(top, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - size.Height)));
        _actions.BringToFront();
    }

    private void ConfirmRegion()
    {
        if (_selection.Width > 0 && _selection.Height > 0) DialogResult = DialogResult.OK;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.DrawImageUnscaled(_snapshot, 0, 0);
        var state = e.Graphics.Save();
        if (!_selection.IsEmpty) e.Graphics.ExcludeClip(_selection);
        using (var shade = new SolidBrush(Color.FromArgb(152, 0, 0, 0))) e.Graphics.FillRectangle(shade, ClientRectangle);
        e.Graphics.Restore(state);
        if (_selection.IsEmpty) return;
        using var pen = new Pen(Accent, 2);
        e.Graphics.DrawRectangle(pen, _selection);
        foreach (var x in new[] { _selection.Left, _selection.Right })
            foreach (var y in new[] { _selection.Top, _selection.Bottom })
                e.Graphics.FillRectangle(Brushes.White, x - 3, y - 3, 6, 6);
        TextRenderer.DrawText(e.Graphics, $"{_selection.Width} × {_selection.Height}", Font,
            new Point(_selection.Left + 7, _selection.Top + 7), Color.White, Color.FromArgb(21, 28, 37));
    }
}
