# Physical cross-platform interoperability probes

These are opt-in diagnostics, not shipping applications. They exercise the actual
Windows `RemoteHostServer`/`RemoteViewerClient`/`RemoteViewerWindow`, Linux host /
`ViewerConnection` / Tk renderer, and Android product viewer and production host.
Frames and input must cross a real authenticated network connection. A synthetic
chart is only the **captured content**, not a substitute protocol server.

Targets are bounded, owned windows: a fullscreen chart on the Windows primary
monitor, an independently allocated Xvfb/Tk desktop on the physical Linux machine,
and `InteractionProbeActivity` on an authorized, unlocked Android test phone.
Assertions read actual target button counters and Unicode text. A text click can
place the caret in the middle, so validation checks exact insertion, not an
incorrect assumption that every click appends at the end.

## Preparation

- Build `InteropProbe.csproj` in Release; it needs .NET 8 on Windows.
- Build/install the separate AndroidViewerProbe and AndroidCodecProbe APKs using
  their READMEs. Never distribute these exported diagnostic receivers as part
  of the production APK. The codec test's sample assets are needed at build time
  but are not the video source for physical interoperability tests.
- Use known-host, key-authenticated SSH to a Linux machine with FFmpeg, Tk/Pillow,
  cryptography, Xvfb, X11/XTest and xdotool. Host text injection needs xdotool even
  when native XTest already supports the mouse and physical keys.
- Use actual reachable LAN addresses. Do not silently fall back to SSH/ADB
  tunnels and label that route a LAN/UDP test.
- Keep the regular Windows host running separately. This runner uses an
  ephemeral port and an independent random, stdin-only secret. Its Windows
  chart temporarily covers the primary display; do not use the desktop during
  this phase. Its configured 50% scale assumes a 4K primary display, producing
  a 1080p test stream; change/record that fixture setting for other displays.

New Windows diagnostic executables may prompt for firewall access. Inspect the
prompt for **RemoteDesk.InteropProbe** only. Permit only the test executable and
the two actual test-device IPs for this test, then remove only those owned rules
afterwards. Never disable the firewall or alter another remote-control product.
`--windows-host-gate` pauses before viewer input while a test-app prompt is being
handled; create the reported `windows-host/continue` file after confirming the
owned chart is unobstructed. A modal firewall prompt invalidates the input test.

```powershell
python -u experiments/run_physical_interop.py --adb '<adb.exe>' --serial '<serial>' --linux '<user@Linux-IP>' --windows-ip '<Windows-IP>' --output artifacts/interop-new --windows-host-gate
```

This tests Windows→Linux, Android→Linux, Linux→Windows and Android→Windows in
sequence. Windows/Linux viewer probes render product frames; Android uses actual
Surface/JPEG presentation and native IME composition plus product send-text.
Android physical text includes Chinese and an emoji. Each target must observe
one click and the exact injected text. Screenshots, renderer telemetry and
per-session evidence are retained under the new output directory.
For a targeted rerun use `--pairs LinuxToWindows` (or multiple explicit names).
The report records the selected subset; its `complete` field does not certify
directions omitted from that invocation.

To test Windows/Linux→Android, use the printed **owned** Linux staging path:

```powershell
python -u experiments/run_android_host_interop.py --adb '<adb.exe>' --serial '<serial>' --linux '<user@Linux-IP>' --linux-stage '/tmp/remotedesk-interop-XXXXXXXX' --phone-ip '<phone-LAN-IP>' --output artifacts/interop-phone-new
```

The phone must have no active screen share or saved host password. The script
refuses to overwrite one. It sets a random temporary password through the normal
product UI and waits for the ordinary system screen-sharing consent. It then
opens only the synthetic input target, tests both viewers and a Windows reconnect.
It stops sharing and removes only its own encrypted password, without clearing
app data. This phone-specific script currently assumes the authorized 1440×3136
portrait test phone when scrolling the host card; adapt and re-inspect for other
devices rather than tapping guessed coordinates.

## Evidence and cleanup

- `complete` requires rendering and real target input; receiving encoded frames
  alone is insufficient. Windows JPEG checks count new bitmaps at actual Paint
  events (`paintedImageChanges`), not periodic timer samples. A busy message loop
  can still paint hundreds of frames while the low-priority sampler fires only a
  handful of times; `renderedImageChanges` is retained only as legacy diagnostics.
  See the documented [WM_TIMER priority](https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-timer).
  Windows hardware presented-frame counts, Android
  rendered FPS and Linux changed PNG counts are different metrics, not directly
  comparable network/latency benchmarks.
