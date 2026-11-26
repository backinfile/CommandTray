# CommandTray Suite 实用功能调研

以 Suite 1.0.5 为基线。盘点了命令管理、共享文件、剪贴板、截图与录屏、目录、终端、待办和 SSH 的现有入口。画板有独立改动。

## 本轮补充

| 功能 | 当前版本缺的步骤 | 改动 |
| --- | --- | --- |
| 剪贴板 | 回发已有文本需要复制、重新打开输入框再粘贴 | 从记录直接打开预填的发送表单，确认后创建新记录。也可以把记录带入文本工具。[CopyQ 编辑流程](https://copyq.readthedocs.io/en/latest/basic-usage.html) |
| 共享文件 | 文件已有 SHA-256，接收方只能在下载校验流程中间接使用 | 在文件菜单复制文件名和 SHA-256。旧记录没有有效校验值时仅显示复制名称。[PowerShell Get-FileHash](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.utility/get-filehash?view=powershell-7.5) |
| 截图与录屏 | 分享捕获文件需要另存、切换页面再选文件 | 捕获历史可选择共享目录并上传。沿用现有队列的进度、取消、重试和文件大小上限。[ShareX 捕获后操作](https://getsharex.com/docs/region-capture.html) |
| 实用工具 | JSON、编码、重复行和文本校验需要切到其他工具 | 文本转换提供 JSON、Base64、URL 组件编解码、按行去重和文本 SHA-256。[PowerToys Advanced Paste](https://learn.microsoft.com/en-us/windows/powertoys/advanced-paste) · [DevToys](https://devtoys.app/) |

文本工具、剪贴板编辑和文件校验值入口在共享原生 UI 与 Web 上实现。捕获上传入口随 Windows 截图与录屏工具提供。本轮沿用现有中转协议。

## 实用工具补充

| 工具 | 操作 |
| --- | --- |
| 本地文件校验 | 选择任意本地文件流式计算 SHA-256，可取消。共享文件「校验本地文件」预填预期值，选择文件后显示是否一致。[PowerShell Get-FileHash](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.utility/get-filehash?view=powershell-7.5) |
| 文本对比 | 按行显示新增、删除、不变和末尾换行变化，可复制差异。每段最多 2,000 行、100 万字符。 |
| 时间戳 | 明确选择 Unix 秒、Unix 毫秒或带偏移的 ISO 8601，同时输出 UTC、本地时间和两种 Unix 单位。[DevToys](https://devtoys.app/) |
| 二维码 | 可把最多 2,000 个 UTF-8 字节的文本或链接生成二维码，预览并保存 PNG。 |

工具在本机或浏览器上运行。Web 打包了 [Nayuki QR Code generator v1.8.0](https://github.com/nayuki/QR-Code-generator/tree/720f62bddb7226106071d4728c292cb1df519ceb) 和 [@noble/hashes 2.4.0](https://github.com/paulmillr/noble-hashes)，许可证随脚本分发。原生二维码由 [QRCoder 1.9.0](https://www.nuget.org/packages/QRCoder/1.9.0) 生成。

## 二维码识别与其他补充

| 功能 | 操作 |
| --- | --- |
| 二维码识别 | 「实用工具 → 二维码」可打开图片并识别一个二维码。Windows 截图历史可直接识别。支持摄像头且已授权的 Web 浏览器可从摄像头扫描，识别成功或关闭时停止摄像头。识别结果可复制或用于生成，链接和命令按文本显示。[ShareX 二维码工具](https://getsharex.com/docs/qr-code) |
| 剪贴板 | 可从一条短文本直接进入二维码生成并填入内容。[DevToys 工具间复用](https://devtoys.app/doc/articles/extension-development/guidelines/UX/support-smart-detection.html) |
| 截图 OCR | 识别到的文字可直接进入文本转换。[ShareX OCR](https://getsharex.com/docs/ocr) |
| 文本工具 | 新增去除每行首尾空格、删除空行及生成 UUID v4。可将结果继续用作输入。[DevToys 文本工具与生成器](https://devtoys.app/) |

识别在设备上完成。图片最多 32 MiB 和 2,400 万像素。Web 使用 [jsQR 1.4.0](https://github.com/cozmo/jsQR)，许可证随脚本分发。原生端使用 [ZXing.Net 0.16.11](https://www.nuget.org/packages/ZXing.Net/0.16.11)。

## 下一批候选

| 候选 | 具体用途 | 实现条件 |
| --- | --- | --- |
| 一图多码 | 扫描含多个二维码的合照，逐项复制结果 | 需要支持多码定位的解码器以及多结果选择界面。[ShareX 二维码工具](https://getsharex.com/docs/qr-code) |
| 可搜索的文本片段 | 保存常用命令、模板和回复，按名称搜到后一键复制 | 先从应用内复制做起。全局自动展开涉及各系统输入权限。[Espanso](https://espanso.org/docs/matches/basics/) |
| 待办到期日与优先级 | 在当前待办卡片筛出今天、逾期或高优先级的项目 | 为任务数据补充可选字段，兼容已有记录。提醒涉及移动后台执行，可独立实现。[Todoist 优先级](https://www.todoist.com/help/articles/set-a-priority-in-todoist-Wy82Jp) |
| 批量重命名 | 在目录组件中批量预览修改，标出冲突并提供撤销 | 需要校验重名、路径权限和中途失败。先在具有本地文件能力的客户端提供。[PowerRename](https://learn.microsoft.com/en-us/windows/powertoys/powerrename) |
| 屏幕取色 | 从本机窗口取色并复制 HEX、RGB 值 | 优先复用 Windows 选区取样，Web 可补图片取色。[PowerToys Color Picker](https://learn.microsoft.com/en-us/windows/powertoys/color-picker) |
| 文件快速预览 | 收到图片或短文本时，在共享文件页先核对内容 | 复用下载校验与预览能力，限制可内嵌的文件类型和大小。[PowerToys Peek](https://learn.microsoft.com/en-us/windows/powertoys/peek) |
| 局域网直传 | 无中转或断网时在同一网络传文件 | 需要设备发现、显式接收、取消和可信设备管理。属于独立的传输模式。[LocalSend](https://github.com/localsend/localsend) |

命令管理已具备启停、重启和日志过滤。下一步可评估在详情里显示用户已配置服务地址的健康结果和最近一次退出原因。目录与终端已有就地联动，SSH 已有密钥和连接入口，常用命令可以和文本片段共用入口。

## 导航与桌宠

| 对照项目 | 采用的做法 |
| --- | --- |
| [Fluent NavigationView](https://learn.microsoft.com/en-us/windows/apps/design/controls/navigationview) | 侧栏分组标题保持展开，常用工具按领域直接进入。总览收录当前客户端的工具和可添加的工作区组件。 |
| [eSheep](https://github.com/Adrianotiger/desktopPet/blob/master/Readme.md) | 桌宠限制在当前显示器的工作区内。拖过显示器时跟随指针，松开后留在该处。 |
| [Desktop Pet Studio](https://github.com/PhantatP/desktop-pet-studio) | 待机、散步、休息与点击反馈使用独立姿态。右键提供自动走动、睡觉或叫醒、打招呼、调整大小和隐藏。 |
