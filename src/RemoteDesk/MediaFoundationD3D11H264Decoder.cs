using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace RemoteDesk;

internal enum MediaFoundationD3D11CapabilityStatus
{
    Available,
    InvalidOptions,
    UnsupportedOperatingSystem,
    MediaFoundationUnavailable,
    D3D11Unavailable,
    HardwareVideoUnavailable,
    DxgiDeviceManagerUnavailable,
    DecoderUnavailable,
    DecoderNotD3D11Aware,
    LowLatencyModeUnavailable,
    InputTypeUnsupported,
    Nv12OutputUnavailable,
    InitializationFailed
}

internal sealed record MediaFoundationD3D11Capability(
    MediaFoundationD3D11CapabilityStatus Status,
    string Detail,
    FeatureLevel? DeviceFeatureLevel = null,
    uint DecoderProfileCount = 0)
{
    public bool IsAvailable =>
        Status == MediaFoundationD3D11CapabilityStatus.Available;
}

internal enum MediaFoundationD3D11DecoderState
{
    Created,
    Streaming,
    Draining,
    DeviceLost,
    Faulted,
    Disposed
}

internal enum MediaFoundationD3D11ReferenceState
{
    NeedsIndependentFrame,
    Ready
}

internal enum MediaFoundationD3D11DecodeStatus
{
    FrameReady,
    AcceptedAwaitingOutput,
    Failed
}

internal readonly record struct MediaFoundationD3D11DecodeResult(
    MediaFoundationD3D11DecodeStatus Status,
    MediaFoundationD3D11DecodedFrame? Frame,
    string? Detail);

internal sealed record MediaFoundationD3D11H264DecoderOptions(
    int Width,
    int Height,
    int FramesPerSecond,
    int MaxAccessUnitBytes = 8 * 1024 * 1024,
    bool AllSamplesIndependent = false)
{
    internal const int MinimumDimension = 48;
    internal const int MaximumWidth = 4096;
    internal const int MaximumHeight = 2304;
    internal const int MaximumFramesPerSecond = 120;
    internal const int MaximumAccessUnitBytes = 64 * 1024 * 1024;

    internal string? Validate()
    {
        if (Width < MinimumDimension ||
            Width > MaximumWidth ||
            Height < MinimumDimension ||
            Height > MaximumHeight)
        {
            return $"The inbox H.264 decoder supports widths from " +
                $"{MinimumDimension} to {MaximumWidth} and heights from " +
                $"{MinimumDimension} to {MaximumHeight} pixels.";
        }

        if ((Width & 1) != 0 || (Height & 1) != 0)
        {
            return "NV12 dimensions must be even.";
        }

        if (FramesPerSecond is < 1 or > MaximumFramesPerSecond)
        {
            return $"Frame rate must be between 1 and " +
                $"{MaximumFramesPerSecond} FPS.";
        }

        if (MaxAccessUnitBytes is < 1 or > MaximumAccessUnitBytes)
        {
            return $"The access-unit limit must be between 1 and " +
                $"{MaximumAccessUnitBytes} bytes.";
        }

        return null;
    }
}

internal readonly record struct AnnexBH264AccessUnitInfo(
    bool IsWithinSizeLimit,
    bool HasStartCode,
    bool HasSequenceParameterSet,
    bool HasPictureParameterSet,
    bool HasIdrSlice,
    bool HasNonIdrSlice,
    bool HasInvalidNalHeader,
    int NalUnitCount)
{
    public bool HasVclSlice =>
        HasIdrSlice || HasNonIdrSlice;

    public bool IsValidAccessUnit =>
        IsWithinSizeLimit &&
        HasStartCode &&
        HasVclSlice &&
        !HasInvalidNalHeader;

    public bool IsIndependentFrame =>
        IsValidAccessUnit &&
        HasSequenceParameterSet &&
        HasPictureParameterSet &&
        HasIdrSlice;

    public string GetFailureDetail(
        bool requireIndependentFrame)
    {
        if (!IsWithinSizeLimit)
        {
            return "The H.264 access unit exceeds the configured size limit.";
        }

        if (!HasStartCode)
        {
            return "The H.264 sample is not Annex-B.";
        }

        if (HasInvalidNalHeader)
        {
            return "The H.264 sample contains an invalid NAL header.";
        }

        if (requireIndependentFrame &&
            !HasSequenceParameterSet)
        {
            return "The independent H.264 sample is missing an SPS NAL.";
        }

        if (requireIndependentFrame &&
            !HasPictureParameterSet)
        {
            return "The independent H.264 sample is missing a PPS NAL.";
        }

        if (requireIndependentFrame && !HasIdrSlice)
        {
            return "The independent H.264 sample is missing an IDR slice.";
        }

        if (!HasVclSlice)
        {
            return "The H.264 access unit contains no VCL slice.";
        }

        return string.Empty;
    }
}

/// <summary>
/// Keeps the process-wide Media Foundation startup reference alive until
/// both the decoder owner and every outstanding output frame have released
/// their leases.
/// </summary>
internal sealed class MediaFoundationRuntimeLifetime
{
    private readonly object _sync = new();
    private readonly Action _shutdownAction;
    private int _frameLeaseCount;
    private bool _ownerReleased;
    private bool _shutdown;

    public MediaFoundationRuntimeLifetime()
        : this(TryShutdownMediaFoundation)
    {
    }

    internal MediaFoundationRuntimeLifetime(
        Action shutdownAction)
    {
        _shutdownAction =
            shutdownAction ??
            throw new ArgumentNullException(
                nameof(shutdownAction));
    }

    public IDisposable AcquireFrameLease()
    {
        lock (_sync)
        {
            if (_ownerReleased || _shutdown)
            {
                throw new ObjectDisposedException(
                    nameof(MediaFoundationRuntimeLifetime));
            }

            _frameLeaseCount++;
            return new FrameLease(this);
        }
    }

    public void ReleaseOwner()
    {
        bool shutdown;
        lock (_sync)
        {
            if (_ownerReleased)
            {
                return;
            }

            _ownerReleased = true;
            shutdown = TryMarkShutdownLocked();
        }

        if (shutdown)
        {
            _shutdownAction();
        }
    }

    internal int OutstandingFrameLeaseCount
    {
        get
        {
            lock (_sync)
            {
                return _frameLeaseCount;
            }
        }
    }

    private void ReleaseFrameLease()
    {
        bool shutdown;
        lock (_sync)
        {
            if (_frameLeaseCount <= 0)
            {
                return;
            }

            _frameLeaseCount--;
            shutdown = TryMarkShutdownLocked();
        }

        if (shutdown)
        {
            _shutdownAction();
        }
    }

    private bool TryMarkShutdownLocked()
    {
        if (_shutdown ||
            !_ownerReleased ||
            _frameLeaseCount != 0)
        {
            return false;
        }

        _shutdown = true;
        return true;
    }

