# Local synthetic layout only. Does not change emulator/device font settings.
#requires -Version 7.0
param(
    [Parameter(Mandatory)][ValidatePattern('^emulator-\d+$')][string]$Serial,
    [Parameter(Mandatory)][string]$ExpectedAvdName,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$probeAdb = Join-Path $env:LOCALAPPDATA 'Android/Sdk/platform-tools/adb.exe'
$identity = & $probeAdb -s $Serial emu avd name
if ($LASTEXITCODE -or $identity[0] -ne $ExpectedAvdName) { throw 'Selected serial is not the owned AVD.' }
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputPath) { throw 'Use a new evidence directory.' }
New-Item -ItemType Directory -Path $outputPath | Out-Null
$results = @()
foreach ($width in @(240, 320)) {
    foreach ($font in @(1.0, 2.0)) {
        $runId = "watch-$width-$([int]($font * 100))"
        & $probeAdb -s $Serial shell am force-stop com.remotedesk.viewerprobe | Out-Null
        & $probeAdb -s $Serial shell am start -W -n com.remotedesk.viewerprobe/com.remotedesk.agent.LayoutProbeActivity `
            --ei width $width --ei height 640 --ef fontScale $font.ToString([Globalization.CultureInfo]::InvariantCulture) `
            --es platform Android --es runId $runId | Out-Null
        if ($LASTEXITCODE) { throw "Could not launch $runId." }
        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        $result = $null
        do {
            Start-Sleep -Milliseconds 250
            $raw = & $probeAdb -s $Serial exec-out run-as com.remotedesk.viewerprobe cat files/layout-probe.json 2>$null
            if ($LASTEXITCODE -eq 0) {
                try {
                    $candidate = ($raw -join "`n") | ConvertFrom-Json
                    if ($candidate.runId -eq $runId) { $result = $candidate }
                } catch { }
            }
        } while ($null -eq $result -and [DateTime]::UtcNow -lt $deadline)
        if ($null -eq $result) { throw "Timed out: $runId." }
        $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $outputPath "$runId.json") -Encoding utf8
        & $probeAdb -s $Serial shell screencap -p "/data/local/tmp/$runId.png"
        if ($LASTEXITCODE) { throw "Screenshot failed: $runId." }
        & $probeAdb -s $Serial pull "/data/local/tmp/$runId.png" (Join-Path $outputPath "$runId.png") | Out-Null
        $results += $result
        $result | ConvertTo-Json -Compress
    }
}
$results | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $outputPath 'watch-layout-matrix.json') -Encoding utf8
if (@($results | Where-Object { -not $_.passed }).Count) { throw 'Only-watch layout regression failed.' }
