using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;
using MapFlags = Vortice.Direct3D11.MapFlags;

namespace RemoteDesk;

/// <summary>
/// Exact RGB comparison of at most 64 requested tiles, not a probabilistic hash
/// or a whole-desktop CPU download. Begin AFTER submitting the base encoder;
/// Complete as soon as that base is ready. A not-ready GPU result is discarded,
/// never a reason to wait or to reuse unverified detail. Single capture owner,
/// one pending base, one previous immutable source lease, 256 B staging memory.
/// Poll nonblocking while the encoder works. One asynchronous Flush submits the
/// trailing optional GPU batch (without a completion wait or encoder drain);
/// otherwise drivers may buffer it until after the corresponding base is sent.
/// Construct away from an active base-frame path (shader/driver startup).
/// </summary>
internal sealed class D3D11NativeDamageRefiner : IDisposable
{
    internal const int MaximumTiles = NativeDetailPresentation.MaximumTiles;
    private static readonly Lazy<byte[]> Code = new(() => D3D11ExperimentalUpscaler.Compile(Shader, "cs", "cs_5_0"));
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11Multithread _multithread;
    private readonly ID3D11ComputeShader _shader;
    private readonly ID3D11Buffer _constants, _results, _staging;
    private readonly ID3D11UnorderedAccessView _resultView;
    private readonly NativeSurfaceContentTracker _versions = new();
    private readonly int[] _rectangles = new int[MaximumTiles * 4];
    private readonly int[] _flags = new int[MaximumTiles];
    private ID3D11Texture2D? _reference, _candidate;
    private NativeSurfaceManifest? _pending;
    private int[]? _comparedTiles;
    private int[] _requestedTiles = [];
    private NativeSurfaceComparison? _proof;
    private bool _disposed;
    public bool IsFailed { get; private set; }
    public string? Failure { get; private set; }
    public int SubmittedComparisons { get; private set; }
    public int CompletedComparisons { get; private set; }
    public int UnreadyComparisons { get; private set; }
    public int PendingCount => _pending is null ? 0 : 1;
    public int StagingBytes => _disposed ? 0 : MaximumTiles * 4;

    public D3D11NativeDamageRefiner(ID3D11Device borrowedDevice)
    {
        var owned = new List<IDisposable>();
        try
        {
            _device = borrowedDevice.QueryInterface<ID3D11Device>(); owned.Add(_device);
            _context = _device.ImmediateContext; owned.Add(_context);
            _multithread = _context.QueryInterface<ID3D11Multithread>(); owned.Add(_multithread);
            _multithread.SetMultithreadProtected(true);
            _shader = _device.CreateComputeShader(Code.Value); owned.Add(_shader);
            _constants = _device.CreateBuffer(MaximumTiles * 16, BindFlags.ConstantBuffer); owned.Add(_constants);
            _results = _device.CreateBuffer(MaximumTiles * 4, BindFlags.UnorderedAccess, ResourceUsage.Default,
                CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, 4); owned.Add(_results);
            _resultView = _device.CreateUnorderedAccessView(_results); owned.Add(_resultView);
            _staging = _device.CreateBuffer(MaximumTiles * 4, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read);
            owned.Add(_staging);
        }
        catch
        {
            for (int i = owned.Count - 1; i >= 0; i--) owned[i].Dispose();
            throw;
        }
    }

