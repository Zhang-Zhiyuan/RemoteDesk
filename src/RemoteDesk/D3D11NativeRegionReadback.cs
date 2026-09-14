using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Box = Vortice.Mathematics.Box;
using MapFlags = Vortice.Direct3D11.MapFlags;

namespace RemoteDesk;

internal sealed record NativeRegionPixels(long SourceTime100Nanoseconds, Rectangle Bounds, byte[] Rgba);

/// <summary>
/// Bounded, single-owner, opportunistic native-pixel readback from the exact
/// immutable BGRA texture submitted to the base encoder. Enqueue only AFTER
/// submitting the base. No whole-frame download, Flush, query wait or blocking
/// Map. The caller remains responsible for source/session/tile-version checks.
/// </summary>
internal sealed class D3D11NativeRegionReadback : IDisposable
{
    internal const int TileEdge = 128;
    internal const int SlotCount = 4;
    internal const int MaximumCopiesPerSource = 2;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11Multithread _multithread;
    private readonly Slot[] _slots;
    private long _latestSourceTime = -1;
    private int _copiesForSource;
    private bool _disposed;
    private bool _failed;

    private sealed class Slot(ID3D11Texture2D texture)
    {
        public ID3D11Texture2D Texture { get; } = texture;
        public long? SourceTime;
        public long EnqueuedAt;
        public Rectangle Bounds;
    }

    public D3D11NativeRegionReadback(ID3D11Device borrowedDevice)
    {
        _device = borrowedDevice.QueryInterface<ID3D11Device>();
        var owned = new List<IDisposable> { _device };
        try
        {
            _context = _device.ImmediateContext; owned.Add(_context);
            _multithread = _context.QueryInterface<ID3D11Multithread>(); owned.Add(_multithread);
            _multithread.SetMultithreadProtected(true);
            _slots = new Slot[SlotCount];
            for (int i = 0; i < SlotCount; i++)
            {
                var texture = _device.CreateTexture2D(new Texture2DDescription
                {
                    Width = TileEdge, Height = TileEdge, MipLevels = 1, ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read
                });
                owned.Add(texture); _slots[i] = new Slot(texture);
            }
        }
        catch
        {
            for (int i = owned.Count - 1; i >= 0; i--) owned[i].Dispose();
            throw;
        }
    }

    public bool IsFailed => _failed;
    public string? Failure { get; private set; }
    public int PendingCount => _slots.Count(slot => slot.SourceTime.HasValue);
    public int StagingBytes => _disposed ? 0 : SlotCount * TileEdge * TileEdge * 4;
    public bool HasPendingRegion(Rectangle bounds) =>
        !_disposed && _slots.Any(slot => slot.SourceTime.HasValue && slot.Bounds == bounds);

    internal static bool IsValidRegion(Size nativeSize, Rectangle bounds) =>
        nativeSize.Width is > 0 and <= 8192 && nativeSize.Height is > 0 and <= 8192 &&
        (long)nativeSize.Width * nativeSize.Height <= 16_777_216 &&
        bounds.X >= 0 && bounds.Y >= 0 && bounds.Width is > 0 and <= TileEdge &&
        bounds.Height is > 0 and <= TileEdge &&
        (long)bounds.X + bounds.Width <= nativeSize.Width &&
        (long)bounds.Y + bounds.Height <= nativeSize.Height;

