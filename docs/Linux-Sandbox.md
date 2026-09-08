# RemoteDesk Linux Sandbox

This sandbox is a WSL-based desktop test fixture for RemoteDesk Linux host/viewer work. It gives us a repeatable X11 desktop with screen capture, input, clipboard, VNC, RDP-client, and ffmpeg tooling so the Linux package can be built and tested against stable local evidence before trying it on a physical Ubuntu machine.

## Start

Install the WSL desktop test dependencies once:

```powershell
.\scripts\Start-RemoteDeskLinuxSandbox.ps1 -InstallDependencies
```

Start or reuse the default sandbox:

```powershell
.\scripts\Start-RemoteDeskLinuxSandbox.ps1
```

Defaults:

- WSL distro: `Ubuntu-24.04`
- X11 display: `:99`
- Virtual screen: `1280x720x24`
- VNC endpoint from Windows: `127.0.0.1:5909`

## Verify

Run the normal environment check with Linux sandbox verification:

```powershell
.\scripts\Invoke-RemoteDeskCheck.ps1 -SkipAndroid -LinuxSandboxStatus
```

Or start the sandbox as part of the check:

```powershell
.\scripts\Invoke-RemoteDeskCheck.ps1 -SkipAndroid -StartLinuxSandbox -LinuxSandboxStatus
```

For a real Windows target, include the RemoteDesk password to prove authentication instead of only the TCP magic handshake:

```powershell
.\scripts\Invoke-RemoteDeskCheck.ps1 -SkipAndroid -Target <被控机IP> -PromptForPassword -LinuxSandboxStatus
```

To prove the encrypted RemoteDesk protocol from Linux/WSL, add `-LinuxProtocolProbe`. The check runs `scripts/linux/remotedesk_protocol_probe.py` in the requested WSL distribution when it is installed, or automatically falls back to native Windows Python. It authenticates to the Windows host, sends viewer codec capability, decodes device/capture-target control messages, and receives the first frame; the result reports the runner that was used:

```powershell
.\scripts\Invoke-RemoteDeskCheck.ps1 -SkipAndroid -Target <被控机IP> -PromptForPassword -LinuxProtocolProbe
```

To also prove Linux-to-Windows file transfer over the same encrypted protocol, add `-LinuxProtocolProbeSendFile`. This creates a temporary local text file, sends it from the selected runner to the Windows host, waits for the host save confirmation, and then deletes the temporary local probe file. If the target advertises `FileChecksum`, the probe sends a SHA-256 checksum before `FileTransferComplete`.

```powershell
.\scripts\Invoke-RemoteDeskCheck.ps1 -SkipAndroid -Target <被控机IP> -PromptForPassword -LinuxProtocolProbe -LinuxProtocolProbeSendFile
```

To prove the reverse Windows-to-Linux file-return path, copy one or more files on the Windows host first, then run `-LinuxProtocolProbePullRemoteFiles`. The check asks the host to return its current file clipboard over the encrypted protocol and saves the response into a temporary receive directory for the duration of the probe. If the target advertises `FileChecksum`, the probe announces viewer checksum support and requires returned SHA-256 values before accepting files.

```powershell
.\scripts\Invoke-RemoteDeskCheck.ps1 -SkipAndroid -Target <被控机IP> -PromptForPassword -LinuxProtocolProbe -LinuxProtocolProbePullRemoteFiles
```

If the target does not advertise the `FileSend` capability, the probe reports the pull path as skipped instead of sending a request that older hosts cannot decode.

If the target advertises `FileTransferCancel`, the probe can decode incoming cancel messages and abort the active temporary receive file. When the probe sends a file to Windows, it also records the source length and modification time, sends only the declared byte range, and emits `FileTransferCancel` if the local source changes or fails after the transfer has started.

## Linux host package

The current canonical release uses Desktop scope. The publisher's Linux tests,
runtime build, and all three Linux package writers use the same WSL distribution;
`-LinuxDistro` defaults to `Ubuntu-24.04` and is recorded in the release manifest:

```powershell
.\scripts\Publish-RemoteDesk.ps1 -SkipAndroid -LinuxDistro Ubuntu-24.04
```

It creates Ubuntu/Linux host artifacts alongside the Windows executable and
does not build, validate, or distribute Android:

- `artifacts/RemoteDesk-linux-host.zip` for convenient inspection on Windows.
- `artifacts/RemoteDesk-linux-host.tar.gz` for self-contained portable Linux extraction.
- `artifacts/remotedesk-linux-host_<version>_amd64.deb` for offline Ubuntu installation.

For local development, the Linux artifacts can be rebuilt alone with the
following command, but that single-platform output is not the Desktop
canonical set:

