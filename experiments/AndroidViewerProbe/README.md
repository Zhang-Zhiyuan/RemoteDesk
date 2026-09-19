# Physical mobile viewer UI probe

This separate `com.remotedesk.viewerprobe` APK compiles the actual Android product
Java/resources. By default its private viewer connects to a synthetic authenticated
TCP peer through an explicitly configured ADB reverse. An explicitly supplied,
app-private `files/interop-endpoint.json` also supports authorized physical LAN
tests against **owned synthetic OS input targets** (see `../InteropProbe/README.md`).
That file is consumed and deleted on launch; the secret remains only in this
probe's keystore-backed viewer preferences until uninstall. Do not connect this
automated probe to an ordinary user's active desktop.

Only this separately compiled viewer probe enables the compile-time
`ALLOW_LOOPBACK_FIXTURES` flag for owned ADB-reverse fixtures. Production APKs,
including normal debug builds, set it to `false`: they reject loopback and every
local interface address before authentication. There is no intent, preference,
or runtime user switch. The probe exception only permits actual loopback sockets;
local-interface and authenticated device-identity checks remain active. Explicit
LAN discovery/port probes still reject self targets in this test APK.

The probe never reads production preferences or declares accessibility/capture
services. Its exported gesture/IME test receiver is **not** part of the
production manifest/APK. Physical mode does send real OS input to the explicitly
selected RemoteDesk host; the default synthetic peer does not.

The public fixture token is deliberately not a real device/server password.
Run only on an authorized, unlocked test phone. Do not publish the probe APK.

From the repository root (PowerShell):

```powershell
$probeAdb = Join-Path $env:LOCALAPPDATA 'Android\Sdk\platform-tools\adb.exe'
$probeSerial = '<authorized adb serial>'
& src/RemoteDesk.Android/gradlew.bat --no-daemon -p experiments/AndroidViewerProbe assembleDebug
& $probeAdb -s $probeSerial install -r experiments/AndroidViewerProbe/build/outputs/apk/debug/RemoteDeskAndroidViewerProbe-debug.apk
python experiments/android_viewer_fixture_server.py --fixtures artifacts/video-quality-20260907-080209-8a9f141c --output artifacts/mobile-ui-peer-new --seconds 1800
```

The fixture prerequisite is `static-current-gop1.h264` (180 recovery access units)
from the existing video-quality experiment. Python requires Pillow and the Linux
protocol probe's crypto dependencies. The synthetic JPEG chart uses Windows fonts.
The peer prints a randomly allocated **127.0.0.1-only** port. In another terminal:

```powershell
& $probeAdb -s $probeSerial reverse --list
# Proceed only if phone tcp:7411 is free (never replace someone else's mapping).
& $probeAdb -s $probeSerial reverse tcp:7411 tcp:<printed-host-port>
& $probeAdb -s $probeSerial shell am start -W -n com.remotedesk.viewerprobe/com.remotedesk.agent.ViewerProbeLauncher --ei port 7411
python experiments/android_viewer_ui_verify.py --adb $probeAdb --serial $probeSerial --server artifacts/mobile-ui-peer-new/server-state.json --output artifacts/mobile-ui-check-new
```

The verifier taps only controls belonging to this probe, makes ADB single-pointer
gestures inside its viewport, and dispatches app-local multi-pointer MotionEvents
and native InputConnection composition. It asserts the encrypted peer's received
mouse/key/control messages, geometry and session continuity, and saves screenshots
and JSON evidence. It stops on failure rather than tapping an unrelated app.

The JPEG second target is 1280×720; the H.264 fixture remains 1920×1080 on both
targets intentionally, to test same-dimension decoder/presentation recovery.
This does not establish real network latency, remote OS text-entry fidelity, or
compatibility with other Android/OEM/IME versions. The green `90` overlay on this
test phone is the phone's developer overlay, not the viewer's rendered FPS.

The same suite can run on explicitly selected `emulator-*` devices. Its report
labels emulators separately from physical phones. On API 26–29, keyboard visibility
uses the actual visible-window rectangle (excluding system bars), since the IME
WindowInsets API is available only on API 30+. This is probe telemetry, not a
change to the production keyboard implementation.

`android_resolution_surface_verify.py --adb <adb> --serial <device> --output
<fresh-directory> [--expect-native]` compares the Surface buffer dimensions during
fit, original-pixel viewing, keyboard changes and rotation. The native expectation
requires the buffer to match the received frame, not the fitted View; the report
also checks session continuity. This measures geometry and Surface callbacks, not
objective image sharpness or end-to-end latency. The probe-only `original_size`
and `fit_size` actions call the same product viewport operations as the menu.
The landscape keyboard phase also checks that fitted pixels stay the same size
while the IME is visible, that closing it restores fit, and that the composer
requests a non-fullscreen IME. The desktop is cropped/panned around the cursor;
this does not reduce the received resolution or certify every third-party IME.

Fresh system images may display a first-fullscreen tutorial. The verifier only
acknowledges the identified Android immersive tutorial while the probe owns app
focus; it does not approve arbitrary dialogs or permissions. `--suite fullscreen`
can resume the final four checks when an inspected probe is already fullscreen.

## Fault-injection / edge checks

Add `--allow-test-controls` to the **synthetic peer** command and `--suite all`
to the verifier to include repeated DeviceInfo during a held drag, letterbox
pinch in direct mode, input revocation/regrant, delayed screen frames and a
forced connection drop/reconnect. Use `--suite edges` for only those checks.
The default remains the standard UI suite. `--record-failures` records all
assertion failures for a before-fix baseline and exits nonzero at the end;
unexpected UI/transport errors still stop immediately.

