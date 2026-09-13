using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RemoteDesk;

internal enum ExperimentalUpscalingAlgorithm { CatmullRom, Nis }

/// <summary>
/// NVIDIA's pinned MIT-licensed NIS 1.0.3, SDR/FP32 compute path. Borrowed
/// device/context; all resources are viewer-local. No frame history or readback.
/// Caller serializes the complete dispatch and presentation with the decoder.
/// </summary>
internal sealed class D3D11NisScaler : IDisposable
{
    internal const float DefaultSharpness = 0.2f;
    private static readonly Lazy<byte[]> Code = new(Compile);
    private static readonly Lazy<float[]> ScaleCoefficients = new(() => ReadCoefficients("coef_scale"));
    private static readonly Lazy<float[]> SharpenCoefficients = new(() => ReadCoefficients("coef_usm"));
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private ID3D11ComputeShader? _shader;
    private ID3D11Buffer? _constants;
    private ID3D11SamplerState? _sampler;
    private ID3D11ShaderResourceView? _scaleView, _sharpenView, _outputView;
    private ID3D11Texture2D? _scaleTexture, _sharpenTexture, _output;
    private ID3D11UnorderedAccessView? _outputUav;
    private Size _outputSize, _configuredSource;

    internal static bool Supports(Size source, Size destination) =>
        source.Width > 0 && source.Height > 0 &&
        destination.Width >= source.Width && destination.Height >= source.Height &&
        (destination.Width > source.Width || destination.Height > source.Height) &&
        destination.Width <= 2L * source.Width && destination.Height <= 2L * source.Height;

    internal D3D11NisScaler(ID3D11Device device, ID3D11DeviceContext context)
    {
        _device = device;
        _context = context;
        if (device.FeatureLevel < FeatureLevel.Level_11_0)
            throw new NotSupportedException("NIS 需要 Direct3D 11 feature level 11.0。");
        try
        {
            _shader = device.CreateComputeShader(Code.Value);
            _constants = device.CreateBuffer(112, BindFlags.ConstantBuffer);
            _sampler = device.CreateSamplerState(SamplerDescription.LinearClamp);
            _scaleTexture = CreateCoefficients(ScaleCoefficients.Value);
            _scaleView = device.CreateShaderResourceView(_scaleTexture);
            _sharpenTexture = CreateCoefficients(SharpenCoefficients.Value);
            _sharpenView = device.CreateShaderResourceView(_sharpenTexture);
        }
        catch { Dispose(); throw; }
    }

    internal static byte[] Compile()
    {
        string main = ReadResource("NIS_Main.hlsl");
        const string include = "#include \"NIS_Scaler.h\"";
        if (!main.Contains(include, StringComparison.Ordinal))
            throw new InvalidOperationException("NIS shader resource is incomplete.");
        string scaler = ReadResource("NIS_Scaler.h");
        // SDK 1.0.3 mixes signed block starts and unsigned pixel/origin
        // offsets before converting to float. At the top/left boundary a
        // negative start can wrap to UINT_MAX and sample the opposite edge.
        // Keep the vendored source unchanged; apply this audited adapter fix.
        foreach (string axis in new[] { "X", "Y" })
        {
            string coordinate = $"srcBlockStart{axis} + p{axis.ToLowerInvariant()} + kInputViewportOrigin{axis}";
            string signedCoordinate = $"NVF(srcBlockStart{axis}) + NVF(p{axis.ToLowerInvariant()}) + NVF(kInputViewportOrigin{axis})";
            if (!scaler.Contains(coordinate, StringComparison.Ordinal))
                throw new InvalidOperationException("NIS coordinate adapter no longer matches upstream.");
            scaler = scaler.Replace(coordinate, signedCoordinate, StringComparison.Ordinal);
        }
        string source = "#define NIS_BLOCK_WIDTH 32\n#define NIS_BLOCK_HEIGHT 24\n" +
            "#define NIS_THREAD_GROUP_SIZE 256\n#define NIS_VIEWPORT_SUPPORT 1\n" +
            "#define NIS_CLAMP_OUTPUT 1\n#define NIS_NV12_SUPPORT 0\n" +
            main.Replace(include, scaler, StringComparison.Ordinal);
        return D3D11ExperimentalUpscaler.Compile(source, "main", "cs_5_0");
    }

