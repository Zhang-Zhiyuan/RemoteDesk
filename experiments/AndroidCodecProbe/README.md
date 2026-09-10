# Isolated Android codec probe

Builds the actual RemoteDesk Java decoder classes into a separate test APK,
`com.remotedesk.codecprobe`. The production application ID is not reused, and its
settings, credentials, installation and connections are not accessed. The test
manifest requests no network, capture, accessibility or storage permissions.

First generate the 1920x1080 / 180-frame fixtures with
[VideoQualityProbe](../VideoQualityProbe/README.md). From the repository root:

```powershell
& src/RemoteDesk.Android/gradlew.bat --no-daemon -p experiments/AndroidCodecProbe '-PfixtureDir=C:/absolute/path/to/video-quality-run' assembleDebug --console=plain
adb -s <test-device-serial> install experiments/AndroidCodecProbe/build/outputs/apk/debug/RemoteDeskAndroidCodecProbe-debug.apk
adb -s <test-device-serial> shell am start -n com.remotedesk.codecprobe/com.remotedesk.agent.CodecProbeActivity
adb -s <test-device-serial> shell run-as com.remotedesk.codecprobe cat files/codec-probe.json
```

Use a dedicated test device/emulator. Keep its window visible and foregrounded
while the test runs. Select the explicit ADB serial, not an arbitrary connected
device. The test uses the debug build so `run-as` can export its own report.

The probe feeds GOP1 and experimental GOP30 streams to a real SurfaceView at a
bounded ~30 Hz. It checks cold-start dependent-frame rejection, render callbacks,
actual light/dark pixels using PixelCopy, Surface destruction/recreation and
decoder closure. PixelCopy runs early enough to distinguish a visible image
without callbacks from a genuinely black Surface. The current acceptance also
allows product-confirmed Surface buffers, with at least 150 actual submitted
outputs initially and 45 after replacement, plus matching pixels. Submission
counts remain separate from render callbacks; absent callbacks must keep FPS
unknown. The probe exercises the real four-second watchdog while feeding.
Later checks are not reached
when an earlier check fails; their absence is not a pass.

Wait for `complete: true`, then inspect each `results[].passed` and any `fatal`
field. **Completion means the probe finished, not that the test passed.** This is
not an instrumentation-test runner, a 60 FPS benchmark or an end-to-end remote
session. A codec's reported hardware flag on an emulator does not prove physical
Android hardware acceleration. The JSON also retains product diagnostic messages.

To test a different actual encoder, pass `-PfixtureNames=jetson-native.h264`
(or comma-separated basenames) together with its `fixtureDir`. Each stream must
still contain exactly 180 complete, AUD-delimited 1920x1080 frames of the same
light-left/dark-right chart; acceptance thresholds are unchanged. The assets task
removes stale generated fixtures, and the report identifies every tested file.
`jetson_encoder_probe.py --frames 180 --native-only` records a fresh compatible
chart on its own Xvfb. Copy its `native.h264` to the selected fixture basename;
do not label repeated frames or a software re-encode as a native capture.

Export the report before removing only the temporary package:

```powershell
adb -s <test-device-serial> uninstall com.remotedesk.codecprobe
```

This removes the test APK and its private report; it does not remove RemoteDesk.
The built APK and any exported JSON remain available locally. Do not install this
probe as a replacement for the production app.

See [the original 2026-09-07 findings](../../docs/PlatformRecheck-20260907.md),
including the MuMu callback false-positive reproduced before the fix.

## Physical host / input target

`InteractionProbeActivity` is an additional synthetic window in the same probe
APK. It records only its own button clicks, editable text and bounds to
`files/interaction-probe.json`. It also checks password styling on actual text,
web and numeric password EditTexts. It requests no extra permissions and keeps
the screen awake only while its Activity is visible.

With the owner's permission, install the actual RemoteDesk APK separately,
set a temporary strong host password, and enable its screen-sharing and
accessibility permissions through normal Android prompts. Start the product
host before foregrounding the synthetic target. Do not use unrelated apps as
input targets or capture sources.

```powershell
adb -s <serial> shell am force-stop com.remotedesk.codecprobe
adb -s <serial> shell am start -W -n com.remotedesk.codecprobe/com.remotedesk.agent.InteractionProbeActivity
adb -s <serial> forward tcp:0 tcp:56565
python experiments/android_physical_session_probe.py --adb <absolute-adb-path> --serial <serial> --port <allocated-local-port> --output artifacts/android-host-unique-run
```

Wait for the target to initialize before starting the Python probe. Enter the
temporary RemoteDesk password at its non-echoing prompt, never as an argument.
It tests bad-password rejection, synthetic JPEG/H.264 pixel decoding, click,
burst Chinese/English/Emoji input, codepoint backspace, reconnect and takeover.
It connects only to localhost via that specific ADB forward. Arrival FPS includes
test overhead, not display refresh; this does not validate Wi-Fi/UDP performance.
Reset only the probe package before another run, not the product service.

Export reports, stop screen sharing and remove just the allocated forward
(`adb -s <serial> forward --remove tcp:<port>`) and the temporary probe APK when
finished. Product force-stop may disable its accessibility service on some OEMs;
check the actual status and re-enable through Android settings if needed.
Realme and Jetson findings are in [the physical test report](../../docs/PhysicalDevices-20260907.md).
