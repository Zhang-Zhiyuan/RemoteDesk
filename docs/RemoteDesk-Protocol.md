# RemoteDesk Protocol Notes

> Public copy: device and server addresses in historical results are anonymized documentation examples, not live endpoints.

This document captures the RemoteDesk protocol surface that Windows, Android, and the Linux host/viewer package keep byte-compatible with. Linux support is still being hardened, so Linux-specific behavior should be verified with the sandbox and the packaged `remotedesk-linux-doctor` output.

## Transport

- TCP default port: `56565`.
- Server greeting: ASCII `RDK1`, followed by a 32-byte nonce.
- Client authentication proof: ASCII `AUTH`, followed by `HMACSHA256(SHA256(password UTF-8), nonce)`.
- Server returns one byte: `0x01` for accepted, otherwise rejected.
- Session keys are derived with HMAC-SHA256:
  - master: `HMAC(passwordKey, "RemoteDesk session v1" + nonce)`
  - client-to-server: `HMAC(master, "client->server")`
  - server-to-client: `HMAC(master, "server->client")`
- Each encrypted record is length-prefixed with a little-endian `Int32`, then AES-GCM ciphertext plus tag.
- AES-GCM nonce is four zero bytes plus an `Int64` little-endian sequence number.
- Hosts admit only one authenticated viewer at a time and use latest-authenticated
  viewer wins. After a replacement viewer authenticates, the host atomically
  transfers ownership, sends encrypted control kind `32` (`SessionRejected`)
  with one bounded UTF-8 takeover reason to the previous viewer, and closes the
  previous socket. A current viewer treats that reason as terminal and disables
  automatic reconnect so two viewers cannot continuously replace each other.

## Message Envelope

After decryption, each message is:

- `Byte messageType`
- `Int32 payloadLength`
- `payload`

Common message types:

- `1`: JPEG frame
- `3`: control message
- `6`: video frame

All multi-byte integers are little-endian. Strings use the .NET `BinaryWriter` 7-bit encoded byte length followed by UTF-8 bytes.

Message type `1` has a 24-byte frame header: `Int32 width`, `Int32 height`,
`Double captureMilliseconds`, and `Double encodeMilliseconds`, followed by the
JPEG bytes. Message type `6` has a 32-byte video-frame header:
`Int32 encoding`, `Int32 width`, `Int32 height`, `Int32 flags`,
`Double captureMilliseconds`, and `Double encodeMilliseconds`, followed by the
encoded bytes. Encoding `1` is JPEG and encoding `2` is H.264 Annex-B. Flag bit
`0` marks a key frame and bit `1` marks included codec configuration. A timing
value of `0` means that the sender cannot report that stage separately; viewers
must treat it as unknown rather than a measured zero-duration operation.

Senders and receivers reject non-positive dimensions, an edge above 32,768,
or a frame above 16,777,216 pixels. Oversized aggregate Windows desktops are
scaled proportionally before capture output is admitted. JPEG/PNG display
paths inspect the dimensions embedded inside the compressed image before
allocating the decoded bitmap; Windows and Android also require those
dimensions to match the authenticated frame header. Windows and Linux FFmpeg
fallback decoders additionally cap one FFmpeg allocation at 128 MiB.

The current latest-only H.264 path requires zero B frames and no display-order
reordering. Without short-GOP negotiation, a frame routed over UDP must carry
both `KeyFrame` and `CodecConfig`, so it can be decoded without any earlier
video frame. When `ShortGopH264` is negotiated, the strict GOP2 Windows path
may instead route one dependent P access unit immediately after the matching
configured IDR; the generation and sequence gates described below make that
two-frame chain the only exception. Senders that cannot maintain these
properties must use the ordered TCP path or negotiate JPEG instead.

On Windows 8 and later, a viewer may advertise H.264 without an external
`ffmpeg` executable because the in-process path probes the inbox H.264 MFT and
D3D11 video device at the first independent access unit. Successful output is
an NV12 D3D11 texture that is presented directly with `VideoProcessorBlt` and a
five-buffer flip-model swap chain. Presentation uses sync interval zero and,
when DXGI reports support, the matching allow-tearing flags. The presenter does
not force `MaximumFrameLatency=1`, because that setting can turn an unsynchronised
present into an implicit DWM wait on some drivers. A static native capability failure hands that
same independent access unit to the external FFmpeg fallback when available;
automatic mode otherwise updates `ViewerInfo` to JPEG. A failed dependent
access unit is never used to bootstrap a new decoder: the viewer waits for and
rate-limits a request for a fresh `KeyFrame | CodecConfig` frame.