    private static void TryShutdownMediaFoundation()
    {
        try
        {
            MediaFactory.MFShutdown();
        }
        catch
        {
            // Runtime lifetime release is no-throw by contract.
        }
    }

    private sealed class FrameLease : IDisposable
    {
        private MediaFoundationRuntimeLifetime? _owner;

        public FrameLease(
            MediaFoundationRuntimeLifetime owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            Interlocked.Exchange(
                ref _owner,
                null)?.ReleaseFrameLease();
        }
    }
}

/// <summary>
/// Owns a decoder output sample and its NV12 D3D11 texture. Keeping the
/// sample alive prevents the Media Foundation decoder from recycling the
/// texture subresource while it is being presented.
/// </summary>
internal sealed class MediaFoundationD3D11DecodedFrame : IDisposable
{
    private readonly object _sync = new();
    private IMFSample? _sample;
    private ID3D11Device? _device;
    private ID3D11Texture2D? _texture;
    private IDisposable? _mediaFoundationLease;

    internal MediaFoundationD3D11DecodedFrame(
        IMFSample sample,
        ID3D11Device device,
        ID3D11Texture2D texture,
        IDisposable mediaFoundationLease,
        uint subresourceIndex,
        int visibleWidth,
        int visibleHeight,
        long sampleTime100Nanoseconds,
        bool hasExplicitSampleTime)
    {
        _sample = sample;
        _device = device;
        _texture = texture;
        _mediaFoundationLease = mediaFoundationLease;
        SubresourceIndex = subresourceIndex;
        VisibleWidth = visibleWidth;
        VisibleHeight = visibleHeight;
        SampleTime100Nanoseconds = sampleTime100Nanoseconds;
        HasExplicitSampleTime = hasExplicitSampleTime;

        Texture2DDescription description = texture.Description;
        TextureWidth = checked((int)description.Width);
        TextureHeight = checked((int)description.Height);
        Format = description.Format;
    }

    public uint SubresourceIndex { get; }

    public int VisibleWidth { get; }

    public int VisibleHeight { get; }

    public int TextureWidth { get; }

    public int TextureHeight { get; }

    public Format Format { get; }

    public long SampleTime100Nanoseconds { get; }

    public bool HasExplicitSampleTime { get; }

    internal ID3D11Device AcquireDeviceLease()
    {
        lock (_sync)
        {
            ID3D11Device device =
                _device ??
                throw new ObjectDisposedException(
                    nameof(MediaFoundationD3D11DecodedFrame));
            return device.QueryInterface<ID3D11Device>();
        }
    }

    internal ID3D11Texture2D AcquireTextureLease()
    {
        lock (_sync)
        {
            ID3D11Texture2D texture =
                _texture ??
                throw new ObjectDisposedException(
                    nameof(MediaFoundationD3D11DecodedFrame));
            return texture.QueryInterface<ID3D11Texture2D>();
        }
    }

    public void Dispose()
    {
        DisposeCore();
        GC.SuppressFinalize(this);
    }

    ~MediaFoundationD3D11DecodedFrame()
    {
        DisposeCore();
    }

    private void DisposeCore()
    {
        ID3D11Texture2D? texture;
        ID3D11Device? device;
        IMFSample? sample;
        IDisposable? mediaFoundationLease;
        lock (_sync)
        {
            texture = _texture;
            _texture = null;
            device = _device;
            _device = null;
            sample = _sample;
            _sample = null;
            mediaFoundationLease =
                _mediaFoundationLease;
            _mediaFoundationLease = null;
        }

        TryDispose(texture);
        TryDispose(device);
        TryDispose(sample);
        TryDispose(mediaFoundationLease);
    }

    private static void TryDispose(IDisposable? disposable)
    {
        try
        {
            disposable?.Dispose();
        }
        catch
        {
            // Frame cleanup must remain idempotent and no-throw.
        }
    }
}

/// <summary>
/// An isolated, default-disabled Windows hardware decode capability. It
/// accepts one complete Annex-B access unit per sample, requires an
/// SPS/PPS/IDR recovery point after startup or a reported gap, and then
/// accepts dependent VCL frames. It emits an NV12 D3D11 texture and has no
/// dependency on WinForms or the existing FFmpeg/Bitmap decode path.
/// </summary>
internal sealed class MediaFoundationD3D11H264Decoder : IDisposable
{
    internal static bool IsPlatformPotentiallySupported =>
        OperatingSystem.IsWindowsVersionAtLeast(8);

    private const int DxgiErrorDeviceRemoved =
        unchecked((int)0x887A0005);
    private const int DxgiErrorDeviceHung =
        unchecked((int)0x887A0006);
    private const int DxgiErrorDeviceReset =
        unchecked((int)0x887A0007);
    private const int DxgiErrorDriverInternalError =
        unchecked((int)0x887A0020);
    private const int MaximumAcceptedInputsWithoutOutput = 3;

    private static readonly Guid InboxH264DecoderClsid = new(
        "62CE7E72-4C71-4D20-B15D-452831A87D9D");
    private static readonly Guid H264VldNoFilmGrainDecoderProfile = new(
        "1B81BE68-A0C7-11D3-B984-00C04F2E73C5");
    private static readonly Guid CodecApiLowLatencyMode = new(
        "9C27891A-ED7A-40E1-88E8-B22727A024EE");

    private readonly object _sync = new();
    private readonly MediaFoundationD3D11H264DecoderOptions _options;
    private readonly ID3D11Device _device;
    private readonly IMFDXGIDeviceManager _deviceManager;
    private readonly IMFTransform _transform;
    private readonly MediaFoundationRuntimeLifetime
        _mediaFoundationLifetime;
    private MediaFoundationD3D11DecoderState _state;
    private MediaFoundationD3D11ReferenceState _referenceState;
    private int _acceptedInputsWithoutOutput;
    private bool _markNextInputDiscontinuity;

    private MediaFoundationD3D11H264Decoder(
        MediaFoundationD3D11H264DecoderOptions options,
        ID3D11Device device,
        IMFDXGIDeviceManager deviceManager,
        IMFTransform transform)
    {
        _options = options;
        _device = device;
        _deviceManager = deviceManager;
        _transform = transform;
        _mediaFoundationLifetime =
            new MediaFoundationRuntimeLifetime();
        _state = MediaFoundationD3D11DecoderState.Streaming;
        _referenceState =
            MediaFoundationD3D11ReferenceState
                .NeedsIndependentFrame;
    }

    public MediaFoundationD3D11ReferenceState ReferenceState
    {
        get
        {
            lock (_sync)
            {
                return _referenceState;
            }
        }
    }

    public MediaFoundationD3D11DecoderState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    internal ID3D11Device AcquireDeviceLease()
    {
        lock (_sync)
        {
            if (_state ==
                MediaFoundationD3D11DecoderState.Disposed ||
                _state ==
                MediaFoundationD3D11DecoderState.DeviceLost)
            {
                throw new ObjectDisposedException(
                    nameof(MediaFoundationD3D11H264Decoder));
            }

            return _device.QueryInterface<ID3D11Device>();
        }
    }

