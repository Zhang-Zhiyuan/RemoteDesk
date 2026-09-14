# Synthetic hardware video quality probe

For the viewer-only NIS / bicubic / legacy GPU comparison:

```powershell
dotnet run --project experiments/VideoQualityProbe -c Release -- --upscale artifacts/upscale-comparison-new
```

This generates Chinese text and line charts, downsamples them, feeds NV12 to
the real D3D11 presenter, and saves the rendered pixels and GPU timestamp
results. The reference uses the same native-resolution NV12/color conversion.
Timers exclude DXGI frame-latency wait, Present, warm-up and validation readback.
Background GPU work and clock changes can still affect timings. The output
directory must not already exist. PSNR is not text readability or network latency.

This Windows-only experiment compares the product's actual NVENC arguments with
bounded parameter experiments. It does **not** change the product video policy.
It draws a synthetic light/dark text desktop in memory, writes it to a new unique
directory below `artifacts/`, and encodes static and scrolling sequences. It never
captures a user's desktop, injects input, contacts an external host, or edits app
settings. The separate `--native-details` mode below uses its own authenticated
ephemeral loopback endpoint only.

```powershell
dotnet run --project experiments/VideoQualityProbe -c Release -- ffmpeg 1920 1080 60 180
```

Arguments: FFmpeg executable, width, height, FPS, frame count. Use a full path to
test a particular FFmpeg build. NVIDIA NVENC and CUVID/NVDEC support are required
for the corresponding checks; missing encoders are logged, not silently replaced
with CPU encoders. Processes have a 90-second deadline and are launched hidden.
Existing outputs are never overwritten, except for the explicit derived-image
refresh command documented below.

The probe:

- Reads the current Windows viewer independence policy and builds the baseline
  with `FfmpegDesktopH264Capture.BuildArguments`. It replaces only the desktop
  input with generated pixels. The current production policy selects GOP1.
- Compares P5, multi-pass, CQ18, experimental GOP2, research GOP30, and HEVC GOP30.
  `Compatible` means the candidate retains the current H.264/GOP contract, **not**
  that it is approved for production or tested on every device.
- Verifies the encoded and explicitly hardware-decoded frame counts. H.264 also
  passes through the product Annex-B parser and Media Foundation D3D11 decoder;
  reports include actual output frame count, IDR count, largest frame and decode
  call p95. HEVC is tested with FFmpeg/NVDEC only, not the product decoder.
- Records PSNR against the same source-time scroll and RGB-to-NV12 conversion.
  This measures compression loss **after** 4:2:0 conversion, not RGB/color
  fidelity. It does not establish lossless reproduction or subjective quality
  on arbitrary desktop content.
- Reports processing FPS including PNG decode, conversion, filters and encode.
  This is an offline throughput test, **not** source-to-display latency, GPU-only
  capture performance, network FPS or a remote-session acceptance test.

Each run retains `results.json`, the generated source PNG, encoded streams and
per-candidate logs. They are ignored by Git. No credentials are used.

The initial 2026-09-07 exploratory run used GOP2 for its `production` label. That
label was corrected after inspecting the Windows viewer's disabled GOP2 gate.
Use the subsequent `current-gop1` run and the final report, not that exploratory
label, when comparing with the actual current Windows policy.

See [the measured result and integration requirements](../../docs/VideoQuality-20260907.md).

## Native Windows recovery check

Reuse a 1920x1080 / 180-frame fixture directory and choose a new report path:

```powershell
dotnet run --project experiments/VideoQualityProbe -c Release -- --recovery artifacts/video-quality-run artifacts/windows-recovery-new.json
```

This performs three rounds each of GOP1/GOP30 through the actual Annex-B parser
and Media Foundation D3D11 decoder: 180 outputs, explicit gap invalidation,
dependent-frame rejection where applicable, 30 outputs after the next IDR, and
30 after a discontinuity reset. It does not create a network connection or test
desktop input, actual packet loss, window presentation or sustained remote FPS.
The parent report directory must exist; an existing report is never overwritten.

See [the cross-platform retest](../../docs/PlatformRecheck-20260907.md). Windows
recovery success alone does not establish Linux or Android stream compatibility.

## Native text, bitrate and temporal checks

