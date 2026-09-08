[CmdletBinding()]
param(
    [string]$Distro = "Ubuntu-24.04",
    [ValidateRange(1, 255)]
    [int]$DisplayNumber = 99,
    [ValidateRange(5900, 5999)]
    [int]$VncPort = 5909,
    [ValidateRange(640, 7680)]
    [int]$Width = 1280,
    [ValidateRange(480, 4320)]
    [int]$Height = 720,
    [switch]$InstallDependencies
)

$ErrorActionPreference = "Stop"

function Invoke-WslScript {
    param(
        [string]$Script,
        [string[]]$Arguments = @(),
        [switch]$AsRoot
    )

    $command = @("-d", $Distro)
    if ($AsRoot) {
        $command += @("-u", "root")
    }

    $command += @("--", "bash", "-s", "--")
    $command += $Arguments
    $Script | & wsl.exe @command
    if ($LASTEXITCODE -ne 0) {
        throw "WSL command failed with exit code $LASTEXITCODE."
    }
}

if ($InstallDependencies) {
    $installScript = @'
set -euo pipefail
apt-get update
DEBIAN_FRONTEND=noninteractive apt-get install -y \
  xvfb x11vnc xdotool xclip xsel wmctrl imagemagick xterm openbox \
  dbus-x11 tigervnc-viewer freerdp2-x11 python3 python3-cryptography
'@
    Invoke-WslScript -Script $installScript -AsRoot
}

$startScript = @'
set -euo pipefail
display_number="$1"
vnc_port="$2"
width="$3"
height="$4"
display=":${display_number}"
export DISPLAY="$display"

if ! command -v Xvfb >/dev/null ||
   ! command -v x11vnc >/dev/null ||
   ! command -v xdotool >/dev/null ||
   ! command -v xclip >/dev/null ||
   ! command -v import >/dev/null ||
   ! command -v openbox >/dev/null ||
   ! command -v python3 >/dev/null ||
   ! python3 -c 'import cryptography' >/dev/null 2>&1; then
  echo "Missing Linux desktop test dependencies. Re-run with -InstallDependencies." >&2
  exit 2
fi

if ! pgrep -f "Xvfb ${display} " >/dev/null; then
  nohup Xvfb "$display" -screen 0 "${width}x${height}x24" >/tmp/remotedesk-xvfb.log 2>&1 &
  sleep 1
fi

if ! xdpyinfo -display "$display" >/dev/null 2>&1; then
  echo "Xvfb display $display is not ready." >&2
  cat /tmp/remotedesk-xvfb.log >&2 || true
  exit 3
fi

if ! DISPLAY="$display" pgrep -f "openbox" >/dev/null; then
  DISPLAY="$display" nohup openbox >/tmp/remotedesk-openbox.log 2>&1 &
  sleep 1
fi

if ! DISPLAY="$display" pgrep -f "xterm.*RemoteDesk Linux test desktop" >/dev/null; then
  DISPLAY="$display" nohup xterm \
    -geometry 100x28 \
    -T "RemoteDesk Linux test desktop" \
    -e bash -lc 'printf "RemoteDesk Linux test desktop\nX11 capture/input/clipboard sandbox ready\n"; sleep 86400' \
    >/tmp/remotedesk-xterm.log 2>&1 &
  sleep 1
fi

if ! pgrep -f "x11vnc.*${display}.*${vnc_port}" >/dev/null; then
  env -u WAYLAND_DISPLAY XDG_SESSION_TYPE=x11 x11vnc \
    -display "$display" \
    -localhost \
    -rfbport "$vnc_port" \
    -forever \
    -shared \
    -nopw \
    -bg \
    -o /tmp/remotedesk-x11vnc.log \
    >/tmp/remotedesk-x11vnc-start.log 2>&1
  sleep 1
fi

DISPLAY="$display" import -window root /tmp/remotedesk-xvfb.png
image_info="$(identify /tmp/remotedesk-xvfb.png)"
vnc_status="$(ss -ltnp | grep ":${vnc_port}" || true)"
if [ -z "$vnc_status" ]; then
  echo "x11vnc is not listening on ${vnc_port}." >&2
  cat /tmp/remotedesk-x11vnc-start.log /tmp/remotedesk-x11vnc.log >&2 || true
  exit 4
fi

distro="$(. /etc/os-release && printf "%s %s" "$NAME" "$VERSION_ID")"
processes="$(pgrep -af 'Xvfb|openbox|x11vnc|xterm' || true)"
printf "Linux sandbox ready\n"
printf "Distro: %s\n" "$distro"
printf "Display: %s\n" "$display"
printf "Resolution: %sx%s\n" "$width" "$height"
printf "VNC: 127.0.0.1:%s\n" "$vnc_port"
printf "Screenshot: %s\n" "$image_info"
printf "Processes:\n%s\n" "$processes"
'@

Invoke-WslScript `
    -Script $startScript `
    -Arguments @(
        $DisplayNumber.ToString(),
        $VncPort.ToString(),
        $Width.ToString(),
        $Height.ToString())

Write-Host ""
Write-Host "Windows can test the sandbox VNC endpoint at 127.0.0.1:$VncPort." -ForegroundColor Green
