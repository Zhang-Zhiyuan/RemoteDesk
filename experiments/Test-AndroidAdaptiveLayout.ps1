# Requires the isolated AndroidViewerProbe APK, not the installed product.
param([string]$Serial = 'emulator-5582', [Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if ($Serial -notmatch '^emulator-\d+$') { throw 'This synthetic matrix runs only on an owned emulator.' }
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$results = @()
$originalFontScale = & adb -s $Serial shell settings get system font_scale
try {
foreach ($font in @(1.0, 1.5, 2.0)) {
    & adb -s $Serial shell settings put system font_scale $font.ToString([Globalization.CultureInfo]::InvariantCulture)
    foreach ($viewport in @(@(240, 640), @(320, 640), @(600, 360))) {
        & adb -s $Serial shell am force-stop com.remotedesk.viewerprobe | Out-Null
        & adb -s $Serial shell run-as com.remotedesk.viewerprobe rm -f files/layout-probe.json
        & adb -s $Serial shell am start -W -n com.remotedesk.viewerprobe/com.remotedesk.agent.LayoutProbeActivity `
            --ei width $viewport[0] --ei height $viewport[1] | Out-Null
        if ($LASTEXITCODE) { throw 'Could not launch isolated layout test.' }
        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        $result = $null
        do {
            Start-Sleep -Milliseconds 250
            $raw = & adb -s $Serial exec-out run-as com.remotedesk.viewerprobe cat files/layout-probe.json 2>$null
            if ($LASTEXITCODE -eq 0 -and ($raw -join "`n").TrimStart().StartsWith('{')) {
                $result = ($raw -join "`n") | ConvertFrom-Json
            }
        } while ($null -eq $result -and [DateTime]::UtcNow -lt $deadline)
        if ($null -eq $result) { throw 'Layout probe timed out.' }
        $case = [pscustomobject]@{ width = $viewport[0]; height = $viewport[1]; font = $font; result = $result }
        $results += $case
        $case | ConvertTo-Json -Depth 4 -Compress
    }
}
} finally {
    if ($originalFontScale.Trim() -eq 'null') { & adb -s $Serial shell settings delete system font_scale | Out-Null }
    else { & adb -s $Serial shell settings put system font_scale $originalFontScale }
}
$results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outputPath 'layout-matrix.json') -Encoding utf8
if (@($results | Where-Object { -not $_.result.passed }).Count) { exit 1 }
