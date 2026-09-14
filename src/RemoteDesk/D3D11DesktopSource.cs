using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Box = Vortice.Mathematics.Box;

namespace RemoteDesk;

// Pointer fields describe this update only. PointerUpdatedAt == 0 means no
// pointer update; it must not be interpreted as "the cursor is now hidden".
internal readonly record struct NativeDesktopSurfaceDiagnostics(long PointerUpdatedAt, bool PointerVisible,
    Point PointerPosition, bool RectanglesCoalesced, bool ProtectedContentMasked, uint AccumulatedFrames,
    int DamageRectangleCount, Rectangle FirstDamageRectangle);

internal sealed class NativeDesktopSurface : IDisposable
{
    private ID3D11Texture2D? _texture;
    private Action? _release;
    internal NativeDesktopSurface(ID3D11Texture2D texture, NativeSurfaceManifest manifest,
        long capturedAt, long desktopPresentedAt, Action release, NativeDesktopSurfaceDiagnostics diagnostics = default)
    {
        _texture = texture; Manifest = manifest; CapturedAtTimestamp = capturedAt;
        DesktopPresentedAtTimestamp = desktopPresentedAt; _release = release; Diagnostics = diagnostics;
    }
    public ID3D11Texture2D Texture => _texture ?? throw new ObjectDisposedException(nameof(NativeDesktopSurface));
    public NativeSurfaceManifest Manifest { get; }
    public long CapturedAtTimestamp { get; }
    public long DesktopPresentedAtTimestamp { get; }
    public NativeDesktopSurfaceDiagnostics Diagnostics { get; }
    public void Dispose()
    {
        Interlocked.Exchange(ref _texture, null)?.Dispose();
        Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}

/// <summary>
/// Nonblocking DXGI native source with conservative, same-AcquireNextFrame tile
/// versions. GPU copies preserve an immutable source after ReleaseFrame. The
/// optional fixed crop lets tests restrict all encoding/readback to their own
/// visible synthetic window; production may use the complete selected output.
/// Never selected automatically by the existing FFmpeg host path.
/// </summary>
internal sealed class D3D11DesktopSource : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGIOutputDuplication _duplication;
    private readonly NativeSurfaceDamageTracker _damage;
    private readonly Rectangle _crop;
    private readonly Size _outputSize;
    private readonly RawRect[] _dirty = new RawRect[NativeSurfaceDamageTracker.MaximumDamageRectangles];
    private readonly OutduplMoveRect[] _moves = new OutduplMoveRect[NativeSurfaceDamageTracker.MaximumDamageRectangles / 2];
    private int _outstanding;
    private bool _hasFrame;
    private bool _disposed;
    private bool _failed;
    private ID3D11Texture2D? _lastSnapshot;

    private D3D11DesktopSource(ID3D11Device device, ID3D11DeviceContext context,
        IDXGIOutputDuplication duplication, Size outputSize, Rectangle crop, long initialSequence)
    {
        _device = device; _context = context; _duplication = duplication;
        _outputSize = outputSize; _crop = crop; _damage = new(crop.Size, initialSequence);
    }

    public bool IsFailed => _failed;
    public string? Failure { get; private set; }
    public int OutstandingFrames => Volatile.Read(ref _outstanding);
    public Size NativeSize => _crop.Size;
    public ID3D11Device AcquireDeviceLease() => _device.QueryInterface<ID3D11Device>();