```powershell
.\scripts\Publish-RemoteDesk.ps1 -LinuxOnly
```

Install the Ubuntu package from the artifact directory:

```bash
sudo apt install ./remotedesk-linux-host_1.0.0_amd64.deb
remotedesk-linux-app
remotedesk-linux-doctor
read -rsp 'RemoteDesk password: ' REMOTEDESK_PASSWORD; echo
printf '%s' "$REMOTEDESK_PASSWORD" | remotedesk-linux-host --password-fd 0 --port 56565 --receive-dir ~/Downloads/RemoteDeskReceived
unset REMOTEDESK_PASSWORD
sudo dpkg -r remotedesk-linux-host
```

The package installs wrappers as `/usr/bin/remotedesk-linux-app`, `/usr/bin/remotedesk-linux-host`, `/usr/bin/remotedesk-protocol-probe`, and `/usr/bin/remotedesk-linux-doctor`, adds a desktop launcher, and installs a private runtime plus RemoteDesk protocol files under `/opt/remotedesk`. The graphical app keeps the Linux host stopped until the user enters a password and explicitly starts it; it can also connect to another RemoteDesk host in a separate remote-control window with JPEG/H.264 screen display, mouse input, wheel input, common-key forwarding, text input, and GUI file/folder sending. Folders are zipped before transfer, and the Linux viewer confirms size, source path, and remote destination before starting the real transfer. The Linux host advertises `InputControl` when XTest or `xdotool` and `DISPLAY` are available, enabling remote mouse, wheel, common key, and text injection into X11/XWayland sessions. Linux viewer input sending and host input injection run on background queues that retain only the latest unsent mouse move, so drags track the newest pointer position instead of replaying stale movement. The Linux host reads input/control messages on a dedicated thread while the frame loop sends screen updates, so large frame writes do not delay mouse and keyboard message intake. Win/Linux viewers request H.264 recovery frames faster and clear stale queued H.264 frames when a recovery frame arrives; the Linux viewer also coalesces older unpainted frame events before handing frames to the active presenter, keeping the UI queue focused on the latest screen image. Mouse, wheel, and common key input prefer native XTest calls, with `xdotool` kept as text-input and compatibility fallback. Complete keyboard shortcut mapping remains future hardening work. It bundles Python and Python dependencies for core protocol/file-transfer operation. The current amd64 deb is built from the Ubuntu 24.04 WSL runtime and requires `libc6 >= 2.38`, so target Ubuntu 24.04 or newer. GUI startup, low-latency JPEG display, and continuous capture require `python3-tk`, `python3-pil`, ImageMagick, and `ffmpeg`, so install with `sudo apt install ./remotedesk-linux-host_1.0.0_amd64.deb` to resolve dependencies. Native H.264 surface presentation is provided by the recommended `mpv` dependency (`sudo apt install mpv` if it was not installed automatically). If you use `sudo dpkg -i`, follow dependency errors with `sudo apt -f install`. The Linux viewer prefers hardware-assisted H.264 decode when FFmpeg exposes a usable CUDA/NVDEC, VA-API, QSV, V4L2 M2M, DRM PRIME, or VDPAU path, and falls back through the remaining candidates before software H.264 or JPEG. The Linux host first probes persistent low-latency `ffmpeg x11grab` hardware H.264 pipelines in NVENC, QSV, VA-API, and V4L2 M2M order; when the viewer supports JPEG it falls back to persistent MJPEG and then per-frame ImageMagick capture. The GUI host form defaults to 1920x1080 and 60fps and exposes 540p/720p/900p/1080p/1440p/4K sizes plus 15/24/30/60fps choices. Physical Ubuntu Wayland sessions can limit X11 capture and XTest/`xdotool` input to XWayland windows; use Ubuntu on Xorg for full desktop control. Windows host adaptive capture now lowers frame rate and JPEG quality first, and only lowers transfer resolution after sustained severe pressure, preserving text clarity whenever the link has enough headroom. X11 clipboard integration uses `xclip`/`xsel`; Wayland file clipboard return uses the optional `wl-clipboard` package (`wl-paste`). Xvfb/openbox/x11vnc/xdotool/wmctrl/dbus-x11 remain optional system integrations for input, clipboard, and desktop test coverage; missing tools degrade those features instead of preventing startup. GUI launch failures are logged to `~/.cache/remotedesk/remotedesk-linux-app.log`; run `remotedesk-linux-doctor` to print OS, dependency, DISPLAY, session type, ImageMagick, ffmpeg, clipboard tools, native XTest, and bundled Python/Tk/cryptography/PIL diagnostics.

