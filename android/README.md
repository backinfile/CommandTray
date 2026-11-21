# CommandTray Android

原生 Android 客户端，支持 Android 10 及以上。提供空间、文件、剪贴板和设置四个入口。

- 在设置中填写中转服务器、设备名称和配对码。使用 HTTPS 连接服务器，开发时可用本机回环 HTTP。
- 空间中的便签和待办可离线编辑。配对后使用「拉取」和「同步」。遇到版本冲突时，选择要保留的版本。画板和 SSH 主机资料可以查看。
- 共享文件通过系统文件选择器添加和下载。剪贴板可在 CommandTray 获得窗口焦点时发送或者接收。
- 配对凭据和本地 MCP 令牌由 Android Keystore 保护。更换服务器后，原服务器的离线空间仍保存在本机。

## 构建

需要带 jlink 的 JDK 17 或 21、Android SDK 平台 36 和 Build Tools 35.0.0。将 ANDROID_HOME 设为 SDK 路径。

在 Windows 上打开 Android 项目目录并运行以下命令。

    .\gradlew.bat :app:assembleDebug

Linux 和 macOS 用同一个 wrapper。

    sh ./gradlew :app:assembleDebug

APK 位于 app/build/outputs/apk/debug/app-debug.apk。

正式签名使用 COMMANDTRAY_ANDROID_KEYSTORE、COMMANDTRAY_ANDROID_STORE_PASSWORD、COMMANDTRAY_ANDROID_KEY_ALIAS 和 COMMANDTRAY_ANDROID_KEY_PASSWORD 四个构建环境变量。运行 :app:assembleRelease 导出发布包。设置 Gradle 参数 commandTrayVersionName 和 commandTrayVersionCode 可指定版本。

## 本地 MCP

在设置中开启本地 MCP 并复制连接配置。MCP 监听手机的 127.0.0.1:58629，接受带有效 Bearer 令牌的 Streamable HTTP 请求。手机端支持工具枚举和调用。操作系统回收应用进程时，本地 MCP 随进程结束。

工具覆盖空间和组件、便签与待办、剪贴板、配对、文件和设置。workspace_read 会返回供读取的画板和 SSH 主机资料。file_upload 和 file_download 接受或返回最多 4 MiB 的 Base64 内容。较大的文件可通过系统文件选择器传输。

可运行 tools/Test-Apk.ps1 检查签名、应用 ID、最低 SDK、启动页和网络权限。
