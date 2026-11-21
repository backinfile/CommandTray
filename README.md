# CommandTray

[English](README.en.md) · [下载](https://github.com/backinfile/CommandTray/releases/latest)

[![Release](https://github.com/backinfile/CommandTray/actions/workflows/release.yml/badge.svg)](https://github.com/backinfile/CommandTray/actions/workflows/release.yml)

CommandTray 在 Windows 用本机窗口和系统托盘管理命令、多个空间、文件与终端。Linux CLI 可以在没有桌面的机器上管理命令和同步数据。Android 应用用于剪贴板、共享文件、待办、便签和设备配对。

| 入口 | 使用方式 |
| --- | --- |
| Windows | 从托盘打开命令管理、工作区、共享文件、捕获、剪贴板、SSH 和本地设置。空间中可新建目录栏、终端和组件，再拖动、调整大小。 |
| Linux | 解压后运行 `commandtray --help`。后台进程、终端、同步和 MCP 说明见 [Linux CLI](packaging/linux/README.md)。 |
| Android | 安装 APK 后在本地设置配对。构建与签名见 [Android](android/README.md)。 |
| 自部署中转 | 使用镜像和 Compose 配置。配对后的设备可共享文件、文本剪贴板和工作区。见 [部署说明](deploy/relay/README.md)。 |

## 功能亮点

- 在系统托盘中集中管理多个 CMD 命令或本地开发服务。
- 一键启动、停止、重启、打开服务网页和查看日志。
- 服务配置按“基本配置 / 启动与停止 / 环境变量”分组，支持搜索、字段校验和未保存提醒。
- 完整中文和英文界面，可在应用设置中即时切换，或跟随系统语言。
- 日志支持搜索、复制、自动换行和暂停跟随输出。
- 支持工作目录、环境变量、自定义停止命令与停止超时。
- 可按服务设置“随 CommandTray 启动”和“异常退出后自动重启”。
- 只跟踪并终止由 CommandTray 启动的进程树，不会按进程名称误杀其他程序。
- 自动记录标准输出和错误输出，并滚动保存日志。
- 使用当前 Windows 用户的 DPAPI 加密标记为敏感的环境变量。
- 内置本地 MCP 服务，让受信任的 AI 客户端管理服务。

Windows 截图和贴图可直接使用。录屏使用 PATH 上的 ffmpeg，OCR 使用 PATH 上的 tesseract 和相应语言数据。设备配对后，可在工作区推送和拉取空间；同步冲突时保留本地数据。

## 下载与安装

前往 [Releases](https://github.com/backinfile/CommandTray/releases/latest) 下载最新版：

- `CommandTray-X.Y.Z-win-x64-setup.exe`：推荐，使用安装向导安装。
- `CommandTray-X.Y.Z-win-x64.zip`：便携版，解压后直接运行 `CommandTray.exe`。
- `SHA256SUMS.txt`：用于校验下载文件完整性。

当前发布包尚未进行 Authenticode 签名，首次运行时 Windows 可能显示 SmartScreen 提示。请从官方 Releases 下载并核对随附的 SHA-256 校验值；签名进度见[代码签名政策](CODE_SIGNING_POLICY.md)。

程序启动后会驻留在系统托盘。关闭设置窗口只会隐藏窗口；如需完全退出，请右键托盘图标并选择“退出”。

## 快速开始

1. 启动 CommandTray，双击托盘图标打开设置。
2. 点击左侧 **“+ 添加服务”** 新建服务。
3. 填写名称和启动命令，按需设置其他字段。
4. 点击 **“保存修改”**，或按 **Ctrl+S**。
5. 点击顶部 **“启动”**，或从托盘菜单启动服务。

在 **“应用设置 → 显示语言”** 中选择 **跟随系统 / 简体中文 / English**，已打开的窗口和托盘菜单会立即更新。服务名称、命令和日志内容会保留原文。

常用快捷键：**Ctrl+N** 添加服务、**Ctrl+F** 搜索服务（日志窗口中搜索日志）、**Ctrl+L** 查看日志、**Ctrl+Tab / Ctrl+Shift+Tab** 前后切换配置页签。页签获得焦点时，也可以用左右方向键及 **Home / End** 切换。切换服务时会提示保存未完成的修改；关闭窗口只隐藏界面并保留编辑内容。

顶部主按钮会根据服务状态显示“启动”或“停止”；执行期间显示进度文字并防止重复操作。日志与网页入口保持可见，删除操作位于右侧并需要确认。

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

## AI / MCP 接入

MCP 默认开启。可在本地设置中启停服务、复制连接地址和本机 MCP 凭据。Windows 的 Streamable HTTP 默认地址为：

```text
http://127.0.0.1:58629/mcp
```

常见 MCP 客户端可按类似方式配置；具体字段名请以客户端文档为准：

```json
{
  "mcpServers": {
    "commandtray": {
      "url": "http://127.0.0.1:58629/mcp",
      "headers": {"Authorization": "Bearer <本机 MCP 凭据>"}
    }
  }
}
```

MCP 仅监听本机 `127.0.0.1`。通过 `tools/list` 查看命令、空间与组件、终端、文件、剪贴板、捕获、SSH、同步及本地设置可用的工具。敏感环境变量的值不会作为配置返回；命令自己输出的内容仍会出现在日志里。Linux 可使用 `commandtray mcp serve --stdio`，Android 可在应用设置中开启同设备连接。

## 数据与日志

- 配置：`%LOCALAPPDATA%\CommandTray\config.json`
- 日志：`%LOCALAPPDATA%\CommandTray\logs\<服务ID>\current.log`
- 单个日志文件达到 5 MB 后自动轮转，并保留最近 4 个历史文件。

在 **“应用设置 → 配置文件”** 中导入或导出 JSON。导入会替换当前服务列表，需先停止所有服务；导出仅包含已保存的配置，并移除敏感环境变量的值。

## 常见问题

**关闭窗口后程序为什么还在运行？**

CommandTray 是托盘程序。关闭设置窗口只会隐藏它，请从托盘菜单选择“退出”以完全关闭。

**为什么服务停止后又启动了？**

检查是否启用了“异常退出重启”。手动点击停止不会触发重启；命令自行退出时会触发有限次数的自动重启。

**如何保护 API Key 等敏感值？**

在环境变量名称前加 `*`，例如 `*API_KEY=value`。保存后该值会使用当前 Windows 用户的 DPAPI 加密。

## 开发

需要 Windows 10/11 和 .NET 10 SDK。

```powershell
dotnet test .\CommandTray.slnx
dotnet run --project .\src\CommandTray\CommandTray.csproj
```

贡献与发布规则见 [`AGENTS.md`](AGENTS.md)。

## 构建跨平台发布包

PowerShell 7 中运行下列命令。Windows 安装包需要 Inno Setup；Android Release 需要配置固定签名密钥；`-RelayImage` 使用本机 Docker 构建和导出镜像。

```powershell
.\scripts\package-suite.ps1 -Version 0.3.0 -RelayImage
```

用 `-Target windows`、`-Target linux`、`-Target relay` 或 `-Target android` 构建单个平台。没有发布签名时可用 `-Target android -AndroidVariant Debug` 构建可安装的调试 APK。产物和 SHA-256 校验文件位于 `artifacts/suite/<版本>/`。

## 许可证与项目政策

- [MIT 许可证](LICENSE)
- [代码签名政策](CODE_SIGNING_POLICY.md)
- [隐私说明](PRIVACY.md)
- [安全政策](SECURITY.md)
