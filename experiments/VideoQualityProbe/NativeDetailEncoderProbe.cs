using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using RemoteDesk;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

// Capability and timing experiment, NOT a production fallback. No desktop
// capture, input injection, network, or installed settings are used here.
// Establishes whether a GPU surface can preserve a source identifier through
// a single-in-flight, hardware-only encode/decode round trip without a drain.
internal static class NativeDetailEncoderProbe
{
    private static readonly Guid LowLatency = new("9C27891A-ED7A-40E1-88E8-B22727A024EE");
    private static readonly Guid GopSize = new("95F31B26-95A4-41AA-9303-246A7FC6EEF1");
    private static readonly Guid BPictureCount = new("8D390AAC-DC5C-4200-B57F-814D04BABAB2");

    public static void Run(string outputDirectory)
    {
        string output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output)) throw new IOException("Use a new output directory.");
        Directory.CreateDirectory(output);
        var candidates = new List<object>();
        bool passed = false;
        MediaFactory.MFStartup().CheckError();
        try
        {
            using ID3D11Device device = D3D11.D3D11CreateDevice(
                DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0]);
            using ID3D11DeviceContext context = device.ImmediateContext;
            using ID3D11Multithread multithread = context.QueryInterface<ID3D11Multithread>();
            multithread.SetMultithreadProtected(true);
            using IMFDXGIDeviceManager manager = MediaFactory.MFCreateDXGIDeviceManager();
            manager.ResetDevice(device).CheckError();
            using IMFActivateCollection activations = MediaFactory.MFTEnumEx(
                TransformCategoryGuids.VideoEncoder,
                (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter),
                new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 },
                new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 });
            foreach (IMFActivate activation in activations)
            {
                using (activation)
                {
                    string name = activation.GetString(TransformAttributeKeys.MftFriendlyNameAttribute);
                    Console.WriteLine($"Hardware candidate: {name}");
                    try
                    {
                        using IMFTransform transform = activation.ActivateObject<IMFTransform>();
                        candidates.Add(Exercise(name, transform, device, context, manager));
                        passed = true;
                    }
                    catch (Exception ex)
                    {
                        candidates.Add(new { name, passed = false, failure = ex.ToString() });
                        Console.WriteLine($"Candidate rejected: {ex.GetType().Name}: {ex.Message}");
                    }
                    finally
                    {
                        activation.ShutdownObject();
                    }
                }
            }
        }
        finally
        {
            MediaFactory.MFShutdown().CheckError();
            File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new
            {
                passed,
                scope = "Synthetic NV12 GPU surfaces; hardware-only MFT; one input in flight; no drain/readback/FFmpeg.",
                isProductionCaptureOrEndToEndLatency = false,
                candidates
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        if (!passed) throw new InvalidOperationException("No hardware encoder met the single-frame source-correlation contract. See summary.json.");
    }

    private static object Exercise(string name, IMFTransform transform, ID3D11Device device,
        ID3D11DeviceContext context, IMFDXGIDeviceManager manager)
    {
        const int width = 1920, height = 1080, count = 120, warmup = 10;
        using (IMFAttributes attributes = transform.Attributes)
        {
            if (attributes.GetUInt32(TransformAttributeKeys.TransformAsync) != 1 ||
                attributes.GetUInt32(TransformAttributeKeys.D3D11Aware) != 1)
                throw new InvalidOperationException("Requires asynchronous, D3D11-aware hardware MFT.");
            attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u).CheckError();
            attributes.Set(SinkWriterAttributeKeys.LowLatency, 1u).CheckError();
        }
        SetCodecOption(transform, LowLatency, true);
        SetCodecOption(transform, GopSize, 1u);
        transform.ProcessMessage(TMessageType.MessageSetD3DManager,
            new UIntPtr(unchecked((ulong)manager.NativePointer.ToInt64())));
        using (IMFMediaType outputType = CreateType(width, height, VideoFormatGuids.H264))
        {
            outputType.Set(MediaTypeAttributeKeys.AvgBitrate, 12_000_000u).CheckError();
            outputType.Set(MediaTypeAttributeKeys.Mpeg2Profile, 100u).CheckError();
            transform.SetOutputType(0, outputType, 0);
        }
        using (IMFMediaType inputType = CreateType(width, height, VideoFormatGuids.NV12))
            transform.SetInputType(0, inputType, 0);
        // Some NVIDIA MFT versions reject this optional property even for
        // all-IDR operation. Record that fact; verify actual output instead.
        bool bPictureOptionAccepted = true;
        try { SetCodecOption(transform, BPictureCount, 0u); }
        catch (ArgumentException) { bPictureOptionAccepted = false; }

        // Four distinct immutable GPU surfaces; never overwrite a texture while
        // an MFT holds a reference. Later probes can fan out the *same* source
        // texture to a video processor and bounded native-region readback.
        var textures = new List<ID3D11Texture2D>();
        using IMFMediaEventGenerator events = transform.QueryInterface<IMFMediaEventGenerator>();
        using var pacingWaiter = new WindowsHighResolutionPacingWaiter();
        var measurements = new List<object>();
        var latency = new List<double>();
        int inputCredit = 0;
        try
        {
            for (int pattern = 0; pattern < 4; pattern++)
            {
                byte[] nv12 = new byte[width * height * 3 / 2];
                nv12.AsSpan(0, width * height).Fill((byte)(48 + 40 * pattern));
                nv12.AsSpan(width * height).Fill(128);
                ID3D11Texture2D texture = device.CreateTexture2D(new Texture2DDescription
                {
                    Width = width, Height = height, MipLevels = 1, ArraySize = 1,
                    Format = Format.NV12, SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget
                });
                context.UpdateSubresource(nv12, texture, 0, width);
                textures.Add(texture);
            }
            if (!MediaFoundationD3D11H264Decoder.TryCreate(
                new(width, height, 60, AllSamplesIndependent: true), out var decoder, out var capability))
                throw new InvalidOperationException(capability.Detail);
            using (decoder)
            {
                transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
                transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
                for (int index = 0; index < count + warmup; index++)
                {
                    // Includes gaps, so a made-up frame counter cannot pass as
                    // preservation of the actual caller-owned source identity.
                    long pts = 5_000_000L + index * 233_333L;
                    var wait = Stopwatch.StartNew();
                    while (inputCredit == 0)
                    {
                        if (wait.ElapsedMilliseconds > 2000) throw new TimeoutException("Encoder did not request input.");
                        using IMFMediaEvent? mediaEvent = Poll(events);
                        if (mediaEvent is null) { pacingWaiter.Wait(TimeSpan.FromMilliseconds(.2), CancellationToken.None); continue; }
                        if (mediaEvent.EventType != MediaEventTypes.TransformNeedInput)
                            throw new InvalidDataException($"Unexpected event before input: {mediaEvent.EventType}");
                        inputCredit++;
                    }
                    using IMFSample input = MediaFactory.MFCreateSample();
                    using IMFMediaBuffer surface = MediaFactory.MFCreateDXGISurfaceBuffer(
                        typeof(ID3D11Texture2D).GUID, textures[index % textures.Count], 0, false);
                    input.AddBuffer(surface);
                    input.SampleTime = pts;
                    input.SampleDuration = 166_667;
                    long started = Stopwatch.GetTimestamp();
                    transform.ProcessInput(0, input, 0);
                    double submitMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    inputCredit--;
                    // Bounded probe waiting, never a production capture loop.
                    // Crucially, no second frame and no drain is supplied to
                    // coax out the current frame.
                    while (true)
                    {
                        if (Stopwatch.GetElapsedTime(started).TotalMilliseconds > 2000)
                            throw new TimeoutException("One input did not produce output without another frame/drain.");
                        using IMFMediaEvent? mediaEvent = Poll(events);
                        if (mediaEvent is null) { pacingWaiter.Wait(TimeSpan.FromMilliseconds(.2), CancellationToken.None); continue; }
                        MediaEventTypes kind = mediaEvent.EventType;
                        if (kind == MediaEventTypes.TransformNeedInput) { inputCredit++; continue; }
                        if (kind != MediaEventTypes.TransformHaveOutput)
                            throw new InvalidDataException($"Unexpected encode event: {kind}");
                        break;
                    }
                    var output = new OutputDataBuffer { StreamID = 0 };
                    try
                    {
                        transform.ProcessOutput(ProcessOutputFlags.None, 1, ref output, out _).CheckError();
                        IMFSample encoded = output.Sample ?? throw new InvalidDataException("No output sample.");
                        long encodedPts = encoded.SampleTime;
                        if (encodedPts != pts) throw new InvalidDataException($"Source PTS changed: {pts} -> {encodedPts}.");
                        byte[] bytes = Read(encoded);
                        var info = MediaFoundationD3D11H264Decoder.InspectAccessUnit(bytes, 8 * 1024 * 1024);
                        if (!info.IsIndependentFrame) throw new InvalidDataException("Encoder did not produce complete SPS/PPS/IDR per frame.");
                        double encodedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                        if (!decoder.TryDecodeAccessUnit(bytes, encodedPts, out var decoded, out string? failure))
                            throw new InvalidDataException("Decode failed: " + failure);
                        using (decoded)
                        {
                            if (decoded.SampleTime100Nanoseconds != pts)
                                throw new InvalidDataException("Decoder did not preserve source PTS.");
                        }
                        if (index >= warmup)
                        {
                            latency.Add(encodedMs);
                            measurements.Add(new { index, pts, bytes = bytes.Length, submitMs, encodedMs });
                        }
                    }
                    finally
                    {
                        output.Events?.Dispose();
                        output.Sample?.Dispose();
                    }
                }
            }
        }
        finally
        {
            try { transform.ProcessMessage(TMessageType.MessageCommandFlush, UIntPtr.Zero); } catch { }
            try { transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero); } catch { }
            foreach (var texture in textures) texture.Dispose();
        }
        latency.Sort();
        Console.WriteLine($"{name}: {count} exact source identifiers; encode median {latency[count / 2]:F3} ms.");
        return new { name, passed = true, frames = count, warmup, maxInFlight = 1, bPictureOptionAccepted,
            medianEncodeMs = latency[count / 2], p95EncodeMs = latency[(int)(count * .95)], measurements };
    }

    private static IMFMediaEvent? Poll(IMFMediaEventGenerator events)
    {
        try { return events.GetEvent(1); } // MF_EVENT_FLAG_NO_WAIT
        catch (SharpGenException ex) when (ex.ResultCode == Vortice.MediaFoundation.ResultCode.NoEventsAvailable) { return null; }
    }

    private static IMFMediaType CreateType(int width, int height, Guid subtype)
    {
        IMFMediaType type = MediaFactory.MFCreateMediaType();
        try
        {
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
            type.Set(MediaTypeAttributeKeys.Subtype, subtype).CheckError();
            type.Set(MediaTypeAttributeKeys.FrameSize, ((ulong)width << 32) | (uint)height).CheckError();
            type.Set(MediaTypeAttributeKeys.FrameRate, (60ul << 32) | 1).CheckError();
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

    private static byte[] Read(IMFSample sample)
    {
        using IMFMediaBuffer buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out nint pointer, out _, out int length);
        try
        {
            if (length is <= 0 or > 8 * 1024 * 1024) throw new InvalidDataException("Invalid encoded size.");
            byte[] bytes = new byte[length];
            Marshal.Copy(pointer, bytes, 0, length);
            return bytes;
        }
        finally { buffer.Unlock(); }
    }
}