For a GOP1 stream, every access unit is independently recoverable. If the inbox
MFT accepts such an access unit but withholds output, the Windows viewer issues
`MFT_MESSAGE_COMMAND_DRAIN`, pumps the retained texture, restarts the stream,
and marks the next input as a discontinuity. It never applies this operation to
a dependent GOP, where draining would destroy the reference chain. On the
`192.0.2.249` test system this first removed a fixed one-frame wait in an
earlier candidate (`43 ms` to `1.39 ms`). The final exact-package gate then
measured `0.89/0.88/0.89 ms` in three consecutive runs.

Viewer scaling is local presentation policy and does not change the encoded
dimensions in the message header. When source and destination sizes differ,
the D3D11 presenter best-effort enables the driver's video-processor edge
enhancement; if the filter is advertised but rejected at blit time, it disables
the filter and retries without dropping the presenter. `F11` uses the complete
bounds of the current monitor for borderless fullscreen and hides the status
and progress areas. These measures improve non-native scaling, but cannot
reconstruct source pixels lost by transport scaling or a lower-resolution
viewer display.

Windows H.264 capture selection is also an implementation policy rather than a
wire guarantee. At 4K the GOP1 encoder target is `0.24 bit/pixel/frame`, about
`59.7 Mbit/s` at 30 FPS. From 30 to 60 FPS the per-frame target is interpolated
continuously down to `0.16 bit/pixel/frame`, so raising the frame rate never
lowers the total bitrate. The resolution policy is likewise blended from QHD
to UHD so crossing an even-size boundary cannot reduce the budget. At 4K60
the native geometry is retained at about `79.6 Mbit/s`, capped at
`160 Mbit/s`. When FFmpeg exposes
`gfxcapture` and the physical monitor can be mapped to a DXGI adapter, the host
first uses a two-frame Windows Graphics Capture pool and keeps the native D3D11
surface on the selected encoding adapter. A candidate is usable only after it
emits a complete recovery access unit. The first WGC/NVENC candidate receives
one bounded `2.5 s` cold-driver allowance. A real first-frame timeout skips the
remaining WGC adapter candidates and proceeds directly to DDA; a fast typed
failure may still try the next adapter. DDA then falls through to precisely
bounded GDI capture with hardware encode, so a failed source does not trigger
indefinite encoder rotation. Native landscape UHD at 60 FPS requests a
240 FPS WGC update interval; the measured compositor then supplies about 160
distinct source frames/s. The filter keeps at most the first source surface in
each 60 Hz timestamp bucket and rebases that selected sequence to an exact 60
FPS encoder time base. It does not synthesize frames after a static WGC pause,
and this policy is not applied to 30 FPS, portrait, or scaled capture. FFmpeg's
private RTP sender is explicitly bound to `127.0.0.1` so the local marker-based
frame boundary path cannot request a public/private Windows Firewall exception.

On the local audit machine, the pre-release source build selected
`gfxcapture/monitor1/adapter0 → h264_nvenc` and passed the 30-second native
`3840x2160` loopback gate for frame count, cadence, MF/D3D11 presentation, UDP
assembly, and input acknowledgement. On `192.0.2.249`, the older exact-package
candidate selected `gfxcapture/monitor0/adapter1 → h264_nvenc` without GDI,
CPU full-frame readback or spatial scaling, but its repeated evidence remains a
4K30 historical baseline. The release report must still record final
exact-package two-machine cadence, hardware presentation, input latency and
long-soak results before 4K60 is accepted for release; those measurements must
not be inferred from the configured `60 FPS` value or from local loopback. DDA remains a fallback
on this entity because its AMD output opens but did not emit a complete
independently decodable AU within the startup deadline. These measurements are
validation status, not protocol requirements.

The external FFmpeg compatibility decoder reads BGRA directly up to roughly
two megapixels. Above that threshold it uses a single-worker MJPEG bridge with
`yuvj444p` and quality `q=2` to avoid the chroma blur and raw-pipe bandwidth of
the previous high-resolution fallback. This is still a software/bitmap
compatibility path, not a zero-copy surface path.

## Capabilities

Device capabilities are an `Int32` bitmask in `DeviceInfo`; viewer-selected capabilities are echoed with `ViewerCapabilities`.

- `1 << 0`: remote desktop
- `1 << 1`: input control
- `1 << 2`: clipboard text
- `1 << 3`: file receive
- `1 << 4`: capture target selection
- `1 << 5`: remote start
- `1 << 6`: file drop/paste
- `1 << 7`: file send
- `1 << 8`: file checksum
- `1 << 9`: file transfer cancel
- `1 << 10`: file transfer preview/confirmation
- `1 << 11`: remote self-update
- `1 << 12`: clipboard input sequence tracking
- `1 << 13`: low-latency UDP video v1
- `1 << 14`: UDP video congestion feedback
- `1 << 15`: low-latency UDP video XOR FEC
- `1 << 16`: authenticated latest-only UDP mouse motion
- `1 << 17`: post-injection acknowledgement for UDP mouse motion
- `1 << 18`: strict short-GOP H.264 decode support
- `1 << 19`: H.264 frame rates above 30 FPS
- `1 << 20`: authenticated UDP heartbeat support
- `1 << 21`: high-quality JPEG desktop-text profile
- `1 << 22`: negotiated stable installation identity
- `1 << 23`: native Ctrl+V clipboard paste (Android accessibility input)
- `1 << 24`: correlated file-save receipts (`FileTransferReceipt`, control kind `35`)

