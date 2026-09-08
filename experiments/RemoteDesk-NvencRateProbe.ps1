param(
    [ValidateSet('Cbr', 'VbrCq18', 'VbrCq20')]
    [string]$Mode = 'Cbr',

    [ValidateRange(2, 60)]
    [int]$Seconds = 12,

    [string]$FfmpegPath = 'ffmpeg.exe',

    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\nvenc-rate-probe')
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$bitrate = 74600000
$vbvBuffer = [int][Math]::Ceiling($bitrate / 30.0)
$outputPath = Join-Path $OutputDirectory (
    'RemoteDesk-nvenc-{0}.h264' -f $Mode.ToLowerInvariant())
$logPath = [IO.Path]::ChangeExtension($outputPath, '.log')

$encoderArguments = switch ($Mode) {
    'Cbr' {
        @(
            '-rc', 'cbr',
            '-b:v', [string]$bitrate,
            '-maxrate', [string]$bitrate,
            '-bufsize', [string]$vbvBuffer
        )
    }
    'VbrCq18' {
        @(
            '-rc', 'vbr',
            '-cq', '18',
            '-b:v', '0',
            '-maxrate', [string]$bitrate,
            '-bufsize', [string]$vbvBuffer
        )
    }
    'VbrCq20' {
        @(
            '-rc', 'vbr',
            '-cq', '20',
            '-b:v', '0',
            '-maxrate', [string]$bitrate,
            '-bufsize', [string]$vbvBuffer
        )
    }
}

$arguments = @(
    '-hide_banner',
    '-loglevel', 'info',
    '-y',
    '-init_hw_device', 'd3d11va=wgc:1',
    '-filter_hw_device', 'wgc',
    '-filter_complex',
    'gfxcapture=monitor_idx=0:capture_cursor=0:display_border=0:max_framerate=30:output_fmt=bgra:resize_mode=crop',
    '-c:v', 'h264_nvenc',
    '-preset', 'p3',
    '-tune', 'ull'
) + $encoderArguments + @(
    '-g', '1',
    '-bf', '0',
    '-rc-lookahead', '0',
    '-surfaces', '1',
    '-delay', '0',
    '-zerolatency', '1',
    '-forced-idr', '1',
    '-aud', '1',
    '-an',
    '-sn',
    '-dn',
    '-fps_mode', 'passthrough',
    '-avioflags', 'direct',
    '-bsf:v', 'dump_extra=freq=keyframe,h264_metadata=aud=insert',
    '-flush_packets', '1',
    '-t', [string]$Seconds,
    '-f', 'h264',
    $outputPath
)

$startedAt = [Diagnostics.Stopwatch]::StartNew()
$previousErrorActionPreference = $ErrorActionPreference
try {
    # Windows PowerShell promotes native stderr lines to ErrorRecord objects.
    # FFmpeg writes normal device and progress diagnostics to stderr, so do
    # not let the script-wide Stop policy terminate a healthy probe.
    $ErrorActionPreference = 'Continue'
    & $FfmpegPath @arguments 2>&1 |
        Tee-Object -FilePath $logPath
    $exitCode = $LASTEXITCODE
} finally {
    $ErrorActionPreference = $previousErrorActionPreference
}
$startedAt.Stop()
if ($exitCode -ne 0) {
    throw "ffmpeg failed with exit code $exitCode. See $logPath"
}

$file = Get-Item -LiteralPath $outputPath
[pscustomobject]@{
    Mode = $Mode
    OutputPath = $file.FullName
    Bytes = $file.Length
    Seconds = $startedAt.Elapsed.TotalSeconds
    MegabitsPerSecond =
        ($file.Length * 8.0) /
        [Math]::Max(0.001, $startedAt.Elapsed.TotalSeconds) /
        1000000.0
    LogPath = $logPath
}