The peer reads only its own output directory's `fixture-command.json`, enabled
explicitly by the flag. Supported actions are a fixed allowlist, not shell
commands; the listener stays bound to 127.0.0.1. Screen holds expire after ten
seconds. A forced drop waits two seconds before accepting the next fixture
connection, leaving time to verify disabled controls and old input cleanup.
These tests do not change the phone's Wi-Fi, permissions or system settings.

Probe snapshots use same-directory atomic rename so API 26 external ADB readers
never see a half-written JSON document. The Windows synthetic peer also retries
brief sharing violations when replacing its evidence file; it does not truncate
the previous snapshot or turn that reader/writer race into a fake disconnect.

`experiments/android_scroll_ui_verify.py` adds jitter, batched direction reversal,
finger replacement and pinch-drift cases in both input modes (37 assertions).
It uses the same `--adb`, `--serial`, `--server` and `--output` arguments; run it
with the probe connected to the synthetic peer. These are generated MotionEvents,
not a claim about subjective touch feel or a real browser's native scrolling.

`experiments/android_history_ui_verify.py` tests the signed production APK's recent
nodes, remark/delete UI, process restart, failed connection and per-node password
selection. It is restricted to an owned emulator with a fresh history, two reverse
ports (7411/7412) to the synthetic peer, and an unused port 7419. It never clears
the app's data or accesses a user's remote machine.

The history verifier scrolls only the production devices page when an item is
below its fixed navigation bar. Its two-port case uses a peer without a device
GUID; it does not assert that one stable GUID must produce duplicate records.
When automatic port discovery offers both synthetic ports, the verifier chooses
the original saved endpoint explicitly. `--resume-reconnect` continues an
inspected run that already contains both synthetic nodes and `History-Node-A`,
without clearing data; retain the earlier report and label the resumed subset.

After inspection, uninstall only this probe, remove only its owned reverse entry
and `/data/local/tmp/remotedesk-test-ui.xml`, and stop only the peer process started
for this test. Keep screenshots/reports locally under ignored `artifacts/`.

Discovery checks use `com.remotedesk.agent.DiscoveryProbeActivity` in this test APK.
An optional `--es target <owned-IP>` verifies a real endpoint without AUTH or input.
The activity exercises the product JSON parser, bounded interface discovery, a
loopback UDP custom-port fixture and TCP-banner fallback when UDP is absent.
Collect its private report with `android_discovery_ui_verify.py --phase collect-probe`.

For signed-product UI checks, run the synthetic peer with `--discovery-port 40566
--machine-name "Discovery test PC"` (loopback only; the port must be unused).
`android_discovery_ui_verify.py --phase create` tests IP-only connect, rediscovery,
saved credentials and cancellation on an owned emulator. Restart the peer with
a new TCP port and use `--phase moved --old-port <previous>` to test confirmed
relocation and cancellation. Both phases take `--adb`, `--serial`, `--server`
(the peer's server-state.json) and a fresh `--output` directory. They preserve the
earlier `Upgrade-Node` history test record and do not clear application data.

Interop snapshots expose cumulative received/presented frame counts and device
uptime. These are read under the product health tracker lock without advancing
its FPS sampling window. `run_physical_interop.py` requires at least five seconds
of same-session counter growth and a recent presented frame; a startup-only FPS
spike is not continuous rendering. `--require-android-h264` additionally rejects
a final JPEG fallback. Older reports using only `any(FPS > 0)` cannot prove
continuous rendering; retain and recheck them instead of treating them as passes.
Use `--min-android-fps 15` to require both the observed average and recent
presentation rate to reach 15 FPS. The default (0) checks basic connectivity,
not smooth video; counter-derived rates are recorded independently of UI labels.

## Received files and slow uploads

Launch `com.remotedesk.agent.MainActivity --ez receivedFilesProbe true` **in this
probe package**, then read its private `files/received-files-probe.json`. It creates
two synthetic MediaStore rows, checks the product file-list/complete location,
blocks only the test storage worker to check UI responsiveness, cancels the query,
and removes its own rows. The baseline records whether the platform already hides
pending downloads; explicit filtering is not counted as a reproduced bug when it
already does. No production preferences or received files are used.

`LayoutProbeActivity --ei width 240 --ei height 480 --ef fontScale 2.0` checks
the actual viewer controls at the requested dimensions/font size without changing
system settings. The requested rectangle must fit the current display; otherwise
clipping is a test-setup error. Repeat at 320×480 and 600×360, fonts 1/1.5/2.

`verify_clipboard_viewer.py --files --file-silence-seconds 25 --upload-only` uploads
a 3 MiB synthetic file while the authenticated peer stops reading and sending
frames for 25 seconds. It verifies exact bytes, save acknowledgement and unchanged
connection ownership. Without the transfer-specific watchdog allowance, the old
viewer disconnects at 18 seconds. This is controlled local fault injection, not
a measurement of public-relay latency. The original clipboard/UI suite still runs
when `--upload-only` is omitted. Owned MuMu instances use `--mumu-manager <exe>`;
the script verifies the loopback serial against that manager's running VM list.
Use `--legacy-file-watchdog` only as a labelled negative control: the **probe**
closes its upload lease to restore the prior 18-second watchdog policy. This test
switch is not compiled into the production APK. Every transfer fixture has a
unique name so DocumentsProvider cannot reuse metadata from an earlier size.
Add `--legacy-no-file-receipt` to emulate an older peer without save ACK support:
the smaller 256 KiB upload completes locally before the 25s quiet interval ends.
The verifier checks that the UI clearly marks saving unconfirmed, the bounded
tail drain is active after the sender finishes, and the same connection survives.
