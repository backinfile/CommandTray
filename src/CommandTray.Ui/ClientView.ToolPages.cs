using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using CommandTray.Core;

namespace CommandTray.Ui;

public sealed partial class ClientView
{
    private string? _sshSelection;

    private async Task RenderSshAsync(CancellationToken token)
    {
        Header(L("SSH 主机", "SSH hosts"));
        var workspace = await ReadPageContent("workspace_read", token);
        var hosts = workspace.Array("sshHosts").OfType<JsonObject>().ToArray();
        var canManage = Has(ClientFeatures.Ssh);
        var add = PrimaryAction(L("添加主机", "Add host"), () => HostForm(null), "plus");
        var keys = Menu("⋯",
            (L("导入密钥", "Import key"), () => Form(L("导入加密 SSH 密钥", "Import encrypted SSH key"), values =>
                Change("ssh_import", values), new Field("path", L("私钥路径", "Private key path")),
                new Field("name", L("名称", "Name")), new Field("passphrase", L("同步口令", "Sync passphrase"), Secret: true))),
            (L("密钥", "Keys"), ShowKeysAsync));
        _contents.Children.Add(PageHeading("SSH", Muted(L($"{hosts.Length} 台主机", $"{hosts.Length} hosts")), canManage ? Row(add, keys) : null));
        if (hosts.Length == 0)
        {
            _contents.Children.Add(EmptyState("ssh", L("暂无主机", "No hosts")));
            return;
        }

        var list = new StackPanel();
        var detail = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        _sshSelection = hosts.Any(host => host.Text("id") == _sshSelection) ? _sshSelection : hosts[0].Text("id");
        var listing = WorkbenchPanel(L("主机", "Hosts"), "ssh", list);
        if (_compact) { _contents.Children.Add(listing); _contents.Children.Add(detail); }
        else _contents.Children.Add(WorkbenchSplit(listing, detail));
        RenderHosts();

        void RenderHosts()
        {
            list.Children.Clear();
            foreach (var host in hosts)
            {
                var row = Action(host.Text("name"), () =>
                {
                    _sshSelection = host.Text("id");
                    RenderHosts();
                    if (_compact) detail.BringIntoView();
                    return Task.CompletedTask;
                });
                row.Content = new StackPanel { Spacing = 4, Children = { Heading(host.Text("name")), Muted(HostAddress(host)) } };
                row.Classes.Add("workbench-list-row");
                row.Classes.Set("selected", _sshSelection == host.Text("id"));
                list.Children.Add(row);
            }
            var current = hosts.First(host => host.Text("id") == _sshSelection);
            var address = new StackPanel { Children =
            {
                SettingRow(L("地址", "Address"), Muted(current.Text("address"))),
                SettingRow(L("用户名", "Username"), Muted(current.Text("username"))),
                SettingRow(L("端口", "Port"), Muted(current.Integer("port", 22).ToString(CultureInfo.InvariantCulture))),
                SettingRow(L("身份", "Identity"), Muted(current.Text("keyId") is { Length: > 0 } id ? id : L("系统 SSH 配置", "System SSH configuration")))
            }};
            var actions = canManage ? Row(
                PrimaryAction(L("在空间中连接", "Connect in space"), () => ConnectHostAsync(current)),
                Menu("⋯", (L("编辑", "Edit"), () => HostForm(current)),
                    (L("删除", "Delete"), () => Confirm(L("删除 SSH 主机？", "Delete SSH host?"),
                        () => Change("ssh_delete", new() { ["id"] = current.Text("id") }))))) : null;
            detail.Content = WorkbenchPanel(current.Text("name"), "ssh", address, actions);
        }
    }
}
