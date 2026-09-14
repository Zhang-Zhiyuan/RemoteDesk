using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace RemoteDesk;

internal sealed record MediaFoundationD3D11H264EncoderOptions(
    Size NativeSize, Size OutputSize, int FramesPerSecond, int BitrateBitsPerSecond)
{
    internal string? Validate()
    {
        if (NativeSize.Width is < 48 or > 8192 || NativeSize.Height is < 48 or > 8192 ||
            (long)NativeSize.Width * NativeSize.Height > 16_777_216)
            return "Invalid native surface dimensions.";
        if (OutputSize.Width is < 48 or > 4096 || OutputSize.Height is < 48 or > 2304 ||
            (OutputSize.Width & 1) != 0 || (OutputSize.Height & 1) != 0 ||
            OutputSize.Width > NativeSize.Width || OutputSize.Height > NativeSize.Height)
            return "Requires an even, non-upscaled H.264 output size.";
        if (FramesPerSecond is < 1 or > 120 || BitrateBitsPerSecond is < 100_000 or > 200_000_000)
            return "Invalid encoder rate.";
        return null;
    }
}

internal sealed record HardwareEncodedSourceFrame(
    long SourceTime100Nanoseconds, Size OutputSize, byte[] AnnexBBytes,
    long SubmittedAtTimestamp, long CompletedAtTimestamp);

/// <summary>
/// Single-owner, single-in-flight GPU encoder. The caller supplies an immutable
/// native BGRA surface, so native detail readback and the base video can refer
/// to the SAME source, before scaling and 4:2:0 conversion. No CPU readback,
/// capture, thread, frame queue, sleep, drain or wait is introduced here.
///
/// Creation is intentionally separate from frame submission. Callers must keep
/// their current capture path while probing and must explicitly elect this
/// backend only after startup/latency validation. Existing FFmpeg selection is
/// unchanged; merely constructing this component does not advertise support.
/// </summary>
internal sealed class MediaFoundationD3D11H264Encoder : IDisposable
{
    private static readonly Guid LowLatency = new("9C27891A-ED7A-40E1-88E8-B22727A024EE");
    private static readonly Guid GopSize = new("95F31B26-95A4-41AA-9303-246A7FC6EEF1");
    private const int MaximumOutputBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan OutputTimeout = TimeSpan.FromSeconds(2);
    private readonly MediaFoundationD3D11H264EncoderOptions _options;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly ID3D11Multithread _multithread;
    private readonly IMFDXGIDeviceManager _manager;
    private readonly IMFActivate _activation;
    private readonly IMFTransform _transform;
    private readonly IMFMediaEventGenerator _events;
    private readonly ID3D11VideoProcessorEnumerator _enumerator;
    private readonly ID3D11VideoProcessor _processor;
    private int _inputCredit;
    private long? _pendingSourceTime;
    private long _pendingStartedAt;
    private long _lastSourceTime = -1;
    private HardwareEncodedSourceFrame? _ready;
    private bool _disposed;
    private bool _failed;

    private MediaFoundationD3D11H264Encoder(MediaFoundationD3D11H264EncoderOptions options,
        ID3D11Device device, ID3D11DeviceContext context, ID3D11VideoDevice videoDevice,
        ID3D11VideoContext videoContext, ID3D11Multithread multithread,
        IMFDXGIDeviceManager manager, IMFActivate activation, IMFTransform transform,
        IMFMediaEventGenerator events, ID3D11VideoProcessorEnumerator enumerator,
        ID3D11VideoProcessor processor, string name)
    {
        _options = options; _device = device; _context = context;
        _videoDevice = videoDevice; _videoContext = videoContext; _multithread = multithread;
        _manager = manager; _activation = activation; _transform = transform;
        _events = events; _enumerator = enumerator; _processor = processor; Name = name;
    }

    public string Name { get; }
    public string? Failure { get; private set; }
    public bool IsFailed => _failed;
    public bool HasInFlightFrame => _pendingSourceTime.HasValue;

