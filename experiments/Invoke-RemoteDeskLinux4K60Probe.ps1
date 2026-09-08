[CmdletBinding()]
param(
    [switch]$Execute,

    [switch]$PlanOnly,

    [ValidateRange(8, 120)]
    [int]$DurationSeconds = 20,

    [ValidateNotNullOrEmpty()]
    [string]$WslDistribution = "Ubuntu-24.04",

    [ValidateRange(80, 199)]
    [int]$DisplayRangeStart = 91,

    [ValidateRange(80, 199)]
    [int]$DisplayRangeEnd = 119,

    [string]$OutputDirectory = ""
)

<#
.SYNOPSIS
Plans or explicitly runs an isolated Linux 4K60 X11/NVENC probe.

.DESCRIPTION
The default behavior is PlanOnly and is read-only. A heavy 4K60 probe starts
only when -Execute is supplied.

The execution path asks the selected WSL distribution to atomically reserve an
unused X display, then starts only processes tagged with a unique run ID:

  Xvfb 3840x2160
    -> ffplay testsrc2 dynamic window
    -> ffmpeg x11grab @ 60fps
    -> format=nv12,hwupload_cuda,scale_cuda(...passthrough=0)
    -> h264_nvenc all-IDR Annex-B

The H.264 file and process logs remain in a unique /tmp directory only for the
duration of the probe. Metrics are collected before cleanup. Cleanup validates
the unique argv[0] tag before signalling each exact PID, releases only the
display lock owned by this run, and removes only this run's validated /tmp
directory. It never uses global process-name termination or a wildcard.

On execution, JSON and Markdown evidence are written under artifacts by
default. The raw 4K H.264 stream is deliberately deleted in finally cleanup.

.EXAMPLE
.\experiments\Invoke-RemoteDeskLinux4K60Probe.ps1 -PlanOnly

.EXAMPLE
.\experiments\Invoke-RemoteDeskLinux4K60Probe.ps1 -Execute -DurationSeconds 20
#>

$ErrorActionPreference = "Stop"

if ($Execute -and $PlanOnly) {
    throw "-Execute and -PlanOnly are mutually exclusive."
}

if ($DisplayRangeEnd -lt $DisplayRangeStart) {
    throw "-DisplayRangeEnd must be greater than or equal to -DisplayRangeStart."
}

$effectivePlanOnly = $PlanOnly -or -not $Execute
$scriptRoot = Split-Path -Parent $PSCommandPath
$repositoryRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $scriptRoot ".."))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path (
        Join-Path $repositoryRoot "artifacts") "linux-4k60-probe"
}
else {
    $OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
}

$width = 3840
$height = 2160
$requestedFps = 60.0
$minimumAcceptedFps = 57.0
$targetBitrateBps = 79626240
$maximumBitrateBps = 160000000
$vbvBufferBytes = 2654208
$targetBitrateTolerance = 0.15
$minimumAcceptedBitrateBps = [int][Math]::Floor(
    $targetBitrateBps * (1.0 - $targetBitrateTolerance))
$maximumAcceptedTargetBitrateBps = [int][Math]::Ceiling(
    $targetBitrateBps * (1.0 + $targetBitrateTolerance))
$resultPrefix = "REMOTEDESK_LINUX4K60_RESULT="

function Get-WslDistributionNames {
    param([System.Management.Automation.CommandInfo]$WslCommand)

    if ($null -eq $WslCommand) {
        return @()
    }

    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $lines = @(& $WslCommand.Source --list --quiet 2>$null)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }

    if ($exitCode -ne 0) {
        return @()
    }

    return @(
        $lines |
            ForEach-Object {
                ([string]$_).Replace([string][char]0, "").Trim()
            } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

function Format-MarkdownCell {
    param([AllowNull()][object]$Value)

    if ($null -eq $Value) {
        return ""
    }

    return ([string]$Value).Replace("|", "\|").Replace(
        "`r",
        " ").Replace(
        "`n",
        " ")
}

function Write-Utf8NoBom {
    param(
        [string]$Path,
        [string]$Text
    )

    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $Text, $encoding)
}

function New-Gate {
    param(
        [string]$Name,
        [bool]$Passed,
        [AllowNull()][object]$Actual,
        [string]$Expected,
        [string]$Detail
    )

    return [pscustomobject]@{
        name = $Name
        passed = $Passed
        actual = $Actual
        expected = $Expected
        detail = $Detail
    }
}