When a host advertises bit 22, an authenticated viewer may send control kind
`33` (`DeviceIdentityRequest`, no fields). The host replies with kind `34`
(`DeviceIdentity`) followed by a bounded .NET UTF-8 string containing a canonical,
nonzero UUID. The host never sends kind 34 unsolicited. `DeviceInfo` is unchanged,
so legacy clients need not understand either new control kind. An identity is
persistent per installation, not a hardware fingerprint or authentication secret.
Saved-device aliases are merged after the authenticated reply; matching names or
unauthenticated discovery responses alone do not rewrite the device book.

Linux clients should only send `ViewerCapabilities` bits that the remote host already advertised.
The Linux protocol probe recognizes bits 13-21 when inspecting a Windows peer.
The Linux host/viewer advertises `ShortGopH264` so that it can decode a
negotiated Windows GOP2 stream over TCP, but it does not advertise bits 13-17
and therefore does not negotiate the UDP video, feedback, FEC, or mouse route.
The current Android host/viewer understands bits 13-20 and advertises only the
locally available subset. Both Android roles advertise the UDP video,
congestion-feedback, XOR-FEC, and authenticated-heartbeat tiers. The Android
viewer advertises UDP mouse motion and applied-ACK support; the Android host
advertises UDP mouse motion only when Accessibility input is enabled and
intentionally omits bit 17 because a move is acknowledged when it enters the
latest-only gesture pump, before the asynchronous Accessibility completion
callback can prove system application. Android H.264 endpoints
advertise bit 18 when an AVC codec candidate exists, and bit 19 is gated on an
actual hardware codec candidate. All extensions are enabled from the strict
intersection of the authenticated peers' capability masks.

The Android Surface-input encoder is configured for GOP1 with zero B frames,
validates a complete SPS/PPS/IDR recovery access unit before accepting a codec
candidate, and requests a recovery frame again after a transport fallback.
H.264 is negotiated at up to 60 FPS only when both peers advertise bit 19 and
the selected Android codec is hardware accelerated; compatibility, unknown,
and software codec paths are capped at 30 FPS. Vendor MediaCodec behavior and
end-to-end latency still require validation on the corresponding physical
device; capability negotiation alone is not that evidence.

Capture targets use the existing authenticated control kinds
`CaptureTargetList`, `CaptureTargetChanged`, and `SelectCaptureTarget`. A host
may resend the list and current target during the same session when display
topology changes. Windows additionally carries target availability through a
backward-compatible `ClipboardStatus`: a human-readable message is followed by
one final line of the form
`RemoteDesk.CaptureTargetStatus/v1|state|base64-id|base64-name|generation`.
New peers classify it only when the marker is unique, is the exact final line,
has canonical strict-UTF-8 Base64 fields, and has state `available` or
`unavailable`; otherwise it remains ordinary clipboard-status text. The
generation orders target changes against JPEG/H.264 frame admission so an old
screen cannot reappear after the viewer has cleared it. A missing explicitly
selected physical monitor is fail-closed rather than remapped to an aggregate
desktop. For an active UDP video route, a target-generation transition first
completes the authenticated `LowLatencyVideoStop` / `LowLatencyVideoStopped`
TCP barrier, then publishes the target list/change/status bundle, and only then
opens new-generation frame admission. Stop reason `4` preserves UDP heartbeat
and mouse motion only when that authenticated input route is actually healthy;
otherwise reason `1` performs a complete UDP fallback. Viewers synchronously
clear pending JPEG/H.264 work, decoder/presenter state, and the last bitmap at
the transition, and reject old-generation results that complete afterward.

Bit 6 describes remote file drop/paste protocol support; it does not guarantee
that the local Windows shell allowed OLE drag/drop registration for a particular
 viewer window. If that registration fails, the Windows UI disables only inbound
 drag for that window and keeps viewing, control, and the session alive. Copying
 files and using the main-window paste-files action remains an independent way to
 invoke the same negotiated file-transfer path. Keyboard paste inside the Windows
 viewer is sent as physical input and therefore uses the remote clipboard.

The Linux host prototype in `scripts/linux/remotedesk_linux_host.py` advertises:

- `RemoteDesktop`
- `ClipboardText`
- `FileReceive`
- `CaptureTargetSelection`
- `FileSend`
- `FileChecksum`
- `FileTransferCancel`
- `FileTransferPreview`
- `ShortGopH264`
- `HighFrameRateH264`

