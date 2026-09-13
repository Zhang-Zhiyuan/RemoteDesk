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
captures a user's desktop, injects input, contacts a host, or edits app settings.

```powershell
dotnet run --project experiments/VideoQualityProbe -c Release -- ffmpeg 1920 1080 60 180
```

Arguments: FFmpeg executable, width, height, FPS, frame count. Use a full path to
test a particular FFmpeg build. NVIDIA NVENC and CUVID/NVDEC support are required
for the corresponding checks; missing encoders are logged, not silently replaced
with CPU encoders. Processes have a 90-second deadline and are launched hidden.
Existing outputs are never overwritten.

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
