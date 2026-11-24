param(
    [Parameter(Mandatory)]
    [string]$Executable,
    [string]$RelayAssembly
)
$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path -LiteralPath $Executable).Path
$root = Split-Path -Parent $PSScriptRoot
$parent = Join-Path $root 'artifacts/windows-mcp-smoke'
New-Item -ItemType Directory -Force -Path $parent | Out-Null
$data = Join-Path $parent ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $data | Out-Null
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
$listener.Stop()
$endpoint = "http://127.0.0.1:$port/mcp"
$launchEnvironment = @{
    COMMANDTRAY_DATA_DIR = $data
    COMMANDTRAY_MCP_PORT = "$port"
}
$headers = @{ Accept = 'application/json, text/event-stream' }
$initialize = @{
    protocolVersion = '2025-11-25'
    capabilities = @{}
    clientInfo = @{ name = 'commandtray-windows-smoke'; version = '1' }
}
$app = $null
$relay = $null
$script:requestId = 0

function Send-McpRequest {
    param([string]$Method, [hashtable]$Arguments, [hashtable]$RequestHeaders = $headers)
    $script:requestId++
    $payload = @{ jsonrpc = '2.0'; id = $script:requestId; method = $Method; params = $Arguments }
    Invoke-WebRequest -Uri $endpoint -Method Post -ContentType 'application/json' -Headers $RequestHeaders `
        -Body ($payload | ConvertTo-Json -Depth 25 -Compress) -TimeoutSec 5 -SkipHttpErrorCheck
}
function Read-McpResult {
    param($Response)
    if ($Response.StatusCode -ne 200) { throw "Unexpected MCP HTTP status $($Response.StatusCode)." }
    $line = ($Response.Content -split '\r?\n' | Where-Object { $_ -match '^data: ' } | Select-Object -Last 1)
    $body = if ($line) { $line.Substring(6) } else { $Response.Content }
    $message = ConvertFrom-Json -InputObject $body -AsHashtable
    if ($message.error) { throw ($message.error | ConvertTo-Json -Compress -Depth 10) }
    if ($message.result.isError) { throw ($message.result.content | ConvertTo-Json -Compress -Depth 10) }
    $message.result
}
function Invoke-Tool {
    param([string]$Name, [hashtable]$Arguments = @{})
    $result = Read-McpResult (Send-McpRequest 'tools/call' @{ name = $Name; arguments = $Arguments })
    if ($result.structuredContent) {
        $structured = $result.structuredContent
        if ($structured.Count -eq 1 -and $structured.Contains("result")) { return $structured.result }
        return $structured
    }
    return $result.content | ForEach-Object { $_.text }
}

try {
    $app = Start-Process -FilePath $exe -WindowStyle Hidden -Environment $launchEnvironment -PassThru
    $unauthenticated = $null
    for ($attempt = 0; $attempt -lt 32; $attempt++) {
        try {
            $unauthenticated = Send-McpRequest 'initialize' $initialize
            break
        } catch {
            if ($app.HasExited) { throw 'The isolated Windows app exited before the MCP server opened.' }
            Start-Sleep -Milliseconds 180
        }
    }
    if ($unauthenticated.StatusCode -ne 401) { throw "Expected HTTP 401 without the local credential, got $($unauthenticated.StatusCode)." }

    Add-Type -AssemblyName System.Security.Cryptography.ProtectedData
    $encrypted = Get-Content -LiteralPath (Join-Path $data 'mcp-credential.json') -Raw | ConvertFrom-Json
    $credential = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect(
        [Convert]::FromBase64String($encrypted), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser))
    $headers.Authorization = "Bearer $credential"
    $badOrigin = $headers.Clone()
    $badOrigin.Origin = 'https://example.invalid'
    if ((Send-McpRequest 'initialize' $initialize $badOrigin).StatusCode -ne 403) { throw 'Invalid origin was accepted.' }

    $server = Read-McpResult (Send-McpRequest 'initialize' $initialize)
    if (!$server.serverInfo) { throw 'MCP initialize omitted serverInfo.' }
    $inventory = Read-McpResult (Send-McpRequest 'tools/list' @{})
    foreach ($expected in @('list_services','create_workspace','add_workspace_component','take_screenshot','list_shared_files','get_desktop_settings')) {
        if ($expected -notin $inventory.tools.name) { throw "Expected tool $expected is missing." }
    }

    $created = Invoke-Tool 'create_workspace' @{ name = 'MCP smoke workspace' }
    $space = $created.id
    if (!$space) { throw ('Workspace creation did not return an ID. ' + ($created | ConvertTo-Json -Compress)) }
    $note = (Invoke-Tool 'add_workspace_component' @{ spaceId = $space; kind = 'note'; title = 'Smoke'; data = @{ text = 'native MCP note' } }).id
    $null = Invoke-Tool 'move_resize_workspace_component' @{ spaceId = $space; itemId = $note; x = 50; y = 60; width = 380; height = 230 }
    $workspaces = Invoke-Tool 'list_workspaces' | ConvertTo-Json -Compress -Depth 25
    if ($workspaces -notmatch 'native MCP note' -or $workspaces -notmatch '"width":380') { throw 'Workspace mutation did not persist.' }

    $terminal = (Invoke-Tool 'add_workspace_component' @{ spaceId = $space; kind = 'terminal'; title = 'Smoke terminal' }).id
    $null = Invoke-Tool 'start_terminal_session' @{ spaceId = $space; itemId = $terminal; shell = 'cmd'; directory = $data }
    $null = Invoke-Tool 'send_terminal_input' @{ itemId = $terminal; input = "echo commandtray-smoke%COMSPEC%`r" }
    $output = ''
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        $output = (Invoke-Tool 'read_terminal_output' @{ itemId = $terminal }).output
        if ($output -match 'commandtray-smoke.*cmd\.exe') { break }
        Start-Sleep -Milliseconds 150
    }
    if ($output -notmatch 'commandtray-smoke.*cmd\.exe') { throw 'ConPTY did not produce the expected command output.' }
    $null = Invoke-Tool 'close_terminal_session' @{ spaceId = $space; itemId = $terminal }
    if ($RelayAssembly) {
        $relayPath = (Resolve-Path -LiteralPath $RelayAssembly).Path
        $portListener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $portListener.Start()
        $relayPort = ([Net.IPEndPoint]$portListener.LocalEndpoint).Port
        $portListener.Stop()
        $relayUrl = "http://127.0.0.1:$relayPort"
        $pairingCode = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
        $relayEnv = @{
            ASPNETCORE_URLS = $relayUrl
            COMMANDTRAY_RELAY_DATA = (Join-Path $data 'relay')
            COMMANDTRAY_PAIRING_CODE = $pairingCode
        }
        $relay = Start-Process -FilePath dotnet -ArgumentList ('"' + $relayPath + '"') -WindowStyle Hidden -Environment $relayEnv -PassThru
        $ready = $false
        for ($attempt = 0; $attempt -lt 25; $attempt++) {
            try { $health = Invoke-RestMethod -Uri "$relayUrl/healthz" -TimeoutSec 2; $ready = $health.ready; break }
            catch { if ($relay.HasExited) { throw }; Start-Sleep -Milliseconds 160 }
        }
        if (!$ready) { throw 'The relay did not start for the Windows sync check.' }
        $null = Invoke-Tool 'pair_relay' @{ url = $relayUrl; deviceName = 'Windows acceptance'; pairingCode = $pairingCode }
        $null = Invoke-Tool 'push_workspace'
        $peerHeaders = @{ 'X-CommandTray-Client-Version' = $health.relayVersion }
        $peer = Invoke-RestMethod -Method Post -Uri "$relayUrl/v1/devices/pair" -ContentType 'application/json' -Headers $peerHeaders -Body (
            @{ deviceName = 'Peer acceptance'; pairingCode = $pairingCode } | ConvertTo-Json)
        $peerHeaders.Authorization = "Bearer $($peer.token)"
        $remote = Invoke-RestMethod -Uri "$relayUrl/v1/workspace" -Headers $peerHeaders
        $found = ($remote.document.spaces | Where-Object id -eq $space).items | Where-Object id -eq $note
        if ($found.data.text -ne 'native MCP note' -or $found.width -ne 380) { throw 'The relay lost Windows workspace content or geometry.' }
        'Windows MCP workspace roundtrip through the relay passed.'
    }
    'Windows MCP smoke passed: local credential, Origin validation, workspace persistence, and ConPTY.'
}
finally {
    if ($relay -and !$relay.HasExited) { Stop-Process -Id $relay.Id; $null = $relay.WaitForExit(4500) }
    if ($app -and !$app.HasExited) {
        Start-Process -FilePath $exe -ArgumentList '--shutdown' -WindowStyle Hidden -Environment $launchEnvironment -Wait
        if (!$app.WaitForExit(5000)) { Stop-Process -Id $app.Id -Force }
    }
    $resolved = (Resolve-Path -LiteralPath $data).Path
    $expected = (Resolve-Path -LiteralPath $parent).Path
    if ([IO.Path]::GetDirectoryName($resolved) -eq $expected) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
