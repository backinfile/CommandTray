using System.Text.Json.Nodes;
using CommandTray.Services;
using static CommandTray.DesktopWorkspacePanel;

namespace CommandTray;

internal sealed class DesktopSshPanel : UserControl
{
    public DesktopSshPanel(DesktopRuntime desktop, Action<JsonObject> connect)
    {
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false };
        list.Columns.Add(Localizer.T("Name", "名称"), 110);
        list.Columns.Add(Localizer.T("Host", "主机"), 170);
        list.Columns.Add(Localizer.T("User", "用户"), 80);
        var add = Button("Add…", "添加…", () => Safe(() => Edit(null)));
        var edit = Button("Edit…", "编辑…", () => Safe(() => Edit(Selected())));
        var delete = Button("Delete", "删除", () => Safe(() =>
        {
            if (Id(Selected()) is not { } id) return;
            desktop.Workspace.DeleteHost(id);
        }));
        var launch = Button("Connect", "连接", () => Safe(() => { if (Selected() is { } host) connect(host); }), true);
        var import = Button("Import key…", "导入密钥…", () => Safe(() =>
        {
            using var picker = new OpenFileDialog();
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            var name = Prompt(this, Localizer.T("Key name", "密钥名称"), Path.GetFileName(picker.FileName));
            if (name is null) return;
            var passphrase = Prompt(this, Localizer.T("New SSH vault passphrase", "设置 SSH 密钥库口令"), "", secret: true);
            if (passphrase is null) return;
            var repeat = Prompt(this, Localizer.T("Confirm passphrase", "确认口令"), "", secret: true);
            if (passphrase != repeat) throw new ArgumentException("The passphrases do not match.");
            desktop.ImportSshKey(picker.FileName, name, passphrase);
        }));
        var keys = Button("Keys…", "密钥…", () => Safe(() =>
        {
            var key = PickKey(this, desktop.Workspace.ListSshKeys(), null);
            if (key is null or "") return;
            if (MessageBox.Show(this, Localizer.T("Delete this key from the workspace?", "从工作空间中删除此密钥？"), "CommandTray",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK) desktop.Workspace.RemoveSshKey(key);
        }));
        Controls.Add(list); Controls.Add(Toolbar(add, edit, delete, launch, import, keys));
        list.DoubleClick += (_, _) => launch.PerformClick();
        Reload();
        void Reload()
        {
            if (IsDisposed) return;
            list.Items.Clear();
            foreach (var host in desktop.Workspace.ReadHosts().OfType<JsonObject>())
            {
                var row = new ListViewItem(Value(host, "name")) { Tag = host };
                row.SubItems.Add($"{Value(host, "address")}:{WorkspaceStore.Number(host["port"], 22)}");
                row.SubItems.Add(Value(host, "username"));
                list.Items.Add(row);
            }
        }
        JsonObject? Selected() => list.SelectedItems.Count > 0 ? list.SelectedItems[0].Tag as JsonObject : null;
        void Edit(JsonObject? host)
        {
            var name = Prompt(this, Localizer.T("Host name", "主机名称"), Value(host, "name"));
            if (name is null) return;
            var address = Prompt(this, Localizer.T("Address", "地址"), Value(host, "address"));
            if (address is null) return;
            var user = Prompt(this, Localizer.T("Username", "用户名"), Value(host, "username"));
            if (user is null) return;
            var port = Prompt(this, Localizer.T("Port", "端口"), (host is null ? 22 : WorkspaceStore.Number(host["port"], 22)).ToString());
            if (port is null) return;
            if (!int.TryParse(port, out var number)) throw new ArgumentException("Invalid port.");
            var key = PickKey(this, desktop.Workspace.ListSshKeys(), Value(host, "keyId"));
            if (key is null) return;
            desktop.Workspace.SetHost(Id(host), name, address, user, number, key == "" ? null : key);
        }
        void Safe(Action action)
        {
            try { action(); }
            catch (Exception ex) { if (!IsDisposed) Error(this, ex); }
        }
    }

    private static string? Id(JsonObject? host) => WorkspaceStore.Text(host?["id"]);
    private static string Value(JsonObject? host, string key) => WorkspaceStore.Text(host?[key]) ?? "";

    private static string? PickKey(IWin32Window owner, IReadOnlyList<SshKeyView> keys, string? selected)
    {
        using var form = new Form { Text = Localizer.T("SSH key", "SSH 密钥"), ClientSize = new Size(320, 85),
            StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false };
        var list = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = nameof(SshKeyView.Name) };
        list.Items.Add(new SshKeyView("", Localizer.T("OpenSSH default", "OpenSSH 默认密钥")));
        list.Items.AddRange(keys.Cast<object>().ToArray());
        list.SelectedIndex = 0;
        foreach (SshKeyView key in list.Items) if (key.Id == selected) { list.SelectedItem = key; break; }
        var confirm = Button("OK", "确定", null, true); confirm.DialogResult = DialogResult.OK;
        var cancel = Button("Cancel", "取消", null); cancel.DialogResult = DialogResult.Cancel;
        form.AcceptButton = confirm; form.CancelButton = cancel;
        var actions = Toolbar(confirm, cancel); actions.Dock = DockStyle.Bottom;
        form.Controls.Add(list); form.Controls.Add(actions);
        return form.ShowDialog(owner) == DialogResult.OK ? ((SshKeyView)list.SelectedItem!).Id : null;
    }
}