    internal int OutstandingFrameLeaseCount =>
        _mediaFoundationLifetime
            .OutstandingFrameLeaseCount;

    internal static bool TryCreate(
        MediaFoundationD3D11H264DecoderOptions? options,
        [NotNullWhen(true)]
        out MediaFoundationD3D11H264Decoder? decoder,
        out MediaFoundationD3D11Capability capability)
    {
        decoder = null;

        if (options is null)
        {
            capability = new(
                MediaFoundationD3D11CapabilityStatus.InvalidOptions,
                "Decoder options are required.");
            return false;
        }

        string? validationFailure = options.Validate();
        if (validationFailure is not null)
        {
            capability = new(
                MediaFoundationD3D11CapabilityStatus.InvalidOptions,
                validationFailure);
            return false;
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(8))
        {
            capability = new(
                MediaFoundationD3D11CapabilityStatus
                    .UnsupportedOperatingSystem,
                "Media Foundation D3D11 decoding requires Windows 8 or later.");
            return false;
        }

        bool mediaFoundationStarted = false;
        ID3D11Device? device = null;
        IMFDXGIDeviceManager? deviceManager = null;
        IMFTransform? transform = null;
        FeatureLevel? featureLevel = null;
        uint decoderProfileCount = 0;
        MediaFoundationD3D11CapabilityStatus failureStatus =
            MediaFoundationD3D11CapabilityStatus
                .MediaFoundationUnavailable;

        try
        {
            MediaFactory.MFStartup().CheckError();
            mediaFoundationStarted = true;

            failureStatus =
                MediaFoundationD3D11CapabilityStatus.D3D11Unavailable;
            device = D3D11.D3D11CreateDevice(
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport |
                    DeviceCreationFlags.VideoSupport,
                [
                    FeatureLevel.Level_11_1,
                    FeatureLevel.Level_11_0,
                    FeatureLevel.Level_10_1,
                    FeatureLevel.Level_10_0
                ]);
            featureLevel = device.FeatureLevel;
            EnableD3D11MultithreadProtection(device);

            failureStatus =
                MediaFoundationD3D11CapabilityStatus
                    .HardwareVideoUnavailable;
            using (ID3D11VideoDevice videoDevice =
                device.QueryInterface<ID3D11VideoDevice>())
            {
                decoderProfileCount =
                    videoDevice.VideoDecoderProfileCount;
                if (!SupportsH264Nv12HardwareDecoding(
                        videoDevice,
                        decoderProfileCount))
                {
                    throw new CapabilityUnavailableException(
                        MediaFoundationD3D11CapabilityStatus
                            .HardwareVideoUnavailable,
                        "The D3D11 device exposes no H.264 VLD hardware " +
                        "decoder profile with NV12 output.");
                }
            }

            failureStatus =
                MediaFoundationD3D11CapabilityStatus
                    .DxgiDeviceManagerUnavailable;
            deviceManager = MediaFactory.MFCreateDXGIDeviceManager();
            deviceManager.ResetDevice(device).CheckError();

            failureStatus =
                MediaFoundationD3D11CapabilityStatus.DecoderUnavailable;
            transform = ActivateInboxH264Decoder();
            if (transform is null)
            {
                throw new CapabilityUnavailableException(
                    MediaFoundationD3D11CapabilityStatus.DecoderUnavailable,
                    "The inbox Media Foundation H.264 decoder was not found.");
            }

            using (IMFAttributes attributes = transform.Attributes)
            {
                attributes.GetUInt32(
                    TransformAttributeKeys.D3D11Aware,
                    out uint d3d11Aware).CheckError();
                if (d3d11Aware == 0)
                {
                    throw new CapabilityUnavailableException(
                        MediaFoundationD3D11CapabilityStatus
                            .DecoderNotD3D11Aware,
                        "The inbox H.264 decoder is not D3D11-aware.");
                }

                failureStatus =
                    MediaFoundationD3D11CapabilityStatus
                        .LowLatencyModeUnavailable;
                attributes.Set(
                    SinkWriterAttributeKeys.LowLatency,
                    1u).CheckError();
            }

            EnableCodecLowLatencyMode(transform);

            failureStatus =
                MediaFoundationD3D11CapabilityStatus
                    .DxgiDeviceManagerUnavailable;
            transform.ProcessMessage(
                TMessageType.MessageSetD3DManager,
                new UIntPtr(
                    unchecked(
                        (ulong)deviceManager.NativePointer.ToInt64())));

            failureStatus =
                MediaFoundationD3D11CapabilityStatus
                    .InputTypeUnsupported;
            using (IMFMediaType inputType =
                CreateInputType(options))
            {
                transform.SetInputType(0, inputType, 0);
            }

            failureStatus =
                MediaFoundationD3D11CapabilityStatus
                    .Nv12OutputUnavailable;
            SetNv12OutputType(
                transform,
                options.Width,
                options.Height);
            EnsureDecoderProvidesOutputSamples(transform);

            failureStatus =
                MediaFoundationD3D11CapabilityStatus
                    .InitializationFailed;
            transform.ProcessMessage(
                TMessageType.MessageNotifyBeginStreaming,
                UIntPtr.Zero);
            transform.ProcessMessage(
                TMessageType.MessageNotifyStartOfStream,
                UIntPtr.Zero);

            decoder = new(
                options,
                device,
                deviceManager,
                transform);
            device = null;
            deviceManager = null;
            transform = null;
            mediaFoundationStarted = false;

            capability = new(
                MediaFoundationD3D11CapabilityStatus.Available,
                "Inbox H.264 MFT is ready for low-latency NV12 D3D11 " +
                    "output on an H.264 VLD hardware decoder profile.",
                featureLevel,
                decoderProfileCount);
            return true;
        }
        catch (CapabilityUnavailableException ex)
        {
            capability = new(
                ex.Status,
                ex.Message,
                featureLevel,
                decoderProfileCount);
            return false;
        }
        catch (Exception ex) when (IsRecoverableCapabilityFailure(ex))
        {
            capability = new(
                failureStatus,
                FormatFailure(ex),
                featureLevel,
                decoderProfileCount);
            return false;
        }
        finally
        {
            if (transform is not null)
            {
                TryDispose(transform);
            }

            if (deviceManager is not null)
            {
                TryDispose(deviceManager);
            }

            if (device is not null)
            {
                TryDispose(device);
            }

            if (mediaFoundationStarted)
            {
                TryShutdownMediaFoundation();
            }
        }
    }

