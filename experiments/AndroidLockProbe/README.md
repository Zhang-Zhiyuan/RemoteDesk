# Android lock-screen API probe

Isolated test APK, not part of the product. It has no Internet permission and
cannot read production preferences. Enable its accessibility service only on an
authorized test phone. Each explicit broadcast to `CaptureReceiver` requests one
official accessibility screenshot; secure content is not bypassed. Results and
images remain in this APK's private storage and must not be published.

Build with the repository Gradle wrapper using `-p experiments/AndroidLockProbe
assembleDebug`. Restore the previous accessibility configuration and uninstall
the probe after testing. Do not disable the device's PIN or screen-share protections.
