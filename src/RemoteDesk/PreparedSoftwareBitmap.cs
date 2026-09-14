using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace RemoteDesk;

// Preserve the existing bicubic pixels, but neither scale nor enter GDI+'s
// image-rendering path on the UI thread while the next frame is being prepared.
internal sealed class PreparedSoftwareBitmap : IDisposable
{
    private nint _nativePreview;
    private readonly Bitmap _source;
    private readonly Size _viewport;
    internal Rectangle Destination { get; }

    private PreparedSoftwareBitmap(Bitmap source, nint preview, Size viewport, Rectangle destination)
    {
        _source = source;
        _nativePreview = preview;
        _viewport = viewport;
        Destination = destination;
    }

    internal static PreparedSoftwareBitmap? Create(Bitmap source, Size viewport, bool allowUpscaling)
    {
        if (viewport.Width <= 0 || viewport.Height <= 0) return null;
        if (!allowUpscaling && source.Width <= viewport.Width && source.Height <= viewport.Height) return null;
        float ratio = Math.Min(viewport.Width / (float)source.Width, viewport.Height / (float)source.Height);
        var size = new Size((int)(source.Width * ratio), (int)(source.Height * ratio));
        if (size.Width <= 0 || size.Height <= 0 || size == source.Size || (long)size.Width * size.Height > 16_777_216)
            return null;
        var destination = new Rectangle((viewport.Width - size.Width) / 2, (viewport.Height - size.Height) / 2, size.Width, size.Height);
        var prepared = new PreparedSoftwareBitmap(source, 0, viewport, destination);
        try
        {
            var info = new BitmapInfo { Size = 40, Width = size.Width, Height = -size.Height, Planes = 1, BitCount = 32 };
            prepared._nativePreview = CreateDIBSection(0, ref info, 0, out nint pixels, 0, 0);
            if (prepared._nativePreview == 0 || pixels == 0) throw new ExternalException("Could not allocate a prepared display surface.");
            // Render directly into the native bitmap: no GetHbitmap copy and
            // no separate second full-frame pixel allocation.
            using var preview = new Bitmap(size.Width, size.Height, checked(size.Width * 4), PixelFormat.Format32bppPArgb, pixels);
            using (Graphics graphics = Graphics.FromImage(preview))
            {
                graphics.Clear(Color.Black);
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(source, new Rectangle(Point.Empty, size));
            }
            return prepared;
        }
        catch { prepared.Dispose(); throw; }
    }

    internal bool TryDraw(Graphics graphics, Image? source, Size viewport, PictureBoxSizeMode mode)
    {
        if (!Matches(source, viewport, mode)) return false;
        nint target = graphics.GetHdc();
        try { return TryDraw(target, paintBackground: false); }
        finally { graphics.ReleaseHdc(target); }
    }

    internal bool Matches(Image? source, Size viewport, PictureBoxSizeMode mode) =>
        _nativePreview != 0 && ReferenceEquals(source, _source) && viewport == _viewport && mode == PictureBoxSizeMode.Zoom;

    internal bool TryDraw(nint target, bool paintBackground)
    {
        nint bitmap = _nativePreview;
        if (bitmap == 0 || target == 0) return false;
        nint memory = 0, previous = 0;
        try
        {
            memory = CreateCompatibleDC(target);
            if (memory == 0) return false;
            previous = SelectObject(memory, bitmap);
            if (previous == 0 || previous == -1) return false;
            if (paintBackground)
            {
                FillBlack(target, new NativeRect(0, 0, _viewport.Width, Destination.Top));
                FillBlack(target, new NativeRect(0, Destination.Bottom, _viewport.Width, _viewport.Height));
                FillBlack(target, new NativeRect(0, Destination.Top, Destination.Left, Destination.Bottom));
                FillBlack(target, new NativeRect(Destination.Right, Destination.Top, _viewport.Width, Destination.Bottom));
            }
            return BitBlt(target, Destination.X, Destination.Y, Destination.Width, Destination.Height, memory, 0, 0, 0x00CC0020) && GdiFlush();
        }
        finally
        {
            if (previous != 0 && previous != -1) _ = SelectObject(memory, previous);
            if (memory != 0) _ = DeleteDC(memory);
        }
    }

    private static void FillBlack(nint hdc, NativeRect rect)
    {
        if (rect.Right > rect.Left && rect.Bottom > rect.Top) _ = FillRect(hdc, ref rect, GetStockObject(4));
    }

    public void Dispose()
    {
        nint bitmap = Interlocked.Exchange(ref _nativePreview, 0);
        if (bitmap != 0) _ = DeleteObject(bitmap);
    }

    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint hdc, nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint hdc, ref BitmapInfo info, uint usage, out nint pixels, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint GetStockObject(int index);
    [DllImport("user32.dll")] private static extern int FillRect(nint hdc, ref NativeRect rect, nint brush);
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeRect(int Left, int Top, int Right, int Bottom);
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ColorsUsed, ColorsImportant, Color;
    }
}
