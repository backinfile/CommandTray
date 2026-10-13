using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using CommandTray.Services;

namespace CommandTray.Tests;

[Collection("Desktop UI")]
public sealed class WorkspaceControlsTests
{
    [Fact]
    public void TabsSupportArrowNavigationSingleTabStopAndAccessibleSelection() => SettingsFormTests.OnUiThread(() =>
    {
        using var form = TestWindow();
        var tabs = new WorkspaceTabs { Dock = DockStyle.Fill };
        var first = tabs.AddPage("Configuration", "基本配置");
        var second = tabs.AddPage("Startup & shutdown", "启动与停止");
        var third = tabs.AddPage("Environment", "环境变量");
        var draft = new TextBox { Text = "unsaved command" }; first.Controls.Add(draft);
        var option = new TextBox(); second.Controls.Add(option);
        var saves = 0;
        var save = new Button { Text = "Save", Dock = DockStyle.Bottom };
        save.Click += (_, _) => saves++;
        form.Controls.Add(tabs); form.Controls.Add(save); form.AcceptButton = save;
        form.Show(); Application.DoEvents();
        var headers = Descendants(tabs).OfType<Button>().ToArray();
        headers[0].Focus();
        Assert.True(CommandKey(headers[0], Keys.Right));
        Assert.Equal(1, tabs.SelectedIndex);
        Assert.True(headers[1].Focused);
        Assert.Single(headers, header => header.TabStop);
        Assert.False(first.Visible); Assert.True(second.Visible); Assert.False(third.Visible);
        Assert.Equal(AccessibleRole.PageTab, headers[1].AccessibilityObject.Role);
        Assert.True(headers[1].AccessibilityObject.State.HasFlag(AccessibleStates.Selected));
        Assert.False(headers[0].AccessibilityObject.State.HasFlag(AccessibleStates.Selected));
        var enter = Message.Create(headers[1].Handle, 0x0100, (nint)Keys.Enter, 0);
        headers[1].PreProcessMessage(ref enter);
        Assert.Equal(0, saves); Assert.Equal(1, tabs.SelectedIndex);
        form.SelectNextControl(headers[1], true, true, true, true);
        Assert.True(option.Focused);
        Assert.True(CommandKey(tabs, Keys.Control | Keys.Shift | Keys.Tab));
        Assert.Equal(0, tabs.SelectedIndex);
        Assert.Equal("unsaved command", draft.Text);
        Assert.True(CommandKey(headers[0], Keys.End)); Assert.Equal(2, tabs.SelectedIndex);
        Assert.True(CommandKey(headers[2], Keys.Right)); Assert.Equal(0, tabs.SelectedIndex);
        headers[2].AccessibilityObject.DoDefaultAction(); Assert.Equal(2, tabs.SelectedIndex);
        Assert.True(headers[2].Focused);
        Assert.True(CommandKey(headers[2], Keys.Home)); Assert.Equal(0, tabs.SelectedIndex);
    });

    [Fact]
    public void LanguageAndSelectionChangesPreserveTabTargetsAndPageDrafts() => SettingsFormTests.OnUiThread(() =>
    {
        Localizer.Configure("en");
        try
        {
            using var form = TestWindow();
            var tabs = new WorkspaceTabs { Dock = DockStyle.Fill };
            tabs.AddPage("Configuration", "基本配置");
            var page = tabs.AddPage("Startup & shutdown", "启动与停止");
            tabs.AddPage("Environment", "环境变量");
            var draft = new TextBox { Text = "preserved" }; page.Controls.Add(draft);
            form.Controls.Add(tabs); form.Show(); Application.DoEvents();
            var headers = Descendants(tabs).OfType<Button>().ToArray();
            var bounds = headers.Select(header => header.Bounds).ToArray();
            headers[1].PerformClick(); Application.DoEvents();
            Assert.Equal(bounds, headers.Select(header => header.Bounds).ToArray());
            Localizer.Configure("zh"); Application.DoEvents();
            Assert.Equal(1, tabs.SelectedIndex); Assert.Equal("启动与停止", headers[1].Text);
            Assert.Equal("preserved", draft.Text);
            Assert.All(headers, header => Assert.True(header.Parent!.ClientRectangle.Contains(header.Bounds)));
            Localizer.Configure("en"); Application.DoEvents();
            Assert.Equal(bounds, headers.Select(header => header.Bounds).ToArray());
        }
        finally { Localizer.Configure("auto"); }
    });

    [Fact]
    public void ButtonDistinguishesHoverPressAndDisabledAndCancelsLostCapture() => SettingsFormTests.OnUiThread(() =>
    {
        using var form = TestWindow();
        var button = new WorkspaceButton { Text = "Start", Tone = ButtonTone.Primary, Location = new Point(20, 20) };
        form.Controls.Add(button); form.Show(); Application.DoEvents();
        var normal = Sample(button);
        Raise(button, "OnMouseEnter", EventArgs.Empty); var hover = Sample(button);
        Raise(button, "OnMouseDown", new MouseEventArgs(MouseButtons.Right, 1, 10, 10, 0));
        Assert.Equal(hover, Sample(button));
        button.Capture = true;
        Raise(button, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 10, 10, 0));
        var pressed = Sample(button);
        Assert.NotEqual(normal, hover); Assert.NotEqual(hover, pressed);
        button.Capture = false; Assert.Equal(hover, Sample(button));
        Raise(button, "OnMouseLeave", EventArgs.Empty); Assert.Equal(normal, Sample(button));
        Raise(button, "OnKeyDown", new KeyEventArgs(Keys.Space)); Assert.Equal(pressed, Sample(button));
        button.Enabled = false; Assert.NotEqual(pressed, Sample(button));
        button.Enabled = true; Assert.Equal(normal, Sample(button));
    });

    private static Form TestWindow() => new()
    {
        ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
        Location = new Point(-20000, -20000), ClientSize = new Size(640, 420)
    };
    internal static IEnumerable<Control> Descendants(Control control)
    {
        foreach (Control child in control.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    internal static bool CommandKey(Control control, Keys keys)
    {
        var method = control.GetType().GetMethod("ProcessCmdKey", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (bool)method.Invoke(control, [new Message(), keys])!;
    }
    private static void Raise(Control control, string method, EventArgs args) =>
        control.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(control, [args]);
    private static int Sample(Control control)
    {
        using var bitmap = new Bitmap(control.Width, control.Height);
        control.DrawToBitmap(bitmap, control.ClientRectangle);
        return bitmap.GetPixel(control.Width / 2, 6).ToArgb();
    }
}
