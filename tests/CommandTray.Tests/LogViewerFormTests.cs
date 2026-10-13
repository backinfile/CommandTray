using System.Reflection;
using System.Windows.Forms;
using CommandTray.Services;

namespace CommandTray.Tests;

[Collection("Desktop UI")]
public sealed class LogViewerFormTests
{
    [Fact]
    public void SearchWrapsAndPreservesSelectionWhenOutputArrives() => SettingsFormTests.OnUiThread(() =>
    {
        var directory = Path.Combine(Path.GetTempPath(), $"CommandTray-log-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "current.log");
        File.WriteAllText(path, "[OUT] ready\n[ERR] example failure\n[OUT] done\n");
        try
        {
            using var form = new LogViewerForm("Example", path);
            _ = form.Handle;
            Invoke(form, "ReadNewContent");
            Field<TextBox>(form, "_search").Text = "failure";
            Invoke(form, "FindNext");
            var output = Field<RichTextBox>(form, "_text");
            var selection = output.SelectionStart;
            Assert.Equal("failure", output.SelectedText);
            Assert.False(Field<CheckBox>(form, "_follow").Checked);
            Invoke(form, "FindNext");
            Assert.Equal(selection, output.SelectionStart);
            File.AppendAllText(path, "[OUT] later output\n");
            Invoke(form, "ReadNewContent");
            Assert.Equal(selection, output.SelectionStart);
            Assert.Equal("failure", output.SelectedText);
            Assert.Contains("later output", output.Text);
        }
        finally { Directory.Delete(directory, true); }
    });

    [Fact]
    public void EmptyLogPlaceholderSwitchesLanguageInAnOpenViewer() => SettingsFormTests.OnUiThread(() =>
    {
        Localizer.Configure("en");
        try
        {
            using var form = new LogViewerForm("Example", Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".log"));
            Invoke(form, "ShowPlaceholderIfEmpty");
            Assert.Equal("No log output yet.", Field<RichTextBox>(form, "_text").Text);
            Localizer.Configure("zh");
            Assert.Equal("暂无日志输出。", Field<RichTextBox>(form, "_text").Text);
            Assert.Equal("跟随输出", Field<CheckBox>(form, "_follow").Text);
            Localizer.Configure("en");
            Assert.Equal("No log output yet.", Field<RichTextBox>(form, "_text").Text);
        }
        finally { Localizer.Configure("auto"); }
    });

    private static T Field<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
    private static void Invoke(object instance, string name) =>
        instance.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, null);
}