    public static bool TryCreate(WindowsDesktopDuplicationTarget target, Rectangle? fixedCrop,
        out D3D11DesktopSource? source, out string? failure, long initialSequence = 0)
    {
        source = null; failure = null;
        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;
        IDXGIOutputDuplication? duplication = null;
        try
        {
            using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            factory.EnumAdapters1((uint)target.AdapterIndex, out IDXGIAdapter1 adapter).CheckError();
            using (adapter)
            {
                adapter.EnumOutputs((uint)target.OutputIndex, out IDXGIOutput output).CheckError();
                using (output)
                {
                    var description = output.Description;
                    var rectangle = description.DesktopCoordinates;
                    var bounds = Rectangle.FromLTRB(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
                    if (!description.AttachedToDesktop || description.DeviceName != target.DeviceName ||
                        bounds != target.Bounds || description.Rotation != ModeRotation.Identity)
                        throw new NotSupportedException("Output changed, detached or rotated; use the existing capture backend.");
                    D3D11.D3D11CreateDevice(adapter, DriverType.Unknown,
                        DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                        [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out device).CheckError();
                    if (device is null) throw new InvalidOperationException("No D3D11 device.");
                    context = device.ImmediateContext;
                    using ID3D11Multithread multithread = context.QueryInterface<ID3D11Multithread>();
                    multithread.SetMultithreadProtected(true);
                    using IDXGIOutput1 output1 = output.QueryInterface<IDXGIOutput1>();
                    duplication = output1.DuplicateOutput(device);
                    Rectangle crop = fixedCrop ?? new Rectangle(Point.Empty, bounds.Size);
                    if (crop.Width < 48 || crop.Height < 48 || crop.X < 0 || crop.Y < 0 ||
                        (long)crop.X + crop.Width > bounds.Width || (long)crop.Y + crop.Height > bounds.Height)
                        throw new ArgumentOutOfRangeException(nameof(fixedCrop));
                    source = new(device, context, duplication, bounds.Size, crop, initialSequence);
                    device = null; context = null; duplication = null;
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { failure = $"{ex.GetType().Name}: {ex.Message}"; return false; }
        finally { duplication?.Dispose(); context?.Dispose(); device?.Dispose(); }
    }

    public NativeDesktopSurface? TryAcquire(bool repeatUnchanged = false)
    {
        if (_disposed || _failed || OutstandingFrames >= 3) return null;
        bool acquired = false;
        ID3D11Texture2D? snapshot = null;
        try
        {
            var result = _duplication.AcquireNextFrame(0, out OutduplFrameInfo info, out IDXGIResource resource);
            if (result == Vortice.DXGI.ResultCode.WaitTimeout) return repeatUnchanged ? RepeatUnchanged() : null;
            result.CheckError(); acquired = true;
            using (resource)
            {
                // A cursor-only update is not a new native image. Keep the last
                // base/manifest pairing rather than inventing damage versions.
                if (_hasFrame && info.LastPresentTime == 0) return repeatUnchanged ? RepeatUnchanged() : null;
                using ID3D11Texture2D frame = resource.QueryInterface<ID3D11Texture2D>();
                var description = frame.Description;
                if (description.Width != _outputSize.Width || description.Height != _outputSize.Height ||
                    description.Format != Format.B8G8R8A8_UNorm)
                    throw new InvalidDataException("DXGI source format changed.");
                List<Rectangle>? dirty = info.ProtectedContentMaskedOut ? null : ReadDamage();
                NativeSurfaceManifest manifest = _damage.Observe(dirty);
                snapshot = _device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)_crop.Width, Height = (uint)_crop.Height, MipLevels = 1, ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget
                });
                _context.CopySubresourceRegion(snapshot, 0, 0, 0, 0, frame, 0,
                    new Box(_crop.Left, _crop.Top, 0, _crop.Right, _crop.Bottom, 1));
                long capturedAt = Stopwatch.GetTimestamp();
                _lastSnapshot?.Dispose();
                _lastSnapshot = snapshot.QueryInterface<ID3D11Texture2D>();
                _hasFrame = true;
                Interlocked.Increment(ref _outstanding);
                var owned = new NativeDesktopSurface(snapshot, manifest, capturedAt, info.LastPresentTime,
                    () => Interlocked.Decrement(ref _outstanding),
                    new(info.LastMouseUpdateTime, info.PointerPosition.Visible,
                        new(info.PointerPosition.Position.X, info.PointerPosition.Position.Y),
                        info.RectsCoalesced, info.ProtectedContentMaskedOut, info.AccumulatedFrames,
                        dirty?.Count ?? -1, dirty is { Count: > 0 } ? dirty[0] : Rectangle.Empty));
                snapshot = null;
                return owned;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { _failed = true; Failure = $"{ex.GetType().Name}: {ex.Message}"; return null; }
        finally
        {
            snapshot?.Dispose();
            if (acquired)
            {
                try { _duplication.ReleaseFrame().CheckError(); }
                catch (Exception ex) { _failed = true; Failure = $"DXGI frame release failed: {ex.Message}"; }
            }
        }
    }

    // AcquireNextFrame has just confirmed no new desktop pixels. Reuse the
    // immutable GPU surface with a NEW publication sequence, so a static
    // desktop can finish detail transfers without waiting for mouse motion.
    private NativeDesktopSurface? RepeatUnchanged()
    {
        if (_lastSnapshot is null) return null;
        var lease = _lastSnapshot.QueryInterface<ID3D11Texture2D>();
        Interlocked.Increment(ref _outstanding);
        return new(lease, _damage.Observe(Array.Empty<Rectangle>()), Stopwatch.GetTimestamp(), 0,
            () => Interlocked.Decrement(ref _outstanding));
    }

    private List<Rectangle>? ReadDamage()
    {
        uint dirtyCapacity = (uint)(_dirty.Length * Marshal.SizeOf<RawRect>());
        uint moveCapacity = (uint)(_moves.Length * Marshal.SizeOf<OutduplMoveRect>());
        if (_duplication.GetFrameDirtyRects(dirtyCapacity, _dirty, out uint dirtyBytes).Failure ||
            _duplication.GetFrameMoveRects(moveCapacity, _moves, out uint moveBytes).Failure ||
            dirtyBytes > dirtyCapacity || moveBytes > moveCapacity ||
            dirtyBytes % Marshal.SizeOf<RawRect>() != 0 || moveBytes % Marshal.SizeOf<OutduplMoveRect>() != 0)
            return null;
        int dirtyCount = (int)(dirtyBytes / Marshal.SizeOf<RawRect>());
        int moveCount = (int)(moveBytes / Marshal.SizeOf<OutduplMoveRect>());
        if (dirtyCount + 2 * moveCount > NativeSurfaceDamageTracker.MaximumDamageRectangles) return null;
        var rectangles = new List<Rectangle>(dirtyCount + 2 * moveCount);
        for (int i = 0; i < dirtyCount; i++)
            if (!AppendClipped(_dirty[i], rectangles)) return null;
        for (int i = 0; i < moveCount; i++)
        {
            var move = _moves[i];
            if (!AppendClipped(move.DestinationRect, rectangles)) return null;
            long right = (long)move.SourcePoint.X + move.DestinationRect.Right - move.DestinationRect.Left;
            long bottom = (long)move.SourcePoint.Y + move.DestinationRect.Bottom - move.DestinationRect.Top;
            if (right > int.MaxValue || right < int.MinValue || bottom > int.MaxValue || bottom < int.MinValue) return null;
            if (!AppendClipped(new RawRect(move.SourcePoint.X, move.SourcePoint.Y, (int)right, (int)bottom), rectangles)) return null;
        }
        return rectangles;
    }

    private bool AppendClipped(RawRect rectangle, List<Rectangle> rectangles)
    {
        if (rectangle.Left < 0 || rectangle.Top < 0 || rectangle.Right < rectangle.Left ||
            rectangle.Bottom < rectangle.Top || rectangle.Right > _outputSize.Width || rectangle.Bottom > _outputSize.Height)
            return false;
        Rectangle clipped = Rectangle.Intersect(_crop,
            Rectangle.FromLTRB(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom));
        if (clipped.Width > 0 && clipped.Height > 0)
        { clipped.Offset(-_crop.X, -_crop.Y); rectangles.Add(clipped); }
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _lastSnapshot?.Dispose(); _lastSnapshot = null;
        _duplication.Dispose(); _context.Dispose(); _device.Dispose();
    }
}
