using System.Drawing.Drawing2D;
using System.Reflection;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class PreparedSoftwareBitmapTests
{
    [Theory]
    [InlineData(320, 180, 197, 113)]
    [InlineData(320, 180, 640, 480)]
    [InlineData(173, 317, 100, 100)]
    [InlineData(384, 216, 353, 198)]
    [InlineData(1920, 1080, 1537, 901)]
    [InlineData(3840, 2160, 3530, 2036)]
    [InlineData(1440, 2560, 1301, 1913)]
    public void PreviewMatchesExistingBicubicPictureBoxPixels(int width, int height, int viewWidth, int viewHeight)
    {
        using var source = CreatePattern(width, height);
        using var reference = new RemoteViewerWindow.BufferedPictureBox
        {
            Size = new Size(viewWidth, viewHeight), BackColor = Color.Black, SizeMode = PictureBoxSizeMode.Zoom,
            Image = source
        };
        using var optimized = new RemoteViewerWindow.BufferedPictureBox
        {
            Size = reference.Size, BackColor = Color.Black, SizeMode = PictureBoxSizeMode.Zoom
        };
        var preview = PreparedSoftwareBitmap.Create(source, reference.ClientSize, allowUpscaling: true);
        Assert.NotNull(preview);
        optimized.SetFrameImage(source, preview);
        using var expected = new Bitmap(viewWidth, viewHeight);
        using var actual = new Bitmap(viewWidth, viewHeight);
        reference.DrawToBitmap(expected, reference.ClientRectangle);
        optimized.DrawToBitmap(actual, optimized.ClientRectangle);
        AssertEqualPixels(expected, actual);
        // Exercise the native WM_PRINTCLIENT branch as well as OnPaint.
        using (Graphics graphics = Graphics.FromImage(actual))
        {
            graphics.Clear(Color.Magenta);
            nint hdc = graphics.GetHdc();
            try { _ = SendMessage(optimized.Handle, 0x0318, hdc, 4); }
            finally { graphics.ReleaseHdc(hdc); }
        }
        AssertEqualPixels(expected, actual);
        // Neither the preview nor the control owns the source bitmap.
        optimized.SetFrameImage(null, null);
        Assert.Equal(width, source.Width);
    }

    [Fact]
    public void ChangingPerformanceDigitsDoesNotRelayoutTheViewerFooter()
    {
        using var client = new RemoteViewerClient();
        using var window = new RemoteViewerWindow(client, "footer-layout-test", inputEnabled: false,
            clipboardTextEnabled: false, filePasteEnabled: false, fileDropPasteEnabled: false, remoteFilePullEnabled: false, isAndroidRemote: false);
        window.SetPerformanceStatus("JPEG 3840x2160 | 10.0 FPS | RTT 20ms");
        var actions = (FlowLayoutPanel)typeof(RemoteViewerWindow).GetField("_fileTransferActionsPanel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        int layouts = 0;
        actions.Layout += (_, _) => layouts++;
        for (int i = 0; i < 20; i++) window.SetPerformanceStatus($"JPEG 3840x2160 | {i}.0 FPS | RTT {i}ms");
        Assert.Equal(0, layouts);
    }

    [Fact]
    public void UnchangedFooterGeometryDoesNotForceAnotherButtonLayout()
    {
        using var footer = new Panel { ClientSize = new Size(1200, 60) };
        using var status = new Panel { Dock = DockStyle.Fill };
        using var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Right };
        actions.Controls.Add(new Button { Text = "接收目录", AutoSize = true });
        RemoteViewerWindow.ConfigureStatusFooterChildren(footer, status, actions);
        RemoteViewerWindow.LayoutStatusFooter(footer, status, actions, hasDetails: true, 96);
        int layouts = 0;
        actions.Layout += (_, _) => layouts++;
        for (int i = 0; i < 20; i++) RemoteViewerWindow.LayoutStatusFooter(footer, status, actions, hasDetails: true, 96);
        Assert.Equal(0, layouts);
    }

    [Fact]
    public void DisposeReleasesTheNativeBitmapAndRetainsTheUnownedSource()
    {
        using var source = CreatePattern(320, 180);
        using var preview = PreparedSoftwareBitmap.Create(source, new Size(197, 113), true);
        Assert.NotNull(preview);
        nint handle = (nint)typeof(PreparedSoftwareBitmap).GetField("_nativePreview", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(preview)!;
        Assert.Equal(7u, GetObjectType(handle)); // OBJ_BITMAP
        preview.Dispose();
        Assert.True(GdiFlush()); // DeleteObject may be batched on this thread.
        // GetObjectType (and a size-only GetObject call) can still return the
        // encoded handle type after deletion. Reading actual bitmap data checks
        // the live object, rather than that cached/type-only answer.
        Assert.Equal(0, GetObject(handle, Marshal.SizeOf<NativeBitmap>(), out _));
        Assert.Equal(320, source.Width);
    }

    [Fact]
    public void ChangedViewportOrDisplayModeUsesOriginalPixelsUntilNextPreparedFrame()
    {
        using var source = CreatePattern(320, 180);
        using var preview = PreparedSoftwareBitmap.Create(source, new Size(197, 113), true);
        using var target = new Bitmap(240, 140);
        using var graphics = Graphics.FromImage(target);
        Assert.False(preview!.TryDraw(graphics, source, target.Size, PictureBoxSizeMode.Zoom));
        Assert.False(preview.TryDraw(graphics, source, new Size(197, 113), PictureBoxSizeMode.CenterImage));
        using var other = new Bitmap(source);
        Assert.False(preview.TryDraw(graphics, other, new Size(197, 113), PictureBoxSizeMode.Zoom));
        Assert.True(preview.TryDraw(graphics, source, new Size(197, 113), PictureBoxSizeMode.Zoom));
        preview.Dispose();
        Assert.False(preview.TryDraw(graphics, source, new Size(197, 113), PictureBoxSizeMode.Zoom));
        Assert.Equal(320, source.Width);
    }

    [Fact]
    public void NativeSizeMinimizedAndOversizedViewportsDoNotAllocatePreview()
    {
        using var source = new Bitmap(320, 180);
        Assert.Null(PreparedSoftwareBitmap.Create(source, source.Size, true));
        Assert.Null(PreparedSoftwareBitmap.Create(source, new Size(640, 480), false));
        Assert.Null(PreparedSoftwareBitmap.Create(source, Size.Empty, true));
        Assert.Null(PreparedSoftwareBitmap.Create(source, new Size(100_000, 100_000), true));
    }

    [Fact]
    public void FrameTransfersOriginalAndPreviewOwnershipWithoutChangingRemoteDimensions()
    {
        using var frame = CreateFrame(new Bitmap(320, 180));
        frame.PrepareSoftwareBitmap(new Size(197, 113), false);
        using var source = frame.DetachBitmap();
        using var preview = frame.DetachPreparedSoftwareBitmap();
        frame.Dispose();
        Assert.NotNull(source);
        Assert.NotNull(preview);
        Assert.Equal(new Size(320, 180), source.Size);
        Assert.Equal(320, frame.Frame.Width);
        Assert.Equal(180, frame.Frame.Height);
        using var target = new Bitmap(197, 113);
        using var graphics = Graphics.FromImage(target);
        Assert.True(preview.TryDraw(graphics, source, target.Size, PictureBoxSizeMode.Zoom));
    }

    [Fact]
    public void StalePreviewCannotReplaceANewerHardwarePresentation()
    {
        using var client = new RemoteViewerClient();
        using var window = new RemoteViewerWindow(client, "software-epoch-test", inputEnabled: false,
            clipboardTextEnabled: false, filePasteEnabled: false, fileDropPasteEnabled: false, remoteFilePullEnabled: false, isAndroidRemote: false);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(RemoteViewerWindow).GetField("_softwarePresentationEpoch", flags)!.SetValue(window, 2L);
        var queuedBitmap = new Bitmap(320, 180);
        using var queued = CreateFrame(queuedBitmap);
        queued.PrepareSoftwareBitmap(new Size(197, 113), false);
        typeof(RemoteViewerWindow).GetMethod("QueueDecodedRemoteImage", flags)!.Invoke(window, [queued]);
        Assert.Throws<ArgumentException>(() => _ = queuedBitmap.Width);

        var mailbox = (LatestFrameMailbox<RemoteViewerWindow.DecodedRemoteFrame>)
            typeof(RemoteViewerWindow).GetField("_decodedFrameMailbox", flags)!.GetValue(window)!;
        Assert.False(mailbox.HasPendingDispatch);
        var dispatchedBitmap = new Bitmap(320, 180);
        using var dispatched = CreateFrame(dispatchedBitmap);
        mailbox.Offer(dispatched); // Epoch advanced after this frame was already queued.
        typeof(RemoteViewerWindow).GetMethod("ProcessLatestRemoteImage", flags)!.Invoke(window, [0L]);
        Assert.Throws<ArgumentException>(() => _ = dispatchedBitmap.Width);
        Assert.Null(typeof(RemoteViewerWindow).GetField("_currentImage", flags)!.GetValue(window));
    }

    [Fact]
    public void DecodedBitmapPassesThroughBoundedBackgroundPreparationBeforeUiDispatch()
    {
        using var client = new RemoteViewerClient();
        using var window = new RemoteViewerWindow(client, "software-pipeline-test", inputEnabled: false,
            clipboardTextEnabled: false, filePasteEnabled: false, fileDropPasteEnabled: false, remoteFilePullEnabled: false, isAndroidRemote: false);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(RemoteViewerWindow).GetField("_pictureBoxClientWidth", flags)!.SetValue(window, 197);
        typeof(RemoteViewerWindow).GetField("_pictureBoxClientHeight", flags)!.SetValue(window, 113);
        var bitmap = new Bitmap(320, 180);
        var metadata = RemoteFrameMetadata.FromFrame(new RemoteFrame(320, 180, RemoteFrameEncoding.Jpeg,
            RemoteFrameFlags.None, [1], 0, 1, 0, 0));
        typeof(RemoteViewerWindow).GetMethod("QueueRemoteImage", flags)!.Invoke(window,
            [metadata, bitmap, null, 0L, 0d, null, false, 0L, 0L, 0L]);
        var mailbox = (LatestFrameMailbox<RemoteViewerWindow.DecodedRemoteFrame>)
            typeof(RemoteViewerWindow).GetField("_decodedFrameMailbox", flags)!.GetValue(window)!;
        Assert.True(SpinWait.SpinUntil(() => mailbox.HasPendingDispatch, TimeSpan.FromSeconds(3)));
        using var frame = mailbox.TakeLatest();
        Assert.NotNull(frame);
        using var preview = frame.DetachPreparedSoftwareBitmap();
        Assert.NotNull(preview);
        Assert.Equal(320, frame.Frame.Width);
        mailbox.CompleteDispatch();
    }

    private static RemoteViewerWindow.DecodedRemoteFrame CreateFrame(Bitmap bitmap) => new(
        new RemoteFrame(bitmap.Width, bitmap.Height, RemoteFrameEncoding.Jpeg, RemoteFrameFlags.None, [1], 0, 1, 0, 0),
        bitmap, null, 0, 0, null, false);

    private static Bitmap CreatePattern(int width, int height)
    {
        var bitmap = new Bitmap(width, height);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        graphics.SmoothingMode = SmoothingMode.None;
        for (int y = 0; y < height; y += 7)
            graphics.DrawLine(y % 2 == 0 ? Pens.Red : Pens.Blue, 0, y, width - 1, y);
        using var font = new Font("Microsoft YaHei UI", 13);
        graphics.DrawString("中文 Abc 0123", font, Brushes.Black, 4, 8);
        graphics.DrawRectangle(Pens.Black, 0, 0, width - 1, height - 1);
        return bitmap;
    }

    private static void AssertEqualPixels(Bitmap expected, Bitmap actual)
    {
        int maximumDifference = 0;
        byte[] expectedPixels = ReadPixels(expected), actualPixels = ReadPixels(actual);
        int differingChannels = 0, worstIndex = 0;
        double squaredError = 0;
        for (int index = 0; index < expectedPixels.Length; index++)
        {
            int difference = Math.Abs(expectedPixels[index] - actualPixels[index]);
            if (difference > 0) differingChannels++;
            squaredError += difference * difference;
            if (difference > maximumDifference) { maximumDifference = difference; worstIndex = index; }
        }
        double psnr = squaredError == 0 ? double.PositiveInfinity : 10 * Math.Log10(255d * 255 * expectedPixels.Length / squaredError);
        // GDI+ can round a few 4K border samples twice when the destination is
        // centered versus rendered at the origin. Allow those two-unit samples
        // only when their aggregate error is below the strict 70 dB bound.
        Assert.True(maximumDifference <= 1 || (maximumDifference <= 2 && psnr >= 70),
            $"max {maximumDifference}, differing {differingChannels}, PSNR {psnr:F2}, worst pixel ({worstIndex / 4 % expected.Width},{worstIndex / 4 / expected.Width})");
    }

    private static byte[] ReadPixels(Bitmap bitmap)
    {
        var pixels = new byte[checked(bitmap.Width * bitmap.Height * 4)];
        var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < bitmap.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * bitmap.Width * 4, bitmap.Width * 4);
        }
        finally { bitmap.UnlockBits(data); }
        return pixels;
    }

    [DllImport("gdi32.dll")] private static extern uint GetObjectType(nint value);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] private static extern int GetObject(nint value, int size, out NativeBitmap bitmap);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int Type, Width, Height, WidthBytes;
        public ushort Planes, BitsPixel;
        public nint Bits;
    }
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
}
