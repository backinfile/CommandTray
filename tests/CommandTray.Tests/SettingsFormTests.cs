using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CommandTray.Models;
using CommandTray.Services;

namespace CommandTray.Tests;

[CollectionDefinition("Desktop UI", DisableParallelization = true)]
public sealed class DesktopUiCollection;

[Collection("Desktop UI")]
public sealed class SettingsFormTests
{
    [Fact]
    public void InvalidEditedCommandPreventsStartingButDoesNotPreventStopping() => OnUiThread(() =>
    {
        WithForm((form, configuration, store) =>
        {
            Field<TextBox>(form, "_command").Text = "";
            var calls = 0;
            Func<ServiceDefinition, CancellationToken, Task> operation = (service, token) =>
            {
                calls++; Assert.Equal("echo original", service.Command); return Task.CompletedTask;
            };
            var method = typeof(SettingsForm).GetMethod("RunSelected", BindingFlags.NonPublic | BindingFlags.Instance)!;
            ((Task)method.Invoke(form, [operation, true, null])!).GetAwaiter().GetResult();
            Assert.Equal(0, calls);
            ((Task)method.Invoke(form, [operation, false, null])!).GetAwaiter().GetResult();
            Assert.Equal(1, calls);
            Assert.Equal("", Field<TextBox>(form, "_command").Text);
        });
    });

