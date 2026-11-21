param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')]
    [string]$Version,
    [ValidateSet('windows','linux','relay','android')]
    [string[]]$Target = @('windows','linux','relay','android'),
    [ValidateSet('Debug','Release')]
    [string]$AndroidVariant = 'Release',
    [string]$InnoSetup,
    [switch]$RelayImage
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root "artifacts/suite/$Version"
$stage = Join-Path $root ('artifacts/suite/stage-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $dest,$stage | Out-Null
$assets = [System.Collections.Generic.List[string]]::new()

function Write-TarGzip {
    param([string]$Source, [string]$Archive, [string]$Executable)
    $stream = [IO.File]::Create($Archive)
    try {
        $gzip = [IO.Compression.GZipStream]::new($stream, [IO.Compression.CompressionLevel]::Optimal, $true)
        try {
            $tar = [System.Formats.Tar.TarWriter]::new($gzip, $true)
            try {
                foreach ($file in Get-ChildItem -LiteralPath $Source -Recurse -Force -File) {
                    $name = [IO.Path]::GetRelativePath($Source, $file.FullName).Replace('\', '/')
                    $entry = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::RegularFile, $name)
                    $entry.Mode = if ($name -eq $Executable -or $name.EndsWith('.sh')) {
                        [IO.UnixFileMode]0x1ED
                    } else { [IO.UnixFileMode]0x1A4 }
                    $entry.DataStream = [IO.File]::OpenRead($file.FullName)
                    try { $tar.WriteEntry($entry) }
                    finally { $entry.DataStream.Dispose() }
                }
            }
            finally { $tar.Dispose() }
        }
        finally { $gzip.Dispose() }
    }
    finally { $stream.Dispose() }
}

Push-Location -LiteralPath $root
try {
    if ('windows' -in $Target) {
        & "$PSScriptRoot/publish.ps1" -Version $Version -SkipTests -InnoSetup $InnoSetup
        foreach ($source in @("artifacts/portable/CommandTray-$Version-win-x64.zip","artifacts/installer/CommandTray-$Version-win-x64-setup.exe")) {
            if (!(Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing package $source. Provide Inno Setup using -InnoSetup." }
            $output = Join-Path $dest ([IO.Path]::GetFileName($source))
            Copy-Item -LiteralPath $source -Destination $output -Force
            $assets.Add($output)
        }
    }
    foreach ($rid in @('linux-x64','linux-arm64')) {
        if ('linux' -in $Target) {
            $build = Join-Path $stage "cli-$rid"
            dotnet publish src/CommandTray.Linux/CommandTray.Linux.csproj -c Release -r $rid --self-contained true -p:PublishSingleFile=true -p:DebugType=None "-p:Version=$Version" -o $build
            $packaging = Join-Path $build 'packaging'
            New-Item -ItemType Directory -Path $packaging | Out-Null
            foreach ($name in @('README.md','install.sh','commandtray.service')) {
                Copy-Item -LiteralPath "packaging/linux/$name" -Destination $packaging
            }
            $output = Join-Path $dest "CommandTray-$Version-$rid.tar.gz"
            Write-TarGzip $build $output 'commandtray'
            $assets.Add($output)
        }
        if ('relay' -in $Target) {
            $build = Join-Path $stage "relay-$rid"
            dotnet publish src/CommandTray.Relay/CommandTray.Relay.csproj -c Release -r $rid --self-contained true -p:PublishSingleFile=true -p:DebugType=None "-p:Version=$Version" -o $build
            $output = Join-Path $dest "CommandTray-Relay-$Version-$rid.tar.gz"
            Write-TarGzip $build $output 'CommandTray.Relay'
            $assets.Add($output)
        }
    }
    if ('relay' -in $Target) {
        $deployment = Join-Path $stage 'deployment'
        New-Item -ItemType Directory -Path $deployment | Out-Null
        foreach ($name in @('compose.yaml','README.md','.env.example')) {
            Copy-Item -LiteralPath (Join-Path $root "deploy/relay/$name") -Destination $deployment
        }
        if ($RelayImage) {
            $tag = "commandtray-relay:$Version"
            docker build -f deploy/relay/Dockerfile -t $tag .
            $image = Join-Path $dest "CommandTray-Relay-$Version-image.tar"
            docker image save -o $image $tag
            $assets.Add($image)
            $envExample = Join-Path $deployment '.env.example'
            (Get-Content -LiteralPath $envExample -Raw).Replace('commandtray-relay:local',$tag) | Set-Content -LiteralPath $envExample
        }
        $output = Join-Path $dest "CommandTray-Relay-$Version-compose.tar.gz"
        Write-TarGzip $deployment $output
        $assets.Add($output)
    }
    if ('android' -in $Target) {
        if ($AndroidVariant -eq 'Release') {
            foreach ($name in @('COMMANDTRAY_ANDROID_KEYSTORE','COMMANDTRAY_ANDROID_STORE_PASSWORD','COMMANDTRAY_ANDROID_KEY_ALIAS','COMMANDTRAY_ANDROID_KEY_PASSWORD')) {
                if (![Environment]::GetEnvironmentVariable($name)) { throw "Set $name to build an installable release APK." }
            }
        }
        $major,$minor,$patch = $Version.Split('.') | ForEach-Object { [int]$_ }
        $versionCode = [Math]::Max(1, $major * 1000000L + $minor * 1000L + $patch)
        if ($minor -gt 999 -or $patch -gt 999 -or $versionCode -gt 2100000000) { throw 'Android version exceeds the version-code range.' }
        $gradle = if ($IsWindows) { Join-Path $root 'android/gradlew.bat' } else { Join-Path $root 'android/gradlew' }
        & $gradle -p (Join-Path $root 'android') ":app:assemble$AndroidVariant" "-PcommandTrayVersionName=$Version" "-PcommandTrayVersionCode=$versionCode"
        $variant = $AndroidVariant.ToLowerInvariant()
        $source = Join-Path $root "android/app/build/outputs/apk/$variant/app-$variant.apk"
        if (!(Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing installable APK $source." }
        $output = Join-Path $dest "CommandTray-$Version-android-$variant.apk"
        Copy-Item -LiteralPath $source -Destination $output -Force
        $assets.Add($output)
    }
    Get-ChildItem -LiteralPath $dest -File | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object Name | ForEach-Object {
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $($_.Name)"
    } | Set-Content -LiteralPath (Join-Path $dest 'SHA256SUMS.txt') -Encoding utf8NoBOM
    $assets
}
finally {
    Pop-Location
    $resolvedStage = (Resolve-Path -LiteralPath $stage).Path
    $expectedParent = (Resolve-Path -LiteralPath (Join-Path $root 'artifacts/suite')).Path
    if ([IO.Path]::GetDirectoryName($resolvedStage) -eq $expectedParent -and
        [IO.Path]::GetFileName($resolvedStage).StartsWith('stage-', [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}
