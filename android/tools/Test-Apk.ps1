param(
    [string]$Apk = (Join-Path $PSScriptRoot '../app/build/outputs/apk/debug/app-debug.apk'),
    [string]$Sdk = $env:ANDROID_HOME
)

$ErrorActionPreference = 'Stop'

if (-not $Sdk) { throw 'Set ANDROID_HOME or pass -Sdk.' }

$apkPath = (Resolve-Path -LiteralPath $Apk).Path
$tools = Join-Path $Sdk 'build-tools/35.0.0'
& (Join-Path $tools 'apksigner.bat') verify --verbose $apkPath
if ($LASTEXITCODE -ne 0) { throw 'APK signature verification failed.' }

$metadata = (& (Join-Path $tools 'aapt2.exe') dump badging $apkPath) -join "\n"
if ($LASTEXITCODE -ne 0) { throw 'Cannot read APK metadata.' }
if ($metadata -notmatch "package: name='dev\.commandtray\.android'" -or
    $metadata -notmatch "sdkVersion:'29'" -or
    $metadata -notmatch "launchable-activity: name='dev\.commandtray\.android\.MainActivity'" -or
    $metadata -notmatch "uses-permission: name='android\.permission\.INTERNET'") {
    throw 'The APK application ID, minimum SDK, launcher, or Internet permission is incorrect.'
}

Get-FileHash -LiteralPath $apkPath -Algorithm SHA256