`HighFrameRateH264` is an explicit end-to-end viewer gate, not merely an
encoder option. A host configured above 30 FPS clamps H.264 to 30 until the
viewer sends bit 19. The Windows viewer declares the capability as part of its
MF/D3D11 H.264 implementation. The Linux viewer starts without it and sends an
updated capability only after mpv confirms both non-copy hardware decode and
native GPU-surface presentation; it withdraws the bit if that path fails.
Unknown/older viewers therefore remain at 30 FPS. A Windows software GDI
capture fallback is also capped at 30 FPS even when bit 19 was negotiated.

## Text clipboard interoperability

Clipboard controls retain their legacy wire format: `4` requests text, `5`
writes text, `6` returns text, and `7` acknowledges a write or reports a
clipboard failure. Text is UTF-8 with a .NET 7-bit byte-length prefix; the
256,000-character limit counts UTF-16 code units on all three platforms.
Clipboard text is never silently truncated.

Viewers allow one outstanding clipboard operation per physical connection.
Pasting waits for a successful write acknowledgement. An eight-second timeout
does not authorize paste or local replacement; its reply slot is retained
until the late response is drained, or the connection is replaced. Empty,
unsolicited, stale and superseded text replies do not clear the local clipboard.
The capture-target status trailer in kind `7` is not a clipboard acknowledgement.
Android bit 23 is advertised only when input accessibility is available; older
Android hosts may require long-press paste. Android background clipboard read
restrictions are reported as failures, not empty successful reads.

## Low-latency UDP video v1

Current Windows and Android peers advertise `LowLatencyUdpVideo` in
`DeviceInfo` or `ViewerCapabilities`, according to their role. If either side
omits the bit, all frames remain on the authenticated TCP session. Linux keeps
using that compatibility path because it does not advertise bits 13-17, while
still being able to advertise bit 18 and decode a negotiated strict GOP2 H.264
stream over TCP.

The current Android implementation uses the same authenticated UDP v1 wire
contract as Windows: AES-GCM authenticated frame fragments, endpoint pinning,
replay protection, latest/adjacent-frame reassembly, `FeedbackV2`, adaptive XOR
FEC, latest-only mouse motion, and authenticated heartbeat datagrams. Reliable
pointer edges and every non-motion input remain on TCP. UDP shutdown and
fallback use the reliable TCP `LowLatencyVideoStop` / `LowLatencyVideoStopped`
ordering barrier; a video-only fallback may preserve a healthy authenticated
UDP mouse route, while route failure falls back to TCP without terminating an
otherwise healthy session. The Android host deliberately does not advertise
the optional mouse applied-ACK bit, although the Android viewer can consume
authenticated acknowledgements from a host that genuinely supports it.

`UdpVideoCongestionFeedback`, `LowLatencyUdpVideoXorFec`,
`LowLatencyUdpMouseInput`, and `LowLatencyUdpMouseInputAppliedAck` are
separately negotiated extensions to the base UDP route. A sender enables an
extension only when both peers advertised its capability; adaptive XOR FEC
additionally requires congestion feedback, and mouse-apply acknowledgements
additionally require UDP mouse input. `ShortGopH264` is transport-independent:
it can be used on TCP, and it also enables the dependent-P exception on an
active UDP route. The current Windows host uses GOP2 only when the viewer
advertises bit 18. If that viewer also advertises the UDP base bit, it must
advertise congestion feedback as well for the host to select GOP2. The
capture/configured frame-rate ratio must also be chain-safe: equal and 2:1 are
the general cases, and the Windows adaptive DDA selector also permits its
explicit 3:1 source/configuration tier. In that tier the idle selector sends
only deadline-selected recovery IDRs at the configured rate and suppresses
each skipped IDR's dependent P; a healthy interactive UDP window may send every
complete IDR/P pair. This is a sequencing rule, not evidence that a particular
device reaches the requested capture rate. Any other ratio retains GOP1. A peer that supports only
`LowLatencyUdpVideo` continues to use the version-1 bind, frame-fragment,
legacy feedback, and TCP fallback behavior without seeing any extension.

The implementation also has a local `LowLatencyVideoFeatures` bitmap:
congestion feedback is bit 0, XOR FEC bit 1, UDP mouse bit 2, mouse applied ACK
bit 3, and short-GOP H.264 bit 4. This bitmap is not serialized in
`LowLatencyVideoOffer`; each endpoint derives it from the authenticated peer
capabilities. Normalization rejects extension sets without congestion feedback
and rejects mouse ACK without UDP mouse input, so both sides arrive at the same
usable subset.