    public bool TryEnqueue(ID3D11Texture2D immutableNativeTexture, long sourceTime100Nanoseconds,
        Rectangle bounds, NativeDetailRenderBudget budget)
    {
        if (_disposed || _failed || !budget.Allows(Stopwatch.GetTimestamp())) return false;
        if (sourceTime100Nanoseconds < 0 || sourceTime100Nanoseconds < _latestSourceTime) return false;
        if (sourceTime100Nanoseconds == _latestSourceTime && _copiesForSource >= MaximumCopiesPerSource) return false;
        Slot? slot = _slots.FirstOrDefault(candidate => !candidate.SourceTime.HasValue);
        if (slot is null) return false;
        Texture2DDescription description = immutableNativeTexture.Description;
        if (description.Format != Format.B8G8R8A8_UNorm || description.ArraySize != 1 ||
            description.MipLevels != 1 || description.SampleDescription.Count != 1 ||
            !IsValidRegion(new((int)description.Width, (int)description.Height), bounds))
            throw new ArgumentException("Expected a valid region of one native BGRA surface.", nameof(bounds));
        // Device is the texture's cached borrowed wrapper (not an owned lease).
        ID3D11Device sourceDevice = immutableNativeTexture.Device;
        if (sourceDevice.NativePointer != _device.NativePointer)
            throw new ArgumentException("Native texture belongs to another device.", nameof(immutableNativeTexture));
        try
        {
            _multithread.Enter();
            try
            {
                if (!budget.Allows(Stopwatch.GetTimestamp())) return false;
                _context.CopySubresourceRegion(slot.Texture, 0, 0, 0, 0, immutableNativeTexture, 0,
                    new Box(bounds.Left, bounds.Top, 0, bounds.Right, bounds.Bottom, 1));
            }
            finally { _multithread.Leave(); }
            if (_latestSourceTime != sourceTime100Nanoseconds)
            {
                _latestSourceTime = sourceTime100Nanoseconds;
                _copiesForSource = 0;
            }
            _copiesForSource++;
            slot.SourceTime = sourceTime100Nanoseconds;
            slot.Bounds = bounds;
            slot.EnqueuedAt = Stopwatch.GetTimestamp();
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Fail(ex); return false; }
    }

    public NativeRegionPixels? TryRead(NativeDetailRenderBudget budget)
    {
        if (_disposed || _failed || !budget.Allows(Stopwatch.GetTimestamp())) return null;
        try
        {
            foreach (Slot slot in _slots)
            {
                if (!slot.SourceTime.HasValue) continue;
                if (Stopwatch.GetElapsedTime(slot.EnqueuedAt) > TimeSpan.FromMilliseconds(1500))
                { slot.SourceTime = null; continue; }
                byte[] rgba;
                long sourceTime;
                Rectangle bounds = slot.Bounds;
                _multithread.Enter();
                try
                {
                    if (!budget.Allows(Stopwatch.GetTimestamp())) return null;
                    MappedSubresource mapped;
                    try { mapped = _context.Map(slot.Texture, 0, MapMode.Read, MapFlags.DoNotWait); }
                    catch (SharpGenException ex) when (ex.ResultCode == Vortice.DXGI.ResultCode.WasStillDrawing) { continue; }
                    try
                    {
                        rgba = new byte[bounds.Width * bounds.Height * 4];
                        int stride = bounds.Width * 4;
                        for (int y = 0; y < bounds.Height; y++)
                            Marshal.Copy(mapped.DataPointer + y * (int)mapped.RowPitch, rgba, y * stride, stride);
                        sourceTime = slot.SourceTime.Value;
                        slot.SourceTime = null;
                    }
                    finally { _context.Unmap(slot.Texture, 0); }
                }
                finally { _multithread.Leave(); }
                // CPU color swizzling does not need to hold the shared GPU
                // context lock used by the hardware base encoder.
                for (int i = 0; i < rgba.Length; i += 4)
                {
                    (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]);
                    rgba[i + 3] = 255;
                }
                return new(sourceTime, bounds, rgba);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Fail(ex); }
        return null;
    }

    public void Clear()
    {
        // Keep the source high-water mark and per-frame quota. A local reset
        // cannot be used to issue an unbounded copy burst for the same frame.
        foreach (Slot slot in _slots) slot.SourceTime = null;
    }

    private void Fail(Exception ex)
    {
        _failed = true; Clear(); Failure = $"{ex.GetType().Name}: {ex.Message}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; Clear();
        foreach (Slot slot in _slots) slot.Texture.Dispose();
        _multithread.Dispose(); _context.Dispose(); _device.Dispose();
    }
}
