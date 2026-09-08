# Windows Graphics Capture cadence probe

This probe measures native Windows Graphics Capture timestamps without
changing the RemoteDesk host. It also owns a small animated window so the
captured desktop changes faster than the display refresh rate.

## Usage

```powershell
dotnet run --project experiments/WgcProbe -- \\.\DISPLAY2 10 1 160 all
dotnet run --project experiments/WgcProbe -- \\.\DISPLAY2 10 1 160 bucket60
dotnet run --project experiments/WgcProbe -- --animate-only \\.\DISPLAY2 10 1
```

The positional arguments are display device, duration in seconds, animation
interval in milliseconds, requested WGC `MinUpdateInterval` rate, and frame
selection mode. `all` records every source timestamp. `bucket60` keeps at most
one existing source frame per 1/60-second timestamp bucket; it never creates or
copies a frame.

`source_*` percentiles use the WGC `SystemRelativeTime` carried by each frame.
`selected_*` percentiles describe the optional no-copy selection. The
animation-only mode is useful while a separately launched FFmpeg `gfxcapture`
process records `framemd5` or `showinfo` output.

## 2026-08-13 hardware result

The local `3840x2160 @ 160 Hz` display was tested with the bundled verified
FFmpeg 8.1.2 build. FFmpeg captured a continuously changing probe window,
scaled on the GPU to `640x360`, downloaded only for `framemd5`, preserved source
timestamps, and ran for six seconds per rate.

| `max_framerate` | Frames | Distinct hashes | Source FPS | p50 | p95 | p99 |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 60 | 320 | 320 | 53.17 | 18.75 ms | 18.75 ms | 18.75 ms |
| 80 | 441 | 439 | 73.41 | 12.50 ms | 18.75 ms | 25.00 ms |
| 120 | 472 | 472 | 78.58 | 12.50 ms | 12.50 ms | 25.00 ms |
| 160 | 648 | 644 | 107.83 | 6.25 ms | 12.50 ms | 18.75 ms |
| 240 | 764 | 684 | 127.17 | 6.25 ms | 12.50 ms | 18.75 ms |

Requesting 160 FPS proves that the former 53.3 FPS result is WGC interval
quantization rather than an encoder, transport, or viewer limit. Requesting
240 FPS starts returning repeated images (80 adjacent duplicate hashes), so it
is not a valid way to improve true cadence.

A D3D11-only `gfxcapture -> select -> h264_nvenc` candidate was also tested.
The selector passed the first existing frame in each 1/60-second source-time
bucket and never downloaded or synthesized frames. It produced 239 selected
frames over 4.01 seconds (59.41 FPS), but selected intervals were 18.75 ms p50
and 25.00 ms p95/p99. The native probe reproduced the same 25.00 ms p95.

The production path therefore remains unchanged. High-rate capture plus a
no-copy 60 Hz selector is not eligible until hardware validation demonstrates
at least 57 distinct frames per second and p95 source interval no greater than
20 ms, without duplicate frames and without weakening static-source, 30 FPS,
or non-integer-refresh behavior.