For H.264 viewing on X11 or XWayland, Tk exposes the realized video widget XID
to an optional `mpv --wid` presenter. Annex-B access units go directly to its
stdin, with `gpu-next`, OpenGL, and the `x11egl` context. Native candidates are
non-copy NVDEC, VAAPI, and DRM PRIME; each command also pins the corresponding
`cuda`, `vaapi`, or `drmprime` GPU interop. `*-copy`, QSV with an uncertain
Linux surface mapping, and V4L2 copy-back modes are deliberately excluded from
the zero-copy claim (QSV and V4L2 remain in the FFmpeg compatibility path).
A candidate is called zero-copy only after its unique local JSON IPC socket
reports both `hwdec-current` as the exact requested non-copy decoder and
`hwdec-interop` as the pinned interop. Verbose output is retained for early
rejection and diagnostics, but log wording cannot grant zero-copy status.
The first candidate is prewarmed immediately before H.264 is advertised. Raw
H.264 probing is forced to its minimum, the OpenGL swap interval is disabled,
and swapchain depth is one. Both authoritative properties are queried in one
IPC connection with an 80 ms per-attempt deadline instead of two serial
connections.
Software decode, copy-back selection, llvmpipe, missing interop evidence,
startup timeout, process exit, or a JPEG switch closes mpv and falls back to
the existing renderer. This is important in the
WSL fixture: mpv is installed and exposes GPU decoder names, but its current
X11/EGL renderer is llvmpipe and therefore must not be reported as zero-copy.

mpv stdin writes run on a dedicated thread. Its pending queue is capped at
two AUs; a new independently decodable recovery AU replaces older pending
writes, while prediction-chain overflow drops that pending chain and requests
recovery. During activation, at most one AU also takes the synchronous
FFmpeg/MJPEG/Tk compatibility-preview path; later AUs keep feeding mpv without
waiting for another compatibility decode, and that one preview is capped at
40 ms rather than the normal 120 ms fallback timeout. Network receive is never
blocked by a slow presenter. The same Tk widget continues to own mouse/keyboard
bindings and size/coordinate mapping. Native Wayland Tk embedding is not
enabled yet.

The compatibility H.264 path still enumerates both `ffmpeg -hwaccels` and
`ffmpeg -decoders`, then tries CUDA/NVDEC, VAAPI, Intel QSV, V4L2 M2M, DRM
PRIME, VDPAU, and finally software H.264. It downloads hardware surfaces,
encodes MJPEG, and paints through Pillow/ImageMagick/Tk. A backend that fails
to start, exits, or repeatedly produces no frame is disabled for that
connection; JPEG is requested only after every usable H.264 path fails.

## Linux 60fps H.264 modes

The Linux host accepts a real capture request up to 60fps. The GUI includes
`2560x1440`, `3840x2160`, and `60.0`; the equivalent command-line modes are:

```bash
read -rsp 'RemoteDesk password: ' REMOTEDESK_PASSWORD; echo
printf '%s' "$REMOTEDESK_PASSWORD" | remotedesk-linux-host --password-fd 0 --width 2560 --height 1440 --fps 60
printf '%s' "$REMOTEDESK_PASSWORD" | remotedesk-linux-host --password-fd 0 --width 3840 --height 2160 --fps 60
unset REMOTEDESK_PASSWORD
```

This is a negotiated H.264 mode, not a renamed 30fps mode.
`HighFrameRateH264` is protocol capability bit 19. The host advertises support,
but keeps a configured rate above 30fps at 30 until the viewer sends that bit.
The Linux viewer starts without the bit and sends an updated capability only
after mpv IPC has confirmed both a non-copy H.264 hardware decoder and the
requested GPU surface interop. If the native presenter later fails or all
native candidates are exhausted, it removes the bit and the host restarts its
hardware encoder at the compatible rate.

At native output size, the NVENC path avoids a redundant scale/pad operation,
converts to NV12, and performs an explicit CUDA upload before encoding.
The `x11grab` frame is still in system memory before that upload: this is
hardware encoding, not a zero-copy capture-to-encode surface path.
1440p60 uses an approximately 39.8Mbps automatic target. 4K30 keeps the
0.24-bit/pixel/frame desktop-detail budget (approximately 59.7Mbps). From
30 to 60fps that per-frame budget is interpolated continuously down to
0.16 bit/pixel/frame (approximately 79.6Mbps at 4K60), and the policy is
blended between QHD and UHD so crossing either threshold cannot reduce total
bitrate. This reduces sustained airtime without lowering the native 4K
geometry. The default ceiling remains 160Mbps;
`--max-video-bitrate-mbps` can lower it for a constrained LAN. This
4K60 budget still requires a physical long-soak result and is not itself an
acceptance claim. The encoder remains all-IDR/GOP1, and the capture reader keeps
only the latest completed AU, so TCP backpressure drops stale visual work
instead of building a frame queue.