    // A false return means "no optional GPU comparison", NOT "drop this base".
    // Complete must still commit the conservative manifest for this frame.
    public bool Begin(ID3D11Texture2D immutableSource, NativeSurfaceManifest manifest,
        ReadOnlySpan<int> requestedTiles, NativeDetailRenderBudget budget)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pending is not null) throw new InvalidOperationException("Only one native base may be pending.");
        _versions.ValidateNext(manifest);
        if (requestedTiles.Length > MaximumTiles) throw new ArgumentOutOfRangeException(nameof(requestedTiles));
        for (int i = 0; i < requestedTiles.Length; i++)
            if (requestedTiles[i] < 0 || requestedTiles[i] >= manifest.Count || requestedTiles[..i].Contains(requestedTiles[i]))
                throw new ArgumentException("Expected unique, in-bounds native tiles.", nameof(requestedTiles));
        var description = immutableSource.Description;
        if (description.Width != manifest.Size.Width || description.Height != manifest.Size.Height ||
            description.Format != Format.B8G8R8A8_UNorm || description.ArraySize != 1 || description.MipLevels != 1 ||
            description.SampleDescription.Count != 1 || !description.BindFlags.HasFlag(BindFlags.ShaderResource) ||
            immutableSource.Device.NativePointer != _device.NativePointer)
            throw new ArgumentException("Expected an immutable native BGRA texture on the owning device.", nameof(immutableSource));
        _candidate = immutableSource.QueryInterface<ID3D11Texture2D>();
        _pending = manifest; _comparedTiles = null; _proof = null;
        _requestedTiles = requestedTiles.ToArray();
        if (IsFailed || _reference is null || requestedTiles.IsEmpty || !budget.Allows(Stopwatch.GetTimestamp())) return false;
        try
        {
            int columns = (manifest.Size.Width + 127) / 128;
            for (int i = 0; i < requestedTiles.Length; i++)
            {
                int x = requestedTiles[i] % columns * 128, y = requestedTiles[i] / columns * 128;
                _rectangles[i * 4] = x; _rectangles[i * 4 + 1] = y;
                _rectangles[i * 4 + 2] = Math.Min(128, manifest.Size.Width - x);
                _rectangles[i * 4 + 3] = Math.Min(128, manifest.Size.Height - y);
            }
            using var before = _device.CreateShaderResourceView(_reference);
            using var after = _device.CreateShaderResourceView(_candidate);
            _multithread.Enter();
            try
            {
                if (!budget.Allows(Stopwatch.GetTimestamp())) return false;
                _context.UpdateSubresource(_rectangles, _constants);
                try
                {
                    _context.CSSetShader(_shader);
                    _context.CSSetConstantBuffer(0, _constants);
                    _context.CSSetShaderResource(0, before);
                    _context.CSSetShaderResource(1, after);
                    _context.CSSetUnorderedAccessView(0, _resultView);
                    _context.Dispatch((uint)requestedTiles.Length, 1, 1);
                }
                finally
                {
                    _context.CSSetUnorderedAccessView(0, null);
                    _context.CSSetShaderResource(0, null); _context.CSSetShaderResource(1, null);
                    _context.CSSetConstantBuffer(0, null); _context.CSSetShader(null);
                }
                _context.CopyResource(_staging, _results);
                // This is asynchronous command submission, not a GPU fence wait
                // or an MFT flush/drain. Early nonblocking polling is also needed
                // by drivers that initiate staging access on the first Map.
                _context.Flush();
            }
            finally { _multithread.Leave(); }
            _comparedTiles = _requestedTiles; SubmittedComparisons++;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Fail(ex); return false; }
    }

    public bool TryPoll(NativeDetailRenderBudget budget)
    {
        if (_disposed || IsFailed || _comparedTiles is null || _pending is null || !budget.Allows(Stopwatch.GetTimestamp())) return false;
        if (_proof is not null) return true;
        try
        {
            _multithread.Enter();
            try
            {
                if (!budget.Allows(Stopwatch.GetTimestamp())) return false;
                MappedSubresource mapped;
                try { mapped = _context.Map(_staging, 0, MapMode.Read, MapFlags.DoNotWait); }
                catch (SharpGenException ex) when (ex.ResultCode == Vortice.DXGI.ResultCode.WasStillDrawing) { return false; }
                try { Marshal.Copy(mapped.DataPointer, _flags, 0, _comparedTiles.Length); }
                finally { _context.Unmap(_staging, 0); }
            }
            finally { _multithread.Leave(); }
            if (_flags.Take(_comparedTiles.Length).Any(value => value is not (0 or 1)))
                throw new InvalidDataException("GPU comparison returned an invalid flag.");
            _proof = new(_pending.Sequence, _versions.Current!.Sequence,
                _comparedTiles.Where((_, index) => _flags[index] == 0).ToArray(),
                _comparedTiles.Where((_, index) => _flags[index] != 0).ToArray());
            CompletedComparisons++;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Fail(ex); return false; }
    }

    public NativeSurfaceManifest Complete(long sourceSequence, NativeDetailRenderBudget budget)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pending is null || sourceSequence != _pending.Sequence)
            throw new ArgumentException("Base output does not match the pending native source.", nameof(sourceSequence));
        TryPoll(budget); // Never wait for a late result; the base is ready NOW.
        // An already completed, exact proof stays true if CPU scheduling later
        // exhausts the render budget. Keeping it costs no further GPU work and
        // avoids restarting the stability interval on every scheduling hiccup.
        // The renderer/sender still independently obey their current budget.
        NativeSurfaceComparison? proof = _proof;
        if (_comparedTiles is not null && proof is null) UnreadyComparisons++;
        var manifest = _versions.Commit(_pending, proof, _requestedTiles);
        _reference?.Dispose(); _reference = _candidate; _candidate = null;
        _pending = null; _proof = null; _comparedTiles = null; _requestedTiles = [];
        return manifest;
    }

    private void Fail(Exception ex)
    { IsFailed = true; Failure = $"{ex.GetType().Name}: {ex.Message}"; _proof = null; _comparedTiles = null; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _pending = null; _proof = null; _comparedTiles = null; _requestedTiles = [];
        _reference?.Dispose(); _candidate?.Dispose(); _reference = null; _candidate = null;
        _resultView.Dispose(); _staging.Dispose(); _results.Dispose(); _constants.Dispose(); _shader.Dispose();
        _multithread.Dispose(); _context.Dispose(); _device.Dispose();
    }

    private const string Shader = """
        Texture2D<float4> previousImage : register(t0);
        Texture2D<float4> currentImage : register(t1);
        RWStructuredBuffer<uint> differences : register(u0);
        cbuffer Regions : register(b0) { int4 rectangles[64]; };
        groupshared uint different;
        [numthreads(16, 16, 1)]
        void cs(uint3 group : SV_GroupID, uint3 thread : SV_GroupThreadID) {
            if (thread.x == 0 && thread.y == 0) different = 0;
            GroupMemoryBarrierWithGroupSync();
            int4 rect = rectangles[group.x];
            uint localDifference = 0;
            for (uint y = thread.y; y < (uint)rect.w; y += 16)
                for (uint x = thread.x; x < (uint)rect.z; x += 16) {
                    int3 p = int3(rect.xy + int2(x, y), 0);
                    // BGRA UNORM Load preserves all 256 byte values exactly.
                    // Alpha is intentionally ignored: native readback is opaque RGB.
                    localDifference |= any(previousImage.Load(p).rgb != currentImage.Load(p).rgb) ? 1 : 0;
                }
            if (localDifference != 0) InterlockedOr(different, 1);
            GroupMemoryBarrierWithGroupSync();
            if (thread.x == 0 && thread.y == 0) differences[group.x] = different;
        }
        """;
}
