# CommandTray 跨平台 UI 一次性交付计划

## 架构决定

采用 **Avalonia 12 + .NET 10 + C# / XAML**。一份 `CommandTray.Ui` 定义页面、组件、视图模型、主题和响应式布局，Windows、Linux 桌面和 Android 分别通过各自的程序入口加载它。宽屏展示并排内容，窄屏在同一页面中改用紧凑列表和详情。现有 C# 进程监管、日志、中转协议及加密代码可继续复用。托盘、终端、系统剪贴板、录屏和密钥存储通过平台适配接入。[Avalonia 跨平台项目结构](https://docs.avaloniaui.net/docs/app-development/cross-platform-solution-setup) · [Avalonia 支持的平台](https://docs.avaloniaui.net/docs/supported-platforms)

本计划承接原《CommandTray 一次性交付计划》和本地 MCP 补充。原计划选用 Windows WPF 和独立 Android 界面，当前功能分支实际采用 Windows WinForms 与 Android Java，三端已有可构建的功能。新的交付以 `codex/multiplatform-local-suite` 的 `491afc8` 为功能基线，将页面收敛到共享项目，并补齐原计划的离线增量同步。当前的中转协议以工作区快照和手动推拉为主。[现有中转协议](../protocol/relay-v1.md)

## 最终效果

| 交付物 | 使用效果 |
| --- | --- |
| **Windows 本地应用，主产品** | 从托盘或桌宠进入同一个应用。命令管理、文件与终端、共享文件、剪贴板、捕获和设置都在应用内。空间用应用页签切换，同一页可平铺多个目录、CMD、PowerShell 或 SSH 会话。组件可拖动、缩放、保存位置。托盘和桌宠共用菜单，点组件通知可以回到对应空间和组件。 |
| **Linux CLI 和可选桌面入口** | CLI 支持无桌面运行，实现命令监管、日志、PTY 终端、SSH、设置、同步、文件和剪贴板操作。可选桌面入口加载与 Windows 相同的页面，对接同一个用户级守护进程。CLI 退出后已托管服务继续运行。 |
| **Android APK** | 加载同一份空间、共享文件、剪贴板和设置页面。打开空间即见便签、待办、画板和 SSH 主机资料。可编辑便签与待办、配对、上传下载及收发剪贴板。文件选取和剪贴板访问遵循 Android 的系统权限。桌面终端和自由布局保留在桌面入口。 |
| **自部署中转** | Docker Compose 提供设备配对、增量同步、共享文件、备份与恢复。服务器保留数据传输和管理员命令，普通设置统一在本地应用或 CLI 内完成。 |

空间首屏直接显示用户内容。桌面将空间切换、添加和同步放在窄工具栏，主区留给多栏目录和并排终端。手机工具栏显示当前空间，下面直接排列便签、待办和画板。共享文件直接展示列表，剪贴板直接展示正文，设备状态随设置展示。明暗主题、中文和英文、空态、错误提示与控件样式都由共享项目维护。

## 代码结构与迁移接口

```text
src/
  CommandTray.Ui/          共享 Avalonia 页面、控件、视图模型、样式
  CommandTray.Core/        共用客户端接口、数据访问和 MCP 工具注册
  CommandTray.Sync/        本地同步队列、协议客户端、版本冲突、加密
  CommandTray/             Windows 入口、托盘、ConPTY、命令、捕获和桌宠
  CommandTray.Linux/       CLI、用户级守护进程、PTY 和业务操作
  CommandTray.Linux.Desktop/  Linux 桌面入口，连接本机守护进程
  CommandTray.Android/     Android 入口、文件选择、剪贴板、Keystore
  CommandTray.Relay/       设备、变更记录、文件存储、备份恢复
protocol/                 版本契约与旧数据样本
deploy/relay/              Compose 与升级说明
packaging/                安装包、CLI、容器、APK
tests/                    共享行为、升级和定向验收
```