function Convert-ReportToMarkdown {
    param([pscustomobject]$Report)

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# RemoteDesk Linux 4K60 probe") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add("- Status: **$($Report.status)**") | Out-Null
    $lines.Add("- Run ID: ``$($Report.runId)``") | Out-Null
    $lines.Add("- Started (UTC): $($Report.startedAtUtc)") | Out-Null
    $lines.Add("- Completed (UTC): $($Report.completedAtUtc)") | Out-Null
    $lines.Add("- WSL distribution: ``$($Report.configuration.wslDistribution)``") |
        Out-Null
    $lines.Add(
        "- Requested mode: " +
        "$($Report.configuration.width)x$($Report.configuration.height)" +
        "@$($Report.configuration.requestedFps) for " +
        "$($Report.configuration.durationSeconds)s") | Out-Null
    $lines.Add(
        "- Pipeline: ``$($Report.configuration.pipeline)``") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add("## Acceptance gates") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add("| Gate | Result | Actual | Expected | Detail |") | Out-Null
    $lines.Add("|---|---:|---:|---|---|") | Out-Null
    foreach ($gate in @($Report.gates)) {
        $result = if ($gate.passed) { "PASS" } else { "FAIL" }
        $lines.Add(
            "| $(Format-MarkdownCell $gate.name) | $result | " +
            "$(Format-MarkdownCell $gate.actual) | " +
            "$(Format-MarkdownCell $gate.expected) | " +
            "$(Format-MarkdownCell $gate.detail) |") | Out-Null
    }

    if ($null -ne $Report.metrics) {
        $metrics = $Report.metrics
        $lines.Add("") | Out-Null
        $lines.Add("## Measured output") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("- Encoded frames: $($metrics.frameCount)") | Out-Null
        $lines.Add(
            "- Encoded cadence: " +
            "$([Math]::Round([double]$metrics.cadenceFps, 3)) fps") | Out-Null
        $lines.Add(
            "- End-to-end probe-process cadence: " +
            "$([Math]::Round([double]$metrics.realtimeFps, 3)) fps") | Out-Null
        $lines.Add(
            "- Raw Annex-B bitrate: " +
            "$([Math]::Round([double]$metrics.megabitsPerSecond, 3)) Mbps") |
            Out-Null
        $lines.Add(
            "- Configured bitrate target/maxrate: " +
            "$($metrics.configuredTargetBitrateBps)/" +
            "$($metrics.configuredMaxrateBps) bps; hard cap " +
            "$($metrics.maximumBitrateBps) bps") | Out-Null
        $lines.Add(
            "- Stream: $($metrics.codecName), " +
            "$($metrics.width)x$($metrics.height), $($metrics.pixelFormat)") |
            Out-Null
        $lines.Add(
            "- Annex-B AUs: $($metrics.accessUnitCount); " +
            "IDR recovery AUs: $($metrics.recoveryAccessUnitCount); " +
            "self-contained SPS/PPS/IDR AUs: " +
            "$($metrics.selfContainedRecoveryAccessUnitCount)") | Out-Null
        $lines.Add(
            "- Display: ``$($metrics.display)`` " +
            "($($metrics.displayDimensions)); dynamic window visible=" +
            "$($metrics.dynamicWindowVisible)") | Out-Null
        $lines.Add("- GPU: $($metrics.gpu)") | Out-Null
        $lines.Add("- FFmpeg: $($metrics.ffmpegVersion)") | Out-Null
    }

    $lines.Add("") | Out-Null
    $lines.Add("## Isolation and cleanup") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add(
        "- Cleanup verified: **$($Report.cleanup.verified)**") | Out-Null
    $lines.Add(
        "- Policy: $($Report.cleanup.policy)") | Out-Null
    $lines.Add(
        "- The raw stream, Xvfb socket, dynamic window, FFmpeg process and " +
        "run-specific WSL temporary directory are not retained.") | Out-Null

    if ($null -ne $Report.metrics -and
        -not [string]::IsNullOrWhiteSpace(
            [string]$Report.metrics.captureCommand)) {
        $lines.Add("") | Out-Null
        $lines.Add("## Capture command") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add('```text') | Out-Null
        $lines.Add(
            ([string]$Report.metrics.captureCommand).Replace('```', '')) |
            Out-Null
        $lines.Add('```') | Out-Null
    }

    if ($null -ne $Report.metrics -and
        -not [string]::IsNullOrWhiteSpace(
            [string]$Report.metrics.captureLogTail)) {
        $lines.Add("") | Out-Null
        $lines.Add("## FFmpeg log tail") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add('```text') | Out-Null
        $lines.Add(
            ([string]$Report.metrics.captureLogTail).Replace('```', '')) |
            Out-Null
        $lines.Add('```') | Out-Null
    }

    $lines.Add("") | Out-Null
    return ($lines -join [Environment]::NewLine)
}

function Write-ProbeEvidence {
    param(
        [pscustomobject]$Report,
        [string]$JsonPath,
        [string]$MarkdownPath
    )

    Write-Utf8NoBom `
        -Path $JsonPath `
        -Text ($Report | ConvertTo-Json -Depth 16)
    Write-Utf8NoBom `
        -Path $MarkdownPath `
        -Text (Convert-ReportToMarkdown -Report $Report)
}

$wsl = Get-Command "wsl.exe" -ErrorAction SilentlyContinue
$distributionNames = Get-WslDistributionNames -WslCommand $wsl
$distributionAvailable = @(
    $distributionNames |
        Where-Object {
            [string]::Equals(
                $_,
                $WslDistribution,
                [System.StringComparison]::OrdinalIgnoreCase)
        }).Count -gt 0

$configuration = [pscustomobject]@{
    wslDistribution = $WslDistribution
    distributionAvailable = $distributionAvailable
    width = $width
    height = $height
    requestedFps = $requestedFps
    minimumAcceptedFps = $minimumAcceptedFps
    durationSeconds = $DurationSeconds
    targetBitrateBps = $targetBitrateBps
    targetBitrateTolerancePercent = $targetBitrateTolerance * 100.0
    minimumAcceptedBitrateBps = $minimumAcceptedBitrateBps
    maximumAcceptedTargetBitrateBps = $maximumAcceptedTargetBitrateBps
    maximumBitrateBps = $maximumBitrateBps
    vbvBufferBytes = $vbvBufferBytes
    displayRange = "$DisplayRangeStart-$DisplayRangeEnd"
    outputDirectory = $OutputDirectory
    pipeline = (
        "Xvfb/testsrc2 -> x11grab -> format=nv12 -> hwupload_cuda -> " +
        "scale_cuda 3840x2160 passthrough=0 -> h264_nvenc GOP1")
}

