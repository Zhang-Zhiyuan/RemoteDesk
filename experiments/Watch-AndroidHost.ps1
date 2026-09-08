param(
    [Parameter(Mandatory)][string]$Serial,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [int]$Minutes = 33
)
$ErrorActionPreference='Stop'
if ($Minutes -lt 1 -or $Minutes -gt 120) { throw 'Duration must be 1 to 120 minutes.' }
$adb=Join-Path $env:LOCALAPPDATA 'Android\Sdk\platform-tools\adb.exe'
$output=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$start=[DateTimeOffset]::UtcNow
$deadline=$start.AddMinutes($Minutes)
$samples=[Collections.Generic.List[object]]::new()
do {
    $processId=(& $adb -s $Serial shell pidof com.remotedesk.agent) -join ''
    $services=(& $adb -s $Serial shell settings get secure enabled_accessibility_services) -join ''
    $power=(& $adb -s $Serial shell dumpsys power) -join "`n"
    $activeLocks=@([regex]::Matches($power,'(?m)^\s*(PARTIAL_WAKE_LOCK|SCREEN_DIM_WAKE_LOCK)\s+.*RemoteDesk.*$') | ForEach-Object Value)
    $sample=[pscustomobject]@{
        Utc=[DateTimeOffset]::UtcNow.ToString('o')
        ElapsedMinutes=[Math]::Round(([DateTimeOffset]::UtcNow-$start).TotalMinutes,2)
        ProcessId=$processId.Trim()
        AccessibilityEnabled=$services.Contains('com.remotedesk.agent/com.remotedesk.agent.RemoteDeskAccessibilityService')
        IdleStreamingLocks=$activeLocks.Count
    }
    $samples.Add($sample)
    $samples | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'idle-samples.json') -Encoding utf8
    $sample | ConvertTo-Json -Compress
    if (-not $sample.ProcessId -or -not $sample.AccessibilityEnabled -or $activeLocks.Count -gt 0) {
        throw 'Idle persistence check failed; no automatic remediation was performed.'
    }
    if ([DateTimeOffset]::UtcNow -ge $deadline) { break }
    Start-Sleep -Seconds 30
} while ($true)
