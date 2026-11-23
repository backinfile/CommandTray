#define AppName "CommandTray Suite"
#ifndef AppVersion
#define AppVersion "0.1.0"
#endif
#define PublishDir "..\artifacts\publish\win-x64"

[Setup]
AppId={{31824658-E8C1-41FA-B6F9-0CF816286536}
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
CloseApplications=no

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\CommandTray"; Filename: "{app}\CommandTray.exe"
Name: "{userdesktop}\CommandTray"; Filename: "{app}\CommandTray.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Run]
Filename: "{app}\CommandTray.exe"; Description: "Launch CommandTray"; Flags: nowait postinstall skipifsilent

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  OldExecutable: String;
  ResultCode: Integer;
  Attempt: Integer;
begin
  Result := '';
  OldExecutable := ExpandConstant('{app}\CommandTray.exe');
  if not FileExists(OldExecutable) then
    Exit;

  { Wait for the Suite instance and its owned jobs before replacing files. }
  Exec(OldExecutable, '--shutdown', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  for Attempt := 1 to 50 do
  begin
    if not CheckForMutexes('Local\CommandTraySuite.SingleInstance') then
      Exit;
    Sleep(100);
  end;

  if CheckForMutexes('Local\CommandTraySuite.SingleInstance') then
    Result := 'CommandTray Suite could not close. Exit it from the tray and retry.';
end;