    private static bool SupportsH264Nv12HardwareDecoding(
        ID3D11VideoDevice videoDevice,
        uint decoderProfileCount)
    {
        for (uint index = 0; index < decoderProfileCount; index++)
        {
            videoDevice.GetVideoDecoderProfile(
                index,
                out Guid profile).CheckError();
            if (!IsH264HardwareDecoderProfile(profile))
            {
                continue;
            }

            videoDevice.CheckVideoDecoderFormat(
                profile,
                Format.NV12,
                out RawBool supported).CheckError();
            if (supported)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsH264HardwareDecoderProfile(
        Guid profile) =>
        profile == H264VldNoFilmGrainDecoderProfile;

    internal bool TryDecodeIndependentAccessUnit(
        ReadOnlyMemory<byte> annexBAccessUnit,
        long sampleTime100Nanoseconds,
        [NotNullWhen(true)]
        out MediaFoundationD3D11DecodedFrame? frame,
        [NotNullWhen(false)]
        out string? failureDetail)
        => TryDecodeAccessUnitCore(
            annexBAccessUnit,
            sampleTime100Nanoseconds,
            requireIndependentFrame: true,
            out frame,
            out failureDetail,
            out _);

    internal bool TryDecodeAccessUnit(
        ReadOnlyMemory<byte> annexBAccessUnit,
        long sampleTime100Nanoseconds,
        [NotNullWhen(true)]
        out MediaFoundationD3D11DecodedFrame? frame,
        [NotNullWhen(false)]
        out string? failureDetail)
        => TryDecodeAccessUnitCore(
            annexBAccessUnit,
            sampleTime100Nanoseconds,
            requireIndependentFrame: false,
            out frame,
            out failureDetail,
            out _);

    internal MediaFoundationD3D11DecodeResult DecodeAccessUnit(
        ReadOnlyMemory<byte> annexBAccessUnit,
        long sampleTime100Nanoseconds)
    {
        bool decoded = TryDecodeAccessUnitCore(
            annexBAccessUnit,
            sampleTime100Nanoseconds,
            requireIndependentFrame: false,
            out MediaFoundationD3D11DecodedFrame? frame,
            out string? detail,
            out bool acceptedAwaitingOutput);
        return new(
            decoded
                ? MediaFoundationD3D11DecodeStatus.FrameReady
                : acceptedAwaitingOutput
                    ? MediaFoundationD3D11DecodeStatus
                        .AcceptedAwaitingOutput
                    : MediaFoundationD3D11DecodeStatus.Failed,
            frame,
            detail);
    }

    internal bool ResetForDiscontinuity(
        [NotNullWhen(false)]
        out string? failureDetail)
    {
        lock (_sync)
        {
            if (!CanAcceptInput(_state))
            {
                failureDetail =
                    $"The decoder cannot reset while {_state}.";
                return false;
            }

            return TryEnterRecoveryStateLocked(
                out failureDetail);
        }
    }

    internal bool NotifyAccessUnitGap(
        [NotNullWhen(false)]
        out string? failureDetail) =>
        ResetForDiscontinuity(out failureDetail);

    internal static AnnexBH264AccessUnitInfo InspectAccessUnit(
        ReadOnlySpan<byte> accessUnit,
        int maxAccessUnitBytes)
    {
        bool withinLimit =
            maxAccessUnitBytes > 0 &&
            accessUnit.Length <= maxAccessUnitBytes;
        bool hasStartCode = false;
        bool hasSps = false;
        bool hasPps = false;
        bool hasIdr = false;
        bool hasNonIdr = false;
        bool hasInvalidNalHeader = false;
        int nalUnitCount = 0;

        int index = 0;
        while (index <= accessUnit.Length - 4)
        {
            int startCodeLength =
                GetAnnexBStartCodeLength(accessUnit, index);
            if (startCodeLength == 0)
            {
                index++;
                continue;
            }

            int headerIndex = index + startCodeLength;
            if (headerIndex >= accessUnit.Length)
            {
                break;
            }

            hasStartCode = true;
            nalUnitCount++;
            byte header = accessUnit[headerIndex];
            hasInvalidNalHeader |= (header & 0x80) != 0;
            switch (header & 0x1F)
            {
                case 1:
                    hasNonIdr = true;
                    break;
                case 5:
                    hasIdr = true;
                    break;
                case 7:
                    hasSps = true;
                    break;
                case 8:
                    hasPps = true;
                    break;
            }

            index = headerIndex + 1;
        }

        return new(
            withinLimit,
            hasStartCode,
            hasSps,
            hasPps,
            hasIdr,
            hasNonIdr,
            hasInvalidNalHeader,
            nalUnitCount);
    }

    internal static bool CanDecodeAccessUnit(
        MediaFoundationD3D11ReferenceState referenceState,
        AnnexBH264AccessUnitInfo accessUnitInfo) =>
        accessUnitInfo.IsValidAccessUnit &&
        (referenceState ==
            MediaFoundationD3D11ReferenceState.Ready ||
            accessUnitInfo.IsIndependentFrame);

    internal static bool ShouldMarkDiscontinuity(
        MediaFoundationD3D11ReferenceState referenceState,
        AnnexBH264AccessUnitInfo accessUnitInfo) =>
        referenceState ==
            MediaFoundationD3D11ReferenceState
                .NeedsIndependentFrame &&
        accessUnitInfo.IsIndependentFrame;

    internal static bool ShouldMarkInputDiscontinuity(
        MediaFoundationD3D11ReferenceState referenceState,
        AnnexBH264AccessUnitInfo accessUnitInfo,
        bool restartingAfterDrain) =>
        restartingAfterDrain ||
        ShouldMarkDiscontinuity(
            referenceState,
            accessUnitInfo);

    internal static bool ShouldEnterRecoveryAfterRejectedAccessUnit(
        MediaFoundationD3D11ReferenceState referenceState,
        AnnexBH264AccessUnitInfo accessUnitInfo) =>
        referenceState ==
            MediaFoundationD3D11ReferenceState.Ready &&
        accessUnitInfo.HasVclSlice;

    internal static bool CanWaitForDelayedOutput(
        MediaFoundationD3D11ReferenceState referenceStateBeforeInput,
        AnnexBH264AccessUnitInfo accessUnitInfo,
        int acceptedInputsWithoutOutput = 0,
        bool drainCompleted = false) =>
        !drainCompleted &&
        acceptedInputsWithoutOutput >= 0 &&
        acceptedInputsWithoutOutput <
            MaximumAcceptedInputsWithoutOutput &&
        // NEED_MORE_INPUT is a normal MFT response for any accepted access
        // unit, not just the first recovery frame. Treating the first bounded
        // dependent-frame delay as corruption flushes valid references and
        // can create a recurring recovery loop. Keep the wait strictly
        // bounded, but allow every access unit that was legal for the current
        // reference state.
        CanDecodeAccessUnit(
            referenceStateBeforeInput,
            accessUnitInfo);

    internal static bool CanAcceptInput(
        MediaFoundationD3D11DecoderState state) =>
        state == MediaFoundationD3D11DecoderState.Streaming;

    internal static bool IsDeviceLostHResult(int hresult) =>
        hresult is
            DxgiErrorDeviceRemoved or
            DxgiErrorDeviceHung or
            DxgiErrorDeviceReset or
            DxgiErrorDriverInternalError;

    internal static bool IsValidStateTransition(
        MediaFoundationD3D11DecoderState from,
        MediaFoundationD3D11DecoderState to) =>
        (from, to) switch
        {
            (MediaFoundationD3D11DecoderState.Created,
                MediaFoundationD3D11DecoderState.Streaming
                    or MediaFoundationD3D11DecoderState.Faulted
                    or MediaFoundationD3D11DecoderState.Disposed) => true,
            (MediaFoundationD3D11DecoderState.Streaming,
                MediaFoundationD3D11DecoderState.Draining
                    or MediaFoundationD3D11DecoderState.DeviceLost
                    or MediaFoundationD3D11DecoderState.Faulted
                    or MediaFoundationD3D11DecoderState.Disposed) => true,
            (MediaFoundationD3D11DecoderState.Draining,
                MediaFoundationD3D11DecoderState.Streaming
                    or MediaFoundationD3D11DecoderState.DeviceLost
                    or MediaFoundationD3D11DecoderState.Faulted
                    or MediaFoundationD3D11DecoderState.Disposed) => true,
            (MediaFoundationD3D11DecoderState.DeviceLost
                    or MediaFoundationD3D11DecoderState.Faulted,
                MediaFoundationD3D11DecoderState.Disposed) => true,
            _ => false
        };

    private bool TryDecodeAccessUnitCore(
        ReadOnlyMemory<byte> annexBAccessUnit,
        long sampleTime100Nanoseconds,
        bool requireIndependentFrame,
        [NotNullWhen(true)]
        out MediaFoundationD3D11DecodedFrame? frame,
        [NotNullWhen(false)]
        out string? failureDetail,
        out bool acceptedAwaitingOutput)
    {
        frame = null;
        acceptedAwaitingOutput = false;
        AnnexBH264AccessUnitInfo accessUnitInfo =
            InspectAccessUnit(
                annexBAccessUnit.Span,
                _options.MaxAccessUnitBytes);

        lock (_sync)
        {
            if (!CanAcceptInput(_state))
            {
                failureDetail =
                    $"The decoder cannot accept input while {_state}.";
                return false;
            }

            if (!accessUnitInfo.IsValidAccessUnit)
            {
                string rejectionDetail =
                    accessUnitInfo.GetFailureDetail(
                        requireIndependentFrame: false);
                if (ShouldEnterRecoveryAfterRejectedAccessUnit(
                    _referenceState,
                    accessUnitInfo))
                {
                    if (!TryEnterRecoveryStateLocked(
                        out string? recoveryFailure))
                    {
                        failureDetail =
                            $"{rejectionDetail} Recovery reset failed: " +
                            $"{recoveryFailure}";
                        return false;
                    }

                    rejectionDetail +=
                        " The decoder now requires a fresh " +
                        "SPS/PPS/IDR recovery access unit.";
                }

                failureDetail = rejectionDetail;
                return false;
            }

            if (requireIndependentFrame &&
                !accessUnitInfo.IsIndependentFrame)
            {
                failureDetail =
                    accessUnitInfo.GetFailureDetail(
                        requireIndependentFrame: true);
                return false;
            }

            if (!CanDecodeAccessUnit(
                _referenceState,
                accessUnitInfo))
            {
                failureDetail =
                    "The decoder requires a fresh SPS/PPS/IDR recovery " +
                    "access unit before dependent video can resume.";
                return false;
            }

            try
            {
                MediaFoundationD3D11ReferenceState
                    referenceStateBeforeInput =
                        _referenceState;
                bool markDiscontinuity =
                    ShouldMarkInputDiscontinuity(
                        _referenceState,
                        accessUnitInfo,
                        _markNextInputDiscontinuity);
                using IMFSample inputSample = CreateInputSample(
                    annexBAccessUnit,
                    sampleTime100Nanoseconds,
                    10_000_000L / _options.FramesPerSecond,
                    isCleanPoint: accessUnitInfo.HasIdrSlice,
                    isDiscontinuity: markDiscontinuity);
                _transform.ProcessInput(0, inputSample, 0);
                _markNextInputDiscontinuity = false;

                IMFSample? newestDecodedSample = null;
                try
                {
                    int outputSampleCount = 0;
                    int streamChangeCount = 0;
                    PumpAvailableOutputLocked(
                        ref newestDecodedSample,
                        ref outputSampleCount,
                        ref streamChangeCount);
                    bool drainedDelayedIndependentOutput = false;
                    if (ShouldDrainDelayedIndependentOutput(
                            _options.AllSamplesIndependent,
                            accessUnitInfo,
                            newestDecodedSample is not null))
                    {
                        DrainDelayedIndependentOutputLocked(
                            ref newestDecodedSample,
                            ref outputSampleCount,
                            ref streamChangeCount);
                        drainedDelayedIndependentOutput = true;
                    }

                    if (newestDecodedSample is null)
                    {
                        bool canWait =
                            CanWaitForDelayedOutput(
                                referenceStateBeforeInput,
                                accessUnitInfo,
                                _acceptedInputsWithoutOutput,
                                drainedDelayedIndependentOutput);
                        _acceptedInputsWithoutOutput++;
                        if (canWait)
                        {
                            _referenceState =
                                MediaFoundationD3D11ReferenceState
                                    .Ready;
                            acceptedAwaitingOutput = true;
                            failureDetail =
                                "The H.264 decoder accepted the " +
                                "complete access unit and is awaiting " +
                                "its bounded delayed output.";
                            return false;
                        }

                        if (TryEnterRecoveryStateLocked(
                            out string? recoveryFailure))
                        {
                            failureDetail =
                                "The H.264 decoder produced no frame " +
                                "for a complete access unit and now " +
                                "requires a fresh SPS/PPS/IDR recovery " +
                                "access unit.";
                        }
                        else
                        {
                            failureDetail =
                                "The H.264 decoder produced no frame, " +
                                $"and recovery reset failed: " +
                                $"{recoveryFailure}";
                        }

                        return false;
                    }

                    frame = CreateDecodedFrame(
                        newestDecodedSample,
                        sampleTime100Nanoseconds);
                    newestDecodedSample = null;
                    _referenceState =
                        MediaFoundationD3D11ReferenceState.Ready;
                    _acceptedInputsWithoutOutput = 0;
                    failureDetail = null;
                    return true;
                }
                finally
                {
                    newestDecodedSample?.Dispose();
                }
            }
            catch (Exception ex) when (IsRecoverableCapabilityFailure(ex))
            {
                TransitionTo(
                    ClassifyFailureState(ex));
                _referenceState =
                    MediaFoundationD3D11ReferenceState
                        .NeedsIndependentFrame;
                _acceptedInputsWithoutOutput = 0;
                _markNextInputDiscontinuity = false;
                failureDetail = FormatFailure(ex);
                return false;
            }
        }
    }

    internal static bool ShouldDrainDelayedIndependentOutput(
        bool allSamplesIndependent,
        AnnexBH264AccessUnitInfo accessUnitInfo,
        bool outputAlreadyAvailable) =>
        allSamplesIndependent &&
        accessUnitInfo.IsIndependentFrame &&
        !outputAlreadyAvailable;

    private void DrainDelayedIndependentOutputLocked(
        ref IMFSample? newestDecodedSample,
        ref int outputSampleCount,
        ref int streamChangeCount)
    {
        // Affected inbox H.264 MFT/driver pairs can accept a complete 4K
        // GOP1 sample yet retain it until the next ProcessInput call, despite
        // low-latency mode. COMMAND_DRAIN is the documented way to request
        // every complete stored output without feeding a future frame.
        // Restrict this fallback to the negotiated all-independent stream:
        // draining a dependent GOP could discard an incomplete reference
        // chain. After a drain, MF requires the next input to be marked as a
        // discontinuity before the stream resumes.
        TransitionTo(
            MediaFoundationD3D11DecoderState.Draining);
        _transform.ProcessMessage(
            TMessageType.MessageCommandDrain,
            UIntPtr.Zero);
        PumpAvailableOutputLocked(
            ref newestDecodedSample,
            ref outputSampleCount,
            ref streamChangeCount);
        _transform.ProcessMessage(
            TMessageType.MessageNotifyStartOfStream,
            UIntPtr.Zero);
        _markNextInputDiscontinuity = true;
        TransitionTo(
            MediaFoundationD3D11DecoderState.Streaming);
    }

    private void PumpAvailableOutputLocked(
        ref IMFSample? newestDecodedSample,
        ref int outputSampleCount,
        ref int streamChangeCount)
    {
        while (true)
        {
            var output = new OutputDataBuffer
            {
                StreamID = 0
            };
            try
            {
                Result result =
                    _transform.ProcessOutput(
                        ProcessOutputFlags.None,
                        1,
                        ref output,
                        out _);
                if (result ==
                    Vortice.MediaFoundation
                        .ResultCode
                        .TransformStreamChange)
                {
                    if (++streamChangeCount > 4)
                    {
                        throw new InvalidOperationException(
                            "The H.264 decoder repeatedly " +
                            "changed its output type.");
                    }

                    output.Sample?.Dispose();
                    output.Sample = null;
                    SetNv12OutputType(
                        _transform,
                        _options.Width,
                        _options.Height);
                    EnsureDecoderProvidesOutputSamples(
                        _transform);
                    continue;
                }

                if (result ==
                    Vortice.MediaFoundation
                        .ResultCode
                        .TransformNeedMoreInput)
                {
                    return;
                }

                result.CheckError();
                if (output.Sample is null)
                {
                    throw new InvalidOperationException(
                        "The H.264 decoder returned no " +
                        "output sample.");
                }

                if (++outputSampleCount > 8)
                {
                    throw new InvalidOperationException(
                        "The H.264 decoder produced more than " +
                        "eight output samples for one input.");
                }

                // Some inbox MFT/driver combinations release the
                // primed frame and the current frame together.
                // Keep only the newest GPU sample so startup never
                // creates a stale presentation queue.
                newestDecodedSample?.Dispose();
                newestDecodedSample =
                    output.Sample;
                output.Sample = null;
            }
            finally
            {
                output.Sample?.Dispose();
                output.Events?.Dispose();
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_state == MediaFoundationD3D11DecoderState.Disposed)
            {
                return;
            }

            TryProcessMessage(
                TMessageType.MessageCommandFlush);
            TryProcessMessage(
                TMessageType.MessageNotifyEndOfStream);
            TryProcessMessage(
                TMessageType.MessageNotifyEndStreaming);
            TryProcessMessage(
                TMessageType.MessageNotifyReleaseResources);

            TryDispose(_transform);
            TryDispose(_deviceManager);
            TryDispose(_device);
            _mediaFoundationLifetime.ReleaseOwner();

            _referenceState =
                MediaFoundationD3D11ReferenceState
                    .NeedsIndependentFrame;
            _acceptedInputsWithoutOutput = 0;
            _markNextInputDiscontinuity = false;
            TransitionTo(
                MediaFoundationD3D11DecoderState.Disposed);
        }
    }

    private static IMFTransform? ActivateInboxH264Decoder()
    {
        var inputInfo = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            // MFVideoFormat_H264 means one complete access unit per sample.
            // H264_ES permits fragmented pictures and adds a frame of
            // buffering for this GOP=1 transport.
            GuidSubtype = VideoFormatGuids.H264
        };
        var outputInfo = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = VideoFormatGuids.NV12
        };

        using IMFActivateCollection activations =
            MediaFactory.MFTEnumEx(
                TransformCategoryGuids.VideoDecoder,
                (uint)(
                    EnumFlag.EnumFlagAll |
                    EnumFlag.EnumFlagSortandfilter),
                inputInfo,
                outputInfo);
        foreach (IMFActivate activation in activations)
        {
            try
            {
                Guid transformClsid = activation.GetGUID(
                    TransformAttributeKeys
                        .MftTransformClsidAttribute);
                if (transformClsid == InboxH264DecoderClsid)
                {
                    return activation
                        .ActivateObject<IMFTransform>();
                }
            }
            finally
            {
                activation.Dispose();
            }
        }

        return null;
    }

