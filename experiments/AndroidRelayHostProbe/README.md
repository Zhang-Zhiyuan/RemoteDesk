# Isolated physical Android relay host

Test-only APK `com.remotedesk.relayhostprobe`, compiling the production MainActivity,
foreground host, projection, input service and private-relay implementation.
Uses an independent app sandbox/keystore; never reads or changes production
preferences. It must not be published. The synthetic interaction target is reused
from AndroidCodecProbe; its observed OS button count/text are the assertions.

Build with the repository Gradle wrapper, `-p experiments/AndroidRelayHostProbe
assembleDebug`. Supply an authorized endpoint JSON (password, relay options) via
ADB stdin to this debuggable app's private `files/interop-endpoint.json` and launch
HostProbeLauncher. The file is consumed, removed and encrypted by product stores.
Never put persistent device credentials in it, source, command lines or reports.

Enable only this test app's accessibility through normal Android Settings and
start screen sharing through the normal product/system consent UI. No permission
bypass is provided. Existing active screen shares must be preserved; the test
runner should refuse to start until the phone is free. The ordinary product may
keep its discovery presence running, but TCP 56565 must be free.

After testing, stop the owned projection/service and uninstall this test APK,
which removes its private test secrets and temporary accessibility service. Keep
the installed production app and its prior permission/settings state unchanged.

`FileReceiveProbeActivity` is a separate, offline regression check for Android
10+ public Downloads. It needs no relay credentials, accessibility or screen
sharing. Launch it in this test APK, then read `files/file-receive-probe.json`
with `adb shell run-as com.remotedesk.relayhostprobe`. It reproduces the old
generic-MIME duplicate-name bug and checks the production receiver's filenames,
MIME types, contents and receipts. Every public test row has a per-run UUID;
only those rows are deleted at completion. Uninstall the test APK afterwards.