```powershell
dotnet run --project experiments/VideoQualityProbe -c Release -- --text-clarity <ffmpeg> artifacts/text-clarity-new
dotnet run --project experiments/VideoQualityProbe -c Release -- --text-short-gop <ffmpeg> artifacts/text-clarity-new artifacts/text-gop2-new
dotnet run --project experiments/VideoQualityProbe -c Release -- --text-temporal artifacts/text-clarity-new artifacts/text-temporal-new
dotnet run --project experiments/VideoQualityProbe -c Release -- --text-temporal artifacts/text-gop2-new artifacts/text-gop2-temporal-new
dotnet run --project experiments/VideoQualityProbe -c Release -- --text-recovery artifacts/text-clarity-new artifacts/text-clarity-new/recovery.json
dotnet run --project experiments/VideoQualityProbe -c Release -- --text-recovery artifacts/text-gop2-new artifacts/text-gop2-new/recovery.json
```

`--text-clarity` draws a 3840x2160, 12/14-native-pixel Chinese/code fixture and
compares 1080p GOP1 (legacy/NIS) with native GOP1/GOP30. The optional short-GOP
command reuses the **same source pixels** loaded from the original PNG, not a
different text fixture (PNG re-encoding can change the file hash). Both run
static and scrolling sequences, 180 frames at 30 FPS, requesting 10/20 Mbps with
the product's NVENC P4/ULL/VBR/AQ settings and an explicit BT.709 conversion.
GOP2 is still disabled in the production Windows viewer; GOP30 is not a
supported production transport contract. Neither command enables those modes.

Read **observed bitrate**, not only `budgetMbps`: NVENC can overshoot the request
on this dense desktop. `largestSerializationMs` is only AU bytes / requested
link rate, not measured network delay. `processingSeconds` includes PNG input
and filters; it is not encoder latency. The final-frame ink-mask IoU and luma
error use one fixed native-pixel crop; they are **not OCR accuracy**. Inspect the
saved 100% crops, too. `--text-temporal` checks 20 timestamp-matched frames,
including both sides of IDR boundaries, so final-frame quality cannot hide
periodic quality drops. It retains each crop and `temporal.json`.

Static candidates also run two paced decode rounds (60 inputs each, motion /
2-Hz quiet / motion) through the real MF/D3D11 decoder. Intervals follow the
preceding decode call; this is not exact wall-clock 30 FPS playback. Results
match explicit output PTS to submissions and report missing outputs and frame
lag without adding drain frames. Hidden-HWND GPU readback checks actual pixels,
not onscreen DWM timing or end-to-end latency. `--text-recovery` finds the native
GOP1/GOP2/GOP30 fixtures present and checks three rounds each of cold start,
explicit gap notification and reset; orphan P frames must be rejected before
the next IDR. This does not simulate transport loss or frame queue policy.

`tiles.json` measures native RGB PNG tile costs (128/256 pixels), viewport
coverage and a small text edit, and checks decoded RGBA against the source.
It does not implement host capture, detail negotiation, tile generations,
network scheduling or viewer composition. The 2 Mbps detail budget is assumed,
not measured. New directories/report paths are required. If only comparison
labels need refreshing, this explicit command overwrites **only the derived
`small-text-comparison.png`**, retaining the original evidence:

```powershell
dotnet run --project experiments/VideoQualityProbe -c Release -- --text-comparison artifacts/text-clarity-new
```

### Linux installed-backend control

Copy `../linux_text_clarity_probe.py` and the two `10mbps-static-native-gop1.h264` /
`10mbps-static-native-gop30.h264` fixtures into an owned temporary directory on
the Linux test device. With Pillow and the app's normal Python dependencies:

```sh
timeout --kill-after=3 150s python3 linux_text_clarity_probe.py --app /path/to/app --fixtures /path/to/fixtures --output paced.json
timeout --kill-after=3 150s python3 linux_text_clarity_probe.py --app /path/to/app --fixtures /path/to/fixtures --output long-wait.json --wait-ms 1000
timeout --kill-after=3 150s python3 linux_text_clarity_probe.py --app /path/to/app --fixtures /path/to/fixtures --output throughput.json --throughput
```

The current probe selects the installed Jetson NVV4L2 backend when available
and one software backend. It imports the specified installed app (checked),
without changing that installation, settings, display, input or permissions.
Pacing records 48 attempted inputs with a fixed outstanding-correlation bound;
FIFO attribution is not an embedded image timestamp. Success means bounded
progress with no more unreturned tail frames than the app's backend lag limit,
not that all 48 frames were immediately displayed. Long waits diagnose
startup/throughput and are **not a suggested production timeout**.