    private static string ReadResource(string name)
    {
        using var stream = typeof(D3D11NisScaler).Assembly.GetManifestResourceStream("RemoteDesk.NIS." + name)
            ?? throw new InvalidOperationException("Missing NIS resource: " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static float[] ReadCoefficients(string name)
    {
        if (name is not ("coef_scale" or "coef_usm")) throw new ArgumentOutOfRangeException(nameof(name));
        string config = ReadResource("NIS_Config.h");
        int start = config.IndexOf("float " + name + "[", StringComparison.Ordinal);
        if (start < 0) throw new InvalidOperationException("Missing NIS coefficients.");
        start = config.IndexOf('{', start);
        int end = config.IndexOf("};", start, StringComparison.Ordinal);
        var values = Regex.Matches(config[start..end], @"-?\d+\.\d+f")
            .Select(m => float.Parse(m.Value.AsSpan(0, m.Length - 1), CultureInfo.InvariantCulture)).ToArray();
        if (values.Length != 64 * 8) throw new InvalidOperationException("Invalid NIS coefficient count.");
        return values;
    }

    private ID3D11Texture2D CreateCoefficients(float[] values)
    {
        var pin = GCHandle.Alloc(values, GCHandleType.Pinned);
        try
        {
            return _device.CreateTexture2D(new Texture2DDescription(
                Format.R32G32B32A32_Float, 2, 64, 1, 1, BindFlags.ShaderResource),
                [new SubresourceData(pin.AddrOfPinnedObject(), 8 * sizeof(float))]);
        }
        finally { pin.Free(); }
    }

    // SDR NVScalerUpdateConfig port; uint fields occupy the same 32-bit slots
    // as in upstream's 28-field cbuffer (112 bytes, a multiple of 16).
    internal static float[] CreateConstants(Size source, Size destination, float sharpness = DefaultSharpness)
    {
        if (!Supports(source, destination) || !float.IsFinite(sharpness))
            throw new ArgumentOutOfRangeException(nameof(destination));
        float slider = Math.Clamp(sharpness, 0, 1) - 0.5f;
        float maxScale = slider >= 0 ? 1.25f : 1.75f;
        float minScale = slider >= 0 ? 1.25f : 1;
        float strengthMin = Math.Max(0, 0.4f + slider * minScale * 1.2f);
        float strengthMax = 1.6f + slider * maxScale * 1.8f;
        float limitMin = Math.Max(0.1f, 0.14f + slider * minScale * 0.32f);
        float limitMax = 0.5f + slider * minScale * 0.6f;
        static float U(int value) => BitConverter.Int32BitsToSingle(value);
        return [2 * 1127f / 1024, 64f / 1024, 2, 1f / 8,
            1, 1f / 255, 0.45f, 1f / (0.9f - 0.45f),
            strengthMin, strengthMax - strengthMin, limitMin, limitMax - limitMin,
            (float)source.Width / destination.Width, (float)source.Height / destination.Height,
            1f / destination.Width, 1f / destination.Height, 1f / source.Width, 1f / source.Height,
            0, 0, U(source.Width), U(source.Height),
            0, 0, U(destination.Width), U(destination.Height), 0, 0];
    }

    internal ID3D11ShaderResourceView Render(ID3D11ShaderResourceView input, Size source, Size destination)
    {
        if (!Supports(source, destination)) throw new ArgumentOutOfRangeException(nameof(destination));
        if (_output is null || _outputSize != destination)
        {
            ReleaseOutput();
            _output = _device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm,
                (uint)destination.Width, (uint)destination.Height, 1, 1,
                BindFlags.ShaderResource | BindFlags.UnorderedAccess));
            _outputUav = _device.CreateUnorderedAccessView(_output);
            _outputView = _device.CreateShaderResourceView(_output);
            _outputSize = destination;
            _configuredSource = Size.Empty;
        }
        if (_configuredSource != source)
        {
            _context.UpdateSubresource(CreateConstants(source, destination), _constants!);
            _configuredSource = source;
        }
        try
        {
            _context.CSSetShader(_shader);
            _context.CSSetConstantBuffer(0, _constants);
            _context.CSSetSampler(0, _sampler);
            _context.CSSetShaderResource(0, input);
            _context.CSSetShaderResource(1, _scaleView);
            _context.CSSetShaderResource(2, _sharpenView);
            _context.CSSetUnorderedAccessView(0, _outputUav);
            _context.Dispatch((uint)(destination.Width + 31) / 32, (uint)(destination.Height + 23) / 24, 1);
        }
        finally
        {
            _context.CSSetUnorderedAccessView(0, null);
            for (uint slot = 0; slot < 3; slot++) _context.CSSetShaderResource(slot, null);
            _context.CSSetSampler(0, null);
            _context.CSSetConstantBuffer(0, null);
            _context.CSSetShader(null);
        }
        return _outputView!;
    }

    private void ReleaseOutput()
    {
        _outputUav?.Dispose(); _outputUav = null;
        _outputView?.Dispose(); _outputView = null;
        _output?.Dispose(); _output = null;
    }

    public void Dispose()
    {
        ReleaseOutput();
        _sharpenView?.Dispose(); _sharpenView = null;
        _scaleView?.Dispose(); _scaleView = null;
        _sharpenTexture?.Dispose(); _sharpenTexture = null;
        _scaleTexture?.Dispose(); _scaleTexture = null;
        _sampler?.Dispose(); _sampler = null;
        _constants?.Dispose(); _constants = null;
        _shader?.Dispose(); _shader = null;
    }
}
