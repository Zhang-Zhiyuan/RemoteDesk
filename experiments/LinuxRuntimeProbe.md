# Isolated Linux runtime probe

`linux_runtime_probe.py` imports the product Linux host, wire protocol and decoder
classes. It does not modify them. Dependencies are Python 3.10+, Pillow,
cryptography, Tk, FFmpeg/ffplay, Xvfb, xauth and the host's usual X11 dependencies.
NVENC/NVDEC checks additionally need a working NVIDIA device and FFmpeg support.
It installs no packages and refuses to overwrite an existing output directory.

Run from the repository root in Linux (or after changing into the mounted repo
inside WSL):

```sh
xvfb-run -a -s '-screen 0 1920x1080x24 -nolisten tcp' env REMOTEDESK_ISOLATED_XVFB=1 python3 experiments/linux_runtime_probe.py --output artifacts/linux-runtime-unique-run
```

Never set the isolation variable on a real desktop display. The wrapper must own
the Xvfb display. The test creates its own synthetic ffplay window and actual
RemoteDesk host, binds only 127.0.0.1 on an ephemeral port, disables discovery and
passes a fresh random password over stdin. It checks bad-password rejection,
encrypted JPEG/H.264 delivery, empty heartbeat replies, dimensions, independent
frame flags and a subsequent reconnect. It sends no viewer input. The product
host may probe XTest with a restored one-pixel motion on this isolated display.
Test-owned host children explicitly set `REMOTEDESK_AUTO_INSTALL=0` so the new
startup dependency helper never prompts for elevation inside a probe.

The captured H.264 is also passed through the actual bounded FFmpeg decoder,
using CUDA and software backends when available. Decoded MJPEG is validated with
Pillow. This is explicitly a copy-back path, not a native GPU Surface benchmark.
The bounded test does not drain every final output; its diagnostic gate requires
at least 55 non-black 1920x1080 outputs from 60 attempts, a live process and no
correlation overflow. Counts remain in the report; a pass is not a lossless-frame
or precise source/output timestamp-correlation guarantee.

Only Popen processes created by the probe and their owned process sessions are
stopped. JSON, synthetic H.264 and logs remain in the unique output directory.
Normal mode exits nonzero for an incomplete session or failed decoder gate.

## Synthetic fixture / startup comparison

This mode needs no display, host or network. Generate the 1920x1080 / 180-frame
fixtures with [VideoQualityProbe](VideoQualityProbe/README.md), then run:

```sh
python3 experiments/linux_runtime_probe.py --decode-fixtures artifacts/video-quality-run --output artifacts/linux-fixtures-unique-run
```

For GOP1 and experimental GOP30 it tests:

- The unmodified product decoder, including its four-entry correlation bound.
- An isolated negative-control instance of that same decoder with the former
  FFmpeg `nobuffer` flag reintroduced. The fixed product no longer uses that flag.
  This does not change the product command builder or queue limits.
- Both commands fed all 60 frames and EOF outside the product queue, to separate
  FFmpeg startup loss from bounded-queue starvation. This is not live latency.

The report distinguishes input attempts, outstanding correlations, actual output
counts, non-black pixels, overflow and EOF-fed diagnostics. A trailing synthetic
AUD can produce an EOF `missing picture` warning; stderr is retained, not hidden.
Legacy-control failure is expected and does not fail the fixed-product gate.
Fixture mode exits 1 if either current product decoder case fails. Missing
backends are not tested. Earlier reports used `experimentalRemoveNobuffer` for
the fix candidate; new reports use `legacyNobuffer` for the negative control.

See [the measured findings and limitations](../docs/PlatformRecheck-20260907.md).

## Bounded 4K stage timing

With the emulator and other test GPU workloads stopped, run:

```sh
xvfb-run -a -s '-screen 0 3840x2160x24 -nolisten tcp' env REMOTEDESK_ISOLATED_XVFB=1 python3 experiments/linux_capture_stage_probe.py --output artifacts/linux-stages-unique-run
```

This times five-second capture-only, NV12 conversion and actual product P4/VBR
NVENC stages with automatic / 1 / 2 / 4 filter threads. Each FFmpeg child has a
15-second deadline. It retains JSON, arguments and logs, not raw video; encoded
NAL/frame counts are checked in memory. The synthetic source, display and codec
processes are owned by the probe. Results are WSL/Xvfb pipeline measurements, not
physical Linux presentation or source-refresh guarantees.

Add `--focus-upload` to compare the former explicit `hwupload_cuda` filter with
the current NVENC internal NV12 upload, in alternating order twice. Both use
the same P4/VBR/AQ/GOP1 settings. Every encoded access unit is normalized and
checked for recovery flags; each encoded stage also hardware-decodes its first
frame and validates 3840x2160 non-flat pixels. Those decode checks are outside
the measured capture duration. No raw 4K stream is kept.

## Physical Linux / Jetson additions

All outputs below must be new directories. The physical host probe adds actual
mouse-down/up and key-down/up assertions in its own synthetic Tk window; it
waits for real capture geometry before mapping input, not the 1x1 startup image.

```sh
xvfb-run -a -s '-screen 0 1920x1080x24 -nolisten tcp' env REMOTEDESK_ISOLATED_XVFB=1 python3 experiments/linux_physical_probe.py --output artifacts/physical-host-new
xvfb-run -a -s '-screen 0 1280x1024x24 -nolisten tcp' env REMOTEDESK_ISOLATED_XVFB=1 python3 experiments/linux_dependency_ui_probe.py --output artifacts/dependency-ui-new
```

The second probe displays actual Tk confirmation/cancel/progress windows but
uses simulated installer subprocesses. It installs no packages and is not a
polkit authorization test. It also launches the actual product GUI entry with
preinstalled dependencies, verifies its X11 window and checks graceful shutdown.
It requires `xwininfo`; if dependencies are missing it refuses the product smoke
instead of authorizing installation. Test the production helper's installation workflow
only with the machine owner's explicit permission.

`linux_jetson_decoder_probe.py` uses product backend discovery to check the
Jetson `h264_nvv4l2dec` backend against both synthetic fixtures. A listed CUVID
decoder is not proof it can initialize on Jetson. This remains the bounded
CPU-visible/MJPEG path, not a zero-copy benchmark. See
[physical results and limitations](../docs/PhysicalDevices-20260907.md).
