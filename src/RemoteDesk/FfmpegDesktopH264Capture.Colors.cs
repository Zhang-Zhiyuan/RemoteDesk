using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using Vortice.MediaFoundation;

namespace RemoteDesk;

internal sealed partial class FfmpegDesktopH264Capture
{
    private static readonly ConcurrentDictionary<(string Path, int Adapter, Size Source, Size Output),
        Lazy<Task<SdrVideoColorSpace?>>> DesktopColorMeasurements = new();

    // scale_d3d11 does not expose range/matrix controls. Sample the selected
    // adapter and filter using synthetic red, before tagging the encoded SPS.
    // This never captures the monitor and runs once for a capture geometry.
    private static async Task<SdrVideoColorSpace?> GetDesktopDuplicationColorsAsync(string path,
        WindowsDesktopDuplicationTarget target, Size output, CancellationToken cancellationToken)
    {
        var key = (path, target.AdapterIndex, target.Bounds.Size, output);
        var measurement = DesktopColorMeasurements.GetOrAdd(key, static k =>
            new Lazy<Task<SdrVideoColorSpace?>>(() => MeasureColorAsync(k.Path, k.Adapter, k.Source, k.Output)));
        var color = await measurement.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (color is null) DesktopColorMeasurements.TryRemove(new(key, measurement));
        return color;
    }

    private static async Task<SdrVideoColorSpace?> MeasureColorAsync(string path, int adapter, Size source, Size output)
    {
        try
        {
            using var process = SystemFfmpegDesktopH264Process.Start(path, [
                "-v", "error", "-init_hw_device", FormattableString.Invariant($"d3d11va=qa:{adapter}"), "-filter_hw_device", "qa",
                "-f", "lavfi", "-i", FormattableString.Invariant($"color=red:s={source.Width}x{source.Height}:r=1,format=bgra"),
                "-vf", FormattableString.Invariant($"hwupload,scale_d3d11=width={output.Width}:height={output.Height}:format=nv12,hwdownload,format=nv12,crop=2:2:0:0"),
                "-frames:v", "1", "-f", "rawvideo", "pipe:1"]);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(900));
            try
            {
                byte[] nv12 = new byte[6];
                var error = process.StandardError.ReadToEndAsync(timeout.Token);
                await Task.WhenAll(process.StandardOutput.ReadExactlyAsync(nv12, timeout.Token).AsTask(),
                    process.Completion, error).WaitAsync(timeout.Token).ConfigureAwait(false);
                return process.ExitCode == 0 ? IdentifySyntheticRed(nv12) : null;
            }
            finally
            {
                if (!process.HasExited) process.KillEntireProcessTree();
            }
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException or
            NotSupportedException or OperationCanceledException) { return null; }
    }

    internal static SdrVideoColorSpace? IdentifySyntheticRed(ReadOnlySpan<byte> nv12)
    {
        if (nv12.Length != 6) return null;
        foreach (var (color, y, u, v) in RedReferences)
        {
            if (Math.Abs(nv12[0] - y) <= 2 && Math.Abs(nv12[1] - y) <= 2 &&
                Math.Abs(nv12[2] - y) <= 2 && Math.Abs(nv12[3] - y) <= 2 &&
                Math.Abs(nv12[4] - u) <= 2 && Math.Abs(nv12[5] - v) <= 2)
                return color;
        }
        return null;
    }

    private static readonly (SdrVideoColorSpace Color, int Y, int U, int V)[] RedReferences =
    [
        (new(true, VideoTransferMatrix.Bt709), 54, 99, 255), (new(false, VideoTransferMatrix.Bt709), 63, 102, 240),
        (new(true, VideoTransferMatrix.Bt601), 76, 85, 255), (new(false, VideoTransferMatrix.Bt601), 81, 90, 240)
    ];

    private static string GetH264MetadataFilter(FfmpegDesktopH264CaptureOptions options)
    {
        const string filter = "dump_extra=freq=keyframe,h264_metadata=aud=insert";
        SdrVideoColorSpace? colors = options.Backend switch
        {
            FfmpegDesktopCaptureBackend.DesktopDuplicationOutput0 => options.DesktopDuplicationColors,
            FfmpegDesktopCaptureBackend.GdiGrabBounds => new(false, VideoTransferMatrix.Bt709),
            _ => null
        };
        if (colors is not { } known) return filter;
        int matrix = known.Matrix == VideoTransferMatrix.Bt709 ? 1 : 6;
        return string.Create(CultureInfo.InvariantCulture,
            $"{filter}:video_full_range_flag={(known.FullRange ? 1 : 0)}:matrix_coefficients={matrix}:colour_primaries=1:transfer_characteristics=13");
    }
}
