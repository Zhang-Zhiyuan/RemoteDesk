#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Serial,
    [Parameter(Mandatory)][string]$TargetAddress,
    [Parameter(Mandatory)][Security.SecureString]$ConnectionPassword,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$adbPath = Join-Path $env:LOCALAPPDATA 'Android/Sdk/platform-tools/adb.exe'
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$oldProcess = ((& $adbPath -s $Serial shell pidof com.remotedesk.agent) -join '').Trim()
if ($oldProcess -notmatch '^\d+$') { throw 'Expected one running RemoteDesk test process.' }
& (Join-Path $PSScriptRoot 'Run-AndroidLockProbe.ps1') -TargetAddress $TargetAddress `
    -ConnectionPassword $ConnectionPassword -OutputDirectory (Join-Path $output 'before') -Seconds 10
# Deliberately crash only the owned app, not the device/system. Force-stop is a
# different user action that Android is allowed to keep stopped; do not conflate them.
& $adbPath -s $Serial shell am crash --user current com.remotedesk.agent
if ($LASTEXITCODE) { throw 'The platform did not accept the app-only crash test.' }
$timer = [Diagnostics.Stopwatch]::StartNew()
$newProcess = ''
$reachable = $false
while ($timer.Elapsed.TotalSeconds -lt 60) {
    $newProcess = ((& $adbPath -s $Serial shell pidof com.remotedesk.agent) -join '').Trim()
    if ($newProcess -match '^\d+$' -and $newProcess -ne $oldProcess) {
        $socket = [Net.Sockets.TcpClient]::new()
        try { $reachable = $socket.ConnectAsync($TargetAddress, 56565).Wait(800) -and $socket.Connected }
        catch { } finally { $socket.Dispose() }
        if ($reachable) { break }
    }
    Start-Sleep -Seconds 2
}
$permissions = ((& $adbPath -s $Serial shell settings get secure enabled_accessibility_services) -join '') -match
    'com.remotedesk.agent/com.remotedesk.agent.RemoteDeskAccessibilityService'
$result = [ordered]@{ OldProcess = $oldProcess; NewProcess = $newProcess;
    ListenerRecovered = $reachable; AccessibilityEnabled = $permissions;
    RecoverySeconds = [math]::Round($timer.Elapsed.TotalSeconds, 1); Frames = 0 }
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding utf8
& $adbPath -s $Serial logcat -d --pid=$oldProcess -s AndroidRuntime:E |
    Set-Content -LiteralPath (Join-Path $output 'injected-crash.txt') -Encoding utf8
if (-not $reachable -or -not $permissions) { throw 'Automatic crash recovery was not confirmed; no restart or permission regrant was forced.' }
& (Join-Path $PSScriptRoot 'Run-AndroidLockProbe.ps1') -TargetAddress $TargetAddress `
    -ConnectionPassword $ConnectionPassword -OutputDirectory (Join-Path $output 'after') -Seconds 12
$after = Get-Content -LiteralPath (Join-Path $output 'after/result.json') -Raw | ConvertFrom-Json
$result.Frames = $after.frames
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding utf8
$result | ConvertTo-Json