- Linux reports the actual decoder backend, including fallback. A discovered
  hardware decoder or a few decoded frames do not prove sustained hardware use.
  `rawStatus` preserves bounded, timestamped diagnostics before UI coalescing;
  `wireProgress` records complete authenticated message sizes/timing, not their
  content. These distinguish heartbeat expiry from a subsequent socket EOF.
  Linux acceptance also requires an open transport and a newly displayed frame
  in the final five seconds; early frames followed by disconnection do not pass.
- The Linux probe always allocates its own Xvfb with `-displayfd` and terminates
  only its own children. Existing displays, user desktops and services are not
  stopped. Remote evidence is copied locally and retained remotely for inspection;
  validate the exact generated `/tmp/remotedesk-interop-*` path before deleting it.
- Uninstall only the two temporary APKs; remove their owned UI dump. No ADB port
  mappings are created by these LAN probes. Keep the production phone app.
- On realme, force-stopping the product during secret cleanup also disables its
  accessibility service. Restore it in normal Android Settings, confirm the final
  enabled-service list, and verify screen sharing is stopped. Do not report the
  pre-cleanup permission state as the final state.
- No device/SSH credentials belong in source or evidence. Desktop session
  secrets use stdin; the temporary phone password is entered through authorized
  ADB UI input and saved only by the product's encrypted password store. Never
  substitute a user's persistent password in this test setup.

These bounded checks do not certify long unattended sessions, Wayland/login
screens, other phone/OEM/IME versions, file transfer, public relay behavior, or
all Windows displays. Their passing matrix is not a claim of zero bugs.

## Native public relay mode

`relay-windows-update` uses the owner's saved, pinned relay login. Its stdin
configuration requires `expectedServer` and a new `output` directory. Omit
`action`, or use `inventory`, to list nodes without connecting to their desktops.
`apply` requires explicit Windows device IDs in `devices`, the verified EXE
`package`, `sha256`, and `buildStamp`; busy sessions additionally require
`allowSessionTakeover: true`. Saved per-device keys are preferred; any explicit
`credentials` map stays on stdin and is not written to reports.

`verify` accepts the same package/identity checks but never sends an update, and
rejects endpoints with a different build. Already-current endpoints also undergo
relay reconnection and frame verification. Reports retain capture diagnostics and
target availability on failure: an authenticated `afterBuild` alone does not mean
the desktop is viewable. These modes never inject input, copy clipboard contents,
change settings, or install the separate Windows lock-screen service.

Clipboard/file regressions also have bounded standalone modes:

- `clipboard-isolated` and `clipboard-shortcuts-isolated` take `output` on stdin;
  they use a private window station and never access the interactive clipboard.
- `clipboard-context-isolated` also takes `output`. It runs the actual Windows
  viewer and native TextBox copy/paste menu handlers against encrypted synthetic
  Windows/Linux peers: no Ctrl+C/V, delayed ACK mouse ordering, timer-driven
  remote copies, stale reply protection, non-text preservation, oversized text
  fallback, and an eight-second blocked-write deadline. `clipboard-isolated`
  additionally checks the actual Windows host snapshot reader/cache. These are
  isolated component/integration checks, not physical cross-platform sessions.
- `file-relay-isolated` takes `output` and `expectedServer`. It reuses the locally
  saved, pinned relay configuration, registers a temporary node and verifies
  text/file round trips with generated fixtures and the product file receiver.
- `file-relay-ui` uses the same configuration but does **not** read/write the
  interactive clipboard. It gives explicit fixture paths to the product's
  file-paste flow and operates only its owned confirmation/result dialogs. It
  verifies the queried directory before consent, no early upload on cancel,
  every saved path in a mixed-success batch, and the actual renamed receipt.
  Screenshots contain only these generated fixtures.
- `keyboard-focus` takes `output`, opens two owned windows on separate UI
  threads and checks the actual foreground boundary, including an asynchronous
  background refocus. Synthetic records enter the product hook callback; no OS
  keystrokes are injected and no clipboard data is read. Keep the test windows
  foreground during its brief run; an unexpected focus change fails the check.