Fallbacks are deliberately conservative: MJPEG/software presentation is
limited to 30fps at 1080p and below, 20fps at 1440p, and 12fps at 4K. Thus a
failed hardware path remains controllable instead of attempting 4K60 MJPEG.
The Annex-B reader locates AUD boundaries with native byte searches instead of
rescanning every encoded byte in Python. On the Ubuntu 24.04 WSL fixture with
RTX 2080 Ti, the host encoder and encrypted localhost transport received
360/360 all-IDR 1440p60 frames in 6.030 seconds: 59.54fps and 39.62Mbps.
This is not an end-to-end presentation result. Xvfb is a CPU-backed
framebuffer, and the current WSL X11/EGL presenter is llvmpipe, so the fixture
does not validate physical capture, a real network, hardware decode, or native
GPU presentation. A physical Xorg desktop with the target GPU, display, and
network still needs separate 1440p60 and 4K60 capture-to-present measurements,
plus a 30-minute soak and recovery matrix.

## Linux host prototype

`scripts/linux/remotedesk_linux_host.py` is a Linux/WSL host prototype for Windows-to-Linux file-transfer compatibility. It speaks the encrypted RemoteDesk protocol, advertises `Linux`, responds to Windows discovery requests, receives files from the Windows viewer, and returns Linux files after an automatic remote-copy probe or when the Windows viewer clicks “取回文件”.

Run it from WSL:

```bash
printf '%s' "$REMOTEDESK_PASSWORD" | \
python3 /mnt/c/Users/Zzy/Desktop/Tools/RemoteDesk/scripts/linux/remotedesk_linux_host.py \
  --password-fd 0 \
  --port 56565 \
  --receive-dir ~/Downloads/RemoteDeskReceived \
  --return-file /path/to/file-or-folder
```

Then connect from the Windows app to the WSL/Linux address and port. Files sent from Windows are saved under `--receive-dir`. Files returned to Windows come from repeated `--return-file` arguments plus GNOME copied-file data or `file://` URI lists; X11 uses `xclip`, while Wayland uses `wl-paste` when `wl-clipboard` is installed. Directories are zipped before return, and each batch is limited to 32 items with overflow reported in the preview and terminal status.

The prototype supports `RemoteDesktop`, `ClipboardText`, `FileReceive`, `FileSend`, `FileChecksum`, `FileTransferCancel`, `FileTransferPreview`, `ShortGopH264`, `HighFrameRateH264`, and `InputControl` when X11 input tooling is available. Linux-to-Windows returned files now send a preview list first when the viewer advertises preview support, so the Windows viewer can confirm size, source path, and destination before file chunks begin. It sends an X11/placeholder frame so the Windows viewer can complete the normal desktop connection flow. `RemoteStart` and full keyboard shortcut parity remain future hardening work.

The probe can also be run directly from WSL:

```bash
printf '%s' "$REMOTEDESK_PASSWORD" | \
python3 /mnt/c/Users/Zzy/Desktop/Tools/RemoteDesk/scripts/linux/remotedesk_protocol_probe.py \
  --host <被控机IP> \
  --port 56565 \
  --password-fd 0 \
  --send-file /path/to/probe.txt \
  --request-remote-files \
  --receive-dir /tmp/remotedesk-received \
  --json
```

## Coverage

The sandbox currently proves:

- X11 display startup through `Xvfb`
- basic window manager and test window startup through `openbox` and `xterm`
- root screenshot capture through ImageMagick `import`
- VNC exposure through `x11vnc`
- input automation through native XTest with `xdotool` fallback
- clipboard tooling through `xclip` / `xsel`, plus Wayland file clipboard reads through `wl-paste`
- future RDP comparison/client testing through `xfreerdp`
- low-latency video tooling availability through `ffmpeg`
- encrypted RemoteDesk protocol compatibility through the Linux Python probe
- Linux-to-Windows encrypted file transfer compatibility through the optional probe file send
- Windows-to-Linux encrypted file return compatibility through the optional remote clipboard file request
- Windows-to-Linux host-side file transfer compatibility through `scripts/linux/remotedesk_linux_host.py`

Remaining Linux work includes physical Xorg 1440p60/4K60
capture-to-hardware-encode-to-network-to-hardware-decode/present validation,
30-minute soak and network/host/GPU/display recovery tests, and real-hardware
NVENC/QSV/VA-API/V4L2/DRM-EGL coverage. Application hardening also remains:
improve Wayland capture performance, broaden keyboard shortcut mapping, and
build a native Linux tray/settings UI. Capability enumeration, the WSL/Xvfb
sample, and Python unit tests do not close those physical-device gaps.