    private static IMFMediaType CreateInputType(
        MediaFoundationD3D11H264DecoderOptions options)
    {
        IMFMediaType mediaType = MediaFactory.MFCreateMediaType();
        try
        {
            mediaType.Set(
                MediaTypeAttributeKeys.MajorType,
                MediaTypeGuids.Video).CheckError();
            mediaType.Set(
                MediaTypeAttributeKeys.Subtype,
                VideoFormatGuids.H264).CheckError();
            mediaType.Set(
                MediaTypeAttributeKeys.FrameSize,
                PackRatio(
                    (uint)options.Width,
                    (uint)options.Height)).CheckError();
            mediaType.Set(
                MediaTypeAttributeKeys.FrameRate,
                PackRatio(
                    (uint)options.FramesPerSecond,
                    1)).CheckError();
            mediaType.Set(
                MediaTypeAttributeKeys.PixelAspectRatio,
                PackRatio(1, 1)).CheckError();
            mediaType.Set(
                MediaTypeAttributeKeys.InterlaceMode,
                (uint)VideoInterlaceMode.Progressive).CheckError();
            mediaType.Set(
                MediaTypeAttributeKeys.AllSamplesIndependent,
                GetAllSamplesIndependentAttributeValue(
                    options)).CheckError();
            return mediaType;
        }
        catch
        {
            mediaType.Dispose();
            throw;
        }
    }