if ($effectivePlanOnly) {
    [pscustomobject]@{
        mode = "PlanOnly"
        safeByDefault = $true
        executeSwitchRequired = $true
        wslCommandAvailable = $null -ne $wsl
        configuration = $configuration
        acceptance = [pscustomobject]@{
            minimumFrameCount = [int][Math]::Ceiling(
                $minimumAcceptedFps * $DurationSeconds)
            exactDimensions = "${width}x${height}"
            requiredEncoder = "h264_nvenc"
            targetBitrateBps = $targetBitrateBps
            acceptedTargetBitrateRangeBps = (
                "$minimumAcceptedBitrateBps-" +
                "$maximumAcceptedTargetBitrateBps")
            maximumBitrateBps = $maximumBitrateBps
            requireEveryAccessUnitToBeIdr = $true
            requireSpsPpsWithEveryRecoveryAccessUnit = $true
            requireDynamicWindowForWholeCapture = $true
        }
        isolation = [pscustomobject]@{
            displayReservation = (
                "Atomic mkdir lock plus unused X socket/X lock checks")
            processOwnership = (
                "Unique argv[0] tag and exact PID verification")
            cleanup = (
                "finally stops only owned exact PIDs and removes only the " +
                "validated run-specific /tmp directory")
            writesEvidence = $false
        }
    }
    return
}

if ($null -eq $wsl) {
    throw "wsl.exe is required to execute the Linux 4K60 probe."
}

if (-not $distributionAvailable) {
    throw (
        "WSL distribution '$WslDistribution' is not installed. Available: " +
        "$($distributionNames -join ', ')")
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$runId = [Guid]::NewGuid().ToString("N")
$stamp = [DateTimeOffset]::UtcNow.ToString("yyyyMMddTHHmmssZ")
$jsonPath = Join-Path (
    $OutputDirectory) "RemoteDesk-linux-4k60-$stamp-$runId.json"
$markdownPath = Join-Path (
    $OutputDirectory) "RemoteDesk-linux-4k60-$stamp-$runId.md"
$startedAtUtc = [DateTimeOffset]::UtcNow

$bashScript = @'
set -eu

run_id="$1"
duration="$2"
width="$3"
height="$4"
fps="$5"
target_bitrate="$6"
maximum_bitrate="$7"
vbv_buffer="$8"
display_start="$9"
display_end="${10}"

work_dir="/tmp/remotedesk-linux4k60-${run_id}"
output_path="${work_dir}/probe.h264"
capture_log="${work_dir}/capture.log"
progress_log="${work_dir}/progress.log"
probe_json="${work_dir}/ffprobe.json"
command_file="${work_dir}/capture-command.txt"
display_lock=""
display_number=""
display=""
xvfb_pid=""
motion_pid=""
capture_pid=""
xvfb_tag="remotedesk-linux4k60-${run_id}-xvfb"
motion_tag="remotedesk-linux4k60-${run_id}-motion"
capture_tag="remotedesk-linux4k60-${run_id}-capture"
window_title="RemoteDesk Linux 4K60 ${run_id}"
cleanup_done=0

pid_has_tag() {
  owned_pid="$1"
  owned_tag="$2"
  if [ -z "$owned_pid" ] || [ ! -r "/proc/${owned_pid}/cmdline" ]; then
    return 1
  fi
  tr '\000' '\n' < "/proc/${owned_pid}/cmdline" 2>/dev/null |
    grep -Fqx -- "$owned_tag"
}

stop_owned_pid() {
  owned_pid="$1"
  owned_tag="$2"
  if ! pid_has_tag "$owned_pid" "$owned_tag"; then
    return 0
  fi

  kill "$owned_pid" 2>/dev/null || true
  index=0
  while [ "$index" -lt 30 ] && pid_has_tag "$owned_pid" "$owned_tag"; do
    sleep 0.1
    index=$((index + 1))
  done
  if pid_has_tag "$owned_pid" "$owned_tag"; then
    kill -KILL "$owned_pid" 2>/dev/null || true
  fi
  wait "$owned_pid" 2>/dev/null || true
}

cleanup() {
  if [ "$cleanup_done" -eq 1 ]; then
    return
  fi
  cleanup_done=1
  set +e

  stop_owned_pid "$capture_pid" "$capture_tag"
  stop_owned_pid "$motion_pid" "$motion_tag"
  stop_owned_pid "$xvfb_pid" "$xvfb_tag"

  if [ -n "$display_lock" ] &&
     [ -d "$display_lock" ] &&
     [ -f "${display_lock}/owner" ] &&
     [ "$(cat "${display_lock}/owner" 2>/dev/null)" = "$run_id" ]; then
    rm -f -- "${display_lock}/owner"
    rmdir -- "$display_lock" 2>/dev/null || true
  fi

  case "$work_dir" in
    /tmp/remotedesk-linux4k60-"$run_id")
      rm -rf -- "$work_dir"
      ;;
    *)
      printf 'Refusing to remove unexpected work directory: %s\n' \
        "$work_dir" >&2
      ;;
  esac
}

trap cleanup EXIT
trap 'cleanup; exit 130' INT
trap 'cleanup; exit 143' TERM

for required_command in \
  Xvfb ffplay ffmpeg ffprobe xdpyinfo xwininfo python3 nvidia-smi; do
  if ! command -v "$required_command" >/dev/null 2>&1; then
    printf 'Missing required command: %s\n' "$required_command" >&2
    exit 20
  fi
done

if ! ffmpeg -hide_banner -encoders 2>/dev/null |
     grep -Eq '[[:space:]]h264_nvenc[[:space:]]'; then
  printf 'FFmpeg does not expose h264_nvenc.\n' >&2
  exit 21
