## CommandTray Suite 1.0.0

- Windows、Linux 桌面和 Android 客户端自动检查并下载套件更新。Windows 和 Linux 桌面在退出时应用已下载的更新，也可在设置中重启并更新。Android 通过系统安装器完成更新。
- 更新优先访问 GitHub，失败时回退到 `ghfast.top` 和 `gh-proxy.com`。客户端验证发布者签名、适用平台、文件大小和 SHA-256。
- Windows 使用独立的安装标识、数据目录和 MCP 端口，可与原版 CommandTray 并用。
- 深色工作台新增直线、箭头、矩形、椭圆和橡皮。剪贴板记录发送设备和时间，并支持在本机打开双向自动同步。
- 中转可通过 `COMMANDTRAY_RELAY_WEB=true` 启用 Web 工作台。

请手动安装一次 1.0.0，以获得自动更新功能。Windows 套件的默认数据目录是 `%LOCALAPPDATA%\CommandTraySuite`。从本地预览版迁移时，请先退出预览版，并在同一个 Windows 账户下复制该预览版的数据到新目录。原版安装和数据仍使用原来的目录。