When `LowLatencyUdpMouseInput` is negotiated, only absolute `MouseMove`
commands use authenticated datagram kind `7`. The input payload remains the
existing fixed-size message-type `2` layout and uses the datagram frame sequence
as a strictly increasing motion sequence. The viewer keeps one replaceable
pending motion and the host accepts it only from the pinned authenticated
endpoint after AES-GCM and replay validation. Mouse buttons, wheel, keys, text,
and all release events stay on reliable TCP; each click/wheel command carries
its own coordinates, and the host serializes its complete cursor-position plus
`SendInput` transaction against UDP motion. While a reliable pointer event is
queued or being written, later motion temporarily remains in the same TCP
ordering domain; the viewer keeps a 25 ms TCP drain window after the write,
discards an unsent UDP motion before the reliable event, and invalidates stale
dequeued TCP motion when switching back to UDP. If the UDP route is absent or
fails, motion immediately returns to the existing coalescing TCP input queue.

For reliable `KeyDown`/`KeyUp`, `Data` remains the portable Windows virtual-key
value. New Windows viewers also place the native scan code in `X`; `Y` bit 0
means that `X` contains a scan code and bit 1 marks an extended key. A Windows
host injects that physical identity with `KEYEVENTF_SCANCODE`; the Linux X11
host uses it to distinguish right/left modifiers and main/keypad keys. Legacy
viewers leave `X/Y` zero, so both hosts retain the virtual-key fallback without
changing the fixed 14-byte input payload.

When `LowLatencyUdpMouseInputAppliedAck` is also negotiated, the host queues an
authenticated datagram kind `8` only after the corresponding latest-only motion
has returned from the platform input-injection call. The acknowledgement carries
the applied motion sequence in the datagram frame-sequence field and has no
plaintext payload. It is independently latest-only and rate-limited to one
datagram per 8 ms, so feedback cannot delay input or video. The viewer validates
the pinned endpoint, AES-GCM tag, replay window, negotiated feature, and empty
metadata before matching it to a bounded 256-entry send-timestamp ring. This
measures viewer-send through actual host injection acknowledgement; failure of
the optional measurement path never disables mouse input.

The host sends `LowLatencyVideoOffer` over encrypted TCP. It contains protocol
version `1`, an ephemeral UDP port, a maximum datagram size of `1200`, an
`8 MiB` frame limit, random channel/epoch identifiers, independent 256-bit keys
and four-byte nonce prefixes for each direction, and a random bind challenge.
These keys are generated per connection and are never the keys or implicit
sequence counters used by the TCP `SecureSession`.

The viewer sends an authenticated UDP `BindProbe` that echoes the challenge.
The host accepts it only from the IP address of the authenticated TCP peer,
pins the observed UDP source port, and returns an authenticated `BindAck`.
The viewer then sends `LowLatencyVideoReady` over TCP; the host continues
sending TCP frames until that message is received.

The viewer retries the probe every 200 ms for a 3.0-second bind window and
binds its UDP socket to the local address selected by the authenticated TCP
connection when that address is available. The host starts its offer clock
before the viewer receives the TCP offer, so the first Offer-to-Probe stage
stays open for 3.5 seconds. The first valid authenticated probe starts a fresh,
independent 2.0-second Probe-to-Ready deadline. This two-stage policy prevents a
final valid `BindAck` followed by the reliable `LowLatencyVideoReady` write
from losing an absolute-deadline race. TCP video remains eligible throughout
negotiation; a failed bind therefore falls back without freezing first-frame
delivery. Once the route has delivered frames, its independent steady-state
frame-stall deadline remains 2.5 seconds. Timeout diagnostics aggregate probe,
address-rejection, authentication-failure, and acknowledgement counts without
logging every rejected datagram.

Each UDP datagram has a 52-byte authenticated header:

- ASCII magic `RDU1`, version, packet kind, and header length
- channel id, epoch, and per-direction `UInt64` packet sequence
- frame sequence and total frame length
- fragment offset, index, count, and plaintext length
- original frame message kind and flags

The header is AES-256-GCM AAD. The nonce is the negotiated four-byte direction
prefix followed by the little-endian packet sequence. The ciphertext and
16-byte tag follow the header. Packet sequences never wrap, invalid
authentication does not advance the replay window, and duplicate or packets
more than 4096 positions behind the highest authenticated packet are rejected.

The UDP route carries either the existing message-type `1` JPEG payload or the
message-type `6` video-frame payload without changing either inner layout.
JPEG frames are always independently recoverable. Without short-GOP
negotiation, only video frames whose flags are exactly
`KeyFrame | CodecConfig` may enter the capacity-one UDP sender; a dependent or
unknown flag set disables the UDP route through the normal TCP ordering barrier
before the sender resumes that stream on TCP.

With `ShortGopH264`, a configured IDR establishes a recovery generation and one
matching dependent P frame may enter UDP only after that IDR is currently being
serialized or has completed serialization. A second dependent frame, a P frame
without a live matching generation, or an orphan left behind by a failed IDR is
suppressed without moving the stream to TCP; a strict GOP2 source supplies
another recovery point within at most two source periods. The receiver publishes
a configured IDR and then only the next consecutive frame sequence as its
dependent P. A sequence gap, duplicate, out-of-order frame, or dependent frame
while waiting for recovery is discarded and closes the gate until the next
configured IDR. This prevents a P frame from being decoded against the wrong
reference chain.

