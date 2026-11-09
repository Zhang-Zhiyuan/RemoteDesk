using System.Buffers;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RemoteDesk;

// The privileged desktop bridge remains local, authenticated IPC. Its captured
// image is converted to H.264 BEFORE entering the network. This bounded software
// path also covers unavailable GPU capture; the normal GPU path is unchanged.
// One input is outstanding at a time. RTP's marker completes that input without
// waiting for a following frame (important for an unchanged login screen).
internal sealed class CapturedFrameH264Encoder : IDisposable
{
    private readonly Process _process;
    private readonly Socket _receiver;
    private readonly SafeFileHandle? _job;
    private readonly Task _stderrReader;
    private readonly H264RtpAccessUnitAssembler _assembler = new();
    private readonly byte[] _packet = new byte[65536];
    private readonly byte[] _pixels;
    private readonly Bitmap _bitmap;
    private readonly Graphics _graphics;
    private string _error = string.Empty;
    private bool _disposed;
    private bool _failed;
    private int _frames;

    internal Size Size { get; }

    internal CapturedFrameH264Encoder(string executable, Size size, int fps)
    {
        ValidateSize(size);
        if (fps is < 1 or > 30) throw new ArgumentOutOfRangeException(nameof(fps));
        Size = size;
        _receiver = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _receiver.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _receiver.ReceiveBufferSize = 4 * 1024 * 1024;
        _pixels = ArrayPool<byte>.Shared.Rent(checked(size.Width * size.Height * 4));
        _bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        _graphics = Graphics.FromImage(_bitmap);
        _process = new Process { StartInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardError = true
        }};
        try
        {
            foreach (string argument in BuildArguments(size, fps, ((IPEndPoint)_receiver.LocalEndPoint!).Port))
                _process.StartInfo.ArgumentList.Add(argument);
            if (!_process.Start()) throw new IOException("H.264 备用编码器未启动。");
            _job = WindowsKillOnCloseJob.TryCreateAndAssign(_process);
            _stderrReader = ReadErrorsAsync();
        }
        catch
        {
            _process.Dispose(); _receiver.Dispose(); _graphics.Dispose(); _bitmap.Dispose();
            ArrayPool<byte>.Shared.Return(_pixels);
            throw;
        }
    }

    internal static Size ResolveOutputSize(Size source, bool adaptive)
    {
        if (source.Width < 2 || source.Height < 2 || source.Width > 8192 || source.Height > 8192)
            throw new ArgumentOutOfRangeException(nameof(source));
        // Respect orientation. The compatibility encoder does not create a
        // large first-frame burst while GPU capture is unavailable.
        double scale = adaptive ? Math.Min(1, Math.Min(1920d / Math.Max(source.Width, source.Height),
            1080d / Math.Min(source.Width, source.Height))) : 1;
        return new Size(Math.Max(2, (int)(source.Width * scale) & ~1),
            Math.Max(2, (int)(source.Height * scale) & ~1));
    }

    internal static int ResolveCaptureScale(Rectangle bounds, int requestedScale, bool adaptive)
    {
        if (!adaptive || bounds.Width < 2 || bounds.Height < 2) return requestedScale;
        Size requested = ScreenCaptureService.CalculateFrameSize(bounds, requestedScale);
        Size output = ResolveOutputSize(requested, true);
        if (requested == output) return requestedScale;
        int scale = Math.Clamp((int)Math.Floor(100 * Math.Min(output.Width / (double)bounds.Width,
            output.Height / (double)bounds.Height)), 25, 100);
        // 67 is a stored QHD ceiling, not a literal percentage.
        return scale == ScreenCaptureService.QhdMaximumScaleMode ? scale - 1 : scale;
    }

    private static void ValidateSize(Size size)
    {
        if (size.Width is < 2 or > 8192 || size.Height is < 2 or > 8192 ||
            (size.Width & 1) != 0 || (size.Height & 1) != 0 || (long)size.Width * size.Height > 16_777_216)
            throw new ArgumentOutOfRangeException(nameof(size));
    }

    internal static IReadOnlyList<string> BuildArguments(Size size, int fps, int port)
    {
        ValidateSize(size);
        if (fps is < 1 or > 30 || port is < 1 or > 65535) throw new ArgumentOutOfRangeException();
        return ["-hide_banner", "-loglevel", "error", "-nostats", "-probesize", "32", "-analyzeduration", "0",
            "-f", "rawvideo", "-pixel_format", "bgra", "-video_size", $"{size.Width}x{size.Height}",
            "-framerate", fps.ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", "pipe:0",
            "-an", "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-threads", "2",
            "-crf", "18", "-g", "1", "-bf", "0", "-pix_fmt", "yuv420p",
            "-x264-params", "repeat-headers=1:scenecut=0", "-flush_packets", "1", "-f", "rtp",
            "-payload_type", "96", "-rtpflags", "skip_rtcp", "-sdp_file", "NUL",
            $"rtp://127.0.0.1:{port}?pkt_size=16384&connect=1&localaddr=127.0.0.1"];
    }

    internal async Task<AnnexBH264AccessUnit> EncodeAsync(ReadOnlyMemory<byte> jpeg, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_failed) throw new IOException("H.264 备用编码器需要重建。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(_frames == 0 ? 5 : 2));
        try
        {
            using (var input = new MemoryStream(jpeg.ToArray(), writable: false))
            using (var image = Image.FromStream(input))
                _graphics.DrawImage(image, new Rectangle(Point.Empty, Size));
            BitmapData data = _bitmap.LockBits(new Rectangle(Point.Empty, Size), ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                int rowBytes = Size.Width * 4;
                for (int y = 0; y < Size.Height; y++)
                    Marshal.Copy(data.Scan0 + y * data.Stride, _pixels, y * rowBytes, rowBytes);
            }
            finally { _bitmap.UnlockBits(data); }
            // Start receiving before writing: a large IDR must not fill the
            // loopback socket while the final raw input bytes are being written.
            Task<AnnexBH264AccessUnit> receiving = ReceiveAsync(deadline.Token);
            try
            {
                await _process.StandardInput.BaseStream.WriteAsync(
                    _pixels.AsMemory(0, Size.Width * Size.Height * 4), deadline.Token).ConfigureAwait(false);
                await _process.StandardInput.BaseStream.FlushAsync(deadline.Token).ConfigureAwait(false);
                AnnexBH264AccessUnit result = await receiving.ConfigureAwait(false);
                _frames++;
                return result;
            }
            catch
            {
                deadline.Cancel();
                try { (await receiving.ConfigureAwait(false)).Dispose(); } catch { }
                throw;
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            _failed = true;
            throw new IOException("H.264 备用编码超时。" + Volatile.Read(ref _error));
        }
        catch { _failed = true; throw; }
    }

    private async Task<AnnexBH264AccessUnit> ReceiveAsync(CancellationToken token)
    {
        while (true)
        {
            int count = await _receiver.ReceiveAsync(_packet, SocketFlags.None, token).ConfigureAwait(false);
            AnnexBH264AccessUnit? frame = _assembler.AppendPacket(_packet.AsSpan(0, count));
            if (frame is null) continue;
            if (frame.Flags.HasFlag(RemoteFrameFlags.KeyFrame) && frame.Flags.HasFlag(RemoteFrameFlags.CodecConfig))
                return frame;
            frame.Dispose();
            throw new InvalidDataException("H.264 备用编码器未返回可独立解码的恢复帧。");
        }
    }

    private async Task ReadErrorsAsync()
    {
        var chars = new char[512];
        try
        {
            int count;
            while ((count = await _process.StandardError.ReadAsync(chars).ConfigureAwait(false)) > 0)
                Volatile.Write(ref _error, new string(chars, 0, count));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _receiver.Dispose();
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        _job?.Dispose();
        try { _process.WaitForExit(1000); } catch (InvalidOperationException) { }
        _process.Dispose(); _graphics.Dispose(); _bitmap.Dispose();
        ArrayPool<byte>.Shared.Return(_pixels);
    }
}
