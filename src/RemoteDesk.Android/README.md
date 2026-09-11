# RemoteDesk Android Agent

Android 1.0.3 支持查找附近的 RemoteDesk 设备，自动填入 IP 和实际端口。只填 IP 连接时也会探测端口。
已保存的直连设备会定期重新发现；地址或端口改变时，确认后可更新原记录并保留备注。
同网段可自动发现；跨网段或网络隔离时，请填 IP 探测端口，或使用私有中转的在线设备列表。

Android 1.0.2 增加“最近连接”：成功连接后保存最近 20 个直连或中转节点，点击即可重连，
右侧 `⋮` 可修改备注或删除记录。每个节点的口令和中转配置分别保留，并在本机加密存储。
双指上下滑动增加防抖，减少误缩放，支持抬指后续滚；直接触摸模式以双指中间位置作为滚动目标。

Android 1.0.1 修正了被控启动后的黑屏误导：Android 14+ 使用系统公开 API 请求整个
屏幕的录制授权；真正开始监听、且当前设置页在前台后，一次性返回系统桌面。
启动失败不会自动离开页面。设置页提供“返回桌面，保持被控”，停止服务需确认。
密码设置页仍可能被 Android/realme 的录屏保护遮蔽；此版本不关闭系统保护，
也不把敏感输入框标记成非敏感内容。录屏被用户或系统停止后仍需重新授权。
相关平台行为见 [Android 屏幕共享保护](https://developer.android.com/about/versions/15/behavior-changes-all#screen-sharing)
及 [整屏录制授权](https://developer.android.com/media/grow/media-projection#opt-out)。

首页分为“设备 / 本机被控 / 设置”。在“设备”页的“公网中继”填写已部署服务器的
地址和 root 密码，点击“登录服务器”。高级设置可改 SSH 端口（默认 22）和管理员账号。
中继端口、内部凭据和服务器身份自动通过 SSH 读取，验证连接后由 Android Keystore
加密保存。root 密码仅用于本次登录，不保存；下次自动连接，已有配置继续有效。
首次连接在线设备时填写该设备自己的密钥，成功后按设备分别记住；长按在线设备可更改。
登录后管理员表单收在“服务器设置”里；上线开关立即保存，退出服务器保留设备历史。
首次登录请在可信网络中完成。已保存的 SSH 身份变化时，会在发送密码前停止登录。
登录不会部署、更新或重启服务器；首次部署入口在 Windows。手机被控仍需要系统
录屏/无障碍授权，服务器登录不会替代这些权限。
详见 [接入说明](../../docs/PrivateRelay.md) 与 [公网六向实测记录](../../docs/RelaySixDirections-20260908.md)。

This is the Android controlled-end preview for RemoteDesk, with a lightweight
Android viewer path for cross-platform control testing. Mobile viewer controls and
the physical-phone regression are described in [Mobile UI check](../../docs/AndroidMobileUI-20260908.md).
See also the [follow-up edge/fault-injection check](../../docs/AndroidMobileUI-Recheck-20260908.md)
for drag preservation on repeated DeviceInfo, letterbox pinch, screen-transition cleanup and reconnect fencing.

Current scope:

- Uses a unified responsive visual system with a branded header, semantic status banners, grouped cards, clear primary/secondary/destructive actions, and a dark remote-viewer toolbar. Phone layouts scroll in one column, while wider landscape, tablet, and desktop windows switch to two columns without losing large-text accessibility.
- Saves a local connection password encrypted with Android Keystore, with automatic migration from the older plain preference value.
- Responds to UDP discovery while the Android UI is open, even before screen-recording permission is granted, so the Windows controller can see that the phone app is online.
- Requests Android screen capture permission through `MediaProjection`.
- Starts a foreground service so Android is less likely to stop the agent in the background.
- Can keep a lightweight foreground discovery service running without screen-capture permission, allowing Windows controllers to scan the phone as online even when the Android activity is not open.
- While the controlled-end screen stream is running, holds best-effort Wi-Fi low-latency/high-performance and partial CPU wake locks to reduce power-saving latency spikes. The lightweight discovery-only service does not take these streaming locks.
- Shows battery-optimization status and provides a shortcut to request ignoring battery optimizations, reducing device-specific background throttling during longer remote sessions.
- Exports or clears a diagnostic log from the Android UI and continuously persists a current private diagnostic log with recent UI status, discovery, authentication, streaming, JPEG adaptive profile changes, H.264 bitrate changes, clipboard, and file-transfer events for device-specific troubleshooting.
- Keeps periodic applied-ACK diagnostics off the UDP receive path with a latest-only asynchronous log mailbox, so slow flash writes cannot stall real-time video or pointer processing.
- Copies local connection details from the Android UI, including IPv4 addresses, ports, screen-capture state, Accessibility state, and H.264 status, so users can manually enter the phone IP on the Windows controller when UDP discovery is blocked.
- Shows local H.264 encoder diagnostics in the Android UI, including hardware/software acceleration, CBR/VBR support, or the reason the phone should fall back to JPEG. Streaming enumerates every Surface AVC encoder in hardware/unknown/software order, creates each candidate by its exact codec name, and records it only after a complete recovery access unit is actually produced so capability discovery cannot silently differ from runtime selection.
- Responds to RemoteDesk UDP discovery on port `56566` with platform `Android` and capability flags.
- Listens for authenticated RemoteDesk TCP connections on port `56565`.
- Applies a short read timeout during TCP authentication so half-open or unauthenticated clients cannot occupy the single controller slot indefinitely; normal blocking reads resume after authentication succeeds.
- Enables TCP no-delay, keep-alive, and buffer tuning on a best-effort basis so device-specific socket option failures do not block otherwise valid controller connections.
- Supervises authenticated sessions with monotonic TCP heartbeats and independent inbound-read watchdogs. A viewer that has received the current connection's `DeviceInfo` automatically reconnects after transient failures with a bounded `0.5/1/2/4/8/10` second backoff; bad passwords and never-qualified first attempts do not enter a reconnect storm.
- Uses the `connectedDevice` foreground-service type while waiting for or serving network peers and switches to the separately declared `mediaProjection` type while screen capture is active, avoiding Android 15's cumulative `dataSync` timeout.
- Gives every viewer connection its own input queue, control/heartbeat workers, write lock, decoder callbacks, and generation token. Host sessions likewise own their negotiated codecs/capabilities, frame geometry, key-frame state, Accessibility gesture state, and liveness tracker, preventing delayed work from a closed socket from corrupting a replacement session.
- Uses the same challenge authentication and AES-GCM message transport as the Windows host.
- Streams the Android screen as adaptive JPEG or low-latency H.264 Annex-B frames.
- Negotiates 60 FPS H.264 only when both peers advertise the high-frame-rate tier and the Android endpoint has a concrete hardware codec candidate; software/unknown compatibility codecs stay capped at 30 FPS. Host discovery likewise advertises short-GOP and high-frame-rate support only when a Surface-input encoder candidate actually exists.
- Automatically adjusts JPEG quality, FPS, and maximum stream edge based on capture, encode, and send pressure.
- Caps the Android virtual display surface before pixel copy on high-resolution phones, while preserving original screen size for input mapping.
- Detects screen rotation, Android 14 captured-content resize callbacks, and display-size changes while controlled. It reuses the one MediaProjection `VirtualDisplay`, swaps or rebuilds only its Surface, refreshes JPEG/input geometry, and restarts a fixed-size H.264 encoder on an aligned size without requesting a second projection grant.
- Preflights each AVC encoder's size/rate and vendor width/height alignment before configuration; unsupported hardware 60 FPS combinations try 30 FPS, while compatibility/software codecs remain capped at 30 FPS.
- Recovers transient `ImageReader`/pixel-buffer failures by replacing only the JPEG Surface on the existing `VirtualDisplay`, with bounded retry backoff so capture failure does not spin or tear down the authenticated control session.
- Reuses Android capture, scaling, and JPEG output buffers to reduce per-frame allocation and GC latency jitter.
- Parses viewer capability messages so the controlled-end can choose a mutually supported video codec; JPEG remains the fallback codec only when the viewer advertises JPEG support, and H.264 Annex-B is sent through `MediaCodec` when the viewer advertises support.
- The Android viewer enumerates every AVC decoder in hardware/unknown/software order and explicitly opens each candidate by codec name. When a decoder exists it advertises JPEG and H.264, decodes H.264 directly to a `SurfaceView` without a Bitmap or CPU pixel copy, and keeps JPEG as a negotiated compatibility fallback. Its windowed canvas starts below the toolbar, defaults to one-device-pixel-per-source-pixel whenever the full frame fits, and exposes both `fit/1:1` and live `H.264/JPEG clarity` switches. A candidate is reported as the actual decoder only after a non-empty decoded frame is rendered; codec-config/EOS output does not count, and a silent candidate is rejected and rotated.
- The Android viewer keeps a small bounded H.264 access-unit queue. A size change, Surface recreation, decoder failure, or saturated dependency chain resets the decoder and requests a fresh key frame; dependent P-frames never cold-start a decoder.
- The Android viewer decodes JPEG on a generation-owned latest-only worker rather than on the TCP reader or UDP reassembler thread. It shows a separate one-second health summary with codec, resolution, rendered FPS, bitrate, route, capture/encode timing, and input acknowledgement latency. H.264 rendered FPS is counted only from `MediaCodec.OnFrameRenderedListener`, not when a buffer is merely submitted to the Surface.
- Remote input is gated by the authenticated host's `InputControl` capability. A viewer connected to a watch-only host labels that state and does not enqueue touch commands; a capability change invalidates queued commands from the older generation.
- Recreates/attaches the Android viewer's `SurfaceView` at zero alpha before waiting for H.264 output after a JPEG preview. Codec/screen presentation epochs reject late JPEG work; a same-sized screen change also resets decoder Surface confirmation before revealing output.
- Provides a bottom keyboard/mode/mouse/screen/more dock. Trackpad is the default: slide to move without dragging, tap to click, long-press to drag. Direct touch, balanced left/right buttons, drag lock, two-finger right-click/scroll/local pinch zoom, original-pixel view, fullscreen restore, screen orientation and disconnect confirmation are available. Input and JPEG/H.264 rendering share one bounded transform.
- Supports native local IME composition followed by explicit Send (up to 128 UTF-16 code units), plus Esc/Tab/arrows/Enter/Backspace and common modifier shortcuts. Drafts are not persisted; a bounded keyboard batch is queued all-or-nothing, with release capacity reserved. Landscape uses a one-row text/shortcut switch and keeps the desktop visible above the IME; magnified text retains pixel size when the viewport resizes within the zoom bounds.
- Responds to later viewer capability updates during an H.264 session, allowing the Windows viewer to request JPEG fallback if H.264 decoding fails in automatic mode.
- Responds to Windows key-frame requests during an H.264 session so transient decoder stalls can recover before falling back to JPEG.
- Exhausts low-latency CBR, VBR, and default-mode attempts for explicitly named hardware `MediaCodec` candidates before trying unknown compatibility or known software codecs; adapts bitrate from observed send pressure and requests a fresh key frame after bitrate changes.
- Configures the Android H.264 host for a GOP1, zero-B-frame recovery baseline, recognizes both separate and in-band SPS/PPS/IDR recovery units, drains encoder backlog latest-only after sender pressure, and uses a sparse static-frame repeat hint when supported. Startup silence still rotates a broken codec, while a previously healthy static display is not torn down merely because its compositor emits no changed buffer.
- Registers an optional Accessibility Service. When enabled by the user, the agent advertises input-control capability, maps RemoteDesk pointer input to basic tap, drag, scroll, and pinch-zoom gestures, and inserts printable text into the currently focused Android input field.
- Shows local IPv4 addresses, ports, password readiness, screen-capture state, notification permission, battery-optimization status, H.264 encoder capability, accessibility status, advertised capabilities, discovery state, a copyable connection summary, and a diagnostic-log export action in the Android UI, and provides a stop button for the foreground service.
- Reports screen-capture and foreground-service launch failures in the UI and restores preview discovery when service startup is rejected by the system.
- Provides an adaptive launcher icon and resource-based app label so the installed agent is easy to identify on Android launchers.
- Wraps the Android control panel in a scrollable layout so small phone screens can still reach all controls.
- Switches the main panel between one and two columns using available window width and font scale, caps content width on tablets/desktop windows, and uses a compact-height viewer toolbar in short landscape or split-screen windows so the remote image remains visible.
- Draws edge-to-edge while applying system-bar, display-cutout, navigation, and IME safe insets; density and font-scale changes rebuild the viewer so Android can re-resolve scaled resources correctly, while UI-mode changes keep the active connection.
- Opens the Android notification settings from the agent UI, and lets users tap the foreground-service notification to return to the agent panel.
- Releases the preview UDP discovery socket before the foreground host service starts, reducing discovery-port handoff races.
- Retries binding the UDP discovery socket for a short period during UI-preview/service handoff, reducing transient "phone is open but cannot be scanned" cases when Android has not released the port yet.
- Maps a few keyboard shortcuts through Accessibility: `Esc` for Back, `Home` for Home, `F12` for Recents, `F11` for Notifications, and `Tab` for Quick Settings.
- Accepts pasted text from the Windows viewer (`Ctrl+V` / `Shift+Insert`), syncs it to the Android text clipboard, and writes it into the currently focused Android input field through Accessibility.
- Maps `Ctrl+mouse wheel` in the Windows viewer to Android pinch zoom; plain mouse wheel remains a scroll gesture.
- Maps right-click to Android Back, middle-click to Home, and Enter to focused text-field newline when connected to Android.
- Supports manual text clipboard sync over the encrypted RemoteDesk control channel. Android may still restrict clipboard reads depending on OS version and foreground state.
- Receives files sent by the Windows controller. Android 10+ exports completed files to public `Downloads/RemoteDeskReceived`; older systems fall back to the app-specific downloads folder. After checksum verification, each completed transfer is detached before the next batch item starts and is published by one bounded per-session finalizer; the authenticated read pump remains available for Ping/input, and disconnect/reconnect generation fences cancel queued work and suppress stale status writes.
- Provides a received-file list in the Android UI. Received files can be opened, shared, or inspected from the Android app; app-specific fallback files are exposed through a narrow read-only content provider.
- Can open an Android viewer window from the main UI by entering a remote `host:port` and password. The viewer authenticates with the same RemoteDesk protocol, prefers direct-to-Surface H.264 decoding when a local decoder candidate exists, and retains JPEG fallback.
- Monitors the active Android default network without binding the process to it. A Wi-Fi/cellular/VPN route loss or live handover closes the stale TCP/UDP owner, wakes reconnect backoff, and lets the next owner negotiate fresh sockets and keys on the new route.
- Coalesces touch motion with latest-wins semantics at an 8 ms cadence while keeping down/up, clicks, wheel, keys, and releases ordered on reliable TCP. When the authenticated UDP tier is negotiated, only mouse motion moves to UDP and reliable pointer edges temporarily gate motion back through TCP. On the controlled Android device, a capacity-one continued-stroke pump submits coalesced drag segments before release and closes an active stroke with a bounded final continuation during teardown. The Android viewer measures authenticated applied ACKs from hosts that provide them; the Android host still omits applied-ACK capability because accepting an Accessibility dispatch is not the same as receiving its asynchronous completion callback.
- Keeps discovery failures independent from an active host session: the responder closes and rebinds only its UDP socket with bounded retry and rate-limited diagnostics. Presence mode uses the Android `connectedDevice` foreground-service type rather than the Android 15 time-limited `dataSync` type; active capture switches to `mediaProjection`. Viewer and host UI lifecycle callbacks only signal socket/worker shutdown; decoder, UDP, and gesture joins run on their owning background worker so Activity/Service teardown does not wait for multi-second cleanup. Periodic UDP input-ACK diagnostics use a capacity-one asynchronous writer instead of doing file I/O on the receive thread.

Not implemented yet:

- Android viewer clipboard/file-send controls and full remote-file workflows.
- Broader real-device H.264 latency testing, adaptive bitrate threshold tuning, and compatibility testing.
- Silent remote start of screen capture without Android user confirmation.
- Full external hardware-key mapping, live per-keystroke remote IME composition,
  and remote native multi-touch (viewer pinch currently zooms the local canvas).
- Broader signed-release upgrade coverage across Android vendors; the connected realme test phone uses the dedicated release keystore.

Build notes:

- Requires Android SDK and JDK 17.
- The Gradle Wrapper is included. From `src/RemoteDesk.Android`, run `./gradlew assembleDebug testDebugUnitTest` (or `gradlew.bat` on Windows).
- The current project uses compile/target SDK 36 and Android Gradle Plugin 8.10.1.
- Release signing can be configured by copying `release-signing.properties.example` to `release-signing.properties`, or by setting the `REMOTEDESK_ANDROID_*` signing environment variables. The real properties file and keystores are ignored by git.
