param(
    [Parameter(Mandatory)][string]$Serial,
    [Parameter(Mandatory)][string]$TargetAddress,
    [Parameter(Mandatory)][Security.SecureString]$ConnectionPassword,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Nullable[int]]$RestoreTimeout,
    [int]$Cycles = 2
)
$ErrorActionPreference='Stop'
$adb=Join-Path $env:LOCALAPPDATA 'Android\Sdk\platform-tools\adb.exe'
$output=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$records=[Collections.Generic.List[object]]::new()
if ($null -eq $RestoreTimeout) {
    $originalTimeout = ((& $adb -s $Serial shell settings get system screen_off_timeout) -join '').Trim()
    $parsedTimeout = 0
    if ($LASTEXITCODE -or -not [int]::TryParse($originalTimeout, [ref]$parsedTimeout)) {
        throw 'Cannot read the original screen timeout; no display settings changed.'
    }
    $RestoreTimeout = $parsedTimeout
}
function Test-Locked {
    $policy=(& $adb -s $Serial shell dumpsys window policy) -join "`n"
    return $policy -match '(?m)^\s+showing=true\s*$'
}
function Connect-OwnedPhone([string]$stage) {
    & (Join-Path $PSScriptRoot 'Run-AndroidLockProbe.ps1') -OutputDirectory (Join-Path $output $stage) `
        -TargetAddress $TargetAddress -ConnectionPassword $ConnectionPassword -Seconds 12 -WakeAtSeconds 1
    if (Test-Locked) { throw 'Authenticated connection did not unlock the phone; no PIN retry performed.' }
    $frames=Get-Content (Join-Path $output "$stage/result.json") -Raw | ConvertFrom-Json
    return [int]$frames.frames
}
try {
    & $adb -s $Serial shell settings put system screen_off_timeout 30000
    $null=Connect-OwnedPhone 'initial'
    for($cycle=1;$cycle -le $Cycles;$cycle++) {
        $start=[DateTimeOffset]::UtcNow
        $locked=$false
        do {
            Start-Sleep -Seconds 5
            $locked=Test-Locked
            $elapsed=([DateTimeOffset]::UtcNow-$start).TotalSeconds
            "Auto-sleep cycle $cycle : waiting ${elapsed}s, locked=$locked"
        } while(-not $locked -and $elapsed -lt 80)
        if(-not $locked){throw 'The phone did not automatically lock with the temporary 30-second setting.'}
        $power=(& $adb -s $Serial shell dumpsys power) -join "`n"
        if($power -match '(?m)^\s*(PARTIAL_WAKE_LOCK|SCREEN_DIM_WAKE_LOCK)\s+.*RemoteDesk.*$'){
            throw 'The idle host retained a streaming wake lock.'
        }
        $processId=((& $adb -s $Serial shell pidof com.remotedesk.agent) -join '').Trim()
        if(-not $processId){throw 'Phone host exited while idle.'}
        $count=Connect-OwnedPhone "cycle-$cycle"
        $records.Add([pscustomobject]@{Cycle=$cycle; AutoLockSeconds=[Math]::Round($elapsed,1); HostProcess=$processId; Frames=$count; AutoUnlock=$true; IdleStreamingLocks=0})
        $records | ConvertTo-Json | Set-Content (Join-Path $output 'result.json') -Encoding utf8
    }
} finally {
    & $adb -s $Serial shell settings put system screen_off_timeout $RestoreTimeout
    $restored=(& $adb -s $Serial shell settings get system screen_off_timeout) -join ''
    "Restored screen_off_timeout=$restored"
}
