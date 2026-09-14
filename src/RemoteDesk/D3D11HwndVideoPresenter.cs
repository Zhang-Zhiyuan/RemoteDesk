using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Box = Vortice.Mathematics.Box;

namespace RemoteDesk;

internal enum D3D11HwndVideoScaleMode
{
    Fit,
    FitWithoutUpscaling,
    Fill
}

internal enum D3D11HwndVideoPresenterCapabilityStatus
{
    Available,
    InvalidOptions,
    UnsupportedOperatingSystem,
    InvalidDevice,
    InvalidWindow,
    DxgiUnavailable,
    VideoProcessorUnavailable,
    Nv12InputUnavailable,
    BgraOutputUnavailable,
    SwapChainUnavailable,
    InitializationFailed
}

internal sealed record D3D11HwndVideoPresenterCapability(
    D3D11HwndVideoPresenterCapabilityStatus Status,
    string Detail,
    SwapEffect? SwapEffect = null,
    bool MaximumFrameLatencyIsOne = false,
    bool AllowTearing = false,
    int BufferCount = 0)
{
    public bool IsAvailable =>
        Status ==
            D3D11HwndVideoPresenterCapabilityStatus.Available;
}

internal enum D3D11HwndVideoPresenterStatus
{
    Presented,
    Resized,
    SkippedMinimized,
    InvalidArgument,
    InvalidTexture,
    WrongDevice,
    Occluded,
    DeviceRemoved,
    DeviceReset,
    DeviceHung,
    DeviceLost,
    Failed,
    Disposed
}

internal readonly record struct D3D11HwndVideoPresenterResult(
    D3D11HwndVideoPresenterStatus Status,
    int HResult,
    int? DeviceRemovedReason,
    string Detail)
{
    public bool IsSuccess =>
        Status is
            D3D11HwndVideoPresenterStatus.Presented or
            D3D11HwndVideoPresenterStatus.Resized;
}

internal sealed record D3D11HwndVideoPresenterOptions(
    nint WindowHandle,
    int SourceWidth,
    int SourceHeight,
    int FramesPerSecond,
    D3D11HwndVideoScaleMode ScaleMode =
        D3D11HwndVideoScaleMode.Fit,
    bool PreferFlipDiscard = true,
    bool PreferAllowTearing = true,
    bool EnableEdgeEnhancement = true,
    bool EnableExperimentalUpscaling = false,
    ExperimentalUpscalingAlgorithm UpscalingAlgorithm = ExperimentalUpscalingAlgorithm.CatmullRom)
{
    internal const int MaximumDimension = 8192;
    internal const int MaximumFramesPerSecond = 120;

    internal string? Validate()
    {
        if (WindowHandle == nint.Zero)
        {
            return "A non-zero HWND is required.";
        }

        if (SourceWidth <= 0 ||
            SourceHeight <= 0 ||
            SourceWidth > MaximumDimension ||
            SourceHeight > MaximumDimension)
        {
            return $"Source dimensions must be between 1 and " +
                $"{MaximumDimension} pixels.";
        }

        if ((SourceWidth & 1) != 0 ||
            (SourceHeight & 1) != 0)
        {
            return "NV12 source dimensions must be even.";
        }

        if (FramesPerSecond is
            < 1 or > MaximumFramesPerSecond)
        {
            return $"Frame rate must be between 1 and " +
                $"{MaximumFramesPerSecond} FPS.";
        }

        if (!Enum.IsDefined(UpscalingAlgorithm)) return "The upscaling algorithm is invalid.";
        if (!Enum.IsDefined(ScaleMode))
        {
            return "The video scale mode is invalid.";
        }

        return null;
    }
}

internal readonly record struct D3D11HwndVideoPresentationGeometry(
    Rectangle Source,
    Rectangle Destination,
    Size Output);

internal readonly record struct D3D11HwndVideoValidationResult(
    bool Attempted,
    bool IsValid,
    string Detail)
{
    public static D3D11HwndVideoValidationResult NotAttempted =>
        new(
            Attempted: false,
            IsValid: false,
            "The presented back buffer has not been validated yet.");
}

/// <summary>
/// Presents borrowed NV12 textures directly to an HWND through the D3D11
/// video processor. The presenter owns an independent COM reference to the
/// supplied device. It never owns or releases textures supplied to Present.
/// </summary>
internal sealed class D3D11HwndVideoPresenter : IDisposable
{
    // Moonlight keeps enough flip-model buffers for the render queue and DWM
    // to avoid turning SyncInterval=0 into an implicit VBlank wait,
    // particularly on AMD drivers.
    internal const int LowLatencyBufferCount = 5;
    internal const int ValidationSampleGrid = 16;
    internal const byte ValidationMinimumAlpha = 0xF0;

    private const int DxgiStatusOccluded =
        unchecked((int)0x087A0001);
    private const int DxgiErrorDeviceRemoved =
        unchecked((int)0x887A0005);
    private const int DxgiErrorDeviceHung =
        unchecked((int)0x887A0006);
    private const int DxgiErrorDeviceReset =
        unchecked((int)0x887A0007);
    private const int DxgiErrorDriverInternalError =
        unchecked((int)0x887A0020);
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 0x00000102;
    private const uint WaitFailed = 0xFFFFFFFF;

    private readonly object _sync = new();
    private D3D11HwndVideoPresenterOptions _options;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly IDXGISwapChain1 _swapChain;
    private readonly PresentFlags _presentFlags;
    private readonly SwapChainFlags _swapChainFlags;
    private nint _frameLatencyWaitableObject;
    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11VideoProcessor? _processor;
    private ID3D11Texture2D? _backBuffer;
    // Flip-model Present rotates swap-chain buffers. Keep a copy of the
    // processor output made before Present for reliable qualification.
    private ID3D11Texture2D? _validationSource;
    private ID3D11Texture2D? _validationReadback;
    private bool _validationSourceReady;
    private ID3D11VideoProcessorOutputView? _outputView;
    private int _outputWidth;
    private int _outputHeight;
    private D3D11HwndVideoScaleMode _scaleMode;
    private bool _edgeEnhancementEnabled;
    private D3D11ExperimentalUpscaler? _experimentalUpscaler;
    private Size _experimentalSourceSize;
    private bool _experimentalUpscalingFailed;
    internal bool ExperimentalUpscalingActive { get; private set; }
    internal string ExperimentalUpscalingDetail { get; private set; } = string.Empty;
    // Optional borrowed queries for the synthetic GPU comparison probe. The
    // installed application never sets these; no timing/readback on normal frames.
    internal (ID3D11Query Begin, ID3D11Query End)? RenderTimingQueriesForTests { get; set; }
    internal event Action<D3D11HwndVideoPresenter, string>? ExperimentalUpscalingFailed;
    private D3D11NativeDetailCompositor? _nativeDetailCompositor;
    private ID3D11RenderTargetView? _nativeDetailRenderTarget;
    private bool _nativeDetailFailed;
    private Task<bool>? _nativeDetailPreparation;
    private long _nativeDetailGeneration;
    private long? _nativeDetailRequestedGeneration;
    internal bool NativeDetailActive { get; private set; }
    internal string NativeDetailStatus { get; private set; } = string.Empty;
    internal int NativeDetailUploadedTiles { get; private set; }
    internal int NativeDetailAtlasBytes => _nativeDetailCompositor is null ? 0 : D3D11NativeDetailCompositor.AtlasBytes;
    internal Action? NativeDetailAfterDrawForTests { get; set; }
    internal Action? NativeDetailPreparationForTests { get; set; }
    private bool _targetMinimized;
    private bool _disposed;

    private D3D11HwndVideoPresenter(
        D3D11HwndVideoPresenterOptions options,
        ID3D11Device device,
        ID3D11DeviceContext context,
        ID3D11VideoDevice videoDevice,
        ID3D11VideoContext videoContext,
        IDXGISwapChain1 swapChain,
        ID3D11VideoProcessorEnumerator enumerator,
        ID3D11VideoProcessor processor,
        ID3D11Texture2D backBuffer,
        ID3D11VideoProcessorOutputView outputView,
        PresentFlags presentFlags,
        SwapChainFlags swapChainFlags,
        nint frameLatencyWaitableObject,
        int outputWidth,
        int outputHeight,
        bool edgeEnhancementEnabled)
    {
        _options = options;
        _device = device;
        _context = context;
        _videoDevice = videoDevice;
        _videoContext = videoContext;
        _swapChain = swapChain;
        _enumerator = enumerator;
        _processor = processor;
        _backBuffer = backBuffer;
        _outputView = outputView;
        _presentFlags = presentFlags;
        _swapChainFlags = swapChainFlags;
        _frameLatencyWaitableObject =
            frameLatencyWaitableObject;
        _outputWidth = outputWidth;
        _outputHeight = outputHeight;
        _scaleMode = options.ScaleMode;
        _edgeEnhancementEnabled = edgeEnhancementEnabled;
    }

