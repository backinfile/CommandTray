#define AppName "CommandTray"
#ifndef AppVersion
#define AppVersion "0.1.0"
#endif
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
CloseApplications=force

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

  { New versions understand --shutdown and stop their owned background jobs.
    Older versions ignored the argument, so use a bounded wait and taskkill
    fallback to prevent Setup hanging forever at "Closing applications". }
  Exec(OldExecutable, '--shutdown', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  for Attempt := 1 to 50 do
  begin
    if not CheckForMutexes('Local\CommandTray.SingleInstance') then
      Exit;
    Sleep(100);
  end;

  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM CommandTray.exe', '',
    SW_HIDE, ewWaitUntilTerminated, ResultCode);
  for Attempt := 1 to 20 do
  begin
    if not CheckForMutexes('Local\CommandTray.SingleInstance') then
      Exit;
    Sleep(100);
  end;

  if CheckForMutexes('Local\CommandTray.SingleInstance') then
    Result := 'CommandTray could not be closed. Please end CommandTray.exe and retry.';
end;
