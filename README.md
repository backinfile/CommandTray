# CommandTray

[中文](#中文说明) · [English](#english-guide) · [下载 / Download](https://github.com/backinfile/CommandTray/releases/latest)

[![Release](https://github.com/backinfile/CommandTray/actions/workflows/release.yml/badge.svg)](https://github.com/backinfile/CommandTray/actions/workflows/release.yml)

CommandTray 是一个适用于 Windows 10/11 x64 的轻量系统托盘工具，用来启动、停止和监控需要长期运行的命令行服务。

CommandTray is a lightweight Windows 10/11 x64 system-tray utility for starting, stopping, and monitoring long-running command-line services.

---

## 中文说明

### 功能亮点

- 在系统托盘中集中管理多个 CMD 命令或本地开发服务。
- 一键启动、停止、重启、打开服务网页和查看日志。
- 支持工作目录、环境变量、自定义停止命令与停止超时。
- 可按服务设置“随 CommandTray 启动”和“异常退出后自动重启”。
- 只跟踪并终止由 CommandTray 启动的进程树，不会按进程名称误杀其他程序。
- 自动记录标准输出和错误输出，并滚动保存日志。
- 使用当前 Windows 用户的 DPAPI 加密标记为敏感的环境变量。
- 内置本地 MCP 服务，让受信任的 AI 客户端管理服务。

### 下载与安装

前往 [Releases](https://github.com/backinfile/CommandTray/releases/latest) 下载最新版：

- `CommandTray-X.Y.Z-win-x64-setup.exe`：推荐，使用安装向导安装。
- `CommandTray-X.Y.Z-win-x64.zip`：便携版，解压后直接运行 `CommandTray.exe`。
- `SHA256SUMS.txt`：用于校验下载文件完整性。

程序启动后会驻留在系统托盘。关闭设置窗口只会隐藏窗口；如需完全退出，请右键托盘图标并选择“退出”。

### 快速开始

1. 启动 CommandTray，双击托盘图标打开设置。
2. 点击左侧 **“+ Add / 添加”** 新建服务。
3. 填写名称和启动命令，按需设置其他字段。
4. 点击 **“Save / 保存”**。
5. 点击顶部 **“Start / 启动”**，或从托盘菜单启动服务。

| 字段 | 说明 |
| --- | --- |
| Name / 名称 | 显示在服务列表和托盘菜单中的名称。 |
| CMD command / 命令 | 启动服务时执行的 CMD 命令。 |
| Working directory / 目录 | 执行启动和停止命令时使用的目录；留空则使用程序当前目录。 |
| Open URL / 网页 | 可选的 HTTP/HTTPS 地址，可从托盘菜单快速打开。 |
| Stop command / 停止命令 | 停止进程前执行的可选命令；`${PID}` 会替换为进程 ID。 |
| Stop timeout / 超时秒 | 等待服务正常停止的时间，超时后终止所属进程树。 |
| Start with manager / 随管理器启动 | CommandTray 启动时自动启动此服务。 |
| Restart on unexpected exit / 异常退出重启 | 服务意外退出时自动重启，手动停止不会触发。 |
| Environment / 环境变量 | 每行填写一个 `NAME=value`；敏感变量名前加 `*`。 |

### AI / MCP 接入

MCP 默认开启，可在设置窗口左侧取消勾选 **“Enable MCP / 启用 MCP”**。Streamable HTTP 地址为：

```text
http://127.0.0.1:58629/mcp
```

常见 MCP 客户端可按类似方式配置；具体字段名请以客户端文档为准：

```json
{
  "mcpServers": {
    "commandtray": {
      "url": "http://127.0.0.1:58629/mcp"
    }
  }
}
```

MCP 仅监听本机 `127.0.0.1`，可查询、创建、修改、删除、启动、停止和重启服务，也能读取日志。敏感环境变量不会返回明文，但服务日志可能包含命令自身输出的敏感信息。AI 可以通过这些工具执行你配置的命令，请只连接可信的 MCP 客户端。

### 数据与日志

- 配置：`%LOCALAPPDATA%\CommandTray\config.json`
- 日志：`%LOCALAPPDATA%\CommandTray\logs\<服务ID>\current.log`
- 单个日志文件达到 5 MB 后自动轮转，并保留最近 4 个历史文件。

### 常见问题

**关闭窗口后程序为什么还在运行？**

CommandTray 是托盘程序。关闭设置窗口只会隐藏它，请从托盘菜单选择“退出”以完全关闭。

**为什么服务停止后又启动了？**

检查是否启用了“异常退出重启”。手动点击停止不会触发重启；命令自行退出时会触发有限次数的自动重启。

**如何保护 API Key 等敏感值？**

在环境变量名称前加 `*`，例如 `*API_KEY=value`。保存后该值会使用当前 Windows 用户的 DPAPI 加密。

---

## English guide

### Highlights

- Manage multiple CMD commands or local development services from one tray menu.
- Start, stop, restart, open service URLs, and inspect logs.
- Configure working directories, environment variables, stop commands, and stop timeouts.
- Start selected services with CommandTray and restart them after unexpected exits.
- Track only process trees launched by CommandTray; unrelated processes are never killed by name.
- Capture stdout and stderr in rotating log files.
- Encrypt secret environment values with Windows DPAPI for the current user.
- Let trusted AI clients manage services through a built-in local MCP server.

### Download and install

Download the latest version from [Releases](https://github.com/backinfile/CommandTray/releases/latest):

- `CommandTray-X.Y.Z-win-x64-setup.exe`: recommended installer.
- `CommandTray-X.Y.Z-win-x64.zip`: portable build; extract it and run `CommandTray.exe`.
- `SHA256SUMS.txt`: checksums for verifying downloads.

CommandTray stays in the notification area after launch. Closing the settings window only hides it; use **Exit** from the tray menu to quit completely.

### Quick start

1. Start CommandTray and double-click its tray icon.
2. Select **“+ Add / 添加”**.
3. Enter a name and CMD command, then configure any optional fields.
4. Select **“Save / 保存”**.
5. Select **“Start / 启动”**, or start the service from the tray menu.

| Field | Description |
| --- | --- |
| Name | Name shown in the service list and tray menu. |
| CMD command | CMD command used to start the service. |
| Working directory | Directory used for start and stop commands; blank uses CommandTray's current directory. |
| Open URL | Optional HTTP/HTTPS address available from the tray menu. |
| Stop command | Optional command run before termination; `${PID}` is replaced with the process ID. |
| Stop timeout | Graceful-stop wait time before CommandTray terminates the owned process tree. |
| Start with manager | Starts this service when CommandTray starts. |
| Restart on unexpected exit | Restarts unexpected exits; manual stops do not trigger it. |
| Environment | One `NAME=value` entry per line; prefix secret names with `*`. |

### AI / MCP integration

MCP is enabled by default. Clear **“Enable MCP / 启用 MCP”** in the settings window to turn it off. The Streamable HTTP endpoint is:

```text
http://127.0.0.1:58629/mcp
```

A typical MCP client configuration looks like this; consult your client's documentation for its exact schema:

```json
{
  "mcpServers": {
    "commandtray": {
      "url": "http://127.0.0.1:58629/mcp"
    }
  }
}
```

The server binds only to local address `127.0.0.1`. It can inspect, create, update, delete, start, stop, and restart services, and read logs. Secret environment values are never returned, although logs may contain sensitive output written by managed commands. Connect only trusted MCP clients because these tools can execute your configured commands.

### Data and logs

- Configuration: `%LOCALAPPDATA%\CommandTray\config.json`
- Logs: `%LOCALAPPDATA%\CommandTray\logs\<service-id>\current.log`
- A log rotates at 5 MB and retains the four newest historical files.

### Troubleshooting

**Why is CommandTray still running after I close the window?**

It is a tray application. Closing settings hides the window; choose **Exit** from the tray menu to quit.

**Why did a service start again after it stopped?**

Check **Restart on unexpected exit**. Manual stops do not restart; commands that exit on their own use bounded automatic retries.

**How do I protect an API key or token?**

Prefix its environment variable name with `*`, for example `*API_KEY=value`. CommandTray encrypts it with DPAPI for the current Windows user when saving.

---

## Development / 开发

Requires Windows 10/11 and the .NET 10 SDK.

```powershell
dotnet test .\CommandTray.slnx
dotnet run --project .\src\CommandTray\CommandTray.csproj
```

Repository contribution and release rules are documented in [`AGENTS.md`](AGENTS.md).
