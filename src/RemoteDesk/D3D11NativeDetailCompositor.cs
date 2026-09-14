using System.Diagnostics;
using System.Drawing;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RemoteDesk;

// Sparse native RGB overlay: one 4 MiB atlas, not a full-size CPU/GPU image.
// Prepare independently of presentation; render/dispose under the owner's lock.
// Each draw sees only current immutable tiles, never an outdated recycled slot.
// Uploads are paced, not a reason to hold back a base frame. No readback or flush.
internal sealed class D3D11NativeDetailCompositor : IDisposable
{
    internal const int AtlasBytes = 128 * 128 * 4 * NativeDetailPresentation.MaximumTiles;
    internal const int MapEdge = 64;
    internal const int MaximumUploadsPerFrame = 2;
    private static readonly Lazy<byte[]> VertexCode = new(() => D3D11ExperimentalUpscaler.Compile(Shader, "vs", "vs_4_0"));
    private static readonly Lazy<byte[]> PixelCode = new(() => D3D11ExperimentalUpscaler.Compile(Shader, "ps", "ps_4_0"));
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private ID3D11Multithread? _multithread;
    private ID3D11Texture2D? _atlas, _map;
    private ID3D11ShaderResourceView? _atlasView, _mapView;
    private ID3D11VertexShader? _vertex;
    private ID3D11PixelShader? _pixel;
    private ID3D11Buffer? _constants;
    private readonly Dictionary<Point, (NativeDetailTile Tile, int Slot)> _resident = new(NativeDetailPresentation.MaximumTiles);
    private readonly Dictionary<Point, NativeDetailTile> _requested = new(NativeDetailPresentation.MaximumTiles);
    private readonly List<Point> _removed = new(NativeDetailPresentation.MaximumTiles);
    private readonly bool[] _occupied = new bool[NativeDetailPresentation.MaximumTiles];
    private readonly uint[] _mapData = new uint[MapEdge * MapEdge];
    private readonly byte[] _padded = new byte[128 * 128 * 4];
    private bool _mapDirty, _disposed;
    private long _epoch, _request;
    private Size _nativeSize;
    internal int UploadedTilesLastRender { get; private set; }
    internal int ResidentTiles => _resident.Count;
    internal void Clear() { _resident.Clear(); _mapDirty = true; }

