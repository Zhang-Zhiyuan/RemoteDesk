using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using RemoteDesk;

// Synthetic pixels only: never captures the desktop, injects input, connects to
// a remote host, modifies settings, or replaces a running release executable.
if (args.ElementAtOrDefault(0) == "--recovery")
{
    if (args.Length != 3)
        throw new ArgumentException("Usage: --recovery <fixture-directory> <new-report-path>");
    RecoveryProbe.Run(args[1], args[2]);
    return;
}
string ffmpeg = args.ElementAtOrDefault(0) ?? "ffmpeg";
int width = int.Parse(args.ElementAtOrDefault(1) ?? "1920", CultureInfo.InvariantCulture);
int height = int.Parse(args.ElementAtOrDefault(2) ?? "1080", CultureInfo.InvariantCulture);
int fps = int.Parse(args.ElementAtOrDefault(3) ?? "60", CultureInfo.InvariantCulture);
int count = int.Parse(args.ElementAtOrDefault(4) ?? "180", CultureInfo.InvariantCulture);
if (width is < 640 or > 3840 || height is < 360 or > 2160 || width % 2 != 0 ||
    height % 2 != 0 || fps is < 1 or > 60 || count is < 30 or > 600)
    throw new ArgumentException("Expected even 640..3840 x 360..2160, FPS 1..60, frames 30..600.");

string output = Path.GetFullPath(Path.Combine("artifacts", "video-quality-" +
    DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" +
    Guid.NewGuid().ToString("N")[..8]));
Directory.CreateDirectory(output);
Console.WriteLine($"Synthetic probe output: {output}");
string input = Path.Combine(output, "source.png");
DrawDesktop(input, width, height);

var options = new FfmpegDesktopH264CaptureOptions(FfmpegDesktopCaptureBackend.GdiGrabBounds,
    new Rectangle(0, 0, width, height), new Size(width, height), fps,
    GopLength: RemoteViewerClient.LocalH264SamplesAreAllIndependent ? 1 : FfmpegDesktopH264Capture.ShortGopLength);
