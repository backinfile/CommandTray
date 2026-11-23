# CommandTray for Android

This builds the shared Avalonia interface with an Android activity and Android device services. Android 10 or newer is required. The app includes 64-bit ARM and x86 runtimes.

Install the .NET 10 Android workload, Android SDK platform 36, SDK Build Tools 35.0.0 and JDK 21. Set ANDROID_HOME and JAVA_HOME or pass the SDK directories to MSBuild.

    dotnet build src/CommandTray.Android/CommandTray.Android.csproj -c Debug

The signed debug APK appears under src/CommandTray.Android/bin/Debug/net10.0-android36.0. Debug builds reuse the standard ~/.android/debug.keystore if available. Set MSBuild property CommandTrayDebugKeyStore to the previous build's debug signing store when it differs.

The app ID remains dev.commandtray.android. An upgrade signed by the same key reads existing workspace files, device settings, and credentials from the original Android Keystore alias. The application version code should be set higher than any previously deployed release when packaging an upgrade.

For a release package, provide COMMANDTRAY_ANDROID_KEYSTORE, COMMANDTRAY_ANDROID_STORE_PASSWORD, COMMANDTRAY_ANDROID_KEY_ALIAS and COMMANDTRAY_ANDROID_KEY_PASSWORD in the build environment, and build with -c Release. Set ApplicationVersion and ApplicationDisplayVersion to override packaging versions.

Local MCP listens on http://127.0.0.1:58629/mcp after you enable it in settings. Clipboard operations require a focused CommandTray activity. Files picked through the Android document picker are streamed through app-private staging. The MCP file tools take or return inline Base64, with a 4 MiB limit.

Workspaces synchronize when the app is resumed. Local edits stay on the device when the relay is unreachable, and the sync settings show conflicts until resolved.