    internal static uint GetAllSamplesIndependentAttributeValue(
        MediaFoundationD3D11H264DecoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.AllSamplesIndependent
            ? 1u
            : 0u;
    }

    private static void SetNv12OutputType(
        IMFTransform transform,
        int expectedVisibleWidth,
        int expectedVisibleHeight)
    {
        IMFMediaType? nv12Type = null;
        string? incompatibleSizeDetail = null;
        for (int index = 0; index < 256; index++)
        {
            IMFMediaType candidate;
            try
            {
                candidate =
                    transform.GetOutputAvailableType(0, index);
            }
            catch (SharpGenException ex)
                when (ex.ResultCode ==
                    Vortice.MediaFoundation.ResultCode.NoMoreTypes)
            {
                break;
            }

            try
            {
                if (candidate.GetGUID(
                    MediaTypeAttributeKeys.Subtype) ==
                    VideoFormatGuids.NV12)
                {
                    MediaFactory.MFGetAttributeSize(
                        candidate,
                        MediaTypeAttributeKeys.FrameSize,
                        out uint codedWidth,
                        out uint codedHeight).CheckError();
                    if (IsNegotiatedSurfaceCompatible(
                        expectedVisibleWidth,
                        expectedVisibleHeight,
                        codedWidth,
                        codedHeight))
                    {
                        nv12Type = candidate;
                        break;
                    }

                    incompatibleSizeDetail =
                        $"The decoder negotiated an NV12 surface of " +
                        $"{codedWidth}x{codedHeight} for a declared visible " +
                        $"frame of {expectedVisibleWidth}x" +
                        $"{expectedVisibleHeight}.";
                }
            }
            finally
            {
                if (!ReferenceEquals(nv12Type, candidate))
                {
                    candidate.Dispose();
                }
            }
        }

        if (nv12Type is null)
        {
            throw new CapabilityUnavailableException(
                MediaFoundationD3D11CapabilityStatus
                    .Nv12OutputUnavailable,
                incompatibleSizeDetail ??
                    "The inbox H.264 decoder offers no compatible NV12 " +
                    "output type.");
        }

        using (nv12Type)
        {
            transform.SetOutputType(0, nv12Type, 0);
        }
    }

