using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RemoteDesk;

/// <summary>
/// Opt-in spatial scaler. Convert NV12 to native-size RGB on the GPU, then
/// apply Catmull-Rom with a local range clamp or optional NIS. No CPU readback,
/// frame history, generated text, driver sharpening, or decoder changes.
/// The legacy video processor is independent and remains the fallback.
/// </summary>
internal sealed class D3D11ExperimentalUpscaler : IDisposable
{
    private static readonly Lazy<byte[]> VertexCode = new(() => Compile("vs", "vs_4_0"));
    private static readonly Lazy<byte[]> PixelCode = new(() => Compile("ps", "ps_4_0"));
    private static readonly Lazy<byte[]> CopyCode = new(() => Compile("psCopy", "ps_4_0"));
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private ID3D11VertexShader? _vertexShader;
    private ID3D11PixelShader? _pixelShader;
    private ID3D11PixelShader? _copyShader;
    private D3D11NisScaler? _nis;
    private bool _nisFailed;
    private readonly ExperimentalUpscalingAlgorithm _algorithm;
    internal string ActiveAlgorithm { get; private set; } = "双三次";
    private ID3D11Texture2D? _nativeRgb;
    private ID3D11ShaderResourceView? _sourceView;
    private ID3D11VideoProcessorOutputView? _rgbOutput;
    private ID3D11VideoProcessor? _processor;
    private ID3D11Multithread? _multithread;
    private Size _sourceSize;

    internal static bool ShouldApply(bool enabled, Size source, Size destination) =>
        enabled && source.Width > 0 && source.Height > 0 &&
        destination.Width > 0 && destination.Height > 0 &&
        (destination.Width > source.Width || destination.Height > source.Height);

