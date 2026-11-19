using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace RemoteDesk;

internal readonly record struct SdrVideoColorSpace(bool FullRange, VideoTransferMatrix Matrix)
{
    internal static readonly SdrVideoColorSpace NativeDesktop = new(true, VideoTransferMatrix.Bt709);
    internal static VideoProcessorColorSpace FullRgb => new() { Nominal_Range = (uint)VideoProcessorNominalRange.Range_0_255 };
    internal VideoProcessorColorSpace Yuv => new()
    {
        YCbCr_Matrix = Matrix == VideoTransferMatrix.Bt709 ? 1u : 0u,
        Nominal_Range = (uint)(FullRange ? VideoProcessorNominalRange.Range_0_255 : VideoProcessorNominalRange.Range_16_235)
    };
    internal ColorSpaceType DxgiYuv => (Matrix == VideoTransferMatrix.Bt709, FullRange) switch
    {
        (true, true) => ColorSpaceType.YcbcrFullG22LeftP709,
        (true, false) => ColorSpaceType.YcbcrStudioG22LeftP709,
        (false, true) => ColorSpaceType.YcbcrFullG22LeftP601,
        _ => ColorSpaceType.YcbcrStudioG22LeftP601
    };

    internal static SdrVideoColorSpace ReadDecoderOutput(IMFTransform transform)
    {
        try
        {
            using var media = transform.GetOutputCurrentType(0);
            var range = media.GetUInt32(MediaTypeAttributeKeys.VideoNominalRange, out uint rangeValue).Success
                ? (NominalRange)rangeValue : NominalRange.Unknown;
            var matrix = media.GetUInt32(MediaTypeAttributeKeys.YuvMatrix, out uint matrixValue).Success
                ? (VideoTransferMatrix)matrixValue : VideoTransferMatrix.Unknown;
            if (range is not (NominalRange.Unknown or NominalRange.Range0_255 or NominalRange.Range16_235)) return default;
            return new(range == NominalRange.Range0_255, matrix == VideoTransferMatrix.Unknown ? VideoTransferMatrix.Bt601 : matrix);
        }
        catch (SharpGenException)
        {
            // Some MFTs omit optional color information. Desktop H.264 without
            // an explicit full-range flag uses studio-range luma.
            return new(false, VideoTransferMatrix.Bt601);
        }
    }

    // Preserve the previous renderer's conversion for untagged Windows hosts.
    internal SdrVideoColorSpace? ForPresentation(bool useDriverVideoColors) =>
        !useDriverVideoColors && (Matrix is VideoTransferMatrix.Bt601 or VideoTransferMatrix.Bt709)
            ? this : null;
}

internal struct VideoProcessorColorState
{
    private SdrVideoColorSpace? _applied;
    private VideoProcessorColorSpace _originalInput, _originalOutput;
    private ColorSpaceType _originalDxgiInput, _originalDxgiOutput;
    private bool _originalSaved;

    internal void Apply(ID3D11VideoContext context, ID3D11VideoProcessor processor, SdrVideoColorSpace? requested)
    {
        if (_applied == requested) return;
        using var modern = context.QueryInterfaceOrNull<ID3D11VideoContext1>();
        if (modern is not null)
        {
            if (!_originalSaved)
            {
                _originalDxgiInput = modern.VideoProcessorGetStreamColorSpace1(processor, 0);
                _originalDxgiOutput = modern.VideoProcessorGetOutputColorSpace1(processor);
            }
            modern.VideoProcessorSetStreamColorSpace1(processor, 0, requested?.DxgiYuv ?? _originalDxgiInput);
            modern.VideoProcessorSetOutputColorSpace1(processor, requested.HasValue ? ColorSpaceType.RgbFullG22NoneP709 : _originalDxgiOutput);
        }
        else
        {
            if (!_originalSaved)
            {
                _originalInput = context.VideoProcessorGetStreamColorSpace(processor, 0);
                _originalOutput = context.VideoProcessorGetOutputColorSpace(processor);
            }
            context.VideoProcessorSetStreamColorSpace(processor, 0, requested?.Yuv ?? _originalInput);
            context.VideoProcessorSetOutputColorSpace(processor, requested.HasValue ? SdrVideoColorSpace.FullRgb : _originalOutput);
        }
        _originalSaved = true;
        _applied = requested;
    }
}
