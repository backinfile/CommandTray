# CommandTray Android

Android 客户端加载与 Windows 和 Linux 桌面相同的 Avalonia UI。支持 Android 10 及以上，可以编辑便签和待办、查看画板及 SSH 主机、配对、收发共享文件和剪贴板。

- 使用 HTTPS 连接中转服务器。开发时允许 Android 设备自己的回环 HTTP。工作区在断网时保留本地修改，重连后继续同步。
- 文件选取和保存通过 Android 系统提供程序。应用在前台时可读取和写入系统剪贴板。
- 继续使用原应用 ID `dev.commandtray.android`。同签名覆盖安装会读取原应用的离线工作区、配对状态和 Android Keystore 凭据。

## 构建

安装 .NET 10、Android 工作负载、JDK 21、Android SDK 平台 36 和 Build Tools 35.0.0。从仓库根目录运行下面命令。

```powershell
dotnet workload install android
dotnet build src/CommandTray.Android/CommandTray.Android.csproj -c Debug
./scripts/package-suite.ps1 -Version 0.3.1 -Target android -AndroidVariant Debug
```

签名 APK 位于 `artifacts/suite/0.3.1`。需要覆盖安装旧调试 APK 时，使用原来的 `debug.keystore`，也可在构建时指定 `CommandTrayDebugKeyStore`。正式包使用 `COMMANDTRAY_ANDROID_KEYSTORE`、`COMMANDTRAY_ANDROID_STORE_PASSWORD`、`COMMANDTRAY_ANDROID_KEY_ALIAS` 和 `COMMANDTRAY_ANDROID_KEY_PASSWORD`。将打包脚本的变体改为 `Release`。

## 本地 MCP

在设置中开启本地 MCP 并复制连接配置。手机的 `127.0.0.1:58629/mcp` 接受带本机 Bearer 凭据的 Streamable HTTP 请求。系统回收应用进程时，MCP 随应用结束。

工具覆盖空间、便签、待办、画板资料、剪贴板、配对、共享文件和设置。文件操作支持最多 4 MiB 的 Base64 内容，较大的文件使用应用中的系统文件选择器。
