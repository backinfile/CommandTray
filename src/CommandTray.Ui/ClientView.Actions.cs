using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace CommandTray.Ui;

public sealed partial class ClientView
{
    private readonly Stack<ActionDialog> _actionDialogs = [];

    private sealed record ActionDialog(Border Panel, IInputElement? PreviousFocus)
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public bool BlocksClose { get; set; }
    }

    private CancellationToken CurrentActionCancellation =>
        _actionDialogs.TryPeek(out var dialog) ? dialog.Cancellation.Token : CancellationToken.None;

    private ActionDialog OpenActionDialog(string title, Control body, Control actions, Control firstFocus)
    {
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 12 };
        var heading = Heading(title);
        var scroll = new ScrollViewer
        {
            Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(scroll, 1);
        Grid.SetRow(actions, 2);
        layout.Children.Add(heading);
        layout.Children.Add(scroll);
        layout.Children.Add(actions);
        var panel = new Border
        {
            MaxWidth = 520, Margin = new Thickness(16), Padding = new Thickness(16),
            VerticalAlignment = VerticalAlignment.Center, Child = layout
        };
        panel.Classes.Add("palette");
        AutomationProperties.SetName(panel, title);
        KeyboardNavigation.SetTabNavigation(panel, KeyboardNavigationMode.Cycle);
        var dialog = new ActionDialog(panel, TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement());
        _actionDialogs.Push(dialog);
        _actionOverlay.Child = panel;
        _actionOverlay.IsVisible = true;
        PostActionFocus(panel, firstFocus);
        return dialog;
    }

    private void CloseActionDialog()
    {
        if (_actionDialogs.TryPeek(out var dialog) && !dialog.BlocksClose) RemoveActionDialog(dialog);
    }

    private void RemoveActionDialog(ActionDialog dialog, bool restoreFocus = true)
    {
        if (!_actionDialogs.TryPeek(out var active) || !ReferenceEquals(active, dialog)) return;
        _actionDialogs.Pop();
        _actionDialogs.TryPeek(out var parent);
        _actionOverlay.Child = parent?.Panel;
        _actionOverlay.IsVisible = parent is not null;
        dialog.Cancellation.Cancel();
        dialog.Cancellation.Dispose();
        if (restoreFocus) PostActionFocus(parent?.Panel, dialog.PreviousFocus);
    }

    private void CloseActionDialogs()
    {
        while (_actionDialogs.TryPeek(out var dialog)) RemoveActionDialog(dialog, restoreFocus: false);
    }

    private void PostActionFocus(Border? panel, IInputElement? control) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_actionOverlay.Child, panel)) return;
            if (control is Visual visual && TopLevel.GetTopLevel(visual) is not null && control.Focus()) return;
            if (panel is not null)
                panel.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => button.IsEnabled)?.Focus();
            else Focus();
        }, DispatcherPriority.Input);

    private Button CloseActionButton(string? label = null) => Action(label ?? L("取消", "Cancel"), () =>
    {
        CloseActionDialog();
        return Task.CompletedTask;
    });

    private readonly record struct Field(string Key, string Label, string Value = "", bool Secret = false,
        bool Multiline = false, bool Toggle = false);

    private Task Form(string title, Func<JsonObject, Task> save, params Field[] fields) =>
        Form(title, L("保存", "Save"), save, fields);

    private Task Form(string title, string submitLabel, Func<JsonObject, Task> save, params Field[] fields) =>
        ShowForm(title, submitLabel, L("正在处理…", "Working…"), (values, _) => save(values), fields);

    private Task Form(string title, string submitLabel, string pendingLabel,
        Func<JsonObject, CancellationToken, Task> save, params Field[] fields) =>
        ShowForm(title, submitLabel, pendingLabel, save, fields, cancellable: true);

    private Task Confirm(string title, Func<Task> save) =>
        Form(title, L("确认", "Confirm"), _ => save());

    private Task ShowForm(string title, string submitLabel, string pendingLabel,
        Func<JsonObject, CancellationToken, Task> save, Field[] fields, Control? extra = null, bool cancellable = false)
    {
        var body = new StackPanel { Spacing = 8 };
        var error = new TextBlock
        {
            IsVisible = false, TextWrapping = TextWrapping.Wrap
        };
        error.Classes.Add("form-error");
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Assertive);
        var inputs = new List<(string Key, Func<JsonNode?> Value)>();
        Control? firstInput = null;
        foreach (var field in fields)
        {
            if (field.Toggle)
            {
                var checkbox = new CheckBox { Content = field.Label, IsChecked = field.Value == "true" };
                body.Children.Add(checkbox);
                firstInput ??= checkbox;
                inputs.Add((field.Key, () => JsonValue.Create(checkbox.IsChecked == true)));
                continue;
            }
            var input = new TextBox
            {
                Text = field.Value, AcceptsReturn = field.Multiline, TextWrapping = TextWrapping.Wrap,
                PasswordChar = field.Secret ? '●' : '\0', MinHeight = field.Multiline ? 100 : 36,
                MaxHeight = field.Multiline ? 220 : double.PositiveInfinity
            };
            input.Classes.Add("entry");
            AutomationProperties.SetName(input, field.Label);
            input.TextChanged += (_, _) => error.IsVisible = false;
            body.Children.Add(Muted(field.Label));
            body.Children.Add(input);
            firstInput ??= input;
            inputs.Add((field.Key, () => JsonValue.Create(input.Text ?? "")));
        }
        if (extra is not null) body.Children.Add(extra);
        body.Children.Add(error);
        var cancel = CloseActionButton();
        var progress = Muted(pendingLabel);
        progress.IsVisible = false;
        progress.VerticalAlignment = VerticalAlignment.Center;
        ActionDialog dialog = null!;
        var submit = PrimaryAction(submitLabel, async () =>
        {
            var token = dialog.Cancellation.Token;
            var values = new JsonObject();
            foreach (var input in inputs) values[input.Key] = input.Value();
            error.IsVisible = false;
            progress.IsVisible = true;
            body.IsEnabled = false;
            dialog.BlocksClose = !cancellable;
            cancel.IsEnabled = cancellable;
            try
            {
                await save(values, token);
                RemoveActionDialog(dialog);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception failure) when (!token.IsCancellationRequested)
            {
                error.Text = failure.Message;
                error.IsVisible = true;
                error.BringIntoView();
            }
            finally
            {
                dialog.BlocksClose = false;
                cancel.IsEnabled = true;
                body.IsEnabled = true;
                progress.IsVisible = false;
            }
        });
        dialog = OpenActionDialog(title, body, new WrapPanel { ItemSpacing = 8, Children = { cancel, submit, progress } },
            firstInput ?? cancel);
        return Task.CompletedTask;
    }
}
