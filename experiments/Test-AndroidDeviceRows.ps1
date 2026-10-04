# Actual product rows in the isolated probe; requires an owned emulator started separately.
# Uses activity-local font overrides and in-memory fixtures, without network or preference access.
#requires -Version 7.0
param(
    [Parameter(Mandatory)][ValidatePattern('^emulator-\d+$')][string]$Serial,
    [Parameter(Mandatory)][string]$ExpectedAvdName,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$probeAdb = Join-Path $env:LOCALAPPDATA 'Android/Sdk/platform-tools/adb.exe'
$identity = & $probeAdb -s $Serial emu avd name
if ($LASTEXITCODE -or $identity[0] -ne $ExpectedAvdName) { throw 'The selected emulator does not match the owned AVD.' }
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputPath) { throw 'Use a fresh evidence directory to preserve earlier reports.' }
New-Item -ItemType Directory -Path $outputPath | Out-Null
$results = @()
$cases = foreach ($mode in @('history', 'lan')) {
    foreach ($font in @(1.0, 1.5, 2.0)) {
        [pscustomobject]@{ Mode = $mode; Font = $font; Legacy = $false }
    }
    [pscustomobject]@{ Mode = $mode; Font = 2.0; Legacy = $true }
}
foreach ($case in $cases) {
    $suffix = if ($case.Legacy) { 'legacy-limit' } else { 'fixed' }
    $runId = "$($case.Mode)-320-$([int]($case.Font * 100))-$suffix"
    & $probeAdb -s $Serial shell am force-stop com.remotedesk.viewerprobe | Out-Null
    & $probeAdb -s $Serial shell am start -W -n com.remotedesk.viewerprobe/com.remotedesk.agent.DeviceRowsProbeActivity `
        --es mode $case.Mode --ei width 320 --ef fontScale $case.Font.ToString([Globalization.CultureInfo]::InvariantCulture) `
        --ez legacy $case.Legacy.ToString().ToLowerInvariant() --es runId $runId | Out-Null
    if ($LASTEXITCODE) { throw "Could not launch $runId." }
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    $result = $null
    do {
        Start-Sleep -Milliseconds 250
        $raw = & $probeAdb -s $Serial exec-out run-as com.remotedesk.viewerprobe cat files/device-rows-probe.json 2>$null
        if ($LASTEXITCODE -eq 0 -and ($raw -join "`n").TrimStart().StartsWith('{')) {
            try {
                $candidate = ($raw -join "`n") | ConvertFrom-Json
                if ($candidate.runId -eq $runId) { $result = $candidate }
            } catch { } # Retry a still-in-flight local report write.
        }
    } while ($null -eq $result -and [DateTime]::UtcNow -lt $deadline)
    if ($null -eq $result) { throw "Probe timed out: $runId." }
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $outputPath "$runId.json") -Encoding utf8
    & $probeAdb -s $Serial shell screencap -p "/data/local/tmp/device-rows-$runId.png"
    if ($LASTEXITCODE) { throw "Screenshot failed: $runId." }
    & $probeAdb -s $Serial pull "/data/local/tmp/device-rows-$runId.png" (Join-Path $outputPath "$runId.png") | Out-Null
    if ($LASTEXITCODE) { throw "Screenshot collection failed: $runId." }
    $results += $result
    $result | ConvertTo-Json -Compress
}
$results | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $outputPath 'device-rows-matrix.json') -Encoding utf8
$unexpected = @($results | Where-Object {
    if ($_.legacyThreeLineLimit) { $_.passed -or $_.ellipsisCharacters -le 0 }
    else { -not $_.passed }
})
if ($unexpected.Count) { throw 'Device row matrix did not match the expected fixed/negative-control outcomes.' }