The viewer validates the `8 MiB` / 8192-fragment limits before allocating,
drops an incomplete older frame when a newer frame arrives, and publishes only
a complete independently owned payload. Incomplete assemblies expire after
250 ms.

After activation a legacy viewer sends authenticated feedback every 200 ms; a
viewer using `FeedbackV2` reports every 100 ms so the sender can react sooner.
The host tolerates an authenticated-feedback gap of up to 2.5 seconds, aligned
with the viewer's established-frame stall deadline. A longer gap makes the host
atomically route subsequent frames back to TCP; missing complete frames makes
the viewer request the same fallback over TCP. Repeated deadline observations
cannot emit the ordering barrier more than once for the negotiated route.
`LowLatencyVideoStopped` is the TCP ordering barrier: the viewer continues to
ignore possibly stale TCP frames until it receives this acknowledgement, then
resumes the normal TCP frame path. UDP failure never cancels the TCP control,
reliable input, file-transfer, or heartbeat session.

### Congestion feedback and pacing

When `UdpVideoCongestionFeedback` was negotiated, the viewer sends authenticated
`FeedbackV2` datagrams instead of relying only on the legacy highest-complete
frame acknowledgement. The extended feedback reports recent delivery progress
and loss observations so the host can distinguish an alive but congested route
from a healthy one. It remains advisory: authentication, endpoint pinning,
replay protection, feedback timeout, and the TCP `LowLatencyVideoStopped`
ordering barrier are unchanged.

`FeedbackV2` is datagram kind `5`. Its plaintext is exactly 96 bytes, with all
fields little-endian:

| Offset | Type | Field |
| ---: | --- | --- |
| 0 | `UInt64` | receiver elapsed microseconds |
| 8 | `UInt64` | largest authenticated packet sequence |
| 16 | `UInt32` | acknowledgement delay in microseconds |
| 20 | `UInt32` | valid-field-group mask |
| 24 | `UInt64` | authenticated packet count |
| 32 | `UInt64` | authenticated wire-byte count |
| 40 | `UInt64` | settled lost-packet count |
| 48 | `UInt64` | late-packet count |
| 56 | `UInt64` | highest completed frame sequence |
| 64 | `UInt64` | completed-frame count |
| 72 | `UInt64` | abandoned incomplete-frame count |
| 80 | `UInt64` | consumer-dropped-frame count |
| 88 | `UInt16` | consumer queue depth |
| 90 | `UInt16` | consumer queue capacity |
| 92 | `UInt32` | average decode time in microseconds |

The valid mask prevents a receiver from presenting reserved zero values as real
measurements. The initial implementation marks only the packet-delivery and
frame-assembly groups as valid; consumer drop/queue/decode fields are reserved,
remain zero, and must not influence pacing until their valid group is set.
Counters are cumulative within one negotiated UDP transport, allowing the host
to derive interval deltas without trusting unauthenticated local state.

The host paces frame fragments rather than emitting a complete large frame as a
single burst. Pacing is updated from `FeedbackV2`, bounds its estimate to a
`180 ms` frame-send budget, and still keeps only the newest pending captured
frame. Once a recovery IDR starts serialization, it is allowed to finish:
neither a dependent P frame nor a newer recovery IDR aborts it. This avoids
starving the receiver of a usable recovery point under sustained capture
pressure. A pending recovery IDR may abort an in-flight dependent P frame at a
data/parity-fragment boundary; all in-flight frames remain bounded by the same
absolute `180 ms` budget. The capacity-one pending slot remains latest-only,
although an in-flight recovery IDR and its one matching pending P frame may
briefly coexist. On a clean high-rate LAN, negotiated short-GOP frames at or
above a `100 Mbit/s` pacing estimate may bypass per-fragment delay while the
frame is at most `256 KiB`; the absolute budget and congestion fallback still
apply.

For negotiated GOP1 video, the sustained congestion estimate remains derived
from encoded bitrate and recent feedback (about `102 Mbit/s`, including
fragment/FEC allowance, for the measured native 4K60 profile). On a clean route
with no adverse feedback, an independent frame of at least `128 KiB` uses a
`160 Mbit/s` serialization floor. This sends the measured `189 KiB` on-wire
frame in about `9.7 ms`, preventing one frame from pacing into the next WGC
timestamp bucket. Loss, stalled feedback, or receiver pressure immediately
restores the congestion target as a strict cap; this floor is not a claim that
the route has 160 Mbit/s sustained capacity.

The latency-critical UDP video sender, host/viewer receivers, mouse sender, and
mouse applied-ACK sender run as named synchronous dedicated threads rather than
thread-pool continuation loops. Receive and mouse paths use `AboveNormal`
priority; the video sender remains `Normal`, and feedback remains an async
control task.