fi
if ! ffmpeg -hide_banner -filters 2>/dev/null |
     grep -Eq '[[:space:]]hwupload_cuda[[:space:]]'; then
  printf 'FFmpeg does not expose hwupload_cuda.\n' >&2
  exit 22
fi
if ! ffmpeg -hide_banner -filters 2>/dev/null |
     grep -Eq '[[:space:]]scale_cuda[[:space:]]'; then
  printf 'FFmpeg does not expose scale_cuda.\n' >&2
  exit 23
fi
if ! nvidia-smi -L >/dev/null 2>&1; then
  printf 'nvidia-smi cannot access an NVIDIA GPU.\n' >&2
  exit 24
fi

mkdir -m 700 -- "$work_dir"

candidate="$display_start"
while [ "$candidate" -le "$display_end" ]; do
  candidate_lock="/tmp/remotedesk-linux4k60-display-${candidate}.lock"
  if mkdir -m 700 -- "$candidate_lock" 2>/dev/null; then
    printf '%s' "$run_id" > "${candidate_lock}/owner"
    if [ ! -e "/tmp/.X${candidate}-lock" ] &&
       [ ! -S "/tmp/.X11-unix/X${candidate}" ] &&
       ! DISPLAY=":${candidate}" xdpyinfo >/dev/null 2>&1; then
      display_number="$candidate"
      display=":${candidate}"
      display_lock="$candidate_lock"
      break
    fi
    rm -f -- "${candidate_lock}/owner"
    rmdir -- "$candidate_lock" 2>/dev/null || true
  fi
  candidate=$((candidate + 1))
done

if [ -z "$display" ]; then
  printf 'No exclusive X display is available in range %s-%s.\n' \
    "$display_start" "$display_end" >&2
  exit 25
fi

xvfb_bin="$(command -v Xvfb)"
ffplay_bin="$(command -v ffplay)"
ffmpeg_bin="$(command -v ffmpeg)"
ffprobe_bin="$(command -v ffprobe)"

bash -c 'exec -a "$1" "$2" "${@:3}"' \
  _ "$xvfb_tag" "$xvfb_bin" "$display" \
  -screen 0 "${width}x${height}x24" -nolisten tcp \
  >"${work_dir}/xvfb.log" 2>&1 &
xvfb_pid="$!"

display_ready=0
index=0
while [ "$index" -lt 100 ]; do
  if ! pid_has_tag "$xvfb_pid" "$xvfb_tag"; then
    break
  fi
  if DISPLAY="$display" xdpyinfo >/dev/null 2>&1; then
    display_ready=1
    break
  fi
  sleep 0.1
  index=$((index + 1))
done
if [ "$display_ready" -ne 1 ]; then
  printf 'Owned Xvfb did not become ready on %s.\n' "$display" >&2
  exit 26
fi

motion_duration=$((duration + 15))
DISPLAY="$display" SDL_VIDEODRIVER=x11 SDL_AUDIODRIVER=dummy \
  bash -c 'exec -a "$1" "$2" "${@:3}"' \
    _ "$motion_tag" "$ffplay_bin" \
    -hide_banner -loglevel error -nostats \
    -f lavfi \
    -i "testsrc2=size=${width}x${height}:rate=${fps}" \
    -an -sn -noborder -left 0 -top 0 -x "$width" -y "$height" \
    -window_title "$window_title" \
    -t "$motion_duration" -autoexit \
    >"${work_dir}/motion.log" 2>&1 &
motion_pid="$!"

dynamic_window_visible=0
index=0
while [ "$index" -lt 100 ]; do
  if ! pid_has_tag "$motion_pid" "$motion_tag"; then
    break
  fi
  if DISPLAY="$display" xwininfo -root -tree 2>/dev/null |
     grep -Fq -- "$window_title"; then
    dynamic_window_visible=1
    break
  fi
  sleep 0.1
  index=$((index + 1))
done
if [ "$dynamic_window_visible" -ne 1 ]; then
  printf 'Owned dynamic testsrc2 window did not become visible.\n' >&2
  exit 27
fi

filter_graph="format=nv12,hwupload_cuda,scale_cuda=w=${width}:h=${height}:format=nv12:passthrough=0"
capture_arguments=(
  -hide_banner
  -loglevel info
  -nostats
  -y
  -fflags nobuffer
  -flags low_delay
  -f x11grab
  -draw_mouse 0
  -video_size "${width}x${height}"
  -framerate "$fps"
  -thread_queue_size 1
  -i "${display}+0,0"
  -vf "$filter_graph"
  -c:v h264_nvenc
  -preset p1
  -tune ull
  -rc cbr
  -b:v "$target_bitrate"
  -maxrate "$target_bitrate"
  -bufsize "$vbv_buffer"
  -g 1
  -bf 0
  -rc-lookahead 0
  -delay 0
  -zerolatency 1
  -forced-idr 1
  -aud 1
  -an
  -sn
  -dn
  -fps_mode passthrough
  -bsf:v "dump_extra=freq=keyframe,h264_metadata=aud=insert"
  -flush_packets 1
  -t "$duration"
  -progress "$progress_log"
  -f h264
  "$output_path"
)

{
  printf '%q ' "$ffmpeg_bin"
  printf '%q ' "${capture_arguments[@]}"
  printf '\n'
} > "$command_file"

capture_started_ns="$(date +%s%N)"
DISPLAY="$display" \
  bash -c 'exec -a "$1" "$2" "${@:3}"' \
    _ "$capture_tag" "$ffmpeg_bin" "${capture_arguments[@]}" \
    >"${work_dir}/capture.stdout" 2>"$capture_log" &
capture_pid="$!"