The runner also accepts `--relay-server`, `--relay-ssh-pin` and `--relay-tls-pin`.
It reads the **existing** authorized server configuration using a hidden SSH
password prompt, after matching the supplied previously verified SSH host key.
It neither deploys nor restarts the relay. No real secret is put in argv/reports.
All viewers use product relay APIs, with UDP disabled and no direct-LAN fallback.
The product's internal loopback adapters are not SSH/ADB tunnels.

Alternatively, `--relay-server <explicit-server> --relay-stdin` accepts an already
approved relay configuration (server, port, access token and verified TLS pin) on
stdin, before spawning any children. Do not supply SSH/pin arguments in this mode.
The server must match, test registrations receive fresh IDs, and evidence records
that no new SSH identity check was performed. This lets a caller reuse its local
encrypted configuration without another administrator login or logging secrets.

Build/install AndroidRelayHostProbe as well when adding `--android-host`. This
mode uses that separate sandbox/keystore, the actual product MainActivity,
foreground host and accessibility service, and the reused owned input target.
The normal Android permission/start-host gate remains mandatory. It refuses to
replace an active projection or an existing test-host password. Uninstall the
owned test-host APK between independent runs to clear its temporary credentials.
All six names are accepted by `--pairs`; selecting an Android-target direction
explicitly requires `--android-host` and runs only the selected directions.
Unselected desktop-host stages are not started. Omitting Android-target names
retains the existing `--android-host` behavior of adding both of those directions.

In direct LAN mode, `--android-host` can reuse an already-running signed production
host: supply its authorized `{ "host": "...", "port": 56565, "password": "..." }`
endpoint only on stdin. No production `run-as`, password replacement, uninstall or
permission reset is performed. `--android-target-package com.remotedesk.relayhostprobe`
reuses that separately installed APK's interaction Activity without starting its
host. The runner starts only the allowlisted test Activity before sending input.
This supports preserving the installed user's configuration during release checks.
The endpoint is consumed before any child process starts. Children without an
explicit payload get closed stdin so SSH cannot swallow a later credential record.

Relay mode gives slow networks longer observation time but retains continuous
rendering and exact target-input assertions. Evidence includes both built and
actually installed APK SHA-256, directory registration and the actual route.
An installed APK/build hash mismatch aborts before any physical sessions start.
The six directions use three physical machines, not synthetic protocol peers,
but the captured windows/desktops are isolated test fixtures. They can share a
LAN while their data path is explicitly forced through the public relay; this
does not certify two independent ISPs/NATs or a roaming/network-switch scenario.

For an Android H.264 host run, also pass `--require-android-host-h264`: a healthy
JPEG fallback still counts as basic interoperability, but must not pass a video
codec qualification. This is separate from `--require-android-h264`, which checks
the Android **viewer**. Isolated Android host logs are retained as `android-host.log`
to distinguish late codec negotiation from an encoder/decoder failure. Transient
directory-query connection/timeouts are recorded and retried only within the
existing registration deadline; identity and authentication failures still abort.

Keep the Windows test desktop exclusive during input assertions. If the owned
target is obscured or unexpected characters appear, stop, preserve the failed
evidence and arrange an exclusive rerun. Never loosen exact-text assertions or
tap a user's other windows to make a run pass.

Unattended Windows viewers disable manual UI input forwarding; their explicit
product-client API actions and exact OS-target assertions remain enabled. This
keeps incidental workstation typing out of API integration tests, but does not
certify the viewer's physical keyboard shortcuts. The Windows owned host waits
for initial foreground ownership before starting capture; after arming, losing
focus or minimizing stops its host and relay connector, without auto-refocusing.
`windows_target_guard_probe.py --output <new-directory>` checks that protection
using two owned windows, without authenticating a viewer or sending remote input.
UI-thread failures in the Windows probe are recorded instead of opening a modal
exception dialog; viewer creation/Load/Shown stages are also logged.

The Android interaction target reports `foregroundOwned` on focus/resume/pause.
The runner refuses stale/background target reports before permitting input, and
retains the actual Activity launch result. In isolated relay-host mode, the
`guardHostedInput` extra also stops that APK's own host if the target loses focus;
it never stops or changes the installed production app. Rebuild the target APK
when using this guard; an older report without the field intentionally fails.

