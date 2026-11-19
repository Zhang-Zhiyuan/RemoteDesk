using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RemoteDesk;

/// <summary>
/// Opt-in spatial scaler. Convert NV12 to native-size RGB on the GPU, then
/// apply pixel coverage, range-clamped Catmull-Rom, text enhancement, or NIS. No CPU readback,
/// frame history, generated text, driver sharpening, or decoder changes.
/// The legacy video processor is independent and remains the fallback.
/// </summary>
internal sealed class D3D11ExperimentalUpscaler : IDisposable
{
    private static readonly Lazy<byte[]> VertexCode = new(() => Compile("vs", "vs_4_0"));
    private static readonly Lazy<byte[]> PixelCode = new(() => Compile("ps", "ps_4_0"));
    private static readonly Lazy<byte[]> TextCode = new(() => Compile("psText", "ps_4_0"));
    private static readonly Lazy<byte[]> GridCode = new(() => Compile("psGrid", "ps_4_0"));
    private static readonly Lazy<byte[]> CopyCode = new(() => Compile("psCopy", "ps_4_0"));
    private static readonly Lazy<Task> BicubicPreparation = Prepare(PixelCode);
    private static readonly Lazy<Task> TextPreparation = Prepare(PixelCode, () => _ = TextCode.Value);
    private static readonly Lazy<Task> GridPreparation = Prepare(GridCode);
    private static readonly Lazy<Task> NisPreparation = Prepare(PixelCode, () =>
    {
        _ = CopyCode.Value;
        D3D11NisScaler.PrepareResources();
    });

    private static Lazy<Task> Prepare(Lazy<byte[]> pixel, Action? additional = null) => new(() => Task.Run(() =>
    {
        _ = VertexCode.Value;
        _ = pixel.Value;
        additional?.Invoke();
    }));