- 界面、CLI 和 MCP 调用同一业务模块。Windows 主进程统一负责托盘、服务和工作区。Linux 由用户级守护进程统一监管后台进程，CLI 与桌面入口通过本机 Unix socket 接入。Android 使用同一业务模型和操作接口，密钥、权限与应用生命周期由 Android 入口处理。
- 从现有 WinForms 工程抽取命令、日志、配置、工作区、ConPTY 和通知逻辑，把文件页、终端页和设置页移入共享 UI。终端控件共用输入、输出、滚动和尺寸变化的接口，Windows 接 ConPTY，Linux 接 PTY。Windows 保留由托盘启动、关闭窗口后留驻、开机自启、截图、录屏、贴图、剪贴板历史和桌宠。录屏与 OCR 的运行依赖纳入打包说明和安装验证。[现有 Windows 功能与依赖](../README.md) · [Avalonia 系统托盘](https://docs.avaloniaui.net/controls/navigation/trayicon/)
- 保留 Android 应用 ID `dev.commandtray.android`。读取旧应用的本地空间、设备配置及 Keystore 凭据，同签名覆盖安装时验证数据迁移。调试包用现有调试签名做升级验收，正式包使用正式发布证书。[Android 应用签名](https://developer.android.com/studio/publish/app-signing)

## 同步与本地 MCP

- 用稳定的空间、组件和条目 ID 同步空间顺序、组件位置尺寸、便签、待办、画板、命令配置、终端配置和允许同步的历史、SSH 主机资料以及共享文件。终端进程、运行状态和绝对目录绑定由各设备管理。其他设备读取布局后按本机目录映射恢复，缺少能力的客户端在保存时继续保留对应组件的数据。
- 在现有配对、文件校验和快照协议上补持久化离线队列。重连按版本增量拉取，用操作 ID 去重并补传本地修改。不同条目的变更自动合并，同一条目的冲突保留两版。中转备份包含版本记录和共享文件，可在另一实例恢复。SSH 私钥通过现有加密封装单独授权同步，解锁在客户端完成，MCP 使用密钥 ID。
- 各客户端用同一份功能清单注册当前平台可用的 MCP 工具，覆盖命令、日志、文件、终端、组件摆放、便签、待办、画板、捕获、桌宠与通知、SSH、同步和设置。工具与界面操作修改同一份数据。系统文件选择、录屏权限或交互选区需要用户参与时，工具返回待完成状态。Windows 和 Android 在设置内开启仅回环监听的 Streamable HTTP，Linux 提供 `stdio` 和本地 Streamable HTTP，中转保留部署主机上的管理员 `stdio`。HTTP 校验来源和本机凭据。[MCP 传输规范](https://modelcontextprotocol.io/specification/latest/basic/transports)

## 实现顺序与交付验收

1. **打通三端构建。** 在从 `dev` 派生的功能工作树接入现有多平台实现，原工作区的托盘修改单独保全。固定 Avalonia、.NET SDK 与 Android 工作负载。先让同一空间页面和真实本地状态在 Windows、Linux 桌面及 Android 启动，确认改一份 XAML 就能更新三端。
2. **迁移共享页面和平台能力。** 完成命令、文件与终端、空间、共享文件、剪贴板和设置，接入 Windows 托盘、桌宠、捕获及 Linux 守护进程。接入旧配置和缓存后，用旧数据覆盖安装验证。
3. **完成同步和 MCP。** 交付离线队列、重连补传、冲突、文件与备份恢复。以共享接口贯通界面、CLI 和 MCP。
4. **只做必要验收。** 用 Windows 打开真实双目录和双终端并保存布局，从托盘或桌宠跳回空间。Linux 在无桌面环境通过 CLI 上传文件，同时验证可选桌面入口能打开共享页面。Android 覆盖安装后下载同一文件并修改待办，断网重连后在 Windows 核对更新，冲突时保留双版本。验证一次中转备份恢复与各入口的本地 MCP 调用。UI 留存 Windows 和 Android 实际截图，核对首屏以内容为主。
5. **一次发布。** 验收通过后合入 `dev`，按仓库规则从 `dev` 建立发布分支和标签。统一生成 Windows 安装包和便携包、Linux CLI 与桌面归档、中转镜像与 Compose、正式签名 APK 及校验清单。[仓库发布规则](../AGENTS.md)

实施时先确定数据结构、平台接口和 MCP 工具清单。共享 UI 由一组负责，Windows/Linux 平台能力、Android 入口、同步与中转分别在独立目录并行，最后统一集成、打包和验收。各阶段是同一次交付的内部顺序。

现有 Android APK 承诺 Android 10（API 29）。Avalonia 将 Android 11 及更早版本列为 Tier 3，因此第一步就要在 API 29 验证安装、启动和覆盖升级，并在用户提供的 Android 14 环境运行定向验收。若 API 29 无法达标，先确认 Android 支持范围再发布。[现有 APK 支持范围](../android/README.md) · [Avalonia Android 支持表](https://docs.avaloniaui.net/docs/supported-platforms#android)