set +e
wait "$capture_pid"
capture_exit_code="$?"
set -e
capture_completed_ns="$(date +%s%N)"
capture_wall_seconds="$(
  python3 -c \
    'import sys; print(max(0.000001, (int(sys.argv[2])-int(sys.argv[1]))/1e9))' \
    "$capture_started_ns" "$capture_completed_ns"
)"

dynamic_source_alive=0
if pid_has_tag "$motion_pid" "$motion_tag"; then
  dynamic_source_alive=1
fi

display_dimensions="$(
  DISPLAY="$display" xdpyinfo 2>/dev/null |
    sed -n 's/^[[:space:]]*dimensions:[[:space:]]*\([^[:space:]]*\).*/\1/p' |
    head -n 1
)"
gpu="$(
  nvidia-smi --query-gpu=name,driver_version \
    --format=csv,noheader 2>/dev/null |
    head -n 1
)"
ffmpeg_version="$(ffmpeg -hide_banner -version 2>/dev/null | head -n 1)"

ffprobe_exit_code=1
printf '{"streams":[]}\n' > "$probe_json"
if [ -s "$output_path" ]; then
  set +e
  "$ffprobe_bin" \
    -v error \
    -count_frames \
    -select_streams v:0 \
    -show_entries stream=codec_name,width,height,pix_fmt,nb_read_frames \
    -of json \
    "$output_path" > "$probe_json" 2>"${work_dir}/ffprobe.log"
  ffprobe_exit_code="$?"
  set -e
fi

metrics_json="$(
  python3 - \
    "$output_path" \
    "$probe_json" \
    "$progress_log" \
    "$capture_log" \
    "$command_file" \
    "$duration" \
    "$capture_wall_seconds" \
    "$capture_exit_code" \
    "$ffprobe_exit_code" \
    "$display" \
    "$display_dimensions" \
    "$dynamic_window_visible" \
    "$dynamic_source_alive" \
    "$gpu" \
    "$ffmpeg_version" \
    "$maximum_bitrate" \
    "$target_bitrate" <<'PY'
import json
import mmap
import os
import shlex
import sys

(
    output_path,
    probe_path,
    progress_path,
    capture_log_path,
    command_path,
    requested_duration_text,
    wall_seconds_text,
    capture_exit_text,
    ffprobe_exit_text,
    display,
    display_dimensions,
    dynamic_window_visible_text,
    dynamic_source_alive_text,
    gpu,
    ffmpeg_version,
    maximum_bitrate_text,
    target_bitrate_text,
) = sys.argv[1:]

requested_duration = float(requested_duration_text)
wall_seconds = max(0.000001, float(wall_seconds_text))
capture_exit_code = int(capture_exit_text)
ffprobe_exit_code = int(ffprobe_exit_text)
maximum_bitrate = int(maximum_bitrate_text)
target_bitrate = int(target_bitrate_text)

def read_text(path, limit=None):
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as stream:
            if limit is None:
                return stream.read()
            lines = stream.readlines()
            return "".join(lines[-limit:])
    except OSError:
        return ""

try:
    with open(probe_path, "r", encoding="utf-8") as stream:
        probe = json.load(stream)
except (OSError, ValueError):
    probe = {"streams": []}
stream = (probe.get("streams") or [{}])[0]

progress = {}
for line in read_text(progress_path).splitlines():
    if "=" in line:
        key, value = line.split("=", 1)
        progress[key.strip()] = value.strip()

def parse_progress_time(value):
    try:
        hours, minutes, seconds = value.split(":")
        return int(hours) * 3600 + int(minutes) * 60 + float(seconds)
    except (AttributeError, TypeError, ValueError):
        return 0.0

content_seconds = parse_progress_time(progress.get("out_time", ""))
if content_seconds <= 0:
    content_seconds = requested_duration

access_unit_count = 0
recovery_access_unit_count = 0
self_contained_recovery_access_unit_count = 0
aud_count = 0
idr_nal_count = 0
sps_nal_count = 0
pps_nal_count = 0
all_access_units_recovery = True
all_recovery_access_units_self_contained = True

def find_start_code(mapped, offset):
    start4 = mapped.find(b"\x00\x00\x00\x01", offset)
    start3 = mapped.find(b"\x00\x00\x01", offset)
    if start4 < 0:
        return (start3, 3) if start3 >= 0 else (-1, 0)
    if start3 < 0 or start4 <= start3:
        return start4, 4
    return start3, 3

def analyze_annex_b(path):
    global access_unit_count
    global recovery_access_unit_count
    global self_contained_recovery_access_unit_count
    global aud_count
    global idr_nal_count
    global sps_nal_count
    global pps_nal_count
    global all_access_units_recovery
    global all_recovery_access_units_self_contained

    try:
        size = os.path.getsize(path)
    except OSError:
        return
    if size <= 0:
        return

    current_types = []
    prefix_types = []

    def finish_access_unit():
        global access_unit_count
        global recovery_access_unit_count
        global self_contained_recovery_access_unit_count
        global all_access_units_recovery
        global all_recovery_access_units_self_contained
        if not current_types or not any(
            nal_type in (1, 2, 3, 4, 5) for nal_type in current_types
        ):
            return
        access_unit_count += 1
        recovery = 5 in current_types
        self_contained = recovery and 7 in current_types and 8 in current_types
        if recovery:
            recovery_access_unit_count += 1
            if self_contained:
                self_contained_recovery_access_unit_count += 1
            else:
                all_recovery_access_units_self_contained = False
        else:
            all_access_units_recovery = False

    with open(path, "rb") as stream_file:
        with mmap.mmap(stream_file.fileno(), 0, access=mmap.ACCESS_READ) as mapped:
            start, prefix_length = find_start_code(mapped, 0)
            while start >= 0:
                header = start + prefix_length
                next_start, next_prefix_length = find_start_code(
                    mapped,
                    header + 1,
                )
                if header < len(mapped):
                    nal_type = mapped[header] & 0x1F
                    if nal_type == 9:
                        aud_count += 1
                        finish_access_unit()
                        current_types.clear()
                        if prefix_types:
                            current_types.extend(prefix_types)
                            prefix_types.clear()
                        current_types.append(nal_type)
                    else:
                        if nal_type == 5:
                            idr_nal_count += 1
                        elif nal_type == 7:
                            sps_nal_count += 1
                        elif nal_type == 8:
                            pps_nal_count += 1
                        if current_types:
                            current_types.append(nal_type)
                        else:
                            prefix_types.append(nal_type)
                if next_start < 0:
                    break
                start, prefix_length = next_start, next_prefix_length
            if not current_types and prefix_types:
                current_types.extend(prefix_types)
            finish_access_unit()

