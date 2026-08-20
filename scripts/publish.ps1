param([string]$Version = "0.1.0")
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
    throw "Version must use X.Y.Z without a leading v: $Version"
}
$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root 'artifacts\publish\win-x64'
$portable = Join-Path $root 'artifacts\portable'
dotnet test (Join-Path $root 'CommandTray.slnx') -c Release
dotnet publish (Join-Path $root 'src\CommandTray\CommandTray.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -o $publish
New-Item -ItemType Directory -Force -Path $portable | Out-Null
$zip = Join-Path $portable "CommandTray-$Version-win-x64.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip }
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip
Get-FileHash -Algorithm SHA256 -LiteralPath $zip | Format-List
# Get-Command returns ApplicationInfo on the runner, whose executable path is Source rather than FullName.
$isccCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -First 1
$isccPath = if ($isccCommand) { $isccCommand.Source } else { $null }
if (-not $isccPath) {
    $isccPath = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe'
    ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if ($isccPath) { & $isccPath "/DAppVersion=$Version" (Join-Path $root 'packaging\CommandTray.iss') }
else { Write-Warning 'Inno Setup was not found; portable package was created, installer was skipped.' }