    internal static bool IsNegotiatedSurfaceCompatible(
        int expectedVisibleWidth,
        int expectedVisibleHeight,
        uint codedWidth,
        uint codedHeight)
    {
        if (expectedVisibleWidth <= 0 ||
            expectedVisibleHeight <= 0)
        {
            return false;
        }

        int maximumCodedWidth =
            AlignToH264Macroblock(expectedVisibleWidth);
        int maximumCodedHeight =
            AlignToH264Macroblock(expectedVisibleHeight);
        return codedWidth >= expectedVisibleWidth &&
            codedHeight >= expectedVisibleHeight &&
            codedWidth <= maximumCodedWidth &&
            codedHeight <= maximumCodedHeight;
    }

    private static void EnsureDecoderProvidesOutputSamples(
        IMFTransform transform)
    {
        OutputStreamInfo streamInfo =
            transform.GetOutputStreamInfo(0);
        if ((((OutputStreamInfoFlags)streamInfo.Flags) &
            OutputStreamInfoFlags.OutputStreamProvidesSamples) == 0)
        {
            throw new CapabilityUnavailableException(
                MediaFoundationD3D11CapabilityStatus
                    .Nv12OutputUnavailable,
                "The H.264 decoder does not provide D3D11 output samples.");
        }
    }

    private static IMFSample CreateInputSample(
        ReadOnlyMemory<byte> annexBAccessUnit,
        long sampleTime100Nanoseconds,
        long sampleDuration100Nanoseconds,
        bool isCleanPoint,
        bool isDiscontinuity)
    {
        IMFMediaBuffer mediaBuffer =
            MediaFactory.MFCreateMemoryBuffer(
                annexBAccessUnit.Length);
        try
        {
            mediaBuffer.Lock(
                out nint destination,
                out _,
                out _);
            try
            {
                byte[] sourceArray;
                int sourceOffset;
                if (MemoryMarshal.TryGetArray(
                        annexBAccessUnit,
                        out ArraySegment<byte> source) &&
                    source.Array is not null)
                {
                    sourceArray = source.Array;
                    sourceOffset = source.Offset;
                }
                else
                {
                    sourceArray = annexBAccessUnit.ToArray();
                    sourceOffset = 0;
                }

                Marshal.Copy(
                    sourceArray,
                    sourceOffset,
                    destination,
                    annexBAccessUnit.Length);
            }
            finally
            {
                mediaBuffer.Unlock();
            }

            mediaBuffer.CurrentLength =
                annexBAccessUnit.Length;
            IMFSample sample = MediaFactory.MFCreateSample();
            try
            {
                sample.AddBuffer(mediaBuffer);
                sample.SampleTime =
                    sampleTime100Nanoseconds;
                sample.SampleDuration =
                    sampleDuration100Nanoseconds;
                if (isCleanPoint)
                {
                    sample.Set(
                        SampleAttributeKeys.CleanPoint,
                        1u).CheckError();
                }

                if (isDiscontinuity)
                {
                    sample.Set(
                        SampleAttributeKeys.Discontinuity,
                        1u).CheckError();
                }

                return sample;
            }
            catch
            {
                sample.Dispose();
                throw;
            }
        }
        finally
        {
            mediaBuffer.Dispose();
        }
    }

    private MediaFoundationD3D11DecodedFrame CreateDecodedFrame(
        IMFSample decodedSample,
        long fallbackSampleTime100Nanoseconds)
    {
        ID3D11Texture2D? texture = null;
        ID3D11Device? textureDevice = null;
        IDisposable? mediaFoundationLease = null;
        try
        {
            using IMFMediaBuffer decodedBuffer =
                decodedSample.GetBufferByIndex(0);
            using IMFDXGIBuffer dxgiBuffer =
                decodedBuffer.QueryInterface<IMFDXGIBuffer>();
            nint texturePointer = dxgiBuffer.GetResource(
                typeof(ID3D11Texture2D).GUID);
            texture = new ID3D11Texture2D(texturePointer);
            Texture2DDescription description =
                texture.Description;
            if (description.Format != Format.NV12)
            {
                throw new InvalidOperationException(
                    $"Expected an NV12 decoder texture, got " +
                    $"{description.Format}.");
            }

            if (description.Width < _options.Width ||
                description.Height < _options.Height)
            {
                throw new InvalidOperationException(
                    $"Decoder texture {description.Width}x" +
                    $"{description.Height} is incompatible with the " +
                    $"declared visible frame {_options.Width}x" +
                    $"{_options.Height}.");
            }

            textureDevice = texture.Device;
            long sampleTime = fallbackSampleTime100Nanoseconds;
            bool hasExplicitSampleTime = false;
            try
            {
                sampleTime = decodedSample.SampleTime;
                hasExplicitSampleTime = true;
            }
            catch (SharpGenException)
            {
                // A timestamp is supplied on every input sample, but keep the
                // caller timestamp if a third-party MFT drops it.
            }

            mediaFoundationLease =
                _mediaFoundationLifetime
                    .AcquireFrameLease();
            var frame = new MediaFoundationD3D11DecodedFrame(
                decodedSample,
                textureDevice,
                texture,
                mediaFoundationLease,
                dxgiBuffer.SubresourceIndex,
                _options.Width,
                _options.Height,
                sampleTime,
                hasExplicitSampleTime);
            decodedSample = null!;
            textureDevice = null;
            texture = null;
            mediaFoundationLease = null;
            return frame;
        }
        finally
        {
            texture?.Dispose();
            textureDevice?.Dispose();
            decodedSample?.Dispose();
            mediaFoundationLease?.Dispose();
        }
    }