    internal Size OutputSize
    {
        get
        {
            lock (_sync)
            {
                return new(
                    _outputWidth,
                    _outputHeight);
            }
        }
    }

    internal static bool TryCreate(
        ID3D11Device? borrowedDevice,
        D3D11HwndVideoPresenterOptions? options,
        [NotNullWhen(true)]
        out D3D11HwndVideoPresenter? presenter,
        out D3D11HwndVideoPresenterCapability capability)
    {
        presenter = null;

        if (options is null)
        {
            capability = new(
                D3D11HwndVideoPresenterCapabilityStatus
                    .InvalidOptions,
                "Presenter options are required.");
            return false;
        }

        string? validationFailure = options.Validate();
        if (validationFailure is not null)
        {
            capability = new(
                D3D11HwndVideoPresenterCapabilityStatus
                    .InvalidOptions,
                validationFailure);
            return false;
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(8))
        {
            capability = new(
                D3D11HwndVideoPresenterCapabilityStatus
                    .UnsupportedOperatingSystem,
                "D3D11 HWND video presentation requires Windows 8 " +
                    "or later.");
            return false;
        }

        if (borrowedDevice is null ||
            borrowedDevice.NativePointer == nint.Zero)
        {
            capability = new(
                D3D11HwndVideoPresenterCapabilityStatus
                    .InvalidDevice,
                "A live D3D11 device is required.");
            return false;
        }

        if (!IsWindow(options.WindowHandle) ||
            !TryGetClientSize(
                options.WindowHandle,
                out int outputWidth,
                out int outputHeight) ||
            outputWidth <= 0 ||
            outputHeight <= 0)
        {
            capability = new(
                D3D11HwndVideoPresenterCapabilityStatus
                    .InvalidWindow,
                "The HWND must identify a live window with a non-empty " +
                    "client area.");
            return false;
        }

        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;
        ID3D11VideoDevice? videoDevice = null;
        ID3D11VideoContext? videoContext = null;
        IDXGISwapChain1? swapChain = null;
        ID3D11VideoProcessorEnumerator? enumerator = null;
        ID3D11VideoProcessor? processor = null;
        ID3D11Texture2D? backBuffer = null;
        ID3D11VideoProcessorOutputView? outputView = null;
        SwapEffect? selectedSwapEffect = null;
        bool allowTearing = false;
        int bufferCount = 0;
        bool edgeEnhancementEnabled = false;
        bool maximumFrameLatencyIsOne = false;
        nint frameLatencyWaitableObject = nint.Zero;
        SwapChainFlags swapChainFlags = SwapChainFlags.None;
        PresentFlags presentFlags = PresentFlags.None;
        D3D11HwndVideoPresenterCapabilityStatus failureStatus =
            D3D11HwndVideoPresenterCapabilityStatus.InvalidDevice;

        try
        {
            // QueryInterface creates an independent reference. Disposing the
            // presenter therefore never releases the caller's device
            // reference.
            device =
                borrowedDevice.QueryInterface<ID3D11Device>();
            context = device.ImmediateContext;
            EnableD3D11MultithreadProtection(context);

            failureStatus =
                D3D11HwndVideoPresenterCapabilityStatus
                    .VideoProcessorUnavailable;
            videoDevice =
                device.QueryInterface<ID3D11VideoDevice>();
            videoContext =
                context.QueryInterface<ID3D11VideoContext>();

            failureStatus =
                D3D11HwndVideoPresenterCapabilityStatus
                    .DxgiUnavailable;
            using IDXGIDevice dxgiDevice =
                device.QueryInterface<IDXGIDevice>();
            using IDXGIAdapter adapter =
                dxgiDevice.GetAdapter();
            using IDXGIFactory2 factory =
                adapter.GetParent<IDXGIFactory2>();

            failureStatus =
                D3D11HwndVideoPresenterCapabilityStatus
                    .SwapChainUnavailable;
            swapChain = CreateLowestLatencySwapChain(
                factory,
                device,
                options,
                outputWidth,
                outputHeight,
                out SwapEffect swapEffect,
                out allowTearing);
            selectedSwapEffect = swapEffect;
            bufferCount = LowLatencyBufferCount;
            swapChainFlags = ResolveSwapChainFlags(
                allowTearing);
            presentFlags = ResolvePresentFlags(
                allowTearing);
            frameLatencyWaitableObject =
                ConfigureMaximumFrameLatency(
                    swapChain);
            maximumFrameLatencyIsOne = true;
            _ = factory.MakeWindowAssociation(
                options.WindowHandle,
                WindowAssociationFlags.IgnoreAltEnter);

            failureStatus =
                D3D11HwndVideoPresenterCapabilityStatus
                    .VideoProcessorUnavailable;
            CreateVideoProcessorResources(
                videoDevice,
                videoContext,
                swapChain,
                options,
                options.ScaleMode,
                outputWidth,
                outputHeight,
                out enumerator,
                out processor,
                out backBuffer,
                out outputView,
                out edgeEnhancementEnabled);

            presenter = new(
                options,
                device,
                context,
                videoDevice,
                videoContext,
                swapChain,
                enumerator,
                processor,
                backBuffer,
                outputView,
                presentFlags,
                swapChainFlags,
                frameLatencyWaitableObject,
                outputWidth,
                outputHeight,
                edgeEnhancementEnabled);

            device = null;
            context = null;
            videoDevice = null;
            videoContext = null;
            swapChain = null;
            enumerator = null;
            processor = null;
            backBuffer = null;
            outputView = null;
            frameLatencyWaitableObject = nint.Zero;

            capability = new(
                D3D11HwndVideoPresenterCapabilityStatus.Available,
                $"NV12 VideoProcessorBlt is ready with a " +
                    $"{bufferCount}-buffer {selectedSwapEffect} swap chain" +
                    $"{(allowTearing ? " and tearing enabled" : string.Empty)}; " +
                    "maximum frame latency is 1.",
                selectedSwapEffect,
                MaximumFrameLatencyIsOne:
                    maximumFrameLatencyIsOne,
                AllowTearing: allowTearing,
                BufferCount: bufferCount);
            return true;
        }
        catch (VideoFormatUnavailableException ex)
        {
            capability = new(
                ex.Status,
                ex.Message,
                selectedSwapEffect,
                MaximumFrameLatencyIsOne:
                    maximumFrameLatencyIsOne,
                AllowTearing: allowTearing,
                BufferCount: bufferCount);
            return false;
        }
        catch (Exception ex)
        {
            capability = new(
                failureStatus,
                FormatFailure(ex),
                selectedSwapEffect,
                MaximumFrameLatencyIsOne:
                    maximumFrameLatencyIsOne,
                AllowTearing: allowTearing,
                BufferCount: bufferCount);
            return false;
        }
        finally
        {
            TryDispose(outputView);
            TryDispose(backBuffer);
            TryDispose(processor);
            TryDispose(enumerator);
            TryDispose(swapChain);
            TryDispose(videoContext);
            TryDispose(videoDevice);
            TryDispose(context);
            TryDispose(device);
            TryCloseHandle(
                ref frameLatencyWaitableObject);
        }
    }

    /// <summary>
    /// Acquires an independent texture lease from a decoded frame and keeps
    /// that lease alive through the GPU submission. The frame remains owned
    /// by the caller.
    /// </summary>
    internal D3D11HwndVideoPresenterResult Present(
        MediaFoundationD3D11DecodedFrame? frame,
        Rectangle visibleSource,
        bool captureValidation = false,
        NativeDetailPresentation? nativeDetails = null,
        NativeDetailRenderBudget nativeDetailBudget = default)
    {
        // Any attempted Present supersedes the previous validation snapshot,
        // including a rejected or unavailable decoded frame.
        InvalidateValidationSnapshot();
        if (frame is null)
        {
            return CreateResult(
                D3D11HwndVideoPresenterStatus.InvalidTexture,
                detail: "A decoded frame is required.");
        }

        try
        {
            using ID3D11Texture2D textureLease =
                frame.AcquireTextureLease();
            return Present(
                textureLease,
                frame.SubresourceIndex,
                visibleSource,
                captureValidation,
                nativeDetails,
                frame.HasExplicitSampleTime ? frame.SampleTime100Nanoseconds : null,
                nativeDetailBudget);
        }
        catch (Exception ex)
        {
            return CreateResult(
                D3D11HwndVideoPresenterStatus.InvalidTexture,
                ex.HResult,
                detail: FormatFailure(ex));
        }
    }

