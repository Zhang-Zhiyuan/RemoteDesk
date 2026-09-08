#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Serial,
    [Parameter(Mandatory)][string]$TargetAddress,
    [Parameter(Mandatory)][Security.SecureString]$ConnectionPassword,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateRange(1, 20)][int]$Reconnects = 5,
    [switch]$CycleWifi
)
$ErrorActionPreference = 'Stop'
$adbPath = Join-Path $env:LOCALAPPDATA 'Android/Sdk/platform-tools/adb.exe'
$probePath = Join-Path $PSScriptRoot 'Run-AndroidLockProbe.ps1'
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$records = [Collections.Generic.List[object]]::new()
function Read-PhoneState {
    $phoneProcess = ((& $adbPath -s $Serial shell pidof com.remotedesk.agent) -join '').Trim()
    $enabled = ((& $adbPath -s $Serial shell settings get secure enabled_accessibility_services) -join '') -match
        'com.remotedesk.agent/com.remotedesk.agent.RemoteDeskAccessibilityService'
    if (-not $phoneProcess -or -not $enabled) { throw 'Host process or accessibility is unavailable; no permission bypass attempted.' }
    [pscustomobject]@{ ProcessId = $phoneProcess; AccessibilityEnabled = $enabled }
}
function Connect-Phone([string]$stage) {
    & $probePath -TargetAddress $TargetAddress -ConnectionPassword $ConnectionPassword `
        -OutputDirectory (Join-Path $output $stage) -Seconds 10
    $result = Get-Content -LiteralPath (Join-Path $output "$stage/result.json") -Raw | ConvertFrom-Json
    $state = Read-PhoneState
    $records.Add([pscustomobject]@{ Stage = $stage; Frames = $result.frames; FirstFrameMs = $result.firstMs;
        ProcessId = $state.ProcessId; AccessibilityEnabled = $state.AccessibilityEnabled })
    $records | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding utf8
    "${stage}: $($result.frames) frames; PID $($state.ProcessId)."
}
$null = Read-PhoneState
Connect-Phone 'warmup'
& $adbPath -s $Serial shell dumpsys meminfo com.remotedesk.agent |
    Set-Content -LiteralPath (Join-Path $output 'memory-before.txt') -Encoding utf8
for ($i = 1; $i -le $Reconnects; $i++) { Connect-Phone "reconnect-$i" }
if ($CycleWifi) {
    $originalWifi = ((& $adbPath -s $Serial shell settings get global wifi_on) -join '').Trim()
    if ($originalWifi -ne '1') { throw 'Wi-Fi must already be enabled for this opt-in interruption test.' }
    $job = $null
    $wifiChanged = $false
    try {
        $liveOutput = Join-Path $output 'wifi-interrupted-session'
        $job = Start-ThreadJob -ScriptBlock {
            param($scriptPath, $target, $secret, $directory)
            try {
                & $scriptPath -TargetAddress $target -ConnectionPassword $secret -OutputDirectory $directory -Seconds 40
                [pscustomobject]@{ ProbeSucceeded = $true }
            } catch { [pscustomobject]@{ ProbeSucceeded = $false } }
        } -ArgumentList $probePath, $TargetAddress, $ConnectionPassword, $liveOutput
        $progressFile = Join-Path $liveOutput 'progress.json'
        $ready = $false
        $timer = [Diagnostics.Stopwatch]::StartNew()
        while ($timer.Elapsed.TotalSeconds -lt 20) {
            if (Test-Path -LiteralPath $progressFile) {
                try {
                    $progress = Get-Content -LiteralPath $progressFile -Raw | ConvertFrom-Json
                    if ($progress.connected -and $progress.frames -ge 10) { $ready = $true; break }
                } catch { }
            }
            Start-Sleep -Milliseconds 500
        }
        if (-not $ready) { throw 'The active connection did not produce frames before network interruption.' }
        $wifiChanged = $true
        & $adbPath -s $Serial shell svc wifi disable
        if ($LASTEXITCODE) { throw 'Wi-Fi interruption was not applied.' }
        'Wi-Fi disabled temporarily during an authenticated video session.'
        Start-Sleep -Seconds 8
    } finally {
        if ($wifiChanged) {
            & $adbPath -s $Serial shell svc wifi enable
            if ($LASTEXITCODE) { Write-Warning 'Could not restore Wi-Fi automatically; reconnect it on the test phone.' }
        }
        if ($null -ne $job) {
            $finished = Wait-Job -Job $job -Timeout 45
            if ($null -eq $finished) { Stop-Job -Job $job }
            Receive-Job -Job $job | Out-Null
            Remove-Job -Job $job
        }
    }
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $reachable = $false
    while ($timer.Elapsed.TotalSeconds -lt 35) {
        $socket = [Net.Sockets.TcpClient]::new()
        try {
            $connect = $socket.ConnectAsync($TargetAddress, 56565)
            $reachable = $connect.Wait(800) -and $socket.Connected
        } catch { } finally { $socket.Dispose() }
        if ($reachable) { break }
        Start-Sleep -Milliseconds 800
    }
    if (-not $reachable) { throw 'The original phone address did not return; Wi-Fi was restored but DHCP may have changed the address.' }
    Connect-Phone 'wifi-restored'
}
& $adbPath -s $Serial shell dumpsys meminfo com.remotedesk.agent |
    Set-Content -LiteralPath (Join-Path $output 'memory-after.txt') -Encoding utf8
$null = Read-PhoneState
'Android reconnect checks completed. Test screenshots remain private in the output directory.'