If feedback is absent, invalid, stale, or indicates that the UDP route cannot
make forward progress within the latency budget, the host stops the UDP route
and resumes TCP through the existing ordering barrier. A peer without bit 14
continues using legacy feedback and the original sender behavior.

### XOR forward-error correction

When `LowLatencyUdpVideoXorFec` was negotiated, datagram kind
`FrameXorParity` (`6`; kind `5` is `FeedbackV2`) can add one XOR parity fragment
for each group of up to 16 data fragments. The UDP protocol/offer version stays
at `1`. The current sender enables parity adaptively after moderate loss, turns
it back off on a clean route or severe congestion, and protects only frames with
at least eight data fragments. A negotiated receiver accepts any otherwise
valid protected group.

For group `g`, the protected data-fragment indexes are
`[g * 16, min(g * 16 + 16, N))`, where `N` is the frame's data-fragment count
and `M = maxDatagramBytes - 52 - 16` is the maximum plaintext fragment size.
The parity plaintext is the bytewise XOR of that group, treating the missing
right side of a short final fragment as zero. Its authenticated header repeats
the frame sequence, total frame length, and original frame kind; uses
`FragmentIndex = g`, `FragmentOffset = g * 16 * M`,
`FragmentCount = N`, and zero flags; and carries
`min(M, frameLength - FragmentOffset)` plaintext bytes.

One parity fragment can recover exactly one missing data fragment in its group.
Two or more missing fragments in the same group remain incomplete. Completion
never waits for parity when all data fragments have already arrived, and FEC
does not delay newer frames or retransmit old ones. Unnegotiated parity is
rejected. An unrecoverable frame is handled by the existing latest-frame
eviction and 250 ms assembly expiry; sustained absence of complete frames still
requests the normal automatic TCP fallback.

## Versioning

`DeviceInfo` carries machine name, platform, and capabilities. New Windows peers also send `DeviceBuildInfo` with a build stamp string in `yyyyMMddHHmmss` UTC format before `DeviceInfo`; the stamp is generated by the publish script from the packaging time and is used only for ordering builds. New peers also accept a transitional `DeviceInfo` packet with an appended build stamp and treat legacy packets without any stamp as version unknown. Old peers can ignore `DeviceBuildInfo` and continue to process `DeviceInfo`.

## File Transfer

The maximum accepted chunk size is `128 KiB`; current interactive senders use
`32 KiB` chunks to reduce head-of-line blocking with input and video messages on
the shared TCP session. The single file limit is `1 GiB`. One authenticated
receive session may accept at most 128 files and 2 GiB of cumulative declared
length. Before reserving a transfer, receivers also require enough free space
for the declared file plus a 256 MiB safety reserve (Android public-download
publication may require space for both the temporary and published copy).

Sender sequence:

1. `FileTransferStart`: transfer id, file name, declared length.
2. `FileTransferChunk`: transfer id, expected offset, chunk length, chunk bytes.
3. Optional `FileTransferChecksum`: transfer id, algorithm `SHA256`, lowercase hex digest.
4. `FileTransferComplete`: transfer id.

Receivers advertising `FileTransferReceipt` send control `35` only to viewers
that also advertise this capability: transfer id (bounded .NET string), success
(Boolean), message (bounded .NET string). Success means checksum verification,
file closure and final publication/rename have completed, not merely that bytes
were received. Start/chunk/checksum/publication errors use a negative receipt for
that same id. Cancellation is never a successful save. Android's asynchronous
publication queue carries the id through to the final callback.

New viewer uploaders wait up to 120 seconds after COMPLETE for that file's save
receipt. Unrelated or late ids do not complete another transfer. Disconnect and
cancel end the wait. A timeout means the save outcome is unknown; it does not
prove that no file exists. Legacy peers keep kind `11` status messages and must
not be represented as providing verified save completion. Remote app updates
retain their separate existing restart/status workflow.

File and text clipboard controls use the same authenticated encrypted session
over both direct TCP and opaque relay tunnels. No additional public file port
or server-side file storage is required. File selection/preview confirmation is
on the initiating device; authenticated unattended receivers save to the receive
directory with safe, non-overwriting names. Confirmation is invalidated if its
connection is replaced. Android uploads use the system document picker (one
document, up to 1 GiB); providers without a known stream length must first
download the document locally. This is distinct from remotely reading another
Android application's file clipboard, which is not supported.

If `FileTransferCancel` was negotiated, a sender that fails after `FileTransferStart` should send `FileTransferCancel` with the same transfer id and a short reason. Receivers should delete the temporary `*.rdtransfer` file immediately.

When returning files from the controlled side to the viewer, a peer that sees `FileTransferPreview` in `ViewerCapabilities` should send `FileTransferClipboardFilesPreview` before any `FileTransferStart`. The preview contains one row per file/folder with item type, original path, transfer file name, byte size, destination hint, and optional note. Actual file bytes must wait for `FileTransferConfirmClipboardFiles`; `FileTransferRejectClipboardFiles` cancels the pending return list.

