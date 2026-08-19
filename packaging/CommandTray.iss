#define AppName "CommandTray"
#define AppVersion "0.1.0"
#define PublishDir "..\artifacts\publish\win-x64"

[Setup]
AppId={{9A055269-5938-46CF-AF27-F667280439E7}
AppName={#AppName}
AppVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
PrivilegesRequired=lowest
OutputDir=..\artifacts\installer
OutputBaseFilename=CommandTray-{#AppVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\CommandTray.exe

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\CommandTray"; Filename: "{app}\CommandTray.exe"
Name: "{userdesktop}\CommandTray"; Filename: "{app}\CommandTray.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Run]
Filename: "{app}\CommandTray.exe"; Description: "Launch CommandTray"; Flags: nowait postinstall skipifsilent