    /// <summary>
    /// Presents a borrowed texture. The caller must keep the texture alive
    /// until this method returns. This method never disposes the texture.
    /// </summary>
    internal D3D11HwndVideoPresenterResult Present(
        ID3D11Texture2D? borrowedTexture,
        uint subresourceIndex,
        Rectangle visibleSource,
        bool captureValidation = false,
        NativeDetailPresentation? nativeDetails = null,
        long? explicitSampleTime100Nanoseconds = null,
        NativeDetailRenderBudget nativeDetailBudget = default)
    {
        lock (_sync)
        {
            // A validation result belongs to this Present call only. Clear it
            // before every guard so minimized, invalid, or unavailable calls
            // cannot leave a stale frame eligible for qualification.
            _validationSourceReady = false;
            ExperimentalUpscalingActive = false;
            NativeDetailActive = false;
            if (nativeDetails is null && !_nativeDetailFailed) NativeDetailStatus = string.Empty;
            NativeDetailUploadedTiles = 0;
            if (_disposed)
            {
                return CreateResult(
                    D3D11HwndVideoPresenterStatus.Disposed,
                    detail: "The presenter is disposed.");
            }

            if (_targetMinimized)
            {
                return CreateResult(
                    D3D11HwndVideoPresenterStatus
                        .SkippedMinimized,
                    detail: "The target client area is empty.");
            }

            if (borrowedTexture is null ||
                borrowedTexture.NativePointer == nint.Zero)
            {
                return CreateResult(
                    D3D11HwndVideoPresenterStatus.InvalidTexture,
                    detail: "A live NV12 texture is required.");
            }

            if (_enumerator is null ||
                _processor is null ||
                _outputView is null)
            {
                return CreateResult(
                    D3D11HwndVideoPresenterStatus.Failed,
                    detail: "The video-processor pipeline is unavailable.");
            }

            try
            {
                Texture2DDescription description =
                    borrowedTexture.Description;
                string? textureFailure =
                    ValidateBorrowedTexture(
                        description,
                        subresourceIndex,
                        visibleSource);
                if (textureFailure is not null)
                {
                    return CreateResult(
                        D3D11HwndVideoPresenterStatus.InvalidTexture,
                        detail: textureFailure);
                }

                // Vortice caches ID3D11DeviceChild.Device on the resource.
                // Disposing that cached wrapper here invalidates the resource's
                // device identity after the first presented frame. The texture
                // owns the cached reference, so inspect it without disposing it.
                ID3D11Device textureDevice =
                    borrowedTexture.Device;
                if (textureDevice.NativePointer !=
                    _device.NativePointer)
                {
                    return CreateResult(
                        D3D11HwndVideoPresenterStatus.WrongDevice,
                        detail: "The texture belongs to a different " +
                            "D3D11 device.");
                }

                uint frameLatencyWait =
                    WaitForSingleObject(
                        _frameLatencyWaitableObject,
                        CalculateFrameLatencyWaitTimeoutMilliseconds(
                            _options.FramesPerSecond));
                if (frameLatencyWait == WaitTimeout)
                {
                    return CreateResult(
                        D3D11HwndVideoPresenterStatus.Occluded,
                        detail:
                            "The frame-latency wait timed out; the stale " +
                            "decoded frame was skipped.");
                }

                if (frameLatencyWait != WaitObject0)
                {
                    int error =
                        frameLatencyWait == WaitFailed
                            ? Marshal.GetHRForLastWin32Error()
                            : unchecked((int)frameLatencyWait);
                    return CreateFailureResult(
                        error,
                        "Waiting for the DXGI frame-latency object failed.");
                }

                D3D11HwndVideoPresentationGeometry geometry =
                    CalculateGeometry(
                        visibleSource,
                        new Size(
                            _outputWidth,
                            _outputHeight),
                        _scaleMode);
                uint mipLevels =
                    Math.Max(1u, description.MipLevels);
                uint mipSlice =
                    subresourceIndex % mipLevels;
                uint arraySlice =
                    subresourceIndex / mipLevels;

                var inputViewDescription =
                    new VideoProcessorInputViewDescription
                    {
                        ViewDimension =
                            VideoProcessorInputViewDimension.Texture2D,
                        Texture2D =
                            new Texture2DVideoProcessorInputView
                            {
                                MipSlice = mipSlice,
                                ArraySlice = arraySlice
                            }
                    };

                // The input view owns its own temporary reference. The
                // caller's texture wrapper remains borrowed and untouched.
                using ID3D11VideoProcessorInputView inputView =
                    _videoDevice.CreateVideoProcessorInputView(
                        borrowedTexture,
                        _enumerator,
                        inputViewDescription);

                if (RenderTimingQueriesForTests is { } startTiming) _context.End(startTiming.Begin);
                var baseFailure = RenderBase(inputView, geometry);
                if (baseFailure is { } failure) return failure;

                if (nativeDetails is not null && !_nativeDetailFailed)
                {
                    if (!nativeDetails.Matches(explicitSampleTime100Nanoseconds))
                        NativeDetailStatus = "原生补清等待匹配的视频帧";
                    else if (_nativeDetailCompositor is null)
                        NativeDetailStatus = "原生补清未就绪，直接显示底图";
                    else if (!nativeDetailBudget.Allows(Stopwatch.GetTimestamp()))
                        NativeDetailStatus = "原生补清暂停，优先输入和底图";
                    else
                    {
                        try
                        {
                            // Resource creation happens ONLY in explicit async
                            // preparation, never on this presentation path.
                            if (D3D11NativeDetailCompositor.CanRender(nativeDetails, visibleSource, geometry))
                            {
                                NativeDetailActive = _nativeDetailCompositor.Render(nativeDetails, visibleSource, geometry, _nativeDetailRenderTarget!, nativeDetailBudget);
                                NativeDetailUploadedTiles = _nativeDetailCompositor.UploadedTilesLastRender;
                            }
                            else _nativeDetailCompositor?.Clear();
                            NativeDetailStatus = NativeDetailActive ? "原生补清" : "原生补清等待可用区域";
                            if (NativeDetailActive) NativeDetailAfterDrawForTests?.Invoke();
                        }
                        catch (Exception ex)
                        {
                            _nativeDetailFailed = true;
                            NativeDetailActive = false;
                            NativeDetailStatus = "原生补清已回退：" + FormatFailure(ex);
                            TryDispose(_nativeDetailCompositor);
                            _nativeDetailCompositor = null;
                            // Failure can happen AFTER part of a GPU overlay was
                            // drawn. Redraw this SAME base before Present; never
                            // expose a partial native layer or reset the decoder.
                            baseFailure = RenderBase(inputView, geometry);
                            if (baseFailure is { } rollbackFailure) return rollbackFailure;
                        }
                    }
                }

                if (RenderTimingQueriesForTests is { } endTiming) _context.End(endTiming.End);
                if (captureValidation)
                {
                    try
                    {
                        EnsureValidationReadback();
                        if (_validationSource is null ||
                            _backBuffer is null)
                        {
                            return CreateResult(
                                D3D11HwndVideoPresenterStatus.Failed,
                                detail:
                                    "Unable to capture the video-processor " +
                                    "output before Present.");
                        }

                        // Present may rotate a flip-model swap chain. Copy
                        // while buffer 0 is still the processor output.
                        _context.CopyResource(
                            _validationSource,
                            _backBuffer);
                    }
                    catch (Exception ex)
                    {
                        return CreateFailureResult(
                            ex.HResult,
                            "Copying the video-processor output for " +
                            $"validation failed: {FormatFailure(ex)}");
                    }
                }

                Result presentResult =
                    _swapChain.Present(
                        syncInterval: 0,
                        _presentFlags);
                if (presentResult.Code == DxgiStatusOccluded)
                {
                    return new(
                        D3D11HwndVideoPresenterStatus.Occluded,
                        presentResult.Code,
                        null,
                        "The target window is occluded.");
                }

                if (presentResult.Failure)
                {
                    return CreateFailureResult(
                        presentResult.Code,
                        "DXGI Present failed.");
                }

                _validationSourceReady = captureValidation;
                return CreateResult(
                    D3D11HwndVideoPresenterStatus.Presented,
                    presentResult.Code,
                    detail: "The NV12 frame was presented.");
            }
            catch (Exception ex)
            {
                return CreateFailureResult(
                    ex.HResult,
                    FormatFailure(ex));
            }
        }
    }