    internal static Task PrepareShadersAsync(ExperimentalUpscalingAlgorithm algorithm) => algorithm switch
    {
        ExperimentalUpscalingAlgorithm.CatmullRom => BicubicPreparation.Value,
        ExperimentalUpscalingAlgorithm.TextStrokes => TextPreparation.Value,
        ExperimentalUpscalingAlgorithm.Nis => NisPreparation.Value,
        ExperimentalUpscalingAlgorithm.PixelGrid => GridPreparation.Value,
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm))
    };
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private ID3D11VertexShader? _vertexShader;
    private ID3D11PixelShader? _pixelShader;
    private ID3D11PixelShader? _textShader;
    private ID3D11PixelShader? _copyShader;
    private D3D11NisScaler? _nis;
    private bool _nisFailed;
    private readonly ExperimentalUpscalingAlgorithm _algorithm;
    private VideoProcessorColorState _colorState;
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

    // At mild magnification, accenting already well-sampled strokes thickens
    // small print. Near 2x the loss of contrast from interpolation dominates.
    internal static bool ShouldEnhanceStrokes(Size source, Size destination) =>
        source.Width > 0 && source.Height > 0 &&
        (long)destination.Width * 4 >= (long)source.Width * 7 &&
        (long)destination.Height * 4 >= (long)source.Height * 7;

    internal D3D11ExperimentalUpscaler(
        ID3D11Device device, ID3D11DeviceContext context,
        ID3D11VideoDevice videoDevice, ID3D11VideoProcessorEnumerator enumerator,
        Size sourceSize, ExperimentalUpscalingAlgorithm algorithm, Exception? nisPreparationFailure)
    {
        _device = device;
        _context = context;
        _sourceSize = sourceSize;
        _algorithm = algorithm;
        try
        {
            _multithread = context.QueryInterface<ID3D11Multithread>();
            _vertexShader = device.CreateVertexShader(VertexCode.Value);
            _pixelShader = device.CreatePixelShader(algorithm == ExperimentalUpscalingAlgorithm.PixelGrid ? GridCode.Value : PixelCode.Value);
            if (algorithm == ExperimentalUpscalingAlgorithm.TextStrokes)
                _textShader = device.CreatePixelShader(TextCode.Value);
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
            if (nisPreparationFailure is not null) DisableNis(nisPreparationFailure);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void Render(ID3D11VideoContext videoContext,
        ID3D11VideoProcessorInputView input, ID3D11Texture2D target,
        D3D11HwndVideoPresentationGeometry geometry, SdrVideoColorSpace? colorSpace)
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
            _colorState.Apply(videoContext, _processor!, colorSpace);
            D3D11HwndVideoPresenter.ConfigureVideoProcessor(videoContext, _processor!,
                new(geometry.Source, new Rectangle(Point.Empty, _sourceSize), _sourceSize));
            videoContext.VideoProcessorBlt(_processor!, _rgbOutput!, 0,
                [new VideoProcessorStream { Enable = true, InputSurface = input }]).CheckError();

            ID3D11ShaderResourceView source = _sourceView!;
            ID3D11PixelShader shader = _pixelShader!;
            ActiveAlgorithm = _algorithm == ExperimentalUpscalingAlgorithm.PixelGrid ? "原像素" : "双三次";
            if (_textShader is not null && ShouldEnhanceStrokes(_sourceSize, geometry.Destination.Size))
            {
                shader = _textShader;
                ActiveAlgorithm = "文字笔画";
            }
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
        _textShader?.Dispose(); _textShader = null;
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
        // Average the source texels covered by this output pixel. At integer
        // enlargement a pixel lies inside one texel, even next to a thin stroke.
        float4 psGrid(Vertex v) : SV_Target {
            uint width, height;
            source.GetDimensions(width, height);
            int2 last = int2(width, height) - 1;
            float2 p = v.uv * float2(width, height) - 0.5;
            int2 base = int2(floor(p));
            float2 f = saturate((frac(p) - 0.5) / max(fwidth(p), 0.00001) + 0.5);
            float3 a = source.Load(int3(clamp(base, 0, last), 0)).rgb;
            float3 b = source.Load(int3(clamp(base + int2(1,0), 0, last), 0)).rgb;
            float3 c = source.Load(int3(clamp(base + int2(0,1), 0, last), 0)).rgb;
            float3 d = source.Load(int3(clamp(base + 1, 0, last), 0)).rgb;
            return float4(lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y), 1);
        }
        float4 reconstruct(Vertex v, bool text) {
            uint width, height;
            source.GetDimensions(width, height);
            int2 size = int2(width, height);
            float2 p = v.uv * size - 0.5;
            int2 base = int2(floor(p));
            float4 wx = weights(frac(p.x)), wy = weights(frac(p.y));
            float3 color = 0, lo = 1, hi = 0;
            float3 surrounding = 0;
            float outerLo = 1, outerHi = 0;
            [unroll] for (int y = 0; y < 4; ++y) {
                [unroll] for (int x = 0; x < 4; ++x) {
                    float3 tap = source.Load(int3(clamp(base + int2(x-1,y-1), 0, size-1),0)).rgb;
                    color += tap * wx[x] * wy[y];
                    if (text) {
                        float luma = dot(tap, float3(0.2126, 0.7152, 0.0722));
                        surrounding += tap;
                        outerLo = min(outerLo, luma); outerHi = max(outerHi, luma);
                    }
                    if (x >= 1 && x <= 2 && y >= 1 && y <= 2) {
                        lo = min(lo, tap); hi = max(hi, tap);
                    }
                }
            }
            color = clamp(color, lo, hi);
            if (text) {
                float3 lumaWeights = float3(0.2126, 0.7152, 0.0722);
                float luma = dot(color, lumaWeights);
                float background = dot(surrounding * (1.0 / 16.0), lumaWeights);
                float contrast = outerHi - outerLo;
                // The mean tends towards the background of thin light OR dark
                // strokes. Only strengthen contrasting ink, not flat noise,
                // coloured edges or a broad response beside a single step.
                float ink = luma - background;
                float side = ink >= 0 ? outerHi - background : background - outerLo;
                float weight = smoothstep(0.09, 0.28, contrast) *
                    smoothstep(0.50, 0.72, side / max(contrast, 0.001));
                float chroma = max(color.r, max(color.g, color.b)) - min(color.r, min(color.g, color.b));
                weight *= 1 - smoothstep(0.08, 0.22, chroma);
                float delta = clamp(ink * (0.65 * weight), -0.12, 0.12);
                // A luminance-only correction, bounded by evidence in the
                // source footprint and by the RGB gamut. Do not invent contours.
                float lower = min(0, max(outerLo - luma, -min(color.r, min(color.g, color.b))));
                float upper = max(0, min(outerHi - luma, 1 - max(color.r, max(color.g, color.b))));
                color += clamp(delta, lower, upper);
            }
            return float4(color, 1);
        }
        float4 ps(Vertex v) : SV_Target { return reconstruct(v, false); }
        float4 psText(Vertex v) : SV_Target { return reconstruct(v, true); }
        """;
}