var production = FfmpegDesktopH264Capture.BuildArguments(options, FfmpegH264Encoder.NvidiaNvenc).ToList();
int start = production.IndexOf("-c:v");
List<string> original = production.GetRange(start, production.IndexOf("-an") - start);
var candidates = new List<(string Name, List<string> Arguments, bool Compatible)>
{
    ("current-gop" + options.GopLength, [..original], true),
    ("p5", Set(original, "-preset", "p5"), true),
    ("p4-qres", Set(original, "-multipass", "qres"), true),
    ("p5-qres", Set(Set(original, "-preset", "p5"), "-multipass", "qres"), true),
    ("p4-cq18", Set(original, "-cq", "18"), true),
    // Research comparisons only: a long GOP violates the current IDR/P wire
    // contract; HEVC/4:4:4 also need new negotiation and decoder support.
    ("experimental-gop2", Set(original, "-g", "2"), false),
    ("research-gop30", Set(original, "-g", "30"), false),
    ("research-hevc-gop30", Set(Set(original, "-c:v", "hevc_nvenc"), "-g", "30"), false)
};
var results = new List<object>();
foreach (bool moving in new[] { false, true })
{
    string scene = moving ? "scroll" : "static";
    string filter = moving ? "scroll=vertical=0.03,format=nv12" : "format=nv12";
    foreach (var candidate in candidates)
    {
        string label = scene + "-" + candidate.Name;
        bool hevc = candidate.Arguments[candidate.Arguments.IndexOf("-c:v") + 1] == "hevc_nvenc";
        string encoded = Path.Combine(output, label + (hevc ? ".hevc" : ".h264"));
        List<string> command = ["-hide_banner", "-nostdin", "-n", "-benchmark",
            "-nostats", "-progress", "pipe:1",
            "-loop", "1", "-framerate", I(fps), "-i", input,
            "-vf", filter, "-frames:v", I(count), ..candidate.Arguments,
            "-an", "-fps_mode", "passthrough", "-flush_packets", "1", encoded];
        var encode = await Run(ffmpeg, command);
        await File.WriteAllTextAsync(Path.Combine(output, label + "-encode.log"), encode.Log);
        if (encode.ExitCode != 0)
        {
            results.Add(new { Scene = scene, Candidate = candidate.Name, Failure = encode.Log });
            Console.WriteLine($"{label}: ENCODE FAILED (see log)");
            continue;
        }
        VerifyFrameCount(encode.Log, count);
        // Compare only after the same RGB->NV12 conversion and the same exact
        // source-time scroll. This measures compression error, not the loss
        // already introduced by chroma subsampling. It is NOT RGB losslessness.
        var metric = await Run(ffmpeg, ["-hide_banner", "-nostdin",
            "-r", I(fps), "-i", encoded, "-loop", "1", "-framerate", I(fps), "-i", input,
            "-filter_complex", $"[0:v]setpts=N/({fps}*TB)[enc];[1:v]{filter},setpts=N/({fps}*TB)[ref];[enc][ref]psnr=shortest=1",
            "-frames:v", I(count), "-f", "null", "NUL"]);
        await File.WriteAllTextAsync(Path.Combine(output, label + "-psnr.log"), metric.Log);
        if (metric.ExitCode != 0) throw new InvalidOperationException(metric.Log);
        double yPsnr = Parse(metric.Log, @"PSNR y:([\d.]+)");
        double average = Parse(metric.Log, @"average:([\d.]+)");
        double processingSeconds = Parse(encode.Log, @"rtime=([\d.]+)s");
        long bytes = new FileInfo(encoded).Length;
        double mbps = bytes * 8d * fps / count / 1_000_000;
        double processingFps = count / processingSeconds;
        // Actual NVDEC via the explicitly selected CUVID decoder. Do not use
        // '-hwaccel auto', which can silently succeed through a CPU fallback.
        var decode = await Run(ffmpeg, ["-hide_banner", "-nostdin", "-v", "warning",
            "-nostats", "-progress", "pipe:1",
            "-c:v", hevc ? "hevc_cuvid" : "h264_cuvid",
            "-i", encoded, "-frames:v", I(count), "-f", "null", "NUL"]);
        await File.WriteAllTextAsync(Path.Combine(output, label + "-nvdec.log"), decode.Log);
        if (decode.ExitCode == 0) VerifyFrameCount(decode.Log, count);
        object? nativeDecoder = hevc ? null : DecodeWithProduct(encoded, width, height, fps, count);
        var row = new { Scene = scene, Candidate = candidate.Name, candidate.Compatible,
            Width = width, Height = height, Fps = fps, Frames = count,
            Bytes = bytes, Mbps = mbps, YPsnr = yPsnr, AveragePsnr = average,
            ProcessingFps = processingFps, WallSeconds = encode.Seconds,
            NvdecPassed = decode.ExitCode == 0, NativeDecoder = nativeDecoder, EncoderArguments = candidate.Arguments };
        results.Add(row);
        Console.WriteLine($"{label}: {mbps:F2} Mbps, Y PSNR {yPsnr:F2}, avg {average:F2} dB, processing {processingFps:F1} FPS, NVDEC {decode.ExitCode == 0}");
        await File.WriteAllTextAsync(Path.Combine(output, "results.json"),
            JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }
}
Console.WriteLine("Processing FPS includes PNG decode, RGB conversion, filters and encode; it is not per-frame latency or network FPS.");

static string I(int value) => value.ToString(CultureInfo.InvariantCulture);

static void VerifyFrameCount(string log, int expected)
{
    MatchCollection matches = Regex.Matches(log, @"(?m)^frame=(\d+)\s*$");
    if (matches.Count == 0 || int.Parse(matches[^1].Groups[1].Value, CultureInfo.InvariantCulture) != expected)
        throw new InvalidOperationException($"Did not process all {expected} frames: {log}");
}

static object DecodeWithProduct(string path, int width, int height, int fps, int expected)
{
    var parser = new AnnexBH264AccessUnitParser();
    List<AnnexBH264AccessUnit> units = [..parser.Append(File.ReadAllBytes(path)), ..parser.Complete()];
    MediaFoundationD3D11H264Decoder? decoder = null;
    try
    {
        if (units.Count != expected) throw new InvalidOperationException($"Parser returned {units.Count}/{expected} frames.");
        int idrs = units.Count(unit => unit.IsIdr);
        int maxBytes = units.Max(unit => unit.Bytes.Length);
        if (!MediaFoundationD3D11H264Decoder.TryCreate(new(width, height, fps,
            AllSamplesIndependent: idrs == units.Count), out decoder, out var capability))
            return new { Passed = false, capability.Detail, IdrFrames = idrs, MaximumFrameBytes = maxBytes };
        int ready = 0;
        var decodeTimes = new List<double>();
        for (int index = 0; index < units.Count; index++)
        {
            long started = Stopwatch.GetTimestamp();
            var decoded = decoder.DecodeAccessUnit(units[index].Bytes, index * 10_000_000L / fps);
            decodeTimes.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            using var frame = decoded.Frame;
            if (decoded.Status == MediaFoundationD3D11DecodeStatus.Failed)
                return new { Passed = false, decoded.Detail, FrameIndex = index, IdrFrames = idrs, MaximumFrameBytes = maxBytes };
            if (frame is not null)
            {
                if (frame.VisibleWidth != width || frame.VisibleHeight != height)
                    throw new InvalidOperationException("Native decoder returned unexpected dimensions.");
                ready++;
            }
        }
        decodeTimes.Sort();
        return new { Passed = ready == expected, SubmittedFrames = units.Count, DecodedFrames = ready,
            IdrFrames = idrs, MaximumFrameBytes = maxBytes,
            DecodeCallP95Milliseconds = decodeTimes[(int)Math.Ceiling(decodeTimes.Count * 0.95) - 1] };
    }
    finally
    {
        decoder?.Dispose();
        foreach (var unit in units) unit.Dispose();
    }
}

static List<string> Set(List<string> source, string key, string value)
{
    var result = new List<string>(source);
    int index = result.IndexOf(key);
    if (index < 0) result.AddRange([key, value]);
    else result[index + 1] = value;
    return result;
}

static double Parse(string log, string pattern)
{
    Match match = Regex.Match(log, pattern);
    return match.Success ? double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) :
        throw new InvalidOperationException($"Metric {pattern} not found in FFmpeg output: {log}");
}