`../verify_android_session_recovery.py --host <authorized-idle-phone> --output
<new-report.json>` accepts the existing password on stdin and checks explicit
authentication rejection without displacing the first session, authenticated
takeover, and a subsequent reconnect. It intentionally replaces the test viewer,
uses real LAN TCP, sends no OS input, and reports metadata rather than claiming
pixel/GUI or automatic network-drop recovery qualification.

Visible-rendering probes now require progress after the startup warm-up, rather
than counting a few early JPEG paints as a later H.264 success. Windows rejects
an unavailable interactive input desktop explicitly; it must be unlocked and
accessible. Its own viewer is shown on top with a thread-scoped display power
request, released on exit without changing the user's power plan. A foreground
owned viewer also captures its visible client surface for verification.

Relay batches check built/installed APK hashes only for the selected Android
roles; a desktop-only batch does not require unrelated test APKs to be installed.

`../run_relay_throughput.py --host <relay> --ssh-pin <verified-fingerprint>
--tls-pin <verified-sha256> --output <new-directory>` reads the existing relay
configuration after pinned SSH authentication, then uses the actual Windows
relay adapters for bounded random-byte echoes. It never provisions/restarts the
relay. Passwords and tokens remain on hidden input/stdin. Round-trip synthetic
byte throughput is not video FPS or end-to-end input-to-pixel latency.

On the Jetson, `../jetson_encoder_probe.py --output <new-directory>` uses an owned
Xvfb/static text/color chart to validate native, scaled and padded H.264 streams,
60 independent frames each, explicit BT.709, decoded dimensions, solid-color
error and native luma PSNR. `../linux_unicode_probe.py --output <new-directory>`
checks 20 exact Chinese/emoji/ASCII text batches in its own Tk entry while the
hardware encoder is active. Both close only their own Xvfb/encoder processes.
The optional Unicode delay override is for recorded A/B diagnostics, not a
replacement for testing the actual product default.

`../run_feature_audit.py --linux <key-authenticated-user@host> --output <new-directory>`
checks the actual Windows client against a fresh, owned Linux Xvfb host:
authentication rejection, capabilities, JPEG/H.264 negotiation, target selection,
Chinese/emoji clipboard send, duplicate/empty/directory uploads, SHA-256 file return,
viewer takeover and repeated reconnect. It verifies saved bytes on Linux via SSH.
The clipboard callback is isolated: no Windows clipboard contents or physical input
are accessed. Only synthetic files in the allocated test directory are transferred.
The temporary host/Xvfb stop on exit; reports and owned fixture files are retained.

Add `--relay-stdin` to run this same feature audit through an existing authorized
public relay. Supply one JSON line on stdin with `serverAddress`, `port`,
`accessToken` and the previously verified `tlsCertificateSha256`; never put the
token in command arguments or a checked-in file. The harness validates the input
before spawning SSH, generates a fresh test device ID and passes credentials only
through child pipes. It does not deploy/restart the relay, reuse an installed
device's registration, change routes or fall back to LAN. The report identifies
the selected transport; file verification still uses key-authenticated SSH.

For this repository's explicitly authorized lab, the `transport` mode can run
that audit using the current user's saved pinned relay login, without exporting
the token through the shell:

```powershell
'{"useInstalledRelay":true,"expectedServer":"8.138.5.232","linuxFeatureAuditTarget":"zzy@10.7.163.74","output":"artifacts/relay-features-new"}' | & experiments/InteropProbe/bin/Release/net8.0-windows/RemoteDesk.InteropProbe.exe transport
```

This wrapper deliberately allows only the named lab targets. It passes the
decrypted token directly to the existing Python runner on stdin; the runner
allocates its own random node and writes evidence under `linux-features/`.
It neither installs nor upgrades the apps, and does not certify the new native
detail protocol. Inspect `result.json`, copy the evidence, and validate each
generated remote temporary path before cleaning it up.

`desktop-status` reads Windows desktop availability without capture/input. A host
probe that loses foreground records its desktop/pointer/owned-button geometry and
stops even if diagnostic writing fails. It never silently refocuses another app.

