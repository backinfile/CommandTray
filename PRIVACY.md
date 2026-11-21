# 隐私说明 / Privacy notice

## 中文

CommandTray 是本地运行的开源工具，不包含遥测、行为分析、广告网络或自动崩溃上报，也不会主动向维护者发送个人数据。

### 本地数据

程序会在当前 Windows 用户目录保存以下内容：

- `%LOCALAPPDATA%\CommandTray\config.json`：服务与程序设置；
- `%LOCALAPPDATA%\CommandTray\logs`：程序和托管命令的日志。

标记为敏感的环境变量使用 Windows DPAPI 加密后保存。托管命令的标准输出和错误输出仍可能包含密钥、路径或其他敏感信息，请在共享日志前检查并脱敏。

### 网络与本地接口

- Windows 的 MCP 默认开启，仅监听本机回环地址，使用本机凭据验证调用者，可在设置中关闭。Linux 支持本地命令或回环地址，Android 的回环监听可在设置中启用。
- 配对并操作同步后，数据将发送到用户配置的中转服务器。内容包括工作区、共享文件和主动发送的剪贴板文本。SSH 私钥用设备上的口令加密后同步。
- “打开地址”功能会把用户配置的 URL 交给系统默认浏览器。
- 用户配置并由 CommandTray 启动的命令可能自行访问网络，其数据处理行为由该命令和相关服务决定，不属于 CommandTray 的数据收集。

### 保存与删除

数据会一直保留在本机，直到用户删除。卸载程序会移除应用文件，但可能保留 `%LOCALAPPDATA%\CommandTray` 中的配置和日志；如不再需要，可手动删除该目录。已经同步的文件和文档由自部署中转服务保留，删除本机数据后仍可在服务器备份中找到。

一般问题可通过 [GitHub Issues](https://github.com/backinfile/CommandTray/issues) 反馈。安全问题请按 [SECURITY.md](SECURITY.md) 私下报告。

---

## English

CommandTray is an open-source tool that runs locally. It contains no telemetry, behavioral analytics, advertising network, or automatic crash reporting, and it does not proactively send personal data to the maintainer.

### Local data

The application stores the following data for the current Windows user:

- `%LOCALAPPDATA%\CommandTray\config.json`: services and application settings;
- `%LOCALAPPDATA%\CommandTray\logs`: application and managed-command logs.

Environment variables marked as sensitive are stored using Windows DPAPI encryption. Standard output and error output from managed commands may still contain secrets, paths, or other sensitive information. Review and redact logs before sharing them.

### Network and local interfaces

- Windows MCP is enabled by default, listens on loopback, and uses a local credential. You can disable it in Settings. Linux supports local stdio or loopback, and Android provides an opt-in loopback listener.
- Pairing and synchronization transfer data to your configured relay. This includes workspaces, shared files, and clipboard text you send. SSH private keys are encrypted on the device with a vault passphrase before synchronization.
- The Open URL action passes a user-configured URL to the system's default browser.
- Commands configured and launched by the user may access the network themselves. Their data handling is determined by those commands and related services, not by CommandTray data collection.

### Retention and deletion

Data remains on the local device until the user deletes it. Uninstalling removes application files but may leave configuration and logs under `%LOCALAPPDATA%\CommandTray`; delete that directory manually if the data is no longer needed. Synced files and documents are retained by your relay and its backups.

Use [GitHub Issues](https://github.com/backinfile/CommandTray/issues) for general questions. Report security issues privately as described in [SECURITY.md](SECURITY.md).
