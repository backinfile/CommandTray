param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$PackageDirectory
)

$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') { throw 'Use X.Y.Z.' }
if (!$env:COMMANDTRAY_UPDATE_SIGNING_KEY) { throw 'The Suite update signing key is missing.' }

Add-Type -TypeDefinition @'
using System;
using System.Security.Cryptography;

public static class SuiteManifestSigning
{
    public static byte[] Sign(byte[] pkcs8, string pinnedPem, byte[] data)
    {
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(pkcs8, out int read);
        if (read != pkcs8.Length || key.ExportSubjectPublicKeyInfoPem().Trim().Replace("\r", "") != pinnedPem.Trim().Replace("\r", ""))
            throw new CryptographicException("The signing key does not match the key embedded in the clients.");
        return key.SignData(data, HashAlgorithmName.SHA256);
    }
}
'@

$assets = [ordered]@{}
$names = [ordered]@{
    'windows-setup' = "CommandTray-Suite-$Version-win-x64-setup.exe"
    'windows-portable' = "CommandTray-Suite-$Version-win-x64.zip"
    'linux-x64' = "CommandTray-$Version-linux-x64.tar.gz"
    'linux-arm64' = "CommandTray-$Version-linux-arm64.tar.gz"
    'linux-x64-desktop' = "CommandTray-$Version-linux-x64-desktop.tar.gz"
    'linux-arm64-desktop' = "CommandTray-$Version-linux-arm64-desktop.tar.gz"
    'android' = "CommandTray-$Version-android-release.apk"
}
foreach ($platform in $names.Keys) {
    $path = Join-Path $PackageDirectory $names[$platform]
    $file = Get-Item -LiteralPath $path
    $assets[$platform] = [ordered]@{
        name = $file.Name
        size = $file.Length
        sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

$payload = [Text.Encoding]::UTF8.GetBytes(([ordered]@{
    schema = 1; channel = 'suite'; version = $Version; assets = $assets
} | ConvertTo-Json -Depth 6 -Compress))
$private = [Convert]::FromBase64String($env:COMMANDTRAY_UPDATE_SIGNING_KEY)
try {
    $public = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../src/CommandTray.Sync/SuiteUpdatePublic.pem') -Raw
    $signature = [SuiteManifestSigning]::Sign($private, $public, $payload)
    [ordered]@{ payload = [Convert]::ToBase64String($payload); signature = [Convert]::ToBase64String($signature) } |
        ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $PackageDirectory 'suite-updates.json') -Encoding utf8NoBOM
}
finally { [Array]::Clear($private) }
