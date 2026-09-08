using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace RemoteDesk;

internal readonly record struct ScreenCaptureResult(
    Rectangle Bounds,
    Size FrameSize,
    ReadOnlyMemory<byte> JpegBytes,
    double CaptureMilliseconds,
    double EncodeMilliseconds);

internal readonly record struct ScreenCaptureTargetAvailability(
    bool IsAvailable,
    Rectangle Bounds);

internal sealed class ScreenCaptureTargetUnavailableException(
    string targetId)
    : InvalidOperationException(
        $"指定屏幕 {targetId} 暂不可用；捕获已暂停，请重新连接该屏幕或选择其他屏幕。")
{
}

internal sealed record ScreenCaptureTarget(string Id, string DisplayName, Rectangle Bounds, bool IsPrimary = false)
{
    public const string AllScreensId = "__all_screens__";

    public bool IsAllScreens => Id == AllScreensId;

    public override string ToString() => DisplayName;
}

internal sealed class ScreenCaptureService : IDisposable
{
    // This settings-compatible sentinel represents a resolution ceiling, not
    // a literal 67% scale. On a 4K source it is exactly 2560x1440; displays
    // already at or below 1440p remain native.
    internal const int QhdMaximumScaleMode = 67;
    internal const int QhdMaximumWidth = 2560;
    internal const int QhdMaximumHeight = 1440;
    internal const int LowLatencyMaximumWidth = 3840;
    internal const int LowLatencyMaximumHeight = 2160;

    private static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
    private static readonly TimeSpan CaptureBoundsRefreshInterval = TimeSpan.FromMilliseconds(500);
    private const int StretchModeColorOnColor = 3;
    private const int SourceCopyRasterOperation = 0x00CC0020;

    private readonly ScreenCaptureTarget _target;
    private Bitmap? _captureBitmap;
    private Graphics? _captureGraphics;
    private Bitmap? _scaledBitmap;
    private Graphics? _scaledGraphics;
    private readonly MemoryStream _jpegOutput = new();
    private EncoderParameters? _encoderParameters;
    private int? _encoderQuality;
    private Rectangle _cachedCaptureBounds;
    private bool _cachedTargetAvailable;
    private long _captureBoundsRefreshedAt;

    public ScreenCaptureService(ScreenCaptureTarget target)
    {
        _target = target;
        _cachedCaptureBounds = NormalizeBounds(target.Bounds, new Rectangle(0, 0, 1, 1));
        _cachedTargetAvailable = target.IsAllScreens;
        // Resolve the real display on the first read. In particular, do not
        // trust a physical target retained across a display-topology change
        // for the duration of the normal bounds cache.
        _captureBoundsRefreshedAt = 0;
    }

    public static IReadOnlyList<ScreenCaptureTarget> GetAvailableTargets()
    {
        Screen[] screens = GetScreensSafely();
        Rectangle virtualScreen = GetSafeVirtualScreenBounds();
        var targets = new List<ScreenCaptureTarget>
        {
            new(
                ScreenCaptureTarget.AllScreensId,
                screens.Length > 1
                    ? $"所有屏幕 ({virtualScreen.Width}x{virtualScreen.Height}，低延迟模式自动使用主屏)"
                    : $"所有屏幕 ({virtualScreen.Width}x{virtualScreen.Height})",
                virtualScreen)
        };

        for (int index = 0; index < screens.Length; index++)
        {
            Screen screen = screens[index];
            Rectangle bounds = NormalizeBounds(screen.Bounds, virtualScreen);
            string primary = screen.Primary ? " 主屏" : string.Empty;
            string name = $"屏幕 {index + 1}{primary} ({bounds.Width}x{bounds.Height} @ {bounds.Left},{bounds.Top})";
            targets.Add(new ScreenCaptureTarget(screen.DeviceName, name, bounds, screen.Primary));
        }

        return targets;
    }

    public static ScreenCaptureTarget GetDefaultTarget()
    {
        return ChooseDefaultTarget(GetAvailableTargets());
    }

    public static ScreenCaptureTarget FindTargetById(string targetId)
    {
        IReadOnlyList<ScreenCaptureTarget> targets = GetAvailableTargets();
        return ResolveTargetOrDefault(targets, targetId);
    }