    private void InvalidateValidationSnapshot()
    {
        lock (_sync)
        {
            _validationSourceReady = false;
            NativeDetailActive = false;
            NativeDetailUploadedTiles = 0;
        }
    }

    // Call when a locally enabled session has useful native work to prepare.
    // The presentation loop must NEVER await this task. Until it completes it
    // continues rendering the base. Tests may await outside measured frames.
    internal Task<bool> PrepareNativeDetailsAsync()
    {
        lock (_sync)
        {
            if (_disposed || _nativeDetailFailed || _backBuffer is null) return Task.FromResult(false);
            if (_nativeDetailCompositor is not null) return Task.FromResult(true);
            ID3D11Device? leaseDevice = null;
            ID3D11DeviceContext? leaseContext = null;
            try
            {
                // This cheap target view is owned ONLY by this generation.
                // The slow worker must not retain a swap-chain buffer lease:
                // that would make a simultaneous ResizeBuffers fail.
                _nativeDetailRenderTarget ??= _device.CreateRenderTargetView(_backBuffer);
                _nativeDetailRequestedGeneration = _nativeDetailGeneration;
                // Shader/atlas preparation depends on the device, not window
                // size. A new EXPLICIT opt-in may join the one active worker.
                // Rapid toggles must not enqueue many blocking compiler tasks.
                if (_nativeDetailPreparation is { IsCompleted: false }) return _nativeDetailPreparation;
                leaseDevice = _device.QueryInterface<ID3D11Device>();
                leaseContext = _context.QueryInterface<ID3D11DeviceContext>();
            }
            catch (Exception ex)
            {
                TryDispose(leaseContext); TryDispose(leaseDevice);
                _nativeDetailFailed = true;
                NativeDetailStatus = "原生补清准备失败：" + FormatFailure(ex);
                return Task.FromResult(false);
            }
            Action? preparationHook = NativeDetailPreparationForTests;
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _nativeDetailPreparation = completion.Task;
            _ = Task.Run(() =>
            {
                using (leaseDevice)
                using (leaseContext)
                {
                    D3D11NativeDetailCompositor? prepared = null;
                    try
                    {
                        preparationHook?.Invoke();
                        lock (_sync)
                            if (!NativeDetailPreparationRequested())
                            { completion.TrySetResult(false); return; }
                        prepared = new D3D11NativeDetailCompositor(leaseDevice, leaseContext);
                        lock (_sync)
                        {
                            if (!NativeDetailPreparationRequested())
                            { completion.TrySetResult(false); return; }
                            _nativeDetailCompositor = prepared; prepared = null;
                            NativeDetailStatus = "原生补清资源已就绪";
                            // Complete under the same lock as publication. A
                            // new request must not join a worker which already
                            // decided to exit but whose Task is not finished yet.
                            completion.TrySetResult(true);
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (_sync)
                        {
                            if (NativeDetailPreparationRequested())
                            { _nativeDetailFailed = true; NativeDetailStatus = "原生补清准备失败：" + FormatFailure(ex); }
                            completion.TrySetResult(false);
                        }
                    }
                    finally { TryDispose(prepared); }
                }
            });
            return _nativeDetailPreparation;
        }
    }

    // Read only under _sync. Completion is never authorization to enable an
    // old session; a current explicit local request and target view are required.
    private bool NativeDetailPreparationRequested() => !_disposed && !_nativeDetailFailed &&
        _nativeDetailRequestedGeneration == _nativeDetailGeneration && _nativeDetailRenderTarget is not null;

    internal void DisableNativeDetails()
    {
        lock (_sync)
        {
            ReleaseNativeDetailResources();
            _nativeDetailFailed = false;
            NativeDetailStatus = string.Empty;
        }
    }

    private void ReleaseNativeDetailResources()
    {
        _nativeDetailGeneration++;
        _nativeDetailRequestedGeneration = null;
        // In-flight work remains single-flight across off/on/resize. Disable
        // still returns immediately and clears the authorization to publish.
        if (_nativeDetailPreparation is { IsCompleted: true }) _nativeDetailPreparation = null;
        TryDispose(_nativeDetailCompositor);
        _nativeDetailCompositor = null;
        TryDispose(_nativeDetailRenderTarget);
        _nativeDetailRenderTarget = null;
        NativeDetailActive = false;
        NativeDetailUploadedTiles = 0;
    }

    private D3D11HwndVideoPresenterResult? RenderBase(
        ID3D11VideoProcessorInputView inputView, D3D11HwndVideoPresentationGeometry geometry)
    {
        if (TryPresentExperimental(inputView, geometry)) return null;
        ConfigureVideoProcessor(_videoContext, _processor!, geometry);
        var stream = new VideoProcessorStream { Enable = true, InputSurface = inputView };
        Result blitResult = _videoContext.VideoProcessorBlt(_processor!, _outputView!, 0, [stream]);
        int completedBltRetries = 0;
        while (ShouldRetryVideoProcessorBlt(blitResult.Failure, _edgeEnhancementEnabled, completedBltRetries))
        {
            completedBltRetries++;
            if (!TryDisableEdgeEnhancementForRetry()) break;
            blitResult = _videoContext.VideoProcessorBlt(_processor!, _outputView!, 0, [stream]);
        }
        return blitResult.Failure ? CreateFailureResult(blitResult.Code, "VideoProcessorBlt failed.") : null;
    }

    internal D3D11HwndVideoValidationResult ValidatePresentedFrame()
    {
        lock (_sync)
        {
            if (_disposed ||
                _targetMinimized ||
                _backBuffer is null ||
                !_validationSourceReady ||
                _outputWidth <= 0 ||
                _outputHeight <= 0)
            {
                return D3D11HwndVideoValidationResult.NotAttempted;
            }

            try
            {
                // Consume the per-Present snapshot before mapping so a
                // repeated call cannot validate the same stale frame again.
                _validationSourceReady = false;
                EnsureValidationReadback();
                if (_validationReadback is null ||
                    _validationSource is null)
                {
                    return new(
                        Attempted: true,
                        IsValid: false,
                        "Unable to create a BGRA validation texture.");
                }

                // The source was copied before Present by a validation-enabled
                // Present call. Reading the swap-chain back buffer here would
                // be invalid after flip-model buffer rotation.
                _context.CopyResource(
                    _validationReadback,
                    _validationSource);
                _context.Flush();
                Result mapResult = _context.Map(
                    _validationReadback,
                    0,
                    MapMode.Read,
                    Vortice.Direct3D11.MapFlags.None,
                    out MappedSubresource mapped);
                if (mapResult.Failure)
                {
                    return new(
                        Attempted: true,
                        IsValid: false,
                        $"Mapping the presented back buffer failed " +
                            $"(0x{mapResult.Code:X8}).");
                }

                try
                {
                    return ValidateBgraSurface(
                        mapped.DataPointer,
                        checked((int)mapped.RowPitch),
                        _outputWidth,
                        _outputHeight);
                }
                finally
                {
                    _context.Unmap(
                        _validationReadback,
                        0);
                }
            }
            catch (Exception ex)
            {
                return new(
                    Attempted: true,
                    IsValid: false,
                    $"Presented-pixel validation failed: {FormatFailure(ex)}");
            }
        }
    }

    internal static D3D11HwndVideoValidationResult ValidateBgraSurface(
        nint dataPointer,
        int rowPitch,
        int width,
        int height)
    {
        if (dataPointer == nint.Zero ||
            width <= 0 ||
            height <= 0 ||
            rowPitch < checked(width * 4))
        {
            return new(
                Attempted: true,
                IsValid: false,
                "The presented BGRA readback surface is invalid.");
        }

        int opaqueSamples = 0;
        int sampleCount = 0;
        for (int sampleY = 0;
            sampleY < ValidationSampleGrid;
            sampleY++)
        {
            int y = (sampleY * height) /
                ValidationSampleGrid;
            for (int sampleX = 0;
                sampleX < ValidationSampleGrid;
                sampleX++)
            {
                int x = (sampleX * width) /
                    ValidationSampleGrid;
                int pixelOffset = checked(
                    (y * rowPitch) +
                    (x * 4));
                byte alpha = Marshal.ReadByte(
                    dataPointer,
                    pixelOffset + 3);
                if (alpha >= ValidationMinimumAlpha)
                {
                    opaqueSamples++;
                }

                sampleCount++;
            }
        }

        bool isValid = opaqueSamples == sampleCount;
        return new(
            Attempted: true,
            isValid,
            isValid
                ? $"Validated {sampleCount} opaque BGRA samples."
                : $"Only {opaqueSamples}/{sampleCount} BGRA samples " +
                    "were opaque after VideoProcessorBlt.");
    }

    private void EnsureValidationReadback()
    {
        uint width = checked((uint)_outputWidth);
        uint height = checked((uint)_outputHeight);
        if (_validationSource is not null &&
            _validationReadback is not null &&
            _validationSource.Description.Width == width &&
            _validationSource.Description.Height == height &&
            _validationReadback.Description.Width == width &&
            _validationReadback.Description.Height == height)
        {
            return;
        }

        TryDispose(_validationSource);
        _validationSource = null;
        TryDispose(_validationReadback);
        _validationReadback = null;
        _validationSourceReady = false;

        _validationSource =
            _device.CreateTexture2D(
                new Texture2DDescription(
                    Format.B8G8R8A8_UNorm,
                    width,
                    height,
                    1,
                    1,
                    BindFlags.None,
                    ResourceUsage.Default,
                    CpuAccessFlags.None,
                    1,
                    0,
                    ResourceOptionFlags.None));
        _validationReadback =
            _device.CreateTexture2D(
                new Texture2DDescription(
                    Format.B8G8R8A8_UNorm,
                    width,
                    height,
                    1,
                    1,
                    BindFlags.None,
                    ResourceUsage.Staging,
                    CpuAccessFlags.Read,
                    1,
                    0,
                    ResourceOptionFlags.None));
    }

    internal D3D11HwndVideoPresenterResult ResizeToWindow()
    {
        if (!TryGetClientSize(
            _options.WindowHandle,
            out int width,
            out int height))
        {
            return CreateResult(
                D3D11HwndVideoPresenterStatus.InvalidArgument,
                detail: "Unable to read the HWND client area.");
        }

        return Resize(width, height);
    }

    internal void SetEdgeEnhancement(bool enabled)
    {
        lock (_sync)
        {
            if (_disposed) return;
            _options = _options with { EnableEdgeEnhancement = enabled };
            RefreshScaledEdgeEnhancement();
        }
    }

    internal void SetExperimentalUpscaling(bool enabled, ExperimentalUpscalingAlgorithm? algorithm = null)
    {
        lock (_sync)
        {
            if (_disposed) return;
            if (algorithm is not null && !Enum.IsDefined(algorithm.Value))
                throw new ArgumentOutOfRangeException(nameof(algorithm));
            bool changed = algorithm is not null && _options.UpscalingAlgorithm != algorithm.Value;
            _options = _options with
            {
                EnableExperimentalUpscaling = enabled,
                UpscalingAlgorithm = algorithm ?? _options.UpscalingAlgorithm
            };
            _experimentalUpscalingFailed = false;
            ExperimentalUpscalingActive = false;
            ExperimentalUpscalingDetail = string.Empty;
            if (!enabled || changed)
            {
                TryDispose(_experimentalUpscaler);
                _experimentalUpscaler = null;
            }
        }
    }

    private bool TryPresentExperimental(ID3D11VideoProcessorInputView input,
        D3D11HwndVideoPresentationGeometry geometry)
    {
        ExperimentalUpscalingDetail = string.Empty;
        if (_experimentalUpscalingFailed || !D3D11ExperimentalUpscaler.ShouldApply(
                _options.EnableExperimentalUpscaling, geometry.Source.Size, geometry.Destination.Size))
            return false;
        try
        {
            if (_experimentalUpscaler is null || _experimentalSourceSize != geometry.Source.Size)
            {
                TryDispose(_experimentalUpscaler);
                _experimentalUpscaler = null;
                _experimentalUpscaler = new D3D11ExperimentalUpscaler(
                    _device, _context, _videoDevice, _enumerator!, geometry.Source.Size, _options.UpscalingAlgorithm);
                _experimentalSourceSize = geometry.Source.Size;
            }
            _experimentalUpscaler.Render(_videoContext, input, _backBuffer!, geometry);
            ExperimentalUpscalingActive = true;
            ExperimentalUpscalingDetail = _experimentalUpscaler.ActiveAlgorithm;
            return true;
        }
        catch (Exception ex)
        {
            // One failure disables this experiment, not hardware decoding or
            // the connection. Present the same frame through the old path.
            _experimentalUpscalingFailed = true;
            TryDispose(_experimentalUpscaler);
            _experimentalUpscaler = null;
            ExperimentalUpscalingFailed?.Invoke(this, FormatFailure(ex));
            return false;
        }
    }

    internal D3D11HwndVideoPresenterResult SetScaleMode(
        D3D11HwndVideoScaleMode scaleMode)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return CreateResult(
                    D3D11HwndVideoPresenterStatus.Disposed,
                    detail: "The presenter is disposed.");
            }