static async Task<(int ExitCode, string Log, double Seconds)> Run(string executable, IReadOnlyList<string> command)
{
    var start = new ProcessStartInfo(executable)
    {
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardError = true, RedirectStandardOutput = true
    };
    foreach (string item in command) start.ArgumentList.Add(item);
    using var process = new Process { StartInfo = start };
    var clock = Stopwatch.StartNew();
    process.Start();
    Task<string> error = process.StandardError.ReadToEndAsync();
    Task<string> output = process.StandardOutput.ReadToEndAsync();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    try { await process.WaitForExitAsync(deadline.Token); }
    catch
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        await process.WaitForExitAsync();
        throw;
    }
    return (process.ExitCode, await error + await output, clock.Elapsed.TotalSeconds);
}

static void DrawDesktop(string path, int width, int height)
{
    using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
    using var graphics = Graphics.FromImage(bitmap);
    graphics.Clear(Color.White);
    graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
    using var small = new Font("Consolas", 12, FontStyle.Regular, GraphicsUnit.Pixel);
    using var medium = new Font("Consolas", 16, FontStyle.Regular, GraphicsUnit.Pixel);
    using var chinese = new Font("Microsoft YaHei UI", 14, FontStyle.Regular, GraphicsUnit.Pixel);
    using var background = new SolidBrush(Color.FromArgb(26, 30, 40));
    graphics.FillRectangle(background, width / 2, 0, width / 2, height);
    string[] samples = ["RemoteDesk | 1920x1080 | ABCDEFGH abcdefgh 0123456789",
        "const pixel = frame[y * stride + x];  // thin text",
        "if (latency < 16.7) { draw(texture); } else { recover(); }",
        ".,:;!|'\" []{}() /\\ +=-_* 0123456789 0O 1Il"];
    for (int y = 16, line = 0; y < height - 48; y += 23, line++)
    {
        graphics.DrawString(samples[line % samples.Length], line % 3 == 0 ? medium : small,
            line % 4 == 2 ? Brushes.Blue : Brushes.Black, 12, y);
        graphics.DrawString(samples[(line + 2) % samples.Length], line % 3 == 0 ? medium : small,
            line % 4 == 2 ? Brushes.Orange : Brushes.White, width / 2 + 12, y);
        if (line % 6 == 0)
        {
            graphics.DrawString("远程桌面：清晰文字、细线、彩色边缘", chinese, Brushes.DarkRed, 25, y + 12);
            graphics.DrawString("远程桌面：清晰文字、细线、彩色边缘", chinese, Brushes.Cyan, width / 2 + 25, y + 12);
        }
    }
    for (int x = 0; x < width; x += 4)
        graphics.DrawLine(x % 8 == 0 ? Pens.Red : Pens.Blue, x, height - 35, x, height - 5);
    bitmap.Save(path, ImageFormat.Png);
}