    internal static ScreenCaptureTarget ResolveTargetOrDefault(
        IReadOnlyList<ScreenCaptureTarget> targets,
        string? targetId)
    {
        if (!string.IsNullOrWhiteSpace(targetId))
        {
            ScreenCaptureTarget? matchedTarget = targets.FirstOrDefault(target =>
                string.Equals(target.Id, targetId, StringComparison.OrdinalIgnoreCase));
            if (matchedTarget is not null)
            {
                return matchedTarget.IsAllScreens
                    ? FindSingleScreenEquivalent(targets, matchedTarget.Bounds) ?? matchedTarget
                    : matchedTarget;
            }
        }

        return ChooseDefaultTarget(targets);
    }

    internal static ScreenCaptureTarget ChooseDefaultTarget(IReadOnlyList<ScreenCaptureTarget> targets)
    {
        if (targets.Count == 0)
        {
            return new ScreenCaptureTarget(
                ScreenCaptureTarget.AllScreensId,
                "所有屏幕 (1x1)",
                new Rectangle(0, 0, 1, 1));
        }

        return targets.FirstOrDefault(target => !target.IsAllScreens && target.IsPrimary) ??
            targets.FirstOrDefault(target => !target.IsAllScreens) ??
            targets[0];
    }

    internal static ScreenCaptureTarget?
        ChooseUnambiguousFallbackTarget(
            IReadOnlyList<ScreenCaptureTarget> targets,
            string? unavailableTargetId)
    {
        ArgumentNullException.ThrowIfNull(targets);

        ScreenCaptureTarget[] physicalTargets = targets
            .Where(target =>
                !target.IsAllScreens &&
                !string.Equals(
                    target.Id,
                    unavailableTargetId,
                    StringComparison.OrdinalIgnoreCase))
            .GroupBy(
                target => target.Id,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (physicalTargets.Length == 1)
        {
            return physicalTargets[0];
        }

        if (physicalTargets.Length > 1)
        {
            return null;
        }

        ScreenCaptureTarget[] remainingTargets = targets
            .Where(target =>
                !string.Equals(
                    target.Id,
                    unavailableTargetId,
                    StringComparison.OrdinalIgnoreCase))
            .GroupBy(
                target => target.Id,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        return remainingTargets.Length == 1
            ? remainingTargets[0]
            : null;
    }

    internal static ScreenCaptureTarget? FindSingleScreenEquivalent(
        IReadOnlyList<ScreenCaptureTarget> targets,
        Rectangle allScreensBounds)
    {
        ScreenCaptureTarget[] physicalTargets = targets
            .Where(target => !target.IsAllScreens)
            .ToArray();
        return physicalTargets.Length == 1 && physicalTargets[0].Bounds == allScreensBounds
            ? physicalTargets[0]
            : null;
    }

    internal static ScreenCaptureTarget
        ChooseLowLatencyStartupTarget(
            ScreenCaptureTarget selectedTarget,
            IReadOnlyList<ScreenCaptureTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(selectedTarget);
        ArgumentNullException.ThrowIfNull(targets);
        if (!selectedTarget.IsAllScreens)
        {
            return selectedTarget;
        }

        return targets.FirstOrDefault(
                   target =>
                       !target.IsAllScreens &&
                       target.IsPrimary) ??
            targets.FirstOrDefault(
                target => !target.IsAllScreens) ??
            selectedTarget;
    }

    public static CaptureTargetInfo ToInfo(ScreenCaptureTarget target)
    {
        return new CaptureTargetInfo(target.Id, target.DisplayName);
    }

    internal static Size CalculateFrameSize(Rectangle bounds, int scalePercent)
    {
        Size requested;
        if (scalePercent == QhdMaximumScaleMode)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return new Size(1, 1);
            }

            bool portrait =
                bounds.Height > bounds.Width;
            Size maximum = portrait
                ? new Size(
                    QhdMaximumHeight,
                    QhdMaximumWidth)
                : new Size(
                    QhdMaximumWidth,
                    QhdMaximumHeight);
            if (bounds.Width <= maximum.Width &&
                bounds.Height <= maximum.Height)
            {
                requested = bounds.Size;
            }
            else
            {
                double qhdScale = Math.Min(
                    maximum.Width /
                        (double)bounds.Width,
                    maximum.Height /
                        (double)bounds.Height);
                requested = new Size(
                    Math.Max(
                        1,
                        (int)Math.Round(
                            bounds.Width * qhdScale,
                            MidpointRounding.AwayFromZero)),
                    Math.Max(
                        1,
                        (int)Math.Round(
                            bounds.Height * qhdScale,
                            MidpointRounding.AwayFromZero)));
            }
        }
        else
        {
            int scale = Math.Clamp(scalePercent, 25, 100);
            requested = new Size(
                (int)Math.Max(
                    1L,
                    (long)Math.Max(0, bounds.Width) * scale / 100),
                (int)Math.Max(
                    1L,
                    (long)Math.Max(0, bounds.Height) * scale / 100));
        }

        return FitWithinProtocolFrameBudget(requested);
    }