The explicit `transport` stdin options `optimizeRoute: true`, `routeLeaseAudit:
true`, and `routeRecovery: true` exercise Windows' configured-relay route optimizer,
native create/renew/dispose/expiry, and relay reconnection after kernel expiry,
respectively. They require elevation and no existing TCP connection or dedicated
route to the authorized relay. They never bypass those guards. `routeRecovery`
shortens only its test lease to 8 seconds and withholds renewal; it does not disable
a NIC. All trials dispose their own leases and report route snapshots. The normal
production lifetime is 90 seconds. No default route, NIC metric, forwarding setting,
server configuration or remote-desktop input is changed by these probes.

`relay-path-quality` samples the owner's saved, pinned public relay on two
already-connected physical uplinks. Supply stdin with `expectedServer` and a new
`output` directory. Eight rounds, 31 seconds apart, exercise the production TLS
sampler and preference policy in an isolated selector, including subsequent dials.
It sends no relay credentials/role and does not change routes, installed sessions,
Wi-Fi associations, or settings. The result is handshake timing, not video latency
or bandwidth. Tests in `tests/data/relay-path-stability.tsv` also cover sustained
improvement, jitter, isolated spikes, failure recovery, and the three-minute hold.

`software-paint-isolated` compares Windows' original software paint path with the
prepared native bitmap path on a private desktop. Supply a new `output` directory
on stdin. It uses synthetic 4K frames, explicitly drives native `WM_PRINTCLIENT`,
warms both paths, and records paint time, UI heartbeat gaps and decoded-frame to
completed-paint latency. No network, input, clipboard, settings or services are
used. See `docs/WindowsUiCheck-20260914.md` for the measured scope and caveats.

`../Measure-WindowsUi.ps1 -TaskProcessId <pid> -Action Sample` performs bounded
`WM_NULL` checks on that RemoteDesk main window without changing its UI. The
explicit `Tabs`, `Resize` and `Permissions` actions manipulate only that window
and restore their selection/bounds; do not use them during someone's active
session. Use Windows PowerShell for UI Automation actions. `Screenshot` requires
a new output path and captures only the specified main window.

`layout-isolated` now also shows the actual main form with disposable settings
on a private desktop. It checks every visible action/input after scrolling,
checks ancestor clipping and layout convergence, and captures the three pages
at repeated widths. It does not start app services or load/save user settings.
It also exercises file confirmation/result dialogs using synthetic long Chinese
paths, partial failures, 9/14-point fonts, and 360–1000-pixel viewports. Every
input/action must become fully visible through normal focus traversal, whole-
dialog horizontal overflow is rejected, and shrinking then restoring a dialog
must settle without repeated layouts. The file list itself may scroll sideways;
the selected file's complete paths remain available in its read-only details.
`Measure-WindowsUi.ps1 -Action AuditLayout -ScreenshotPath <new-directory>`
captures those pages on an explicitly selected installed instance and restores
its bounds/tab. Evidence and limitations: `docs/UiAudit-20260915.md`.

`main-layout-isolated`, `popup-layout-isolated`, and `viewer-layout-isolated`
extend the same private-window-station checks with runtime font changes, long
diagnostic text, narrow add-device/shared-name/address dialogs, and the viewer's
overflow menu. Supply `{"output":"<new-directory>"}` on stdin. These probes use
synthetic settings and disconnected viewer controls, never real sessions or
clipboard data. They check actual scroll ranges, focus accessibility and idle
layout convergence; the viewer also checks the real scaling menu action and
fullscreen restoration. Their screenshots are rendered at the machine's current
DPI, not a claim of physical multi-monitor DPI coverage. Findings and evidence:
`docs/WindowsUiAudit-20260919.md`.

`background-fault` drives a real WinForms message loop in its own process and
collects one faulted task. It verifies that the product records the unobserved
background exception without closing the host. No windows or remote sessions
are opened. Supply a new `output` directory on stdin.

`slow-file-transfer` uses an owned loopback peer and generated 1 MiB file, with
normal encrypted protocol/authentication and the production file receiver.
It consumes each 32 KiB chunk one second apart, so heartbeats queue behind the
upload. The 90-second probe checks SHA-256, the save receipt and the surviving
connection, with no user clipboard, settings, relay or desktop access. Supply
a new `output` directory; existing evidence is never overwritten.
`slow-file-transfer-legacy` repeats this with a peer that does not advertise
save receipts, checking that local send completion cannot truncate the bytes
still queued for delivery. It independently checks the peer's saved file.