    internal D3D11ExperimentalUpscaler(
        ID3D11Device device, ID3D11DeviceContext context,
        ID3D11VideoDevice videoDevice, ID3D11VideoProcessorEnumerator enumerator,
        Size sourceSize, ExperimentalUpscalingAlgorithm algorithm = ExperimentalUpscalingAlgorithm.CatmullRom)
    {
        _device = device;
        _context = context;
        _sourceSize = sourceSize;
        _algorithm = algorithm;
        try
        {
            _multithread = context.QueryInterface<ID3D11Multithread>();
            _vertexShader = device.CreateVertexShader(VertexCode.Value);
            _pixelShader = device.CreatePixelShader(PixelCode.Value);
            _nativeRgb = device.CreateTexture2D(new Texture2DDescription(
                Format.B8G8R8A8_UNorm, (uint)sourceSize.Width, (uint)sourceSize.Height,
                1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource));
            _sourceView = device.CreateShaderResourceView(_nativeRgb);
            _rgbOutput = videoDevice.CreateVideoProcessorOutputView(_nativeRgb, enumerator,
                new VideoProcessorOutputViewDescription
                {
                    ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                    Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 }
                });
            // A separate processor keeps all legacy filter settings untouched.
            _processor = videoDevice.CreateVideoProcessor(enumerator, 0);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void Render(ID3D11VideoContext videoContext,
        ID3D11VideoProcessorInputView input, ID3D11Texture2D target,
        D3D11HwndVideoPresentationGeometry geometry)
    {
        if (geometry.Source.Size != _sourceSize)
            throw new InvalidOperationException("Upscaler source geometry changed.");

        bool useNis = _algorithm == ExperimentalUpscalingAlgorithm.Nis &&
            D3D11NisScaler.Supports(_sourceSize, geometry.Destination.Size);
        // Shader compilation may be slow on first use. Never hold the shared
        // MF immediate-context lock while compiling or creating device objects.
        if (useNis && !_nisFailed && _nis is null)
        {
            try
            {
                _nis = new D3D11NisScaler(_device, _context);
                _copyShader ??= _device.CreatePixelShader(CopyCode.Value);
            }
            catch (Exception ex) { DisableNis(ex); }
        }

        // Decoder and presenter share this immediate context. Keep the entire
        // shader setup/draw/unbind sequence atomic, not merely each API call.
        _multithread!.Enter();
        try
        {
            D3D11HwndVideoPresenter.ConfigureVideoProcessor(videoContext, _processor!,
                new(geometry.Source, new Rectangle(Point.Empty, _sourceSize), _sourceSize));
            videoContext.VideoProcessorBlt(_processor!, _rgbOutput!, 0,
                [new VideoProcessorStream { Enable = true, InputSurface = input }]).CheckError();

            ID3D11ShaderResourceView source = _sourceView!;
            ID3D11PixelShader shader = _pixelShader!;
            ActiveAlgorithm = "双三次";
            if (_algorithm == ExperimentalUpscalingAlgorithm.Nis)
            {
                ActiveAlgorithm = _nisFailed ? "双三次（NIS 回退）" : "双三次（NIS 限 1～2 倍）";
                if (useNis && !_nisFailed)
                {
                    try
                    {
                        source = _nis!.Render(_sourceView!, _sourceSize, geometry.Destination.Size);
                        shader = _copyShader!;
                        ActiveAlgorithm = "NIS";
                    }
                    catch (Exception ex)
                    {
                        // Only one attempt per renderer generation. Continue the
                        // SAME frame through bicubic; don't rebuild the decoder.
                        DisableNis(ex);
                        ActiveAlgorithm = "双三次（NIS 回退）";
                    }
                }
            }

            using ID3D11RenderTargetView renderTarget = _device.CreateRenderTargetView(target);
            try
            {
                _context.ClearRenderTargetView(renderTarget, new Vortice.Mathematics.Color4(0, 0, 0, 1));
                _context.OMSetRenderTargets(renderTarget);
                _context.OMSetBlendState(null);
                _context.OMSetDepthStencilState(null);
                _context.RSSetState(null);
                _context.RSSetViewport(geometry.Destination.X, geometry.Destination.Y,
                    geometry.Destination.Width, geometry.Destination.Height);
                _context.IASetInputLayout(null);
                _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                _context.VSSetShader(_vertexShader);
                _context.GSSetShader(null);
                _context.PSSetShader(shader);
                _context.PSSetShaderResource(0, source);
                _context.Draw(3, 0);
            }
            finally
            {
                // Do not retain swap-chain or source bindings across Present/Resize.
                _context.PSSetShaderResource(0, null!);
                _context.OMSetRenderTargets((ID3D11RenderTargetView)null!);
                _context.VSSetShader(null);
                _context.PSSetShader(null);
            }
        }
        finally { _multithread.Leave(); }
    }

    private void DisableNis(Exception ex)
    {
        _nisFailed = true;
        _nis?.Dispose(); _nis = null;
        try { WindowsDiagnosticLog.CreateDefault().Append("VIEWER", "NIS 已回退到双三次：" + ex.Message); }
        catch { /* Diagnostics must not prevent same-frame fallback. */ }
    }

    public void Dispose()
    {
        _nis?.Dispose(); _nis = null;
        _copyShader?.Dispose(); _copyShader = null;
        _rgbOutput?.Dispose(); _rgbOutput = null;
        _sourceView?.Dispose(); _sourceView = null;
        _nativeRgb?.Dispose(); _nativeRgb = null;
        _processor?.Dispose(); _processor = null;
        _pixelShader?.Dispose(); _pixelShader = null;
        _vertexShader?.Dispose(); _vertexShader = null;
        _multithread?.Dispose(); _multithread = null;
    }

    // Kept self-contained so enabling an experiment never downloads a compiler
    // or changes application dependencies. The Windows system compiler is used.
    internal static byte[] Compile(string entry, string profile) => Compile(Shader, entry, profile);

    internal static byte[] Compile(string shader, string entry, string profile)
    {
        byte[] source = Encoding.UTF8.GetBytes(shader);
        int result = D3DCompile(source, (nuint)source.Length, "RemoteDesk.ExperimentalUpscale",
            0, 0, entry, profile, 1u << 15, 0, out ShaderBlob? code, out ShaderBlob? errors);
        try
        {
            if (result < 0 || code is null)
                throw new InvalidOperationException("GPU 放大着色器编译失败：" +
                    (errors is null ? $"0x{result:X8}" : Marshal.PtrToStringAnsi(errors.GetBufferPointer())));
            byte[] bytes = new byte[checked((int)code.GetBufferSize())];
            Marshal.Copy(code.GetBufferPointer(), bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            if (errors is not null) Marshal.ReleaseComObject(errors);
            if (code is not null) Marshal.ReleaseComObject(code);
        }
    }

    [ComImport, Guid("8BA5FB08-5195-40e2-AC58-0D989C3A0102"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ShaderBlob
    {
        [PreserveSig] nint GetBufferPointer();
        [PreserveSig] nuint GetBufferSize();
    }

    [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.Winapi)]
    private static extern int D3DCompile(byte[] source, nuint length,
        [MarshalAs(UnmanagedType.LPStr)] string sourceName, nint defines, nint include,
        [MarshalAs(UnmanagedType.LPStr)] string entry,
        [MarshalAs(UnmanagedType.LPStr)] string profile, uint flags, uint effectFlags,
        out ShaderBlob? code, out ShaderBlob? errors);

    internal const string Shader = """
        Texture2D<float4> source : register(t0);
        struct Vertex { float4 position : SV_Position; float2 uv : TEXCOORD0; };
        Vertex vs(uint id : SV_VertexID) {
            Vertex v;
            v.uv = float2((id << 1) & 2, id & 2);
            v.position = float4(v.uv * float2(2, -2) + float2(-1, 1), 0, 1);
            return v;
        }
        float4 weights(float t) {
            float t2 = t*t, t3 = t2*t;
            return float4(-0.5*t + t2 - 0.5*t3, 1 - 2.5*t2 + 1.5*t3,
                          0.5*t + 2*t2 - 1.5*t3, -0.5*t2 + 0.5*t3);
        }
        float4 psCopy(Vertex v) : SV_Target {
            uint width, height;
            source.GetDimensions(width, height);
            int2 p = clamp(int2(v.uv * float2(width, height)), 0, int2(width, height)-1);
            return float4(source.Load(int3(p,0)).rgb, 1);
        }
        float4 ps(Vertex v) : SV_Target {
            uint width, height;
            source.GetDimensions(width, height);
            int2 size = int2(width, height);
            float2 p = v.uv * size - 0.5;
            int2 base = int2(floor(p));
            float4 wx = weights(frac(p.x)), wy = weights(frac(p.y));
            float3 color = 0, lo = 1, hi = 0;
            [unroll] for (int y = 0; y < 4; ++y) {
                [unroll] for (int x = 0; x < 4; ++x) {
                    float3 tap = source.Load(int3(clamp(base + int2(x-1,y-1), 0, size-1),0)).rgb;
                    color += tap * wx[x] * wy[y];
                    if (x >= 1 && x <= 2 && y >= 1 && y <= 2) {
                        lo = min(lo, tap); hi = max(hi, tap);
                    }
                }
            }
            return float4(clamp(color, lo, hi), 1);
        }
        """;
}