    internal static Size FitWithinProtocolFrameBudget(Size requested)
    {
        int width = Math.Max(1, requested.Width);
        int height = Math.Max(1, requested.Height);
        long pixels = (long)width * height;
        if (width <= RemoteMessageCodec.MaxFrameDimension &&
            height <= RemoteMessageCodec.MaxFrameDimension &&
            pixels <= RemoteMessageCodec.MaxFramePixels)
        {
            return new Size(width, height);
        }

        double scale = Math.Min(
            1d,
            Math.Min(
                RemoteMessageCodec.MaxFrameDimension /
                    (double)width,
                Math.Min(
                    RemoteMessageCodec.MaxFrameDimension /
                        (double)height,
                    Math.Sqrt(
                        RemoteMessageCodec.MaxFramePixels /
                        (double)pixels))));
        width = Math.Max(
            1,
            (int)Math.Floor(width * scale));
        height = Math.Max(
            1,
            (int)Math.Floor(height * scale));

        // Defend against floating-point rounding at the exact pixel limit.
        if ((long)width * height >
            RemoteMessageCodec.MaxFramePixels)
        {
            if (width >= height)
            {
                width = (int)Math.Max(
                    1L,
                    RemoteMessageCodec.MaxFramePixels / height);
            }
            else
            {
                height = (int)Math.Max(
                    1L,
                    RemoteMessageCodec.MaxFramePixels / width);
            }
        }

        return new Size(width, height);
    }

    internal static string FormatScaleMode(int scalePercent) =>
        scalePercent == QhdMaximumScaleMode
            ? "最高 1440p"
            : $"{Math.Clamp(scalePercent, 25, 100)}%";

    internal static ScreenCaptureTargetAvailability
        ResolveTargetAvailability(
            ScreenCaptureTarget target,
            IReadOnlyList<ScreenCaptureTarget> availableTargets)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(availableTargets);