`--throughput` compares full-file decoding with/without the installed MJPEG
conversion, counts all 180 outputs and includes process startup time. Image
output goes to `/dev/null`. No Tk conversion, mpv presentation, capture or remote
network is measured. Existing reports are refused; keep the baseline failures.

See [the text-clarity findings and next implementation gates](../../docs/TextClarity-20260913.md).

## Native regional detail transport

```powershell
dotnet test experiments/NativeDetail.Tests/NativeDetail.Tests.csproj -c Release
dotnet run --project experiments/VideoQualityProbe -c Release -- --native-details <ffmpeg> artifacts/text-clarity-new artifacts/native-detail-new
```

Reuses the original/edited synthetic 4K fixtures and the 10 Mbps 1080p GOP1
stream. Sends real encrypted RDK1 records over its own private loopback socket,
decodes the video through actual MF/D3D11/NIS, then composites received native
lossless RGBA tiles on the CPU. Checks exact viewport pixels, autonomous text
changes, stale rejection, unchanged-detail retention and exact disable rollback.
It also generates golden wire/state events for independent Python/Java receivers.

This is an **isolated prototype**, not an installed app feature or a WAN latency
test. No live capture, continuous competing video, production GPU tile compositor
or Android hardware is exercised. See [protocol, limits and reproduction](../NativeDetail/README.md)
and [the measured result and remaining integration work](../../docs/NativeDetail-20260914.md).

### GPU composition and paired live-capture controls

```powershell
dotnet run --project experiments/VideoQualityProbe -c Release -- --native-detail-gpu artifacts/text-clarity-new artifacts/native-detail-new artifacts/native-detail-gpu-new
dotnet run --project experiments/VideoQualityProbe -c Release -- --native-detail-live <ffmpeg> artifacts/text-clarity-new artifacts/native-detail-live-new
```

`--native-detail-gpu` reuses the first probe's edited H.264 and both native PNGs.
It exercises the Windows presenter with a 4 MiB sparse native atlas, paced uploads
(at most two tiles per frame), same-sample binding, exact disable/post-draw-fault
rollback, cache reuse, odd native edges, missing neighbours, crop and fractional
output geometry. It reads back real GPU pixels. Cached-render GPU timestamps
exclude initialization, DXGI wait/Present, warm-up and readback; they are not
end-to-end latency. A two-second artificial admission budget is for correctness,
not a production latency policy. Preparation is awaited only during test setup.

`--native-detail-live` temporarily **shows its own synthetic capture window**
without activating it, then closes it on completion/failure. It captures only
that window with PrintWindow, never the desktop or other apps. One native RGBA
snapshot feeds both the damage manifest and a persistent hardware NVENC process.
Strict single-pending input, RTP sequence/timestamp validation, explicit decoder
PTS and an independently decoded on-image barcode check every frame's identity.
The base/detail streams use real RDK1 encrypted loopback and actual MF/D3D11/NIS
plus GPU composition; autonomous text edits must reject late old fragments.

This is a deliberately slow CPU capture bridge with per-frame validation readback
and one admitted detail fragment per frame. The capture cost and full-refinement
time are recorded honestly; it is **not the production WGC path or an FPS/WAN/input
latency qualification**. It does not enable a product capability or change an
installed app. It uses only owned windows, encoder processes and ephemeral local
sockets, disposed on exit. Existing output directories are refused. See
[the GPU findings and no-wait admission contract](../../docs/NativeDetailGpu-20260914.md).

### Frame-deadline stress

```powershell
dotnet run --project experiments/VideoQualityProbe -c Release -- --native-detail-pacing artifacts/text-clarity-new artifacts/native-detail-new artifacts/native-detail-pacing-new
```

Opens only an owned 960x600 preview, checks its window visibility, and renders a
4K back buffer. The real MF decoder and D3D11 presenter process 30 warm-up frames
plus seven 120-frame phases at 60 Hz: base, cold preparation, cached detail, base,
synthetic input/backlog/congestion/expired budgets, changing text, base. The
driver uses the product's high-resolution waiter and a one-frame local deadline;
it never creates a catch-up queue. Preparation is not awaited in a frame.

Reports CPU decode/adapter and Present durations, schedule misses, deadline
overruns, admitted detail and upload counts. No validation readback, GPU query
wait or Flush is added to the measured loop. Native fixture generation is timed
separately and is not production capture. These are submission/correctness checks,
not DWM/photon timing, a real input ACK, WAN contention, or an automatic release
gate. `functionalPassed` must never be treated as authorization to ship. See the
[release audit](../../docs/NativeDetailReleaseAudit-20260914.md).