Receivers must reject:

- out-of-order chunks
- chunks exceeding the declared length
- missing checksum when checksum was negotiated
- checksum mismatch
- invalid file names or paths that escape the receive directory
- a per-session file-count or cumulative declared-byte quota violation
- insufficient target-disk space for the file and safety reserve

Senders should snapshot source file length and modification time before transfer and abort if they change before completion.

## Remote Update

Windows controlled hosts advertise `RemoteUpdate` when the running canonical `RemoteDesk.exe` has a valid embedded build stamp and is either unsigned or has a Windows-trusted embedded Authenticode signature. The viewer can push a newer local build to the host with `RemoteUpdateStart`, whose payload is identical to `FileTransferStart`: transfer id, file name, and declared length. The file name must be `RemoteDesk.exe` and the declared length must be positive and within the normal file limit. The remaining bytes use the normal file-transfer sequence (`FileTransferChunk`, `FileTransferChecksum`, `FileTransferComplete`, or `FileTransferCancel`). Windows hosts require checksum and cancel negotiation for this path.

If the remote build stamp is newer than the viewer build stamp, the viewer sends `RemoteUpdatePackageRequest` with no payload. A Windows host that supports `RemoteUpdate` replies by sending its current `RemoteDesk.exe` back to the viewer as `RemoteUpdateStart`, again followed by the normal file-transfer sequence. Before scheduling installation, a signed receiver requires a valid trusted Authenticode signature, the same signer public key as its current executable, and a strictly newer build stamp. An unsigned receiver uses personal-LAN mode and accepts only another unsigned RemoteDesk with a strictly newer valid build stamp; a signed-but-invalid file is not treated as unsigned. Equal-version, downgrade, and signed/unsigned mode-crossing packages are rejected.

After the update package is saved and verified, the host pins its SHA-256 and, for signed mode, signer-certificate SHA-256, starts a separate updater process, sends a final status, and exits. The updater revalidates the hash plus the selected signed/unsigned mode before and after moving the package into place, retains the old executable as a temporary `.old` backup, and starts the new `RemoteDesk.exe --tray`. It removes the backup only after verifying the new process path, listening-port ownership, and loopback `RDK1` handshake; otherwise it restores and health-checks the old executable. Unsigned personal-LAN mode relies on the existing password-authenticated encrypted session and provides integrity/build-order checks, not public publisher identity.

## Private relay shared names

The relay's length-prefixed TLS JSON protocol accepts an authenticated v1 request with
`role: "rename-device"`, the existing relay `token`, the target UUID in `deviceId`, and
`name`. A trimmed empty name removes the shared label; other names must be single-line,
at most 80 UTF-16 units and contain no Unicode control/format/surrogate or line/paragraph separator characters.
The target must be currently registered or already have a stored label. This changes only
directory metadata, never an operating-system name, device credential, or session identity.
Any client enrolled with the same relay credentials may rename a device.

Success is sent only after atomic persistence: `ok: true`, canonical `deviceId`, and
`sharedName`. Clients require all three to match before reporting success. A timeout is
ambiguous (the reply may have been lost); clients ask the user to refresh and verify.

Paged directory replies advertise `deviceNaming` and an optional `deviceNamingError`.
Each device includes `sharedName` and `originalMachineName`; the existing `machineName`
field contains the effective shared-or-original name, so older clients also see labels.
An absent/false capability disables editing with an update/storage explanation. Labels
are keyed by installation UUID, persist independently of registrations/addresses, and
survive relay restarts. There is no push notification: clients reread the directory.

## Session capture diagnostics

Windows hosts advertising `HostVideoDiagnostics` (capability bit 25) accept the
authenticated, empty `HostVideoDiagnosticsRequest` control (kind 36) and reply
with `HostVideoDiagnostics` (kind 37, a BinaryWriter UTF-8 string, at most 4096
characters). Requests are limited to one per second and replies are scheduled
off the input reader. This optional diagnostic contains only the current
session's bounded capture/encoder status and managed dependency progress;
it does not read operating-system logs, credentials, settings or clipboard.
Older peers are never sent this request. No unsolicited diagnostic is sent.

## Discovery

UDP discovery uses port `56566` by default. A client sends the UTF-8 payload `RemoteDesk.Discover.v1`; a host responds with JSON:

- `Type`: `RemoteDesk.Discover.Response.v1`
- `MachineName`
- `Port`
- `CaptureTarget`
- `IsHostRunning`
- `CanRemoteStart`
- `Platform`
- `Capabilities`
- `BuildStamp` (optional `yyyyMMddHHmmss` UTC packaging time)
- `DeviceId` (optional installation UUID; discovery hint only)

The Linux host prototype emits the same discovery response shape as Windows and Android.