    [Fact]
    public void ExistingErrorSnapshotChangesLanguageWithoutChangingBackendMessage() => OnUiThread(() =>
    {
        WithForm((form, configuration, store) =>
        {
            var snapshot = new ServiceSnapshot(configuration.Services[0].Id, "Original", ServiceState.Faulted, null, "Exited with code 7.");
            var method = typeof(SettingsForm).GetMethod("ApplySnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!;
            Localizer.Configure("en"); method.Invoke(form, [snapshot]);
            Assert.Equal("Exited with code 7.", Field<Label>(form, "_stateDetail").Text);
            Localizer.Configure("zh"); method.Invoke(form, [snapshot]);
            Assert.Equal("进程已退出，退出码：7。", Field<Label>(form, "_stateDetail").Text);
            Assert.Equal("Exited with code 7.", snapshot.Error);
        });
    });

    [Fact]
    public void MonitorDpiChangesScaleExplicitFontsWithoutAccumulating() => OnUiThread(() =>
    {
        WithForm((form, configuration, store) =>
        {
            form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual;
            form.Location = new System.Drawing.Point(-20000, -20000); form.Show();
            Application.DoEvents();
            SetDpi(form, 144);
            Assert.Equal(144, form.DeviceDpi);
            Assert.Equal(27f, Field<Label>(form, "_title").Font.Size, 1);
            Assert.Equal(15f, Field<TextBox>(form, "_command").Font.Size, 1);
            AssertTabsFit(form);
            SetDpi(form, 192);
            Assert.Equal(36f, Field<Label>(form, "_title").Font.Size, 1);
            AssertTabsFit(form);
            SetDpi(form, 96);
            Assert.Equal(18f, Field<Label>(form, "_title").Font.Size, 1);
            Assert.Equal(10f, Field<TextBox>(form, "_command").Font.Size, 1);
            AssertTabsFit(form);
        });
    });

    private static void AssertTabsFit(SettingsForm form)
    {
        var tabs = Field<WorkspaceTabs>(form, "_tabs");
        foreach (var header in WorkspaceControlsTests.Descendants(tabs).OfType<Button>().Where(button => button.AccessibleRole == AccessibleRole.PageTab))
        {
            Assert.True(header.Parent!.ClientRectangle.Contains(header.Bounds), $"{header.Text}: {header.Bounds}, strip {header.Parent.ClientRectangle}, preferred {header.PreferredSize}, dpi {form.DeviceDpi}");
            Assert.True(header.Height >= 48 * form.DeviceDpi / 96);
        }
    }

    [Fact]
    public void ServiceActionsShowProgressWithoutMovingTargetsOrAcceptingDuplicateCommands() => OnUiThread(() =>
    {
        WithForm((form, configuration, store) =>
        {
            form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual;
            form.Location = new System.Drawing.Point(-20000, -20000); form.Show(); Application.DoEvents();
            var start = Field<Button>(form, "_start"); var stop = Field<Button>(form, "_stop");
            var restart = Field<Button>(form, "_restartButton");
            Assert.True(start.Visible); Assert.False(stop.Visible);
            var startBounds = start.Bounds; var restartBounds = restart.Bounds;
            var completion = new TaskCompletionSource(); var calls = 0;
            Func<ServiceDefinition, CancellationToken, Task> operation = (_, _) => { calls++; return completion.Task; };
            var run = typeof(SettingsForm).GetMethod("RunSelected", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var task = (Task)run.Invoke(form, [operation, true, start])!;
            try
            {
                Application.DoEvents();
                Assert.Equal("Starting…", start.Text); Assert.False(start.Enabled);
                Assert.Equal(startBounds, start.Bounds); Assert.Equal(restartBounds, restart.Bounds);
                ((Task)run.Invoke(form, [operation, true, start])!).GetAwaiter().GetResult();
                Assert.Equal(1, calls);
                var apply = typeof(SettingsForm).GetMethod("ApplySnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!;
                apply.Invoke(form, [new ServiceSnapshot(configuration.Services[0].Id, "Original", ServiceState.Running, 42, null)]);
                Assert.True(start.Visible); Assert.False(stop.Visible);
                Localizer.Configure("zh"); Assert.Equal("启动中…", start.Text);
            }
            finally
            {
                completion.SetResult();
                while (!task.IsCompleted) { Application.DoEvents(); Thread.Yield(); }
                task.GetAwaiter().GetResult();
            }
            Assert.True(start.Enabled); Assert.Equal("启动", start.Text);
            typeof(SettingsForm).GetMethod("ApplySnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(form,
                [new ServiceSnapshot(configuration.Services[0].Id, "Original", ServiceState.Running, 42, null)]);
            Assert.False(start.Visible); Assert.True(stop.Visible); Assert.True(stop.Enabled); Assert.True(restart.Enabled);
        });
    });

    [Fact]
    public void SearchingAndExternalRefreshPreserveUnsavedEdits() => OnUiThread(() =>
    {
        WithForm((form, configuration, store) =>
        {
            Field<TextBox>(form, "_name").Text = "Unsaved name";
            Field<TextBox>(form, "_search").Text = "no match";
            Assert.Equal("Unsaved name", Field<TextBox>(form, "_name").Text);
            Assert.Equal("Original", configuration.Services[0].Name);
            form.RefreshFromExternalChange();
            Assert.Equal("Unsaved name", Field<TextBox>(form, "_name").Text);
        });
    });

    [Fact]
    public void NewDraftDoesNotEnterLiveConfigurationUntilSaved() => OnUiThread(() =>
    {
        WithForm((form, configuration, store) =>
        {
            Invoke(form, "AddService");
            Assert.Single(configuration.Services);
            Assert.False((bool)Invoke(form, "SaveSelected")!);
            Assert.Single(configuration.Services);
            Field<TextBox>(form, "_command").Text = "echo new";
            Assert.True((bool)Invoke(form, "SaveSelected")!);
            Assert.Equal(2, configuration.Services.Count);
            Assert.Equal(2, store.Load().Services.Count);
        });
    });

    [Fact]
    public void LanguageSwitchUpdatesOpenControlsWithoutDiscardingEdits() => OnUiThread(() =>
    {
        WithForm((form, configuration, store) =>
        {
            Field<TextBox>(form, "_command").Text = "echo unsaved";
            Field<ComboBox>(form, "_language").SelectedIndex = 2;
            Assert.Equal("保存修改", Field<Button>(form, "_save").Text);
            Assert.Equal("zh", store.Load().Language);
            Assert.Equal("echo unsaved", Field<TextBox>(form, "_command").Text);
            Field<ComboBox>(form, "_language").SelectedIndex = 1;
            Assert.Equal("Save changes", Field<Button>(form, "_save").Text);
            Assert.Equal("en", store.Load().Language);
            Assert.Equal("Original", configuration.Services[0].Name);
        });
    });

    [Fact]
    public void McpSettingsStayOpenAndAlignedAcrossLanguageAndDpiChanges() => OnUiThread(() =>
    {
        WithForm((form, configuration, store) =>
        {
            form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual;
            form.Location = new System.Drawing.Point(-20000, -20000); form.Size = form.MinimumSize;
            form.Show(); Field<Button>(form, "_settings").PerformClick(); Application.DoEvents();
            var global = Field<Panel>(form, "_global");
            var toggle = Field<ToggleSwitch>(form, "_enableMcp");
            var status = Field<Label>(form, "_mcpStatus");
            var endpoint = Field<TextBox>(form, "_mcpEndpoint");
            var copy = Field<Button>(form, "_copyMcpEndpoint");
            foreach (var dpi in new[] { 96, 144, 192, 96 })
            {
                SetDpi(form, dpi);
                form.Size = form.MinimumSize; Application.DoEvents();
                // Native ListBox handle recreation must not navigate away.
                Assert.True(global.Visible);
                foreach (var language in new[] { "zh", "en" })
                {
                    Localizer.Configure(language); Application.DoEvents();
                    Assert.True(global.Visible); Assert.False(global.HorizontalScroll.Visible);
                    Assert.True(toggle.Parent!.ClientRectangle.Contains(toggle.Bounds));
                    Assert.True(status.Parent!.ClientRectangle.Contains(status.Bounds));
                    Assert.True(status.Parent.Right + toggle.Margin.Left <= toggle.Left);
                    Assert.InRange(Math.Abs(toggle.Top * 2 + toggle.Height - toggle.Parent.Height), 0, 1);
                    Assert.True(toggle.Height >= 40 * dpi / 96);
                    Assert.True(copy.Parent!.ClientRectangle.Contains(copy.Bounds));
                    Assert.True(endpoint.Right + endpoint.Margin.Right <= copy.Left);
                    Assert.InRange(Math.Abs(endpoint.Top * 2 + endpoint.Height - copy.Top * 2 - copy.Height), 0, 2);
                    Assert.True(endpoint.Width >= TextRenderer.MeasureText(endpoint.Text, endpoint.Font).Width);
                    var normalWidth = copy.Width;
                    typeof(SettingsForm).GetField("_mcpCopied", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(form, true);
                    Invoke(form, "UpdateMcpPresentation"); Application.DoEvents();
                    Assert.Equal(Localizer.T("Copied", "已复制"), copy.Text);
                    Assert.Equal(normalWidth, copy.Width);
                    typeof(SettingsForm).GetField("_mcpCopied", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(form, false);
                    Invoke(form, "UpdateMcpPresentation");
                }
            }
            var select = typeof(SettingsForm).GetMethod("SelectService", BindingFlags.NonPublic | BindingFlags.Instance)!;
            select.Invoke(form, [false]); Assert.True(global.Visible);
            select.Invoke(form, [true]); Assert.False(global.Visible);
        });
    });

    [Fact]
    public void McpLabelAndSpaceTogglePreferenceOnceAndPreserveServiceDraft() => OnUiThread(() =>
    {
        WithForm((form, configuration, store) =>
        {
            form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual;
            form.Location = new System.Drawing.Point(-20000, -20000); form.Show();
            Field<TextBox>(form, "_command").Text = "unsaved command";
            Field<Button>(form, "_settings").PerformClick();
            var toggle = Field<ToggleSwitch>(form, "_enableMcp");
            var events = 0; form.McpEnabledChanged += (_, _) => events++;
            var label = WorkspaceControlsTests.Descendants(Field<Panel>(form, "_global"))
                .OfType<Label>().Single(control => control.Text == "Enable local MCP server");
            typeof(Control).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(label, [EventArgs.Empty]);
            Assert.False(toggle.Checked); Assert.False(store.Load().McpEnabled); Assert.Equal(1, events);
            Assert.Equal("Disabled", Field<Label>(form, "_mcpStatus").Text);
            Assert.True(Field<Button>(form, "_copyMcpEndpoint").Enabled);
            toggle.Focus();
            typeof(ToggleSwitch).GetMethod("OnKeyDown", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(toggle, [new KeyEventArgs(Keys.Space)]);
            typeof(ToggleSwitch).GetMethod("OnKeyUp", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(toggle, [new KeyEventArgs(Keys.Space)]);
            Assert.True(toggle.Checked); Assert.True(store.Load().McpEnabled); Assert.Equal(2, events);
            Assert.Equal("unsaved command", Field<TextBox>(form, "_command").Text);
        });
    });

    private static void WithForm(Action<SettingsForm, AppConfiguration, ConfigurationStore> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"CommandTray-ui-{Guid.NewGuid():N}");
        var configuration = new AppConfiguration { Services = [new ServiceDefinition { Name = "Original", Command = "echo original" }] };
        var store = new ConfigurationStore(Path.Combine(directory, "config.json"));
        var supervisor = new ProcessSupervisor(Path.Combine(directory, "logs"));
        Localizer.Configure("en");
        try
        {
            using var form = new SettingsForm(configuration, store, supervisor);
            _ = form.Handle;
            action(form, configuration, store);
            form.AllowExit();
        }
        finally
        {
            supervisor.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Localizer.Configure("auto");
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static T Field<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
    private static object? Invoke(object instance, string name) =>
        instance.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, null);

    private static void SetDpi(Form form, int dpi)
    {
        var rectangle = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.WriteInt32(rectangle, 0, 0); Marshal.WriteInt32(rectangle, 4, 0);
            Marshal.WriteInt32(rectangle, 8, 2000); Marshal.WriteInt32(rectangle, 12, 1600);
            SendMessage(form.Handle, 0x02E0, (dpi << 16) | dpi, rectangle);
            Application.DoEvents();
        }
        finally { Marshal.FreeHGlobal(rectangle); }
    }
    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);

    internal static void OnUiThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var previous = SetThreadDpiAwarenessContext(-4); // Per-monitor V2, matching the application.
            try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); action(); }
            catch (Exception ex) { failure = ex; }
            finally { if (previous != 0) SetThreadDpiAwarenessContext(previous); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint context);
}
