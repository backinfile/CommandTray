# 隐私说明 / Privacy notice

## 中文

CommandTray 是本地运行的开源工具，不包含遥测、行为分析、广告网络或自动崩溃上报，也不会主动向维护者发送个人数据。

### 本地数据

程序会在当前 Windows 用户目录保存以下内容：

- `%LOCALAPPDATA%\CommandTray\config.json`：服务与程序设置；
- `%LOCALAPPDATA%\CommandTray\logs`：程序和托管命令的日志。

标记为敏感的环境变量使用 Windows DPAPI 加密后保存。托管命令的标准输出和错误输出仍可能包含密钥、路径或其他敏感信息，请在共享日志前检查并脱敏。

### 网络与本地接口

- MCP 服务默认开启，仅监听 `127.0.0.1:58629`。受信任的本地客户端可通过它查看或修改服务、启动或停止命令并读取日志；可在设置中关闭 MCP。
- “打开地址”功能会把用户配置的 URL 交给系统默认浏览器。
- 用户配置并由 CommandTray 启动的命令可能自行访问网络，其数据处理行为由该命令和相关服务决定，不属于 CommandTray 的数据收集。

### 保存与删除

数据会一直保留在本机，直到用户删除。卸载程序会移除应用文件，但可能保留 `%LOCALAPPDATA%\CommandTray` 中的配置和日志；如不再需要，可手动删除该目录。

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

- The MCP server is enabled by default and listens only on `127.0.0.1:58629`. Trusted local clients can use it to inspect or modify services, start or stop commands, and read logs. MCP can be disabled in Settings.
- The Open URL action passes a user-configured URL to the system's default browser.
- Commands configured and launched by the user may access the network themselves. Their data handling is determined by those commands and related services, not by CommandTray data collection.

### Retention and deletion

Data remains on the local device until the user deletes it. Uninstalling removes application files but may leave configuration and logs under `%LOCALAPPDATA%\CommandTray`; delete that directory manually if the data is no longer needed.

Use [GitHub Issues](https://github.com/backinfile/CommandTray/issues) for general questions. Report security issues privately as described in [SECURITY.md](SECURITY.md).