### Same-source GPU capture

```powershell
dotnet run --project experiments/VideoQualityProbe -c Release -- --native-detail-encoder artifacts/encoder-new
dotnet run --project experiments/VideoQualityProbe -c Release -- --native-detail-capture artifacts/text-clarity-new artifacts/capture-new
dotnet run --project experiments/VideoQualityProbe -c Release -- --native-detail-capture-coarse artifacts/text-clarity-new artifacts/coarse-new
dotnet run --project experiments/VideoQualityProbe -c Release -- --native-detail-capture-redraw artifacts/text-clarity-new artifacts/redraw-new
dotnet run --project experiments/VideoQualityProbe -c Release -- --native-damage-refinement artifacts/refinement-timing-new
```

The encoder probe uses only synthetic GPU surfaces. It enumerates hardware MFTs,
requires one input to produce an independent H.264 output without another input
or drain, and verifies caller-owned timestamps through the real decoder.

The capture probe requires a 4K-class primary display for its owned, fixed
2560x1440 source window and a separate small preview. It stops if that window
moves/closes and closes both on exit. DDA supplies one immutable native GPU
snapshot plus damage metadata; GPU scaling/MF encoding and bounded native-region
readback consume that SAME snapshot. The original production host is unchanged.

It exercises 240 real frames: base, cold/cached detail, edited text, busy skip,
and off. The presenter renders a native-size buffer; the small preview itself
is not 1:1. Pixel oracles run after the timed loop; only changed fixture regions
are repainted. Failed pixel checks save the owned block and reference. Reports
are refused if the destination already exists. No public transport, installed
settings, system clipboard or input injection is involved. Timings end at CPU
submission, not input-to-photon. This is not a release gate; see the
[same-source capture audit](../../docs/NativeDetailCapture-20260914.md).

`-coarse` conservatively forces every tile dirty on every acquired frame;
`-redraw` really repaints the entire owned source window. Both exercise bounded
exact GPU tile comparison while the base encoder runs; an unavailable result
never delays a ready base. Prepared fixture pixels are checked before capture
to catch DPI-dependent drawing errors. `--native-damage-refinement` separately
times 6/64-tile GPU comparisons and checks one-bit changes in all 64 tiles; its
GPU query waits belong only to that microbenchmark. See
[fixes, retained failures and timing scope](../../docs/NativeDetailRefinement-20260914.md).

## Actual Windows viewer session

```powershell
dotnet run --project experiments/VideoQualityProbe -c Release -- --native-detail-session artifacts/text-clarity-new artifacts/session-new
```

This opens an owned `RemoteViewerWindow` and uses the actual client, encrypted
loopback TCP, pooled frames, MF decoder and GPU presenter. The sender replays a
synthetic H.264 fixture with its native pixels; it is not `RemoteHostServer` or
a production WAN scheduler. Checks cover exact displayed pixels, asynchronous
patch reception, input cutoff, re-request, resize and explicit off. Pixel
readback is outside active measurement; cold setup/readback stalls in the
telemetry are not a low-latency A/B benchmark. The installed application is not
changed. See [historical session results](../../docs/NativeDetailSession-20260914.md).

## Production Windows host and real relay

```powershell
dotnet run --project experiments/VideoQualityProbe -c Release -- --native-detail-host artifacts/text-clarity-new artifacts/host-new
dotnet run --project experiments/VideoQualityProbe -c Release -- --native-detail-host-relay 8.138.5.232 artifacts/text-clarity-new artifacts/public-host-new
dotnet run --project experiments/VideoQualityProbe -c Release -- --native-detail-vectors artifacts/new-interop-vectors.txt
```

The host probes need two monitors: an owned fixture completely covers the primary
monitor; the real viewer occupies the other. They verify visible coverage and
focus before injecting Chinese text into their own field. They exercise actual
`RemoteHostServer`, DDA/MFT capture, UI toggling, static native-pixel equality,
input cutoff and repeated off/on recovery. No clipboard or installed endpoint is
changed. Relay mode reuses only the already pinned login for the explicitly named
server, registers an ephemeral node and removes the connection on exit.
See [results, retained failures and timing limits](../../docs/NativeDetailHost-20260914.md).