        ScreenCaptureTarget? current =
            availableTargets.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.Id,
                    target.Id,
                    StringComparison.OrdinalIgnoreCase));
        if (current is not null)
        {
            return new ScreenCaptureTargetAvailability(
                IsAvailable: true,
                NormalizeBounds(
                    current.Bounds,
                    NormalizeBounds(
                        target.Bounds,
                        new Rectangle(0, 0, 1, 1))));
        }

        // A missing physical target must never inherit the aggregate desktop
        // bounds. Retain only its last known physical rectangle so callers can
        // preserve metadata while the capture and pointer paths remain closed.
        return new ScreenCaptureTargetAvailability(
            IsAvailable: target.IsAllScreens,
            NormalizeBounds(
                target.Bounds,
                new Rectangle(0, 0, 1, 1)));
    }

    internal ScreenCaptureTargetAvailability
        GetTargetAvailability(bool forceRefresh = false)
    {
        long now = Stopwatch.GetTimestamp();
        if (!forceRefresh &&
            _captureBoundsRefreshedAt != 0 &&
            Stopwatch.GetElapsedTime(
                _captureBoundsRefreshedAt,
                now) < CaptureBoundsRefreshInterval)
        {
            return new ScreenCaptureTargetAvailability(
                _cachedTargetAvailable,
                _cachedCaptureBounds);
        }

        return GetTargetAvailability(
            GetAvailableTargets());
    }

    internal ScreenCaptureTargetAvailability
        GetTargetAvailability(
            IReadOnlyList<ScreenCaptureTarget> availableTargets)
    {
        ArgumentNullException.ThrowIfNull(availableTargets);
        ScreenCaptureTargetAvailability availability =
            ResolveTargetAvailability(
                _target,
                availableTargets);
        _cachedTargetAvailable = availability.IsAvailable;
        _cachedCaptureBounds = availability.Bounds;
        _captureBoundsRefreshedAt = Stopwatch.GetTimestamp();
        return availability;
    }

    public Rectangle GetCaptureBounds(bool forceRefresh = false)
    {
        ScreenCaptureTargetAvailability availability =
            GetTargetAvailability(forceRefresh);
        if (!availability.IsAvailable)
        {
            throw new ScreenCaptureTargetUnavailableException(
                _target.Id);
        }

        return availability.Bounds;
    }

    public ScreenCaptureResult CaptureJpeg(int quality, int scalePercent)
    {
        long captureStartedAt = Stopwatch.GetTimestamp();
        Rectangle bounds = GetCaptureBounds();
        Size frameSize = CalculateFrameSize(bounds, scalePercent);

        if (frameSize == bounds.Size)
        {
            Bitmap fullSizeBitmap = EnsureCaptureBitmap(bounds.Size);
            _captureGraphics!.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
            return EncodeResult(bounds, fullSizeBitmap.Size, fullSizeBitmap, quality, captureStartedAt);
        }

        Bitmap scaledBitmap = EnsureScaledBitmap(frameSize);
        if (ShouldUseDirectScaledDesktopCopy(bounds, frameSize) && TryCopyScaledFromDesktop(bounds, frameSize))
        {
            return EncodeResult(bounds, scaledBitmap.Size, scaledBitmap, quality, captureStartedAt);
        }

        Bitmap bitmap = EnsureCaptureBitmap(bounds.Size);
        _captureGraphics!.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        _scaledGraphics!.DrawImage(bitmap, new Rectangle(Point.Empty, frameSize));

        return EncodeResult(bounds, scaledBitmap.Size, scaledBitmap, quality, captureStartedAt);
    }

    internal static bool ShouldUseDirectScaledDesktopCopy(Rectangle bounds, Size frameSize)
    {
        return bounds.Width > 0 &&
            bounds.Height > 0 &&
            frameSize.Width > 0 &&
            frameSize.Height > 0 &&
            frameSize != bounds.Size;
    }

    public void Dispose()
    {
        _captureGraphics?.Dispose();
        _captureBitmap?.Dispose();
        _scaledGraphics?.Dispose();
        _scaledBitmap?.Dispose();
        _encoderParameters?.Dispose();
        _jpegOutput.Dispose();
    }

    private Bitmap EnsureCaptureBitmap(Size size)
    {
        var safeSize = new Size(Math.Max(1, size.Width), Math.Max(1, size.Height));
        if (_captureBitmap is not null && _captureBitmap.Size == safeSize)
        {
            return _captureBitmap;
        }

        _captureGraphics?.Dispose();
        _captureBitmap?.Dispose();

        _captureBitmap = new Bitmap(safeSize.Width, safeSize.Height, PixelFormat.Format24bppRgb);
        _captureGraphics = Graphics.FromImage(_captureBitmap);
        return _captureBitmap;
    }

    private Bitmap EnsureScaledBitmap(Size size)
    {
        var safeSize = new Size(Math.Max(1, size.Width), Math.Max(1, size.Height));
        if (_scaledBitmap is not null && _scaledBitmap.Size == safeSize)
        {
            return _scaledBitmap;
        }

        _scaledGraphics?.Dispose();
        _scaledBitmap?.Dispose();

        _scaledBitmap = new Bitmap(safeSize.Width, safeSize.Height, PixelFormat.Format24bppRgb);
        _scaledGraphics = Graphics.FromImage(_scaledBitmap);
        _scaledGraphics.CompositingQuality = CompositingQuality.HighSpeed;
        _scaledGraphics.InterpolationMode = InterpolationMode.Low;
        _scaledGraphics.PixelOffsetMode = PixelOffsetMode.Half;
        _scaledGraphics.SmoothingMode = SmoothingMode.HighSpeed;
        return _scaledBitmap;
    }

    private bool TryCopyScaledFromDesktop(Rectangle sourceBounds, Size destinationSize)
    {
        IntPtr sourceDc = GetDC(IntPtr.Zero);
        if (sourceDc == IntPtr.Zero)
        {
            return false;
        }

        IntPtr destinationDc = IntPtr.Zero;
        try
        {
            destinationDc = _scaledGraphics!.GetHdc();
            _ = SetStretchBltMode(destinationDc, StretchModeColorOnColor);
            return StretchBlt(
                destinationDc,
                0,
                0,
                destinationSize.Width,
                destinationSize.Height,
                sourceDc,
                sourceBounds.Left,
                sourceBounds.Top,
                sourceBounds.Width,
                sourceBounds.Height,
                SourceCopyRasterOperation);
        }
        catch (ExternalException)
        {
            return false;
        }
        finally
        {
            if (destinationDc != IntPtr.Zero)
            {
                try
                {
                    _scaledGraphics!.ReleaseHdc(destinationDc);
                }
                catch (ExternalException)
                {
                }
            }

            _ = ReleaseDC(IntPtr.Zero, sourceDc);
        }
    }

    private static Screen[] GetScreensSafely()
    {
        try
        {
            return Screen.AllScreens;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.ExternalException)
        {
            return Array.Empty<Screen>();
        }
    }

    private static Rectangle GetSafeVirtualScreenBounds()
    {
        try
        {
            return NormalizeBounds(SystemInformation.VirtualScreen, GetSafePrimaryScreenBounds());
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.ExternalException)
        {
            return GetSafePrimaryScreenBounds();
        }
    }

    private static Rectangle GetSafePrimaryScreenBounds()
    {
        try
        {
            return NormalizeBounds(Screen.PrimaryScreen?.Bounds ?? Rectangle.Empty, new Rectangle(0, 0, 1, 1));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.ExternalException)
        {
            return new Rectangle(0, 0, 1, 1);
        }
    }

    private static Rectangle NormalizeBounds(Rectangle bounds, Rectangle fallback)
    {
        if (bounds.Width > 0 && bounds.Height > 0)
        {
            return bounds;
        }

        if (fallback.Width > 0 && fallback.Height > 0)
        {
            return fallback;
        }

        return new Rectangle(0, 0, 1, 1);
    }

    private ReadOnlyMemory<byte> SaveJpeg(Image image, int quality)
    {
        _jpegOutput.SetLength(0);
        image.Save(_jpegOutput, JpegCodec, GetEncoderParameters(quality));
        return _jpegOutput.GetBuffer().AsMemory(0, (int)_jpegOutput.Length);
    }

    private ScreenCaptureResult EncodeResult(
        Rectangle bounds,
        Size frameSize,
        Image image,
        int quality,
        long captureStartedAt)
    {
        double captureMilliseconds = Stopwatch.GetElapsedTime(captureStartedAt).TotalMilliseconds;
        long encodeStartedAt = Stopwatch.GetTimestamp();
        ReadOnlyMemory<byte> jpegBytes = SaveJpeg(image, quality);
        double encodeMilliseconds = Stopwatch.GetElapsedTime(encodeStartedAt).TotalMilliseconds;

        return new ScreenCaptureResult(bounds, frameSize, jpegBytes, captureMilliseconds, encodeMilliseconds);
    }

    private EncoderParameters GetEncoderParameters(int quality)
    {
        int clampedQuality = Math.Clamp(quality, 30, 90);
        if (_encoderParameters is not null && _encoderQuality == clampedQuality)
        {
            return _encoderParameters;
        }

        _encoderParameters?.Dispose();
        _encoderParameters = new EncoderParameters(1);
        _encoderParameters.Param[0] = new EncoderParameter(Encoder.Quality, clampedQuality);
        _encoderQuality = clampedQuality;
        return _encoderParameters;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StretchBlt(
        IntPtr hdcDest,
        int xDest,
        int yDest,
        int widthDest,
        int heightDest,
        IntPtr hdcSrc,
        int xSrc,
        int ySrc,
        int widthSrc,
        int heightSrc,
        int rasterOperation);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int SetStretchBltMode(IntPtr hdc, int mode);

}