    public static bool TryCreate(ID3D11Device borrowedDevice,
        MediaFoundationD3D11H264EncoderOptions options,
        out MediaFoundationD3D11H264Encoder? encoder, out string? failure)
    {
        ArgumentNullException.ThrowIfNull(borrowedDevice);
        ArgumentNullException.ThrowIfNull(options);
        encoder = null;
        failure = options.Validate();
        if (failure is not null) return false;
        var owned = new List<IDisposable>();
        bool started = false;
        IMFActivate? selected = null;
        IMFTransform? transform = null;
        try
        {
            MediaFactory.MFStartup().CheckError(); started = true;
            T Own<T>(T value) where T : IDisposable { owned.Add(value); return value; }
            ID3D11Device device = Own(borrowedDevice.QueryInterface<ID3D11Device>());
            ID3D11DeviceContext context = Own(device.ImmediateContext);
            ID3D11Multithread multithread = Own(context.QueryInterface<ID3D11Multithread>());
            multithread.SetMultithreadProtected(true);
            ID3D11VideoDevice videoDevice = Own(device.QueryInterface<ID3D11VideoDevice>());
            ID3D11VideoContext videoContext = Own(context.QueryInterface<ID3D11VideoContext>());
            IMFDXGIDeviceManager manager = Own(MediaFactory.MFCreateDXGIDeviceManager());
            manager.ResetDevice(device).CheckError();
            ID3D11VideoProcessorEnumerator enumerator = Own(videoDevice.CreateVideoProcessorEnumerator(
                new VideoProcessorContentDescription
                {
                    InputFrameFormat = VideoFrameFormat.Progressive,
                    InputFrameRate = new Rational((uint)options.FramesPerSecond, 1),
                    InputWidth = (uint)options.NativeSize.Width, InputHeight = (uint)options.NativeSize.Height,
                    OutputFrameRate = new Rational((uint)options.FramesPerSecond, 1),
                    OutputWidth = (uint)options.OutputSize.Width, OutputHeight = (uint)options.OutputSize.Height,
                    Usage = VideoUsage.PlaybackNormal
                }));
            if (!enumerator.CheckVideoProcessorFormat(Format.B8G8R8A8_UNorm).HasFlag(VideoProcessorFormatSupport.Input) ||
                !enumerator.CheckVideoProcessorFormat(Format.NV12).HasFlag(VideoProcessorFormatSupport.Output))
                throw new NotSupportedException("The GPU cannot convert native BGRA to NV12.");
            ID3D11VideoProcessor processor = Own(videoDevice.CreateVideoProcessor(enumerator, 0));
            videoContext.VideoProcessorSetStreamFrameFormat(processor, 0, VideoFrameFormat.Progressive);
            videoContext.VideoProcessorSetStreamAutoProcessingMode(processor, 0, false);
            videoContext.VideoProcessorSetStreamSourceRect(processor, 0, true,
                new RawRect(0, 0, options.NativeSize.Width, options.NativeSize.Height));
            var target = new RawRect(0, 0, options.OutputSize.Width, options.OutputSize.Height);
            videoContext.VideoProcessorSetStreamDestRect(processor, 0, true, target);
            videoContext.VideoProcessorSetOutputTargetRect(processor, true, target);

            // Enumeration can contain stale vendor registrations. A failed
            // activation is not permission to silently substitute CPU encoding.
            var reasons = new List<string>();
            string name = string.Empty;
            using (IMFActivateCollection activations = MediaFactory.MFTEnumEx(
                TransformCategoryGuids.VideoEncoder,
                (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter),
                new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 },
                new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 }))
            {
                foreach (IMFActivate activation in activations)
                {
                    if (selected is not null) { activation.Dispose(); continue; }
                    IMFTransform? candidate = null;
                    bool adopted = false;
                    try
                    {
                        name = activation.GetString(TransformAttributeKeys.MftFriendlyNameAttribute);
                        candidate = activation.ActivateObject<IMFTransform>();
                        ConfigureTransform(candidate, manager, options);
                        Own(activation); Own(candidate);
                        selected = activation;
                        transform = candidate;
                        adopted = true;
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        reasons.Add($"{name}: {ex.GetType().Name} (0x{ex.HResult:X8})");
                    }
                    finally
                    {
                        if (!adopted)
                        {
                            candidate?.Dispose();
                            try { activation.ShutdownObject(); } catch { }
                            activation.Dispose();
                        }
                    }
                }
            }
            if (transform is null || selected is null)
                throw new NotSupportedException("No compatible hardware encoder. " + string.Join("; ", reasons));
            IMFMediaEventGenerator events = Own(transform.QueryInterface<IMFMediaEventGenerator>());
            transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
            encoder = new(options, device, context, videoDevice, videoContext, multithread,
                manager, selected, transform, events, enumerator, processor, name);
            owned.Clear(); started = false;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failure = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
        finally
        {
            if (encoder is null)
            {
                try { selected?.ShutdownObject(); } catch { }
                for (int i = owned.Count - 1; i >= 0; i--) TryDispose(owned[i]);
                if (started) { try { MediaFactory.MFShutdown(); } catch { } }
            }
        }
    }

    // false without IsFailed means ordinary backpressure: retain/drop only the
    // caller's newest base frame; never build another input queue here.
    public bool TrySubmit(ID3D11Texture2D immutableNativeTexture, long sourceTime100Nanoseconds)
    {
        if (_disposed || _failed) return false;
        if (sourceTime100Nanoseconds < 0 || sourceTime100Nanoseconds <= _lastSourceTime)
            throw new ArgumentOutOfRangeException(nameof(sourceTime100Nanoseconds));
        Pump();
        if (_failed || _pendingSourceTime.HasValue || _ready is not null || _inputCredit == 0) return false;
        Texture2DDescription description = immutableNativeTexture.Description;
        if (description.Width != _options.NativeSize.Width || description.Height != _options.NativeSize.Height ||
            description.Format != Format.B8G8R8A8_UNorm || description.ArraySize != 1 ||
            description.MipLevels != 1 || description.SampleDescription.Count != 1)
            throw new ArgumentException("Expected one native BGRA surface.", nameof(immutableNativeTexture));
        // Vortice caches this wrapper on the texture; it is borrowed, not a
        // temporary lease. Disposing it breaks subsequent fan-out consumers.
        ID3D11Device sourceDevice = immutableNativeTexture.Device;
        if (sourceDevice.NativePointer != _device.NativePointer)
            throw new ArgumentException("Source texture belongs to another device.", nameof(immutableNativeTexture));
        try
        {
            long started = Stopwatch.GetTimestamp();
            // Do not recycle this NV12 storage before a driver releases its
            // sample. The sample/texture COM ownership outlives these wrappers;
            // a stalled driver can retain at most the single submitted input.
            using ID3D11Texture2D nv12 = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)_options.OutputSize.Width, Height = (uint)_options.OutputSize.Height,
                Format = Format.NV12, MipLevels = 1, ArraySize = 1,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget
            });
            using ID3D11VideoProcessorInputView inputView = _videoDevice.CreateVideoProcessorInputView(
                immutableNativeTexture, _enumerator, new VideoProcessorInputViewDescription
                {
                    ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                    Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 }
                });
            using ID3D11VideoProcessorOutputView outputView = _videoDevice.CreateVideoProcessorOutputView(
                nv12, _enumerator, new VideoProcessorOutputViewDescription
                {
                    ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                    Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 }
                });
            _multithread.Enter();
            try
            {
                _videoContext.VideoProcessorBlt(_processor, outputView, 0,
                    [new VideoProcessorStream { Enable = true, InputSurface = inputView }]).CheckError();
            }
            finally { _multithread.Leave(); }
            using IMFSample sample = MediaFactory.MFCreateSample();
            using IMFMediaBuffer buffer = MediaFactory.MFCreateDXGISurfaceBuffer(
                typeof(ID3D11Texture2D).GUID, nv12, 0, false);
            sample.AddBuffer(buffer);
            sample.SampleTime = sourceTime100Nanoseconds;
            sample.SampleDuration = 10_000_000L / _options.FramesPerSecond;
            _transform.ProcessInput(0, sample, 0);
            _pendingSourceTime = sourceTime100Nanoseconds;
            _lastSourceTime = sourceTime100Nanoseconds;
            _pendingStartedAt = started;
            _inputCredit--;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Fail(ex); return false; }
    }

    public HardwareEncodedSourceFrame? TryRead()
    {
        if (_disposed || _failed) return null;
        Pump();
        HardwareEncodedSourceFrame? ready = _ready;
        _ready = null;
        return ready;
    }

    private void Pump()
    {
        try
        {
            if (_pendingSourceTime.HasValue && Stopwatch.GetElapsedTime(_pendingStartedAt) > OutputTimeout)
                throw new TimeoutException("Hardware encoder withheld a source frame; no drain or second input is allowed.");
            for (int i = 0; i < 16 && _ready is null; i++)
            {
                IMFMediaEvent mediaEvent;
                try { mediaEvent = _events.GetEvent(1); } // MF_EVENT_FLAG_NO_WAIT
                catch (SharpGenException ex) when (ex.ResultCode == Vortice.MediaFoundation.ResultCode.NoEventsAvailable) { return; }
                using (mediaEvent)
                {
                    mediaEvent.Status.CheckError();
                    switch (mediaEvent.EventType)
                    {
                        case MediaEventTypes.TransformNeedInput:
                            if (++_inputCredit > 8) throw new InvalidDataException("Unbounded input credit from encoder.");
                            break;
                        case MediaEventTypes.TransformHaveOutput:
                            ReadOutput();
                            return;
                        default:
                            throw new InvalidDataException($"Unexpected hardware encoder event: {mediaEvent.EventType}.");
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Fail(ex); }
    }

    private void ReadOutput()
    {
        var output = new OutputDataBuffer { StreamID = 0 };
        try
        {
            _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref output, out _).CheckError();
            IMFSample sample = output.Sample ?? throw new InvalidDataException("Missing output sample.");
            if (!_pendingSourceTime.HasValue || sample.SampleTime != _pendingSourceTime.Value)
                throw new InvalidDataException("Hardware encoder changed or reordered the source identifier.");
            using IMFMediaBuffer buffer = sample.ConvertToContiguousBuffer();
            buffer.Lock(out nint pointer, out _, out int length);
            byte[] bytes;
            try
            {
                if (length is <= 0 or > MaximumOutputBytes) throw new InvalidDataException("Invalid H.264 sample length.");
                bytes = new byte[length];
                Marshal.Copy(pointer, bytes, 0, length);
            }
            finally { buffer.Unlock(); }
            // Capabilities and accepted properties are not enough: verify every
            // output is independently decodable and retains the explicit PTS.
            if (!MediaFoundationD3D11H264Decoder.InspectAccessUnit(bytes, MaximumOutputBytes).IsIndependentFrame)
                throw new InvalidDataException("Hardware encoder violated the SPS/PPS/IDR contract.");
            _ready = new(_pendingSourceTime.Value, _options.OutputSize, bytes, _pendingStartedAt, Stopwatch.GetTimestamp());
            _pendingSourceTime = null;
        }
        finally { output.Events?.Dispose(); output.Sample?.Dispose(); }
    }

    private static void ConfigureTransform(IMFTransform transform, IMFDXGIDeviceManager manager,
        MediaFoundationD3D11H264EncoderOptions options)
    {
        using (IMFAttributes attributes = transform.Attributes)
        {
            if (attributes.GetUInt32(TransformAttributeKeys.TransformAsync) != 1 ||
                attributes.GetUInt32(TransformAttributeKeys.D3D11Aware) != 1)
                throw new NotSupportedException("Hardware MFT is not asynchronous and D3D11-aware.");
            attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u).CheckError();
            attributes.Set(SinkWriterAttributeKeys.LowLatency, 1u).CheckError();
        }
        SetCodecOption(transform, LowLatency, true);
        SetCodecOption(transform, GopSize, 1u);
        transform.ProcessMessage(TMessageType.MessageSetD3DManager,
            new UIntPtr(unchecked((ulong)manager.NativePointer.ToInt64())));
        using (IMFMediaType output = CreateType(options, VideoFormatGuids.H264))
        {
            output.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)options.BitrateBitsPerSecond).CheckError();
            output.Set(MediaTypeAttributeKeys.Mpeg2Profile, 100u).CheckError();
            transform.SetOutputType(0, output, 0);
        }
        using (IMFMediaType input = CreateType(options, VideoFormatGuids.NV12)) transform.SetInputType(0, input, 0);
        // Sample allocation is driver-owned in an asynchronous hardware MFT.
        if ((((OutputStreamInfoFlags)transform.GetOutputStreamInfo(0).Flags) &
            OutputStreamInfoFlags.OutputStreamProvidesSamples) == 0)
            throw new NotSupportedException("Hardware encoder does not provide its own output samples.");
    }

    private static IMFMediaType CreateType(MediaFoundationD3D11H264EncoderOptions options, Guid subtype)
    {
        IMFMediaType type = MediaFactory.MFCreateMediaType();
        try
        {
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
            type.Set(MediaTypeAttributeKeys.Subtype, subtype).CheckError();
            type.Set(MediaTypeAttributeKeys.FrameSize, ((ulong)options.OutputSize.Width << 32) | (uint)options.OutputSize.Height).CheckError();
            type.Set(MediaTypeAttributeKeys.FrameRate, ((ulong)options.FramesPerSecond << 32) | 1).CheckError();
            type.Set(MediaTypeAttributeKeys.PixelAspectRatio, (1ul << 32) | 1).CheckError();
            type.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive).CheckError();
            return type;
        }
        catch { type.Dispose(); throw; }
    }

    private static void SetCodecOption(IMFTransform transform, Guid key, object value)
    {
        var codec = (ICodecApi)Marshal.GetTypedObjectForIUnknown(transform.NativePointer, typeof(ICodecApi));
        try { Marshal.ThrowExceptionForHR(codec.SetValue(ref key, ref value)); }
        finally { Marshal.ReleaseComObject(codec); }
    }

    private void Fail(Exception ex)
    {
        _failed = true; _ready = null; _pendingSourceTime = null;
        Failure = $"{ex.GetType().Name}: {ex.Message}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _ready = null; _pendingSourceTime = null;
        try { _transform.ProcessMessage(TMessageType.MessageCommandFlush, UIntPtr.Zero); } catch { }
        try { _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero); } catch { }
        TryDispose(_events); TryDispose(_transform);
        try { _activation.ShutdownObject(); } catch { }
        TryDispose(_activation); TryDispose(_manager);
        TryDispose(_processor); TryDispose(_enumerator);
        TryDispose(_videoContext); TryDispose(_videoDevice); TryDispose(_multithread);
        TryDispose(_context); TryDispose(_device);
        try { MediaFactory.MFShutdown(); } catch { }
    }

    private static void TryDispose(IDisposable value) { try { value.Dispose(); } catch { } }
}