analyze_annex_b(output_path)

try:
    frame_count = int(stream.get("nb_read_frames") or 0)
except (TypeError, ValueError):
    frame_count = 0
if frame_count <= 0:
    frame_count = access_unit_count

try:
    output_bytes = os.path.getsize(output_path)
except OSError:
    output_bytes = 0

capture_log = read_text(capture_log_path)
capture_command = read_text(command_path).strip()
try:
    capture_command_arguments = shlex.split(capture_command)
except ValueError:
    capture_command_arguments = []

def read_integer_option(name):
    try:
        return int(capture_command_arguments[
            capture_command_arguments.index(name) + 1
        ])
    except (ValueError, IndexError):
        return 0

configured_target_bitrate = read_integer_option("-b:v")
configured_maxrate = read_integer_option("-maxrate")
megabits_per_second = (
    output_bytes * 8.0 / max(0.000001, content_seconds) / 1_000_000.0
)

metrics = {
    "captureExitCode": capture_exit_code,
    "ffprobeExitCode": ffprobe_exit_code,
    "frameCount": frame_count,
    "contentSeconds": content_seconds,
    "wallSeconds": wall_seconds,
    "cadenceFps": frame_count / max(0.000001, content_seconds),
    "realtimeFps": frame_count / wall_seconds,
    "outputBytes": output_bytes,
    "megabitsPerSecond": megabits_per_second,
    "targetBitrateBps": target_bitrate,
    "maximumBitrateBps": maximum_bitrate,
    "configuredTargetBitrateBps": configured_target_bitrate,
    "configuredMaxrateBps": configured_maxrate,
    "targetBitrateConfigured": (
        configured_target_bitrate == target_bitrate and
        configured_maxrate == target_bitrate
    ),
    "codecName": str(stream.get("codec_name") or ""),
    "width": int(stream.get("width") or 0),
    "height": int(stream.get("height") or 0),
    "pixelFormat": str(stream.get("pix_fmt") or ""),
    "encoderLogConfirmed": "h264_nvenc" in capture_log.lower(),
    "cudaFilterConfigured": (
        "hwupload_cuda" in capture_command and
        "scale_cuda=" in capture_command and
        "passthrough=0" in capture_command
    ),
    "accessUnitCount": access_unit_count,
    "recoveryAccessUnitCount": recovery_access_unit_count,
    "selfContainedRecoveryAccessUnitCount": (
        self_contained_recovery_access_unit_count
    ),
    "audCount": aud_count,
    "idrNalCount": idr_nal_count,
    "spsNalCount": sps_nal_count,
    "ppsNalCount": pps_nal_count,
    "allAccessUnitsRecovery": (
        access_unit_count > 0 and all_access_units_recovery
    ),
    "allRecoveryAccessUnitsSelfContained": (
        recovery_access_unit_count > 0 and
        all_recovery_access_units_self_contained
    ),
    "display": display,
    "displayDimensions": display_dimensions,
    "dynamicWindowVisible": dynamic_window_visible_text == "1",
    "dynamicSourceAliveAfterCapture": dynamic_source_alive_text == "1",
    "gpu": gpu,
    "ffmpegVersion": ffmpeg_version,
    "captureCommand": capture_command,
    "captureLogTail": read_text(capture_log_path, 80),
}
print(json.dumps(metrics, separators=(",", ":")))
PY
)"

owned_pids_json="$(
  printf '{"xvfb":%s,"dynamicWindow":%s,"capture":%s}' \
    "${xvfb_pid:-0}" "${motion_pid:-0}" "${capture_pid:-0}"
)"
allocated_display="$display"
owned_display_lock="$display_lock"

cleanup
trap - EXIT INT TERM

cleanup_verified=1
if pid_has_tag "$capture_pid" "$capture_tag" ||
   pid_has_tag "$motion_pid" "$motion_tag" ||
   pid_has_tag "$xvfb_pid" "$xvfb_tag"; then
  cleanup_verified=0
fi
if [ -n "$owned_display_lock" ] && [ -e "$owned_display_lock" ]; then
  cleanup_verified=0
fi
if [ -e "$work_dir" ]; then
  cleanup_verified=0
fi

final_json="$(
  METRICS_JSON="$metrics_json" \
  OWNED_PIDS_JSON="$owned_pids_json" \
  CLEANUP_VERIFIED="$cleanup_verified" \
  ALLOCATED_DISPLAY="$allocated_display" \
  python3 - <<'PY'
import json
import os