    internal D3D11NativeDetailCompositor(ID3D11Device device, ID3D11DeviceContext context)
    {
        // Own independent leases: asynchronous preparation may outlive the
        // presenter generation which requested it.
        _device = device.QueryInterface<ID3D11Device>();
        _context = null!;
        // Shader creation is outside the shared immediate-context lock.
        try
        {
            _context = context.QueryInterface<ID3D11DeviceContext>();
            _vertex = _device.CreateVertexShader(VertexCode.Value);
            _pixel = _device.CreatePixelShader(PixelCode.Value);
            _constants = _device.CreateBuffer(64, BindFlags.ConstantBuffer);
            _atlas = _device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm,
                128, 128, NativeDetailPresentation.MaximumTiles, 1, BindFlags.ShaderResource));
            _map = _device.CreateTexture2D(new Texture2DDescription(Format.R32_UInt,
                MapEdge, MapEdge, 1, 1, BindFlags.ShaderResource));
            _atlasView = _device.CreateShaderResourceView(_atlas);
            _mapView = _device.CreateShaderResourceView(_map);
            _multithread = _context.QueryInterface<ID3D11Multithread>();
        }
        catch { Dispose(); throw; }
    }

    // Native coordinates map to the original decoded image, then use the SAME
    // crop and destination as the base. Window/DPI coordinates never enter here.
    internal static float[] Constants(NativeDetailPresentation frame, Rectangle baseImage,
        D3D11HwndVideoPresentationGeometry geometry)
    {
        if (baseImage.Width <= 0 || baseImage.Height <= 0 || !baseImage.Contains(geometry.Source) ||
            geometry.Source.Width <= 0 || geometry.Source.Height <= 0 || geometry.Destination.Width <= 0 || geometry.Destination.Height <= 0)
            throw new ArgumentException("Invalid native detail render geometry.");
        double sx = frame.NativeSize.Width / (double)baseImage.Width, sy = frame.NativeSize.Height / (double)baseImage.Height;
        return [
            (float)((geometry.Source.X - baseImage.X) * sx), (float)((geometry.Source.Y - baseImage.Y) * sy),
            (float)(geometry.Source.Width * sx / geometry.Destination.Width), (float)(geometry.Source.Height * sy / geometry.Destination.Height),
            geometry.Destination.X, geometry.Destination.Y, frame.NativeSize.Width, frame.NativeSize.Height,
            frame.Viewport.Left, frame.Viewport.Top, frame.Viewport.Right, frame.Viewport.Bottom,
            0, 0, 0, 0];
    }

    internal bool Render(NativeDetailPresentation frame, Rectangle baseImage,
        D3D11HwndVideoPresentationGeometry geometry, ID3D11RenderTargetView renderTarget, NativeDetailRenderBudget budget)
    {
        float[] constants = Constants(frame, baseImage, geometry);
        UploadedTilesLastRender = 0;
        // A >2x native reduction needs a larger reconstruction filter and is
        // deliberately left to the normal base renderer, not aliased point data.
        if (frame.Tiles.IsEmpty || constants[2] > 2 || constants[3] > 2) { Clear(); return false; }
        if (!budget.Allows(Stopwatch.GetTimestamp())) return false;
        _multithread!.Enter();
        try
        {
            if (!budget.Allows(Stopwatch.GetTimestamp())) return false;
            if (_epoch != frame.Epoch || _request != frame.Request || _nativeSize != frame.NativeSize)
            { Clear(); _epoch = frame.Epoch; _request = frame.Request; _nativeSize = frame.NativeSize; }
            _requested.Clear(); _removed.Clear(); Array.Clear(_occupied);
            foreach (var tile in frame.Tiles) _requested.Add(tile.Bounds.Location, tile);
            // Invalidate ALL changed/absent tiles before spending any upload
            // budget. Even the tiles deferred to later frames must not show
            // their old text. A dirty map is never drawn until rewritten.
            foreach (var item in _resident)
                if (!_requested.TryGetValue(item.Key, out var tile) || !ReferenceEquals(tile, item.Value.Tile)) _removed.Add(item.Key);
            foreach (var position in _removed) { _resident.Remove(position); _mapDirty = true; }
            foreach (var item in _resident.Values) _occupied[item.Slot] = true;
            foreach (var tile in frame.Tiles)
            {
                if (_resident.ContainsKey(tile.Bounds.Location)) continue;
                if (UploadedTilesLastRender >= MaximumUploadsPerFrame || !budget.Allows(Stopwatch.GetTimestamp())) break;
                int slot = Array.IndexOf(_occupied, false);
                if (slot < 0) throw new InvalidOperationException("Native atlas capacity exceeded.");
                _occupied[slot] = true;
                // A bounded tile staging array also pads odd native edge tiles;
                // the shader clamps in native coordinates, never into padding.
                Array.Clear(_padded);
                for (int y = 0; y < tile.Bounds.Height; y++)
                    tile.Rgba.Span.Slice(y * tile.Bounds.Width * 4, tile.Bounds.Width * 4).CopyTo(_padded.AsSpan(y * 128 * 4));
                _context.UpdateSubresource(_padded, _atlas!, (uint)slot, 128 * 4, (uint)_padded.Length);
                _resident[tile.Bounds.Location] = (tile, slot); UploadedTilesLastRender++; _mapDirty = true;
            }
            if (_resident.Count == 0 || !budget.Allows(Stopwatch.GetTimestamp())) return false;
            if (_mapDirty)
            {
                Array.Clear(_mapData);
                foreach (var item in _resident)
                    _mapData[item.Key.Y / 128 * MapEdge + item.Key.X / 128] = (uint)item.Value.Slot + 1;
                _context.UpdateSubresource(_mapData, _map!, 0, MapEdge * 4, (uint)(_mapData.Length * 4));
                _mapDirty = false;
            }
            if (!budget.Allows(Stopwatch.GetTimestamp())) return false;
            _context.UpdateSubresource(constants, _constants!);
            try
            {
                _context.OMSetRenderTargets(renderTarget);
                _context.OMSetBlendState(null);
                _context.OMSetDepthStencilState(null);
                _context.RSSetState(null);
                _context.RSSetViewport(geometry.Destination.X, geometry.Destination.Y, geometry.Destination.Width, geometry.Destination.Height);
                _context.IASetInputLayout(null);
                _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                _context.VSSetShader(_vertex);
                _context.GSSetShader(null);
                _context.PSSetShader(_pixel);
                _context.PSSetShaderResource(0, _atlasView!);
                _context.PSSetShaderResource(1, _mapView!);
                _context.PSSetConstantBuffer(0, _constants);
                _context.Draw(3, 0);
            }
            finally
            {
                _context.PSSetShaderResource(0, null!);
                _context.PSSetShaderResource(1, null!);
                _context.PSSetConstantBuffer(0, null);
                _context.OMSetRenderTargets((ID3D11RenderTargetView)null!);
                _context.VSSetShader(null);
                _context.PSSetShader(null);
            }
            return true;
        }
        finally { _multithread.Leave(); }
    }

    internal static bool CanRender(NativeDetailPresentation frame, Rectangle baseImage,
        D3D11HwndVideoPresentationGeometry geometry)
    {
        if (frame.Tiles.IsEmpty) return false;
        float[] constants = Constants(frame, baseImage, geometry);
        return constants[2] <= 2 && constants[3] <= 2;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _resident.Clear();
        _requested.Clear(); _removed.Clear();
        _atlasView?.Dispose(); _atlasView = null;
        _mapView?.Dispose(); _mapView = null;
        _atlas?.Dispose(); _atlas = null;
        _map?.Dispose(); _map = null;
        _constants?.Dispose(); _constants = null;
        _pixel?.Dispose(); _pixel = null;
        _vertex?.Dispose(); _vertex = null;
        _multithread?.Dispose(); _multithread = null;
        _context?.Dispose();
        _device.Dispose();
    }

    internal const string Shader = """
        Texture2DArray<float4> atlas : register(t0);
        Texture2D<uint> tileMap : register(t1);
        cbuffer Mapping : register(b0) {
            float4 nativeMapping; // native origin.xy, native pixels per output pixel.zw
            float4 outputMapping; // destination origin.xy, native dimensions.zw
            float4 visibleNative; // left, top, right, bottom
            float4 reserved;
        };
        float4 vs(uint id : SV_VertexID) : SV_Position {
            float2 uv = float2((id << 1) & 2, id & 2);
            return float4(uv * float2(2,-2) + float2(-1,1), 0, 1);
        }
        void weights(float center, float footprint, out int start, out float4 value) {
            if (footprint <= 1.0) {
                float p = center - 0.5;
                start = (int)floor(p);
                float f = frac(p);
                value = float4(1-f, f, 0, 0);
            } else {
                float lo = center - footprint * 0.5, hi = center + footprint * 0.5;
                start = (int)floor(lo);
                float4 p = start + float4(0,1,2,3);
                value = max(0.0, min(p+1.0, hi) - max(p, lo)) / footprint;
            }
        }
        float3 nativePixel(int2 p) {
            p = clamp(p, int2(0,0), int2(outputMapping.zw)-1);
            uint slot = tileMap.Load(int3(p / 128, 0));
            // Missing neighbours must not sample recycled/uninitialised atlas
            // data. Leave this output pixel's already-rendered base unchanged.
            if (slot == 0) discard;
            return atlas.Load(int4(p % 128, slot-1, 0)).rgb;
        }
        float4 ps(float4 position : SV_Position) : SV_Target {
            float2 center = nativeMapping.xy + (position.xy-outputMapping.xy) * nativeMapping.zw;
            if (any(center < visibleNative.xy) || any(center >= visibleNative.zw)) discard;
            int x0, y0;
            float4 wx, wy;
            weights(center.x, nativeMapping.z, x0, wx);
            weights(center.y, nativeMapping.w, y0, wy);
            float3 rgb = 0;
            [unroll] for (int y = 0; y < 4; y++) {
                [unroll] for (int x = 0; x < 4; x++) {
                    float w = wx[x] * wy[y];
                    if (w > 0) rgb += nativePixel(int2(x0+x,y0+y)) * w;
                }
            }
            return float4(rgb, 1);
        }
        """;
}