    private static void EnableD3D11MultithreadProtection(
        ID3D11Device device)
    {
        using ID3D11DeviceContext context =
            device.ImmediateContext;
        using ID3D11Multithread multithread =
            context.QueryInterface<ID3D11Multithread>();
        multithread.SetMultithreadProtected(true);
    }

    private static void EnableCodecLowLatencyMode(
        IMFTransform transform)
    {
        var codecApi =
            (ICodecApi)Marshal.GetTypedObjectForIUnknown(
                transform.NativePointer,
                typeof(ICodecApi));
        try
        {
            Guid key = CodecApiLowLatencyMode;
            int supported = codecApi.IsSupported(ref key);
            Marshal.ThrowExceptionForHR(supported);
            if (supported != 0)
            {
                throw new CapabilityUnavailableException(
                    MediaFoundationD3D11CapabilityStatus
                        .LowLatencyModeUnavailable,
                    "The H.264 decoder does not support low-latency mode.");
            }

            int modifiable = codecApi.IsModifiable(ref key);
            Marshal.ThrowExceptionForHR(modifiable);
            if (modifiable != 0)
            {
                throw new CapabilityUnavailableException(
                    MediaFoundationD3D11CapabilityStatus
                        .LowLatencyModeUnavailable,
                    "The H.264 decoder low-latency mode is not modifiable.");
            }

            // The inbox H.264 decoder specifically requires VT_UI4 here,
            // rather than the VT_BOOL used by most ICodecAPI properties.
            object value = 1u;
            Marshal.ThrowExceptionForHR(
                codecApi.SetValue(
                    ref key,
                    ref value));
        }
        finally
        {
            Marshal.ReleaseComObject(codecApi);
        }
    }

    private static int GetAnnexBStartCodeLength(
        ReadOnlySpan<byte> bytes,
        int index)
    {
        if (index < 0 ||
            index + 3 >= bytes.Length ||
            bytes[index] != 0 ||
            bytes[index + 1] != 0)
        {
            return 0;
        }

        if (bytes[index + 2] == 1)
        {
            return 3;
        }

        return index + 4 < bytes.Length &&
            bytes[index + 2] == 0 &&
            bytes[index + 3] == 1
                ? 4
                : 0;
    }

    private static ulong PackRatio(
        uint numerator,
        uint denominator) =>
        ((ulong)numerator << 32) | denominator;

    private static int AlignToH264Macroblock(int value) =>
        checked((value + 15) & ~15);

    private static bool IsRecoverableCapabilityFailure(
        Exception exception) =>
        exception is not OutOfMemoryException;

    private static string FormatFailure(Exception exception) =>
        $"{exception.GetType().Name}: {exception.Message}";

    private static void TryShutdownMediaFoundation()
    {
        try
        {
            MediaFactory.MFShutdown();
        }
        catch
        {
            // Capability cleanup and Dispose must remain no-throw so the
            // existing software path can be selected without damage.
        }
    }

    private static void TryDispose(IDisposable disposable)
    {
        try
        {
            disposable.Dispose();
        }
        catch
        {
            // Teardown must not prevent selection of the fallback path.
        }
    }

    private void TryProcessMessage(TMessageType message)
    {
        try
        {
            _transform.ProcessMessage(
                message,
                UIntPtr.Zero);
        }
        catch
        {
            // Best-effort teardown only.
        }
    }

    private bool TryEnterRecoveryStateLocked(
        [NotNullWhen(false)]
        out string? failureDetail)
    {
        _referenceState =
            MediaFoundationD3D11ReferenceState
                .NeedsIndependentFrame;
        _acceptedInputsWithoutOutput = 0;
        _markNextInputDiscontinuity = false;
        try
        {
            _transform.ProcessMessage(
                TMessageType.MessageCommandFlush,
                UIntPtr.Zero);
            _transform.ProcessMessage(
                TMessageType.MessageNotifyStartOfStream,
                UIntPtr.Zero);
            failureDetail = null;
            return true;
        }
        catch (Exception ex) when (IsRecoverableCapabilityFailure(ex))
        {
            if (_state ==
                MediaFoundationD3D11DecoderState.Streaming)
            {
                TransitionTo(
                    ClassifyFailureState(ex));
            }

            failureDetail = FormatFailure(ex);
            return false;
        }
    }

    private MediaFoundationD3D11DecoderState
        ClassifyFailureState(Exception exception)
    {
        int hresult =
            exception is SharpGenException sharpGenException
                ? sharpGenException.ResultCode.Code
                : exception.HResult;
        if (IsDeviceLostHResult(hresult))
        {
            return MediaFoundationD3D11DecoderState.DeviceLost;
        }

        try
        {
            if (IsDeviceLostHResult(
                _device.DeviceRemovedReason.Code))
            {
                return MediaFoundationD3D11DecoderState.DeviceLost;
            }
        }
        catch
        {
            // Preserve the original failure classification.
        }

        return MediaFoundationD3D11DecoderState.Faulted;
    }

    private void TransitionTo(
        MediaFoundationD3D11DecoderState next)
    {
        if (!IsValidStateTransition(_state, next))
        {
            throw new InvalidOperationException(
                $"Invalid decoder state transition: {_state} -> {next}.");
        }

        _state = next;
    }

    private sealed class CapabilityUnavailableException : Exception
    {
        public CapabilityUnavailableException(
            MediaFoundationD3D11CapabilityStatus status,
            string message)
            : base(message)
        {
            Status = status;
        }

        public MediaFoundationD3D11CapabilityStatus Status { get; }
    }
}

[ComImport]
[Guid("901DB4C7-31CE-41A2-85DC-8FA0BF41B8DA")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICodecApi
{
    [PreserveSig]
    int IsSupported([In] ref Guid api);

    [PreserveSig]
    int IsModifiable([In] ref Guid api);

    [PreserveSig]
    int GetParameterRange(
        [In] ref Guid api,
        nint minimum,
        nint maximum,
        nint step);

    [PreserveSig]
    int GetParameterValues(
        [In] ref Guid api,
        out nint values,
        out uint valueCount);

    [PreserveSig]
    int GetDefaultValue(
        [In] ref Guid api,
        nint value);

    [PreserveSig]
    int GetValue(
        [In] ref Guid api,
        nint value);

    [PreserveSig]
    int SetValue(
        [In] ref Guid api,
        [In, MarshalAs(UnmanagedType.Struct)]
        ref object value);
}