print(json.dumps({
    "metrics": json.loads(os.environ["METRICS_JSON"]),
    "ownedPids": json.loads(os.environ["OWNED_PIDS_JSON"]),
    "allocatedDisplay": os.environ["ALLOCATED_DISPLAY"],
    "cleanupVerified": os.environ["CLEANUP_VERIFIED"] == "1",
}, separators=(",", ":")))
PY
)"
printf 'REMOTEDESK_LINUX4K60_RESULT=%s\n' "$final_json"
'@

$previousPreference = $ErrorActionPreference
try {
    $ErrorActionPreference = "Continue"
    $executionOutput = @(
        $bashScript |
            & $wsl.Source `
                -d $WslDistribution `
                -- `
                bash -s -- `
                $runId `
                $DurationSeconds `
                $width `
                $height `
                ([string][int]$requestedFps) `
                $targetBitrateBps `
                $maximumBitrateBps `
                $vbvBufferBytes `
                $DisplayRangeStart `
                $DisplayRangeEnd `
                2>&1 |
            ForEach-Object { [string]$_ })
    $wslExitCode = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $previousPreference
}

$resultLine = @(
    $executionOutput |
        Where-Object { $_.StartsWith($resultPrefix) } |
        Select-Object -Last 1)
$completedAtUtc = [DateTimeOffset]::UtcNow

if ($resultLine.Count -ne 1) {
    $detail = (
        @($executionOutput | Select-Object -Last 40) -join
        [Environment]::NewLine)
    $failureGate = New-Gate `
        -Name "WSL probe orchestration" `
        -Passed $false `
        -Actual "exit=$wslExitCode; no result payload" `
        -Expected "exit=0 and a structured result payload" `
        -Detail $detail
    $failureReport = [pscustomobject]@{
        schemaVersion = 1
        status = "Failed"
        runId = $runId
        startedAtUtc = $startedAtUtc.ToString("O")
        completedAtUtc = $completedAtUtc.ToString("O")
        configuration = $configuration
        metrics = $null
        gates = @($failureGate)
        cleanup = [pscustomobject]@{
            verified = $false
            policy = (
                "Bash finally cleanup uses unique argv[0] tags and exact PIDs; " +
                "no broad process matcher is used.")
        }
        evidence = [pscustomobject]@{
            json = $jsonPath
            markdown = $markdownPath
        }
    }
    Write-ProbeEvidence `
        -Report $failureReport `
        -JsonPath $jsonPath `
        -MarkdownPath $markdownPath
    throw (
        "Linux 4K60 probe setup failed before producing metrics. " +
        "Evidence: $jsonPath")
}

try {
    $nativeResult = $resultLine[0].Substring(
        $resultPrefix.Length) | ConvertFrom-Json
}
catch {
    throw "Linux 4K60 probe returned invalid JSON: $($_.Exception.Message)"
}

$metrics = $nativeResult.metrics
$minimumFrameCount = [int][Math]::Ceiling(
    $minimumAcceptedFps * $DurationSeconds)
$gates = New-Object System.Collections.Generic.List[object]
$gates.Add((New-Gate `
    -Name "Capture process" `
    -Passed (
        $wslExitCode -eq 0 -and
        [int]$metrics.captureExitCode -eq 0 -and
        [int]$metrics.ffprobeExitCode -eq 0) `
    -Actual (
        "wsl=$wslExitCode, ffmpeg=$($metrics.captureExitCode), " +
        "ffprobe=$($metrics.ffprobeExitCode)") `
    -Expected "all exit codes are 0" `
    -Detail "The fixed-duration output and its stream metadata parsed cleanly.")) |
    Out-Null
$gates.Add((New-Gate `
    -Name "True encoded cadence" `
    -Passed (
        [int]$metrics.frameCount -ge $minimumFrameCount -and
        [double]$metrics.cadenceFps -ge $minimumAcceptedFps -and
        [double]$metrics.realtimeFps -ge $minimumAcceptedFps) `
    -Actual (
        "$($metrics.frameCount) frames; " +
        "$([Math]::Round([double]$metrics.cadenceFps, 3)) encoded fps; " +
        "$([Math]::Round([double]$metrics.realtimeFps, 3)) realtime fps") `
    -Expected (
        "at least $minimumFrameCount frames and both rates >= " +
        "$minimumAcceptedFps fps") `
    -Detail (
        "The gate uses counted H.264 frames, encoded duration and wall time; " +
        "it does not trust the requested -framerate value alone."))) | Out-Null
$gates.Add((New-Gate `
    -Name "Native 4K dimensions" `
    -Passed (
        [int]$metrics.width -eq $width -and
        [int]$metrics.height -eq $height -and
        [string]$metrics.displayDimensions -eq "${width}x${height}") `
    -Actual (
        "stream=$($metrics.width)x$($metrics.height), " +
        "Xvfb=$($metrics.displayDimensions)") `
    -Expected "${width}x${height} for both source display and encoded stream" `
    -Detail "ffprobe supplies stream dimensions; xdpyinfo supplies X geometry.")) |
    Out-Null
$gates.Add((New-Gate `
    -Name "NVENC CUDA pipeline" `
    -Passed (
        [string]$metrics.codecName -eq "h264" -and
        [bool]$metrics.encoderLogConfirmed -and
        [bool]$metrics.cudaFilterConfigured) `
    -Actual (
        "codec=$($metrics.codecName), nvencLog=" +
        "$($metrics.encoderLogConfirmed), cudaFilters=" +
        "$($metrics.cudaFilterConfigured)") `
    -Expected (
        "H.264 through h264_nvenc with hwupload_cuda and active scale_cuda") `
    -Detail (
        "scale_cuda is forced with passthrough=0 even though source and output " +
        "are both 4K."))) | Out-Null