            if (!Enum.IsDefined(scaleMode))
            {
                return CreateResult(
                    D3D11HwndVideoPresenterStatus.InvalidArgument,
                    detail: "The video scale mode is invalid.");
            }

            if (_scaleMode == scaleMode)
            {
                return CreateResult(
                    D3D11HwndVideoPresenterStatus.Resized,
                    detail: "The video scale mode is already current.");
            }

            _scaleMode = scaleMode;
            RefreshScaledEdgeEnhancement();
            return CreateResult(
                D3D11HwndVideoPresenterStatus.Resized,
                detail: $"The video scale mode changed to {scaleMode}.");
        }
    }

    internal D3D11HwndVideoPresenterResult Resize(
        int width,
        int height)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return CreateResult(
                    D3D11HwndVideoPresenterStatus.Disposed,
                    detail: "The presenter is disposed.");
            }

            if (width < 0 ||
                height < 0 ||
                width > D3D11HwndVideoPresenterOptions.MaximumDimension ||
                height > D3D11HwndVideoPresenterOptions.MaximumDimension)
            {
                return CreateResult(
                    D3D11HwndVideoPresenterStatus.InvalidArgument,
                    detail: $"Output dimensions must be between 0 and " +
                        $"{D3D11HwndVideoPresenterOptions.MaximumDimension} " +
                        "pixels.");
            }

            if (width == 0 || height == 0)
            {
                _targetMinimized = true;
                return CreateResult(
                    D3D11HwndVideoPresenterStatus
                        .SkippedMinimized,
                    detail: "Resize was deferred for an empty client area.");
            }

            if (width == _outputWidth &&
                height == _outputHeight &&
                _enumerator is not null &&
                _processor is not null &&
                _outputView is not null)
            {
                _targetMinimized = false;
                return CreateResult(
                    D3D11HwndVideoPresenterStatus.Resized,
                    detail: "The swap-chain size is already current.");
            }

            try
            {
                int oldWidth = _outputWidth;
                int oldHeight = _outputHeight;
                ReleaseVideoProcessorResources();

                Result resizeResult =
                    _swapChain.ResizeBuffers(
                        LowLatencyBufferCount,
                        checked((uint)width),
                        checked((uint)height),
                        Format.B8G8R8A8_UNorm,
                        _swapChainFlags);
                if (resizeResult.Failure)
                {
                    TryRestoreVideoProcessorResources(
                        oldWidth,
                        oldHeight);
                    return CreateFailureResult(
                        resizeResult.Code,
                        "DXGI ResizeBuffers failed.");
                }

                _outputWidth = width;
                _outputHeight = height;
                _targetMinimized = false;
                CreateVideoProcessorResources(
                    _videoDevice,
                    _videoContext,
                    _swapChain,
                    _options,
                    _scaleMode,
                    width,
                    height,
                    out ID3D11VideoProcessorEnumerator enumerator,
                    out ID3D11VideoProcessor processor,
                    out ID3D11Texture2D backBuffer,
                    out ID3D11VideoProcessorOutputView outputView,
                    out bool edgeEnhancementEnabled);
                _enumerator = enumerator;
                _processor = processor;
                _backBuffer = backBuffer;
                _outputView = outputView;
                _edgeEnhancementEnabled = edgeEnhancementEnabled;

                return CreateResult(
                    D3D11HwndVideoPresenterStatus.Resized,
                    resizeResult.Code,
                    detail: $"The swap chain was resized to " +
                        $"{width}x{height}.");
            }
            catch (Exception ex)
            {
                return CreateFailureResult(
                    ex.HResult,
                    FormatFailure(ex));
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ReleaseVideoProcessorResources();
            TryCloseHandle(
                ref _frameLatencyWaitableObject);
            TryDispose(_swapChain);
            TryDispose(_videoContext);
            TryDispose(_videoDevice);
            TryDispose(_context);
            TryDispose(_device);
        }

        GC.SuppressFinalize(this);
    }

    internal static D3D11HwndVideoPresentationGeometry
        CalculateGeometry(
            Rectangle visibleSource,
            Size output,
            D3D11HwndVideoScaleMode scaleMode)
    {
        if (visibleSource.X < 0 ||
            visibleSource.Y < 0 ||
            visibleSource.Width <= 0 ||
            visibleSource.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(visibleSource),
                "The source rectangle must be positive.");
        }

        if (output.Width <= 0 ||
            output.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(output),
                "The output dimensions must be positive.");
        }

        if (!Enum.IsDefined(scaleMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(scaleMode));
        }

        if (scaleMode is
                D3D11HwndVideoScaleMode.Fit or
                D3D11HwndVideoScaleMode.FitWithoutUpscaling)
        {
            Rectangle destination =
                CalculateFitDestination(
                    visibleSource.Size,
                    output,
                    allowUpscaling:
                        scaleMode ==
                            D3D11HwndVideoScaleMode.Fit);
            return new(
                visibleSource,
                destination,
                output);
        }

        Rectangle source =
            CalculateFillSource(
                visibleSource,
                output);
        return new(
            source,
            new Rectangle(
                Point.Empty,
                output),
            output);
    }

    internal static D3D11HwndVideoPresenterStatus ClassifyFailure(
        int hResult,
        int? deviceRemovedReason)
    {
        int classificationCode =
            IsDeviceLossCode(deviceRemovedReason)
                ? deviceRemovedReason!.Value
                : hResult;

        return classificationCode switch
        {
            DxgiErrorDeviceRemoved =>
                D3D11HwndVideoPresenterStatus.DeviceRemoved,
            DxgiErrorDeviceReset =>
                D3D11HwndVideoPresenterStatus.DeviceReset,
            DxgiErrorDeviceHung =>
                D3D11HwndVideoPresenterStatus.DeviceHung,
            DxgiErrorDriverInternalError =>
                D3D11HwndVideoPresenterStatus.DeviceLost,
            _ => D3D11HwndVideoPresenterStatus.Failed
        };
    }

    private static IDXGISwapChain1 CreateLowestLatencySwapChain(
        IDXGIFactory2 factory,
        ID3D11Device device,
        D3D11HwndVideoPresenterOptions options,
        int outputWidth,
        int outputHeight,
        out SwapEffect selectedSwapEffect,
        out bool allowTearing)
    {
        bool tearingSupported =
            options.PreferAllowTearing &&
            OperatingSystem.IsWindowsVersionAtLeast(10) &&
            IsPresentAllowTearingSupported(factory);

        if (options.PreferFlipDiscard &&
            OperatingSystem.IsWindowsVersionAtLeast(10))
        {
            selectedSwapEffect = SwapEffect.FlipDiscard;
            try
            {
                allowTearing = tearingSupported;
                return CreateSwapChainForHwnd(
                    factory,
                    device,
                    options.WindowHandle,
                    outputWidth,
                    outputHeight,
                    selectedSwapEffect,
                    allowTearing);
            }
            catch
            {
                if (tearingSupported)
                {
                    try
                    {
                        allowTearing = false;
                        return CreateSwapChainForHwnd(
                            factory,
                            device,
                            options.WindowHandle,
                            outputWidth,
                            outputHeight,
                            selectedSwapEffect,
                            allowTearing);
                    }
                    catch
                    {
                    }
                }

                // Windows 10 can still expose an older display driver that
                // does not implement flip-discard. Flip-sequential is the
                // Windows 8-compatible fallback below.
            }
        }

        selectedSwapEffect = SwapEffect.FlipSequential;
        try
        {
            allowTearing = tearingSupported;
            return CreateSwapChainForHwnd(
                factory,
                device,
                options.WindowHandle,
                outputWidth,
                outputHeight,
                selectedSwapEffect,
                allowTearing);
        }
        catch when (tearingSupported)
        {
            allowTearing = false;
            return CreateSwapChainForHwnd(
                factory,
                device,
                options.WindowHandle,
                outputWidth,
                outputHeight,
                selectedSwapEffect,
                allowTearing);
        }
    }

    private static IDXGISwapChain1 CreateSwapChainForHwnd(
        IDXGIFactory2 factory,
        ID3D11Device device,
        nint windowHandle,
        int width,
        int height,
        SwapEffect swapEffect,
        bool allowTearing)
    {
        return factory.CreateSwapChainForHwnd(
            device,
            windowHandle,
            CreateSwapChainDescription(
                width,
                height,
                swapEffect,
                allowTearing),
            null,
            null);
    }

    internal static SwapChainDescription1
        CreateSwapChainDescription(
            int width,
            int height,
            SwapEffect swapEffect,
            bool allowTearing) =>
        new(
            checked((uint)width),
            checked((uint)height),
            Format.B8G8R8A8_UNorm,
            stereo: false,
            Usage.RenderTargetOutput,
            bufferCount: LowLatencyBufferCount,
            Scaling.Stretch,
            swapEffect,
            AlphaMode.Ignore,
            ResolveSwapChainFlags(
                allowTearing));

    internal static SwapChainFlags ResolveSwapChainFlags(
        bool allowTearing) =>
        SwapChainFlags.FrameLatencyWaitableObject |
        (allowTearing
            ? SwapChainFlags.AllowTearing
            : SwapChainFlags.None);

    internal static uint
        CalculateFrameLatencyWaitTimeoutMilliseconds(
            int framesPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            framesPerSecond,
            1);
        return checked(
            (uint)Math.Clamp(
                (int)Math.Ceiling(
                    2_000d / framesPerSecond),
                16,
                100));
    }

    internal static PresentFlags ResolvePresentFlags(
        bool allowTearing) =>
        allowTearing
            ? PresentFlags.AllowTearing
            : PresentFlags.None;

    private static bool IsPresentAllowTearingSupported(
        IDXGIFactory2 factory)
    {
        try
        {
            using IDXGIFactory5 factory5 =
                factory.QueryInterface<IDXGIFactory5>();
            return factory5.PresentAllowTearing;
        }
        catch
        {
            return false;
        }
    }

    private static nint ConfigureMaximumFrameLatency(
        IDXGISwapChain1 swapChain)
    {
        using IDXGISwapChain2 swapChain2 =
            swapChain.QueryInterface<IDXGISwapChain2>();
        swapChain2.MaximumFrameLatency = 1;
        nint waitableObject =
            swapChain2.FrameLatencyWaitableObject;
        if (waitableObject == nint.Zero)
        {
            throw new InvalidOperationException(
                "DXGI did not provide a frame-latency waitable object.");
        }

        return waitableObject;
    }

    private static void CreateVideoProcessorResources(
        ID3D11VideoDevice videoDevice,
        ID3D11VideoContext videoContext,
        IDXGISwapChain1 swapChain,
        D3D11HwndVideoPresenterOptions options,
        D3D11HwndVideoScaleMode scaleMode,
        int outputWidth,
        int outputHeight,
        out ID3D11VideoProcessorEnumerator enumerator,
        out ID3D11VideoProcessor processor,
        out ID3D11Texture2D backBuffer,
        out ID3D11VideoProcessorOutputView outputView,
        out bool edgeEnhancementEnabled)
    {
        enumerator = null!;
        processor = null!;
        backBuffer = null!;
        outputView = null!;
        edgeEnhancementEnabled = false;

        try
        {
            var content = new VideoProcessorContentDescription
            {
                InputFrameFormat = VideoFrameFormat.Progressive,
                InputFrameRate =
                    new Rational(
                        checked((uint)options.FramesPerSecond),
                        1),
                InputWidth =
                    checked((uint)options.SourceWidth),
                InputHeight =
                    checked((uint)options.SourceHeight),
                OutputFrameRate =
                    new Rational(
                        checked((uint)options.FramesPerSecond),
                        1),
                OutputWidth =
                    checked((uint)outputWidth),
                OutputHeight =
                    checked((uint)outputHeight),
                // PlaybackNormal keeps the hardware video processor on its
                // balanced scaling path. OptimalSpeed is useful for raw
                // throughput benchmarks but visibly softens small desktop
                // text when a 1080p source is shown on a higher-DPI panel.
                Usage = VideoUsage.PlaybackNormal
            };
            enumerator =
                videoDevice.CreateVideoProcessorEnumerator(
                    content);

            VideoProcessorFormatSupport inputSupport =
                enumerator.CheckVideoProcessorFormat(
                    Format.NV12);
            if (!inputSupport.HasFlag(
                VideoProcessorFormatSupport.Input))
            {
                throw new VideoFormatUnavailableException(
                    D3D11HwndVideoPresenterCapabilityStatus
                        .Nv12InputUnavailable,
                    "The D3D11 video processor cannot consume NV12.");
            }

            VideoProcessorFormatSupport outputSupport =
                enumerator.CheckVideoProcessorFormat(
                    Format.B8G8R8A8_UNorm);
            if (!outputSupport.HasFlag(
                VideoProcessorFormatSupport.Output))
            {
                throw new VideoFormatUnavailableException(
                    D3D11HwndVideoPresenterCapabilityStatus
                        .BgraOutputUnavailable,
                    "The D3D11 video processor cannot produce BGRA.");
            }

            VideoProcessorCaps caps =
                enumerator.VideoProcessorCaps;
            if (caps.RateConversionCapsCount == 0)
            {
                throw new VideoFormatUnavailableException(
                    D3D11HwndVideoPresenterCapabilityStatus
                        .VideoProcessorUnavailable,
                    "The D3D11 video processor exposes no rate-conversion " +
                        "capabilities.");
            }

            processor =
                videoDevice.CreateVideoProcessor(
                    enumerator,
                    rateConversionIndex: 0);
            edgeEnhancementEnabled =
                TryEnableScaledEdgeEnhancement(
                    videoContext,
                    enumerator,
                    processor,
                    options,
                    scaleMode,
                    new Size(
                        outputWidth,
                        outputHeight),
                    caps);
            backBuffer =
                swapChain.GetBuffer<ID3D11Texture2D>(0);
            var outputViewDescription =
                new VideoProcessorOutputViewDescription
                {
                    ViewDimension =
                        VideoProcessorOutputViewDimension.Texture2D,
                    Texture2D =
                        new Texture2DVideoProcessorOutputView
                        {
                            MipSlice = 0
                        }
                };
            outputView =
                videoDevice.CreateVideoProcessorOutputView(
                    backBuffer,
                    enumerator,
                    outputViewDescription);
        }
        catch
        {
            TryDispose(outputView);
            TryDispose(backBuffer);
            TryDispose(processor);
            TryDispose(enumerator);
            outputView = null!;
            backBuffer = null!;
            processor = null!;
            enumerator = null!;
            edgeEnhancementEnabled = false;
            throw;
        }
    }

    internal static void ConfigureVideoProcessor(
        ID3D11VideoContext context,
        ID3D11VideoProcessor processor,
        D3D11HwndVideoPresentationGeometry geometry)
    {
        context.VideoProcessorSetOutputTargetRect(
            processor,
            true,
            ToRawRect(
                new Rectangle(
                    Point.Empty,
                    geometry.Output)));
        context.VideoProcessorSetOutputBackgroundColor(
            processor,
            yCbCr: false,
            new VideoColor
            {
                Rgba =
                    new VideoColorRgba
                    {
                        R = 0,
                        G = 0,
                        B = 0,
                        A = 1
                    }
            });
        // DXGI flip-model BGRA buffers do not guarantee initialized alpha.
        // Force opaque output so readback can distinguish a processor-written
        // black desktop from an untouched transparent back buffer.
        context.VideoProcessorSetOutputAlphaFillMode(
            processor,
            VideoProcessorAlphaFillMode.Opaque,
            streamIndex: 0);
        context.VideoProcessorSetStreamFrameFormat(
            processor,
            streamIndex: 0,
            VideoFrameFormat.Progressive);
        context.VideoProcessorSetStreamAutoProcessingMode(
            processor,
            0,
            false);
        context.VideoProcessorSetStreamSourceRect(
            processor,
            0,
            true,
            ToRawRect(geometry.Source));
        context.VideoProcessorSetStreamDestRect(
            processor,
            0,
            true,
            ToRawRect(geometry.Destination));
    }

    internal static int? CalculateEdgeEnhancementLevel(
        int minimum,
        int maximum,
        int defaultLevel)
    {
        if (minimum > maximum ||
            defaultLevel < minimum ||
            defaultLevel > maximum ||
            defaultLevel >= maximum)
        {
            return null;
        }

        int available = maximum - defaultLevel;
        int increase = Math.Max(
            1,
            checked(
                (int)Math.Round(
                    available * 0.20,
                    MidpointRounding.AwayFromZero)));
        return Math.Clamp(
            defaultLevel + increase,
            minimum,
            maximum);
    }

    internal static bool ShouldEnableEdgeEnhancementForScaling(
        Size source,
        Size destination)
    {
        if (source.Width <= 0 ||
            source.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(source));
        }

        if (destination.Width <= 0 ||
            destination.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(destination));
        }

        return source != destination;
    }

    internal static bool ShouldRetryVideoProcessorBlt(
        bool blitFailed,
        bool edgeEnhancementEnabled,
        int completedRetryCount)
    {
        if (completedRetryCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedRetryCount));
        }

        return blitFailed &&
            edgeEnhancementEnabled &&
            completedRetryCount == 0;
    }

    private static bool TryEnableScaledEdgeEnhancement(
        ID3D11VideoContext context,
        ID3D11VideoProcessorEnumerator enumerator,
        ID3D11VideoProcessor processor,
        D3D11HwndVideoPresenterOptions options,
        D3D11HwndVideoScaleMode scaleMode,
        Size output,
        VideoProcessorCaps caps)
    {
        if (!options.EnableEdgeEnhancement || !caps.FilterCaps.HasFlag(
                VideoProcessorFilterCaps.EdgeEnhancement))
        {
            return false;
        }

        D3D11HwndVideoPresentationGeometry geometry =
            CalculateGeometry(
                new Rectangle(
                    0,
                    0,
                    options.SourceWidth,
                    options.SourceHeight),
                output,
                scaleMode);
        if (!ShouldEnableEdgeEnhancementForScaling(
                geometry.Source.Size,
                geometry.Destination.Size))
        {
            return false;
        }

        try
        {
            enumerator.GetVideoProcessorFilterRange(
                VideoProcessorFilter.EdgeEnhancement,
                out VideoProcessorFilterRange range);
            int? level = CalculateEdgeEnhancementLevel(
                range.Minimum,
                range.Maximum,
                range.Default);
            if (level is null)
            {
                return false;
            }

            context.VideoProcessorSetStreamFilter(
                processor,
                streamIndex: 0,
                VideoProcessorFilter.EdgeEnhancement,
                true,
                level.Value);
            return true;
        }
        catch
        {
            // Driver filter support is best-effort. The same zero-copy
            // presenter remains valid when a driver advertises the filter but
            // rejects its range or level at runtime.
            return false;
        }
    }

    private bool TryDisableEdgeEnhancementForRetry()
    {
        // Consume this one-shot recovery before calling into the driver. If
        // disabling the advertised filter itself fails, later frames must not
        // repeat the same potentially expensive operation.
        _edgeEnhancementEnabled = false;
        try
        {
            _videoContext.VideoProcessorSetStreamFilter(
                _processor!,
                streamIndex: 0,
                VideoProcessorFilter.EdgeEnhancement,
                false,
                level: 0);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void RefreshScaledEdgeEnhancement()
    {
        if (_enumerator is null ||
            _processor is null ||
            _outputWidth <= 0 ||
            _outputHeight <= 0)
        {
            _edgeEnhancementEnabled = false;
            return;
        }

        if (_edgeEnhancementEnabled &&
            !TryDisableEdgeEnhancementForRetry())
        {
            return;
        }

        try
        {
            _edgeEnhancementEnabled =
                TryEnableScaledEdgeEnhancement(
                    _videoContext,
                    _enumerator,
                    _processor,
                    _options,
                    _scaleMode,
                    new Size(
                        _outputWidth,
                        _outputHeight),
                    _enumerator.VideoProcessorCaps);
        }
        catch
        {
            _edgeEnhancementEnabled = false;
        }
    }

    private static string? ValidateBorrowedTexture(
        Texture2DDescription description,
        uint subresourceIndex,
        Rectangle visibleSource)
    {
        if (description.Format != Format.NV12)
        {
            return $"Expected NV12 but received " +
                $"{description.Format}.";
        }

        uint mipLevels =
            Math.Max(1u, description.MipLevels);
        uint arraySize =
            Math.Max(1u, description.ArraySize);
        ulong subresourceCount =
            (ulong)mipLevels * arraySize;
        if (subresourceIndex >= subresourceCount)
        {
            return "The texture subresource index is out of range.";
        }

        if (visibleSource.X < 0 ||
            visibleSource.Y < 0 ||
            visibleSource.Width <= 0 ||
            visibleSource.Height <= 0 ||
            (long)visibleSource.Right > description.Width ||
            (long)visibleSource.Bottom > description.Height)
        {
            return "The visible source rectangle is outside the texture.";
        }

        if ((visibleSource.X & 1) != 0 ||
            (visibleSource.Y & 1) != 0 ||
            (visibleSource.Width & 1) != 0 ||
            (visibleSource.Height & 1) != 0)
        {
            return "NV12 source coordinates and dimensions must be even.";
        }

        return null;
    }

    private static Rectangle CalculateFitDestination(
        Size source,
        Size output,
        bool allowUpscaling)
    {
        if (!allowUpscaling &&
            source.Width <= output.Width &&
            source.Height <= output.Height)
        {
            return new(
                (output.Width - source.Width) / 2,
                (output.Height - source.Height) / 2,
                source.Width,
                source.Height);
        }

        int width;
        int height;
        if ((long)output.Width * source.Height <=
            (long)output.Height * source.Width)
        {
            width = output.Width;
            height = Math.Max(
                1,
                checked(
                    (int)Math.Round(
                        (double)source.Height * output.Width /
                            source.Width,
                        MidpointRounding.AwayFromZero)));
        }
        else
        {
            height = output.Height;
            width = Math.Max(
                1,
                checked(
                    (int)Math.Round(
                        (double)source.Width * output.Height /
                            source.Height,
                        MidpointRounding.AwayFromZero)));
        }

        width = Math.Min(width, output.Width);
        height = Math.Min(height, output.Height);
        return new(
            (output.Width - width) / 2,
            (output.Height - height) / 2,
            width,
            height);
    }

    private static Rectangle CalculateFillSource(
        Rectangle source,
        Size output)
    {
        if ((long)source.Width * output.Height >
            (long)source.Height * output.Width)
        {
            int croppedWidth =
                AlignDownToEven(
                    Math.Max(
                        2,
                        checked(
                            (int)((long)source.Height *
                                output.Width /
                                output.Height))));
            croppedWidth =
                Math.Min(croppedWidth, source.Width);
            int left =
                AlignDownToEven(
                    source.Left +
                        ((source.Width - croppedWidth) / 2));
            left = Math.Clamp(
                left,
                source.Left,
                source.Right - croppedWidth);
            return new(
                left,
                source.Top,
                croppedWidth,
                source.Height);
        }

        if ((long)source.Width * output.Height <
            (long)source.Height * output.Width)
        {
            int croppedHeight =
                AlignDownToEven(
                    Math.Max(
                        2,
                        checked(
                            (int)((long)source.Width *
                                output.Height /
                                output.Width))));
            croppedHeight =
                Math.Min(croppedHeight, source.Height);
            int top =
                AlignDownToEven(
                    source.Top +
                        ((source.Height - croppedHeight) / 2));
            top = Math.Clamp(
                top,
                source.Top,
                source.Bottom - croppedHeight);
            return new(
                source.Left,
                top,
                source.Width,
                croppedHeight);
        }

        return source;
    }

    private D3D11HwndVideoPresenterResult CreateFailureResult(
        int hResult,
        string detail)
    {
        int? deviceRemovedReason =
            TryGetDeviceRemovedReason();
        D3D11HwndVideoPresenterStatus status =
            ClassifyFailure(
                hResult,
                deviceRemovedReason);
        return new(
            status,
            hResult,
            deviceRemovedReason,
            detail);
    }

    private int? TryGetDeviceRemovedReason()
    {
        try
        {
            Result result =
                _device.DeviceRemovedReason;
            return result.Failure
                ? result.Code
                : null;
        }
        catch
        {
            return null;
        }
    }

    private void ReleaseVideoProcessorResources()
    {
        ReleaseNativeDetailResources();
        TryDispose(_experimentalUpscaler);
        _experimentalUpscaler = null;
        ExperimentalUpscalingActive = false;
        _validationSourceReady = false;
        TryDispose(_validationReadback);
        _validationReadback = null;
        TryDispose(_validationSource);
        _validationSource = null;
        _edgeEnhancementEnabled = false;
        TryDispose(_outputView);
        _outputView = null;
        TryDispose(_backBuffer);
        _backBuffer = null;
        TryDispose(_processor);
        _processor = null;
        TryDispose(_enumerator);
        _enumerator = null;
    }

    private void TryRestoreVideoProcessorResources(
        int width,
        int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        try
        {
            CreateVideoProcessorResources(
                _videoDevice,
                _videoContext,
                _swapChain,
                _options,
                _scaleMode,
                width,
                height,
                out ID3D11VideoProcessorEnumerator enumerator,
                out ID3D11VideoProcessor processor,
                out ID3D11Texture2D backBuffer,
                out ID3D11VideoProcessorOutputView outputView,
                out bool edgeEnhancementEnabled);
            _enumerator = enumerator;
            _processor = processor;
            _backBuffer = backBuffer;
            _outputView = outputView;
            _edgeEnhancementEnabled = edgeEnhancementEnabled;
            _outputWidth = width;
            _outputHeight = height;
        }
        catch
        {
            // Keep the original ResizeBuffers failure as the operation
            // result. A caller can recreate the presenter if restoration
            // also fails.
        }
    }

    private static void EnableD3D11MultithreadProtection(
        ID3D11DeviceContext context)
    {
        using ID3D11Multithread multithread =
            context.QueryInterface<ID3D11Multithread>();
        multithread.SetMultithreadProtected(true);
    }

    private static RawRect ToRawRect(
        Rectangle rectangle) =>
        new(
            rectangle.Left,
            rectangle.Top,
            rectangle.Right,
            rectangle.Bottom);

    private static int AlignDownToEven(int value) =>
        value & ~1;

    private static bool IsDeviceLossCode(
        int? hResult) =>
        hResult is
            DxgiErrorDeviceRemoved or
            DxgiErrorDeviceReset or
            DxgiErrorDeviceHung or
            DxgiErrorDriverInternalError;

    private static D3D11HwndVideoPresenterResult CreateResult(
        D3D11HwndVideoPresenterStatus status,
        int hResult = 0,
        int? deviceRemovedReason = null,
        string detail = "") =>
        new(
            status,
            hResult,
            deviceRemovedReason,
            detail);

    private static bool TryGetClientSize(
        nint windowHandle,
        out int width,
        out int height)
    {
        if (!GetClientRect(
            windowHandle,
            out NativeRectangle rectangle))
        {
            width = 0;
            height = 0;
            return false;
        }

        width =
            Math.Max(
                0,
                rectangle.Right - rectangle.Left);
        height =
            Math.Max(
                0,
                rectangle.Bottom - rectangle.Top);
        return true;
    }

    private static string FormatFailure(
        Exception exception) =>
        $"{exception.GetType().Name}: {exception.Message}";

    private static void TryDispose(
        IDisposable? disposable)
    {
        if (disposable is null)
        {
            return;
        }

        try
        {
            disposable.Dispose();
        }
        catch
        {
            // Creation failure and teardown must preserve the software
            // presentation fallback.
        }
    }

    private static void TryCloseHandle(
        ref nint handle)
    {
        nint ownedHandle = handle;
        handle = nint.Zero;
        if (ownedHandle == nint.Zero)
        {
            return;
        }

        try
        {
            _ = CloseHandle(ownedHandle);
        }
        catch
        {
            // Teardown must preserve the software presentation fallback.
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(
        nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(
        nint windowHandle,
        out NativeRectangle rectangle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(
        nint handle,
        uint milliseconds);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(
        nint handle);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeRectangle
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;
    }

    private sealed class VideoFormatUnavailableException :
        Exception
    {
        internal VideoFormatUnavailableException(
            D3D11HwndVideoPresenterCapabilityStatus status,
            string message)
            : base(message)
        {
            Status = status;
        }

        internal D3D11HwndVideoPresenterCapabilityStatus Status
        {
            get;
        }
    }
}