$gates.Add((New-Gate `
    -Name "Configured 4K60 bitrate target" `
    -Passed (
        [bool]$metrics.targetBitrateConfigured -and
        [int64]$metrics.targetBitrateBps -eq $targetBitrateBps -and
        [int64]$metrics.configuredTargetBitrateBps -eq $targetBitrateBps -and
        [int64]$metrics.configuredMaxrateBps -eq $targetBitrateBps -and
        $targetBitrateBps -le $maximumBitrateBps) `
    -Actual (
        "target=$($metrics.configuredTargetBitrateBps), " +
        "maxrate=$($metrics.configuredMaxrateBps), " +
        "hardCap=$maximumBitrateBps bps") `
    -Expected (
        "target and maxrate exactly $targetBitrateBps bps; hard cap remains " +
        "$maximumBitrateBps bps") `
    -Detail (
        "The command is parsed rather than accepting any stream merely below " +
        "the broad 160 Mbps ceiling."))) | Out-Null
$measuredBitrateBps = [double]$metrics.megabitsPerSecond * 1000000.0
$gates.Add((New-Gate `
    -Name "Measured 4K60 bitrate envelope" `
    -Passed (
        $measuredBitrateBps -ge $minimumAcceptedBitrateBps -and
        $measuredBitrateBps -le $maximumAcceptedTargetBitrateBps -and
        $measuredBitrateBps -le $maximumBitrateBps) `
    -Actual (
        "$([Math]::Round([double]$metrics.megabitsPerSecond, 3)) Mbps") `
    -Expected (
        "$([Math]::Round($minimumAcceptedBitrateBps / 1000000.0, 3))-" +
        "$([Math]::Round($maximumAcceptedTargetBitrateBps / 1000000.0, 3)) " +
        "Mbps around the 79.626 Mbps target, and <=160 Mbps") `
    -Detail (
        "Measured from exact Annex-B bytes and encoded duration with a strict " +
        "plus/minus 15 percent target envelope."))) | Out-Null
$gates.Add((New-Gate `
    -Name "Recoverable access units" `
    -Passed (
        [int]$metrics.accessUnitCount -ge $minimumFrameCount -and
        [int]$metrics.recoveryAccessUnitCount -eq
            [int]$metrics.accessUnitCount -and
        [int]$metrics.selfContainedRecoveryAccessUnitCount -eq
            [int]$metrics.accessUnitCount -and
        [bool]$metrics.allAccessUnitsRecovery -and
        [bool]$metrics.allRecoveryAccessUnitsSelfContained) `
    -Actual (
        "AU=$($metrics.accessUnitCount), IDR=" +
        "$($metrics.recoveryAccessUnitCount), SPS/PPS/IDR=" +
        "$($metrics.selfContainedRecoveryAccessUnitCount)") `
    -Expected (
        "every counted AU is IDR and carries SPS/PPS; AU count >= " +
        "$minimumFrameCount") `
    -Detail "The raw Annex-B NAL stream is parsed before it is deleted.")) |
    Out-Null
$gates.Add((New-Gate `
    -Name "Dynamic 4K source" `
    -Passed (
        [bool]$metrics.dynamicWindowVisible -and
        [bool]$metrics.dynamicSourceAliveAfterCapture) `
    -Actual (
        "windowVisible=$($metrics.dynamicWindowVisible), aliveAfterCapture=" +
        "$($metrics.dynamicSourceAliveAfterCapture)") `
    -Expected "the owned testsrc2 window remains visible for the whole capture" `
    -Detail (
        "The source is a unique, full-size ffplay testsrc2 X11 window, not a " +
        "static root background."))) | Out-Null
$gates.Add((New-Gate `
    -Name "Exclusive cleanup" `
    -Passed ([bool]$nativeResult.cleanupVerified) `
    -Actual ([bool]$nativeResult.cleanupVerified) `
    -Expected "True" `
    -Detail (
        "Only exact PIDs with this run's argv[0] tags are signalled; the owned " +
        "display lock and validated /tmp directory must be absent."))) | Out-Null

# PowerShell 7 can fail with "Argument types do not match" when @() asks its
# pipeline binder to enumerate a generic List[object].  Materialize through
# List<T>.ToArray(), which is also available on Windows PowerShell 5.1, before
# filtering or attaching the gates to the report.
$gateArray = [object[]]$gates.ToArray()
$passed = @($gateArray | Where-Object { -not $_.passed }).Count -eq 0
$report = [pscustomobject]@{
    schemaVersion = 1
    status = if ($passed) { "Passed" } else { "Failed" }
    runId = $runId
    startedAtUtc = $startedAtUtc.ToString("O")
    completedAtUtc = $completedAtUtc.ToString("O")
    configuration = $configuration
    environment = [pscustomobject]@{
        gpu = $metrics.gpu
        ffmpegVersion = $metrics.ffmpegVersion
        allocatedDisplay = $nativeResult.allocatedDisplay
    }
    metrics = $metrics
    gates = $gateArray
    cleanup = [pscustomobject]@{
        verified = [bool]$nativeResult.cleanupVerified
        ownedPids = $nativeResult.ownedPids
        policy = (
            "Unique argv[0] ownership tags plus exact PIDs; no global or broad " +
            "process-name matching. Only the atomically owned display " +
            "lock and run-specific /tmp directory are removed.")
    }
    evidence = [pscustomobject]@{
        json = $jsonPath
        markdown = $markdownPath
        rawH264Retained = $false
    }
}

Write-ProbeEvidence `
    -Report $report `
    -JsonPath $jsonPath `
    -MarkdownPath $markdownPath

$report
if (-not $passed) {
    throw (
        "Linux 4K60 probe failed one or more strict gates. " +
        "Evidence: $jsonPath")
}
