using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace RemoteDesk;

internal enum FfmpegH264DecodeStatus
{
    Frame,
    Failed
}

internal sealed record FfmpegH264DecodeResult(
    FfmpegH264DecodeStatus Status,
    long SubmissionId,
    Bitmap? Bitmap)
{
    public static FfmpegH264DecodeResult Frame(
        long submissionId,
        Bitmap bitmap)
    {
        return new FfmpegH264DecodeResult(
            FfmpegH264DecodeStatus.Frame,
            submissionId,
            bitmap);
    }

    public static FfmpegH264DecodeResult Failed(long submissionId)
    {
        return new FfmpegH264DecodeResult(
            FfmpegH264DecodeStatus.Failed,
            submissionId,
            Bitmap: null);
    }
}

internal enum FfmpegH264OutputMode
{
    BgraRaw,
    Mjpeg
}

internal enum FfmpegH264Backend
{
    Software,
    Cuda,
    D3D11Va,
    D3D12Va,
    Dxva2,
    Qsv,
    Amf
}

internal readonly record struct FfmpegH264RaceWinner(
    int CandidateIndex,
    FfmpegH264DecodeResult DecodeResult);

internal readonly record struct FfmpegH264BackendCacheKey(
    string FfmpegPath,
    int Width,
    int Height);

internal readonly record struct FfmpegH264DecodedFrame(
    long SubmissionId,
    Bitmap Bitmap);

internal sealed class FfmpegH264PipelineState
{
    internal const int MaxPendingInputs = 8;
    internal const int MaxDecodedFrames = 8;

    private readonly object _lock = new();
    private readonly Queue<long> _pendingInputs = new();
    private readonly Queue<FfmpegH264DecodedFrame> _decodedFrames = new();

    public int PendingInputCount
    {
        get
        {
            lock (_lock)
            {
                return _pendingInputs.Count;
            }
        }
    }

    public int DecodedFrameCount
    {
        get
        {
            lock (_lock)
            {
                return _decodedFrames.Count;
            }
        }
    }

    public bool TrySubmit(long submissionId)
    {
        lock (_lock)
        {
            if (_pendingInputs.Count >= MaxPendingInputs)
            {
                return false;
            }

            _pendingInputs.Enqueue(submissionId);
            return true;
        }
    }

    public bool TryPublish(
        Bitmap bitmap,
        out long submissionId)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        lock (_lock)
        {
            if (_pendingInputs.Count == 0 ||
                _decodedFrames.Count >= MaxDecodedFrames)
            {
                submissionId = 0;
                return false;
            }

            submissionId = _pendingInputs.Dequeue();
            _decodedFrames.Enqueue(new FfmpegH264DecodedFrame(
                submissionId,
                bitmap));
            return true;
        }
    }

    public bool TryTake(out FfmpegH264DecodedFrame decodedFrame)
    {
        lock (_lock)
        {
            if (_decodedFrames.Count == 0)
            {
                decodedFrame = default;
                return false;
            }

            decodedFrame = _decodedFrames.Dequeue();
            return true;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _pendingInputs.Clear();
            while (_decodedFrames.TryDequeue(
                out FfmpegH264DecodedFrame decodedFrame))
            {
                decodedFrame.Bitmap.Dispose();
            }

            _decodedFrames.Clear();
        }
    }
}

internal sealed class FfmpegH264Decoder : IDisposable
{
    internal const int MaximumFfmpegAllocationBytes =
        128 * 1024 * 1024;
    private static Lazy<IReadOnlyList<string>> FfmpegPaths =
        new(FindFfmpegPaths);
    private static long _availabilityVersion;
    private static readonly IReadOnlyList<FfmpegH264Backend>
        HardwareBackendCandidates = Array.AsReadOnly(
        [
            FfmpegH264Backend.Cuda,
            FfmpegH264Backend.D3D11Va,
            FfmpegH264Backend.D3D12Va,
            FfmpegH264Backend.Dxva2,
            FfmpegH264Backend.Qsv,
            FfmpegH264Backend.Amf
        ]);
    private static readonly ConcurrentDictionary<
        FfmpegH264BackendCacheKey,
        FfmpegH264Backend> WinningBackendCache = new();
    private static readonly byte[] JpegStartMarker = [0xFF, 0xD8];
    private static readonly byte[] AccessUnitBoundary =
        [0x00, 0x00, 0x00, 0x01, 0x09, 0xF0];
    private static readonly TimeSpan DecodeTimeout = TimeSpan.FromMilliseconds(250);
    // A cold process must initialize the decoder and, above 1080p, the
    // high-quality MJPEG bridge before its first output. Native 4K
    // portrait desktops cannot use the inbox MF height-limited path, and
    // their valid first output can take longer than the steady-state budget.
    // These are deadlines, not delays: the first successful backend wins
    // immediately. The >1080p MJPEG bridge also needs a bounded allowance
    // for its extra encode/decode work under load; raw 1080p stays at 250 ms.
    private static readonly TimeSpan StartupDecodeTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan LargeFrameDecodeTimeout = TimeSpan.FromMilliseconds(500);
    private const int DisposeWaitMilliseconds = 500;
    private const int ProbeTimeoutMilliseconds = 1500;
    private const int ErrorTailLength = 2048;
    // The >1080p compatibility bridge now preserves 4:4:4 chroma at q=2.
    // A detailed native-4K desktop can legitimately exceed the former 8 MiB
    // JPEG ceiling, while the detached bitmap already occupies about 32 MiB.
    // Keep one encoded frame bounded at that same order of magnitude.
    internal const int MaxDecodedJpegBytes = 32 * 1024 * 1024;
    internal const int MaxBgraRawPixels = 1920 * 1080;

    private readonly Process _process;
    private readonly Size _frameSize;
    private readonly FfmpegH264OutputMode _outputMode;
    private readonly FfmpegH264Backend _backend;
    private readonly FfmpegH264BackendCacheKey _cacheKey;
    private readonly int _rawFrameByteCount;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    // Losing/failing the software candidate must not cancel the other
    // hardware candidates. Only disposal owns the whole decoder race.
    private readonly CancellationTokenSource _lifetimeCancellationTokenSource = new();
    private readonly SemaphoreSlim _frameSignal = new(0);
    private readonly SemaphoreSlim _transactionGate = new(1, 1);
    private readonly SemaphoreSlim _selectionGate = new(1, 1);
    private readonly FfmpegH264PipelineState _pipeline = new();
    private readonly object _errorLock = new();
    private readonly Task _readTask;
    private readonly Task _errorDrainTask;
    private IReadOnlyList<Task<FfmpegH264Decoder?>>?
        _raceCandidateTasks;
    private FfmpegH264Decoder? _winner;
    private string _lastError = string.Empty;
    private int _bridgeDesynchronized;
    private int _backendDisposeState;
    private int _disposeState;
    private bool _hasWrittenAccessUnit;
    private int _hasDecodedFrame;

    private FfmpegH264Decoder(
        Process process,
        Size frameSize,
        FfmpegH264OutputMode outputMode,
        FfmpegH264Backend backend,
        FfmpegH264BackendCacheKey cacheKey)
    {
        _process = process;
        _frameSize = frameSize;
        _outputMode = outputMode;
        _backend = backend;
        _cacheKey = cacheKey;
        _rawFrameByteCount = outputMode == FfmpegH264OutputMode.BgraRaw
            ? CalculateRawFrameByteCount(frameSize)
            : 0;
        _readTask = StartBackgroundReader(
            ReadOutputLoop);
        _errorDrainTask = StartBackgroundReader(
            DrainError);
    }

    private static Task StartBackgroundReader(Action reader)
    {
        return Task.Factory.StartNew(
            reader,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public static bool IsAvailable => AvailablePath is not null;

    public static string? AvailablePath =>
        AvailablePaths.FirstOrDefault();

    internal static IReadOnlyList<string> AvailablePaths =>
        Volatile.Read(ref FfmpegPaths).Value;

    internal static long AvailabilityVersion => Interlocked.Read(ref _availabilityVersion);

    internal static void RefreshAvailablePaths()
    {
        Interlocked.Exchange(ref FfmpegPaths, new Lazy<IReadOnlyList<string>>(FindFfmpegPaths));
        Interlocked.Increment(ref _availabilityVersion);
    }

    internal static TimeSpan DecodeWaitTimeout => DecodeTimeout;

    internal static TimeSpan GetDecodeWaitTimeout(bool hasDecodedFrame, Size frameSize = default) =>
        !hasDecodedFrame ? StartupDecodeTimeout :
        (long)frameSize.Width * frameSize.Height > MaxBgraRawPixels
            ? LargeFrameDecodeTimeout
            : DecodeTimeout;

    internal static IReadOnlyList<FfmpegH264Backend>
        HardwareRaceBackends => HardwareBackendCandidates;

    public string BackendName
    {
        get
        {
            FfmpegH264Decoder? winner =
                Volatile.Read(ref _winner);
            return winner is not null &&
                !ReferenceEquals(winner, this)
                    ? winner.BackendName
                    : GetBackendName(_backend);
        }
    }

    public bool UsesHardwareAcceleration
    {
        get
        {
            FfmpegH264Decoder? winner =
                Volatile.Read(ref _winner);
            return winner is not null &&
                !ReferenceEquals(winner, this)
                    ? winner.UsesHardwareAcceleration
                    : IsHardwareBackend(_backend);
        }
    }

    public bool IsRunning
    {
        get
        {
            if (Volatile.Read(ref _disposeState) != 0)
            {
                return false;
            }

            FfmpegH264Decoder? winner =
                Volatile.Read(ref _winner);
            bool running = winner is not null &&
                !ReferenceEquals(winner, this)
                    ? winner.IsRunning
                    : IsOwnBackendRunning;
            if (!running && winner is not null)
            {
                ForgetWinningBackend(
                    _cacheKey,
                    winner._backend);
            }

            return running;
        }
    }

    private bool IsOwnBackendRunning
    {
        get
        {
            try
            {
                return Volatile.Read(ref _backendDisposeState) == 0 &&
                    Volatile.Read(ref _bridgeDesynchronized) == 0 &&
                    !_process.HasExited;
            }
            catch (Exception ex) when (ex is InvalidOperationException or
                ObjectDisposedException or
                System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }
    }

    public string FailureDetail
    {
        get
        {
            FfmpegH264Decoder? winner =
                Volatile.Read(ref _winner);
            if (winner is not null &&
                !ReferenceEquals(winner, this))
            {
                return winner.FailureDetail;
            }

            string errorTail;
            lock (_errorLock)
            {
                errorTail = _lastError.Trim();
            }

            if (IsRunning)
            {
                return errorTail;
            }

            try
            {
                return string.IsNullOrWhiteSpace(errorTail)
                    ? $"ffmpeg 已退出，退出码 {_process.ExitCode}"
                    : $"ffmpeg 已退出，退出码 {_process.ExitCode}，{errorTail}";
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                return errorTail;
            }
        }
    }

    internal static string BuildDecoderArguments(Size frameSize)
    {
        return BuildDecoderArguments(
            frameSize,
            FfmpegH264Backend.Software);
    }

    internal static string BuildDecoderArguments(
        Size frameSize,
        FfmpegH264Backend backend)
    {
        ValidateFrameSize(frameSize);
        FfmpegH264OutputMode outputMode =
            GetOutputMode(frameSize);
        var arguments = new List<string>
        {
            "-hide_banner",
            "-loglevel error",
            $"-max_alloc {MaximumFfmpegAllocationBytes}"
        };
        AddBackendInputArguments(arguments, backend);
        arguments.AddRange(
        [
            "-flags low_delay",
            "-probesize 32",
            "-analyzeduration 0",
            "-fpsprobesize 0",
            "-thread_type slice",
            "-threads 0",
            "-f h264",
            "-i pipe:0",
            "-an",
            "-sn",
            "-dn",
            "-vsync 0"
        ]);

        if (outputMode == FfmpegH264OutputMode.BgraRaw)
        {
            arguments.AddRange(
            [
                "-vf",
                BuildRawBgraFilter(frameSize, backend),
                "-f rawvideo",
                "-c:v rawvideo",
                "-threads 1",
                "-pix_fmt bgra"
            ]);
        }
        else
        {
            string? hardwareDownloadFilter =
                BuildMjpegInputFilter(backend);
            if (hardwareDownloadFilter is not null)
            {
                arguments.AddRange(
                [
                    "-vf",
                    hardwareDownloadFilter
                ]);
            }

            arguments.AddRange(
            [
                "-f image2pipe",
                "-c:v mjpeg",
                "-threads 1",
                // Above 1080p this compatibility path has to encode the
                // decoded frame once more before System.Drawing can detach
                // it from the pipe. Preserve full chroma resolution and use
                // a visually transparent quality step so small colored
                // desktop text is not softened by a second 4:2:0 pass.
                "-pix_fmt yuvj444p",
                "-q:v 2"
            ]);
        }

        arguments.AddRange(
        [
            "-flush_packets 1",
            "pipe:1"
        ]);
        return string.Join(' ', arguments);
    }

    private static void AddBackendInputArguments(
        List<string> arguments,
        FfmpegH264Backend backend)
    {
        switch (backend)
        {
            case FfmpegH264Backend.Software:
                return;
            case FfmpegH264Backend.Cuda:
                arguments.AddRange(
                [
                    "-hwaccel cuda",
                    "-hwaccel_output_format cuda"
                ]);
                return;
            case FfmpegH264Backend.D3D11Va:
                arguments.AddRange(
                [
                    "-hwaccel d3d11va",
                    "-hwaccel_output_format d3d11"
                ]);
                return;
            case FfmpegH264Backend.D3D12Va:
                arguments.AddRange(
                [
                    "-hwaccel d3d12va",
                    "-hwaccel_output_format d3d12"
                ]);
                return;
            case FfmpegH264Backend.Dxva2:
                arguments.AddRange(
                [
                    "-hwaccel dxva2",
                    "-hwaccel_output_format dxva2_vld"
                ]);
                return;
            case FfmpegH264Backend.Qsv:
                arguments.AddRange(
                [
                    "-hwaccel qsv",
                    "-hwaccel_output_format qsv",
                    "-c:v h264_qsv",
                    "-async_depth 1"
                ]);
                return;
            case FfmpegH264Backend.Amf:
                arguments.AddRange(
                [
                    "-c:v h264_amf",
                    "-decoder_mode low_latency",
                    "-lowlatency 1",
                    "-timestamp_mode decode"
                ]);
                return;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(backend));
        }
    }

    private static string BuildRawBgraFilter(
        Size frameSize,
        FfmpegH264Backend backend)
    {
        string scaleAndFormat =
            $"scale={frameSize.Width}:{frameSize.Height}:" +
            "flags=fast_bilinear,format=bgra";
        return UsesHardwareSurfaceOutput(backend)
            ? $"hwdownload,format=nv12,{scaleAndFormat}"
            : scaleAndFormat;
    }

    private static string? BuildMjpegInputFilter(
        FfmpegH264Backend backend)
    {
        return UsesHardwareSurfaceOutput(backend)
            ? "hwdownload,format=nv12"
            : null;
    }

    private static bool UsesHardwareSurfaceOutput(
        FfmpegH264Backend backend)
    {
        return backend is
            FfmpegH264Backend.Cuda or
            FfmpegH264Backend.D3D11Va or
            FfmpegH264Backend.D3D12Va or
            FfmpegH264Backend.Dxva2 or
            FfmpegH264Backend.Qsv;
    }

    internal static string GetBackendName(
        FfmpegH264Backend backend)
    {
        return backend switch
        {
            FfmpegH264Backend.Software => "Software",
            FfmpegH264Backend.Cuda => "CUDA",
            FfmpegH264Backend.D3D11Va => "D3D11VA",
            FfmpegH264Backend.D3D12Va => "D3D12VA",
            FfmpegH264Backend.Dxva2 => "DXVA2",
            FfmpegH264Backend.Qsv => "QSV",
            FfmpegH264Backend.Amf => "AMF",
            _ => throw new ArgumentOutOfRangeException(
                nameof(backend))
        };
    }

    internal static bool IsHardwareBackend(
        FfmpegH264Backend backend)
    {
        _ = GetBackendName(backend);
        return backend != FfmpegH264Backend.Software;
    }

    internal static FfmpegH264BackendCacheKey
        CreateBackendCacheKey(
            string ffmpegPath,
            Size frameSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        ValidateFrameSize(frameSize);
        return new FfmpegH264BackendCacheKey(
            Path.GetFullPath(ffmpegPath),
            frameSize.Width,
            frameSize.Height);
    }

    internal static bool TryGetCachedBackend(
        FfmpegH264BackendCacheKey cacheKey,
        out FfmpegH264Backend backend)
    {
        return WinningBackendCache.TryGetValue(
            cacheKey,
            out backend);
    }

    internal static void RememberWinningBackend(
        FfmpegH264BackendCacheKey cacheKey,
        FfmpegH264Backend backend)
    {
        _ = GetBackendName(backend);
        WinningBackendCache[cacheKey] = backend;
    }

    internal static bool ForgetWinningBackend(
        FfmpegH264BackendCacheKey cacheKey,
        FfmpegH264Backend backend)
    {
        if (!WinningBackendCache.TryGetValue(
                cacheKey,
                out FfmpegH264Backend cached) ||
            cached != backend)
        {
            return false;
        }

        return WinningBackendCache.TryRemove(
            cacheKey,
            out _);
    }

    internal static FfmpegH264OutputMode GetOutputMode(
        Size frameSize)
    {
        ValidateFrameSize(frameSize);
        return (long)frameSize.Width * frameSize.Height <=
            MaxBgraRawPixels
                ? FfmpegH264OutputMode.BgraRaw
                : FfmpegH264OutputMode.Mjpeg;
    }

    internal static int CalculateRawFrameByteCount(
        Size frameSize)
    {
        ValidateFrameSize(frameSize);
        return checked(frameSize.Width * frameSize.Height * 4);
    }

    private static void ValidateFrameSize(Size frameSize)
    {
        if (!RemoteMessageCodec.IsFrameDimensionsAllowed(
                frameSize.Width,
                frameSize.Height))
        {
            throw new ArgumentOutOfRangeException(
                nameof(frameSize),
                "H.264 解码尺寸超过协议安全上限。");
        }
    }

    public static FfmpegH264Decoder? TryCreate(Size frameSize)
    {
        try
        {
            ValidateFrameSize(frameSize);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }

        string? path = AvailablePath;
        if (path is null)
        {
            return null;
        }

        FfmpegH264BackendCacheKey cacheKey =
            CreateBackendCacheKey(path, frameSize);
        if (TryGetCachedBackend(
                cacheKey,
                out FfmpegH264Backend cachedBackend))
        {
            FfmpegH264Decoder? cached =
                TryCreateBackend(
                    path,
                    frameSize,
                    cachedBackend,
                    cacheKey);
            if (cached is not null)
            {
                Volatile.Write(ref cached._winner, cached);
                return cached;
            }

            ForgetWinningBackend(
                cacheKey,
                cachedBackend);
        }

        FfmpegH264Decoder? software = TryCreateBackend(
            path,
            frameSize,
            FfmpegH264Backend.Software,
            cacheKey);
        if (software is null)
        {
            return null;
        }

        var candidateTasks =
            new Task<FfmpegH264Decoder?>[
                HardwareBackendCandidates.Count];
        for (int index = 0;
            index < HardwareBackendCandidates.Count;
            index++)
        {
            FfmpegH264Backend backend =
                HardwareBackendCandidates[index];
            candidateTasks[index] = Task.Run(() =>
                software.CreateRaceCandidate(
                    path,
                    frameSize,
                    backend,
                    cacheKey));
        }

        software._raceCandidateTasks =
            Array.AsReadOnly(candidateTasks);
        return software;
    }

    private FfmpegH264Decoder? CreateRaceCandidate(
        string path,
        Size frameSize,
        FfmpegH264Backend backend,
        FfmpegH264BackendCacheKey cacheKey)
    {
        if (Volatile.Read(ref _disposeState) != 0)
        {
            return null;
        }

        FfmpegH264Decoder? candidate = TryCreateBackend(
            path,
            frameSize,
            backend,
            cacheKey);
        if (candidate is not null &&
            Volatile.Read(ref _disposeState) != 0)
        {
            candidate.Dispose();
            return null;
        }

        return candidate;
    }

    private static FfmpegH264Decoder? TryCreateBackend(
        string path,
        Size frameSize,
        FfmpegH264Backend backend,
        FfmpegH264BackendCacheKey cacheKey)
    {
        Process? process = null;
        try
        {
            FfmpegH264OutputMode outputMode =
                GetOutputMode(frameSize);
            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = BuildDecoderArguments(
                        frameSize,
                        backend),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                },
                EnableRaisingEvents = false
            };

            if (!process.Start())
            {
                process.Dispose();
                return null;
            }

            TrySetDecoderPriority(process);
            return new FfmpegH264Decoder(
                process,
                frameSize,
                outputMode,
                backend,
                cacheKey);
        }
        catch
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception ex) when (ex is
                    InvalidOperationException or
                    ObjectDisposedException or
                    System.ComponentModel.Win32Exception)
                {
                }

                process.Dispose();
            }

            return null;
        }
    }

    private static void TrySetDecoderPriority(Process process)
    {
        try
        {
            process.PriorityClass = ProcessPriorityClass.AboveNormal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }

    public async Task<FfmpegH264DecodeResult> DecodeAsync(
        long submissionId,
        ReadOnlyMemory<byte> h264Bytes)
    {
        if (Volatile.Read(ref _disposeState) != 0 ||
            h264Bytes.IsEmpty)
        {
            return FfmpegH264DecodeResult.Failed(submissionId);
        }

        FfmpegH264Decoder? winner =
            Volatile.Read(ref _winner);
        if (winner is not null)
        {
            return await DecodeWithWinnerAsync(
                winner,
                submissionId,
                h264Bytes).ConfigureAwait(false);
        }

        IReadOnlyList<Task<FfmpegH264Decoder?>>?
            candidateTasks = _raceCandidateTasks;
        if (candidateTasks is null ||
            candidateTasks.Count == 0)
        {
            return await DecodeOwnBackendAsync(
                submissionId,
                h264Bytes).ConfigureAwait(false);
        }

        bool entered = false;
        try
        {
            await _selectionGate.WaitAsync(
                _lifetimeCancellationTokenSource.Token).ConfigureAwait(false);
            entered = true;
            winner = Volatile.Read(ref _winner);
            if (winner is not null)
            {
                return await DecodeWithWinnerAsync(
                    winner,
                    submissionId,
                    h264Bytes).ConfigureAwait(false);
            }

            // The first-frame backend race intentionally lets losing decoder
            // tasks finish in the background. Keep their input independent
            // from the viewer's pooled frame lease, which may be returned as
            // soon as this method reports the winning result.
            byte[] stableRaceInput =
                CopyBackendRaceInput(h264Bytes);
            return await RaceFirstFrameAsync(
                submissionId,
                stableRaceInput,
                candidateTasks).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsDecoderBridgeFailure(ex))
        {
            return FfmpegH264DecodeResult.Failed(submissionId);
        }
        finally
        {
            if (entered)
            {
                try
                {
                    _selectionGate.Release();
                }
                catch (ObjectDisposedException)
                    when (Volatile.Read(
                        ref _disposeState) != 0)
                {
                }
            }
        }
    }

    internal static byte[] CopyBackendRaceInput(
        ReadOnlyMemory<byte> h264Bytes) =>
        h264Bytes.ToArray();

    private async Task<FfmpegH264DecodeResult> DecodeWithWinnerAsync(
        FfmpegH264Decoder winner,
        long submissionId,
        ReadOnlyMemory<byte> h264Bytes)
    {
        FfmpegH264DecodeResult result =
            await (ReferenceEquals(winner, this)
                ? DecodeOwnBackendAsync(
                    submissionId,
                    h264Bytes)
                : winner.DecodeOwnBackendAsync(
                    submissionId,
                    h264Bytes)).ConfigureAwait(false);
        if (result.Status == FfmpegH264DecodeStatus.Failed)
        {
            ForgetWinningBackend(
                _cacheKey,
                winner._backend);
        }

        return result;
    }

    private async Task<FfmpegH264DecodeResult> RaceFirstFrameAsync(
        long submissionId,
        ReadOnlyMemory<byte> h264Bytes,
        IReadOnlyList<Task<FfmpegH264Decoder?>>
            candidateTasks)
    {
        var decodeTasks =
            new Task<FfmpegH264DecodeResult>[
                candidateTasks.Count + 1];
        decodeTasks[0] = DecodeOwnBackendAsync(
            submissionId,
            h264Bytes);
        for (int index = 0;
            index < candidateTasks.Count;
            index++)
        {
            decodeTasks[index + 1] =
                DecodeCreatedCandidateAsync(
                    candidateTasks[index],
                    submissionId,
                    h264Bytes);
        }

        using var raceTimeout =
            new CancellationTokenSource(GetDecodeWaitTimeout(hasDecodedFrame: false));
        using var linkedRaceTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                raceTimeout.Token,
                _lifetimeCancellationTokenSource.Token);
        FfmpegH264RaceWinner? raceWinner;
        try
        {
            raceWinner = await SelectFirstSuccessfulAsync(
                decodeTasks,
                linkedRaceTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (raceTimeout.IsCancellationRequested)
            {
                AppendErrorText("ffmpeg 首帧解码启动超时（1500 ms）。");
            }

            ScheduleRaceLoserCleanup(
                candidateTasks,
                decodeTasks,
                winnerIndex: -1);
            return FfmpegH264DecodeResult.Failed(submissionId);
        }

        if (raceWinner is null)
        {
            AppendErrorText("ffmpeg 首帧解码失败：没有可用后端返回完整画面。");
            ScheduleRaceLoserCleanup(
                candidateTasks,
                decodeTasks,
                winnerIndex: -1);
            return FfmpegH264DecodeResult.Failed(submissionId);
        }

        FfmpegH264Decoder? selected =
            raceWinner.Value.CandidateIndex == 0
                ? this
                : await candidateTasks[
                    raceWinner.Value.CandidateIndex - 1]
                    .ConfigureAwait(false);
        if (selected is null)
        {
            raceWinner.Value.DecodeResult.Bitmap?.Dispose();
            ScheduleRaceLoserCleanup(
                candidateTasks,
                decodeTasks,
                winnerIndex: -1);
            return FfmpegH264DecodeResult.Failed(submissionId);
        }

        RememberWinningBackend(
            _cacheKey,
            selected._backend);
        Volatile.Write(ref _winner, selected);
        ScheduleRaceLoserCleanup(
            candidateTasks,
            decodeTasks,
            raceWinner.Value.CandidateIndex);
        return raceWinner.Value.DecodeResult;
    }

    private async Task<FfmpegH264DecodeResult>
        DecodeCreatedCandidateAsync(
            Task<FfmpegH264Decoder?> candidateTask,
            long submissionId,
            ReadOnlyMemory<byte> h264Bytes)
    {
        try
        {
            FfmpegH264Decoder? candidate =
                await candidateTask.ConfigureAwait(false);
            return candidate is null
                ? FfmpegH264DecodeResult.Failed(
                    submissionId)
                : await candidate.DecodeOwnBackendAsync(
                    submissionId,
                    h264Bytes).ConfigureAwait(false);
        }
        catch
        {
            return FfmpegH264DecodeResult.Failed(
                submissionId);
        }
    }

    internal static async Task<FfmpegH264RaceWinner?>
        SelectFirstSuccessfulAsync(
            IReadOnlyList<Task<FfmpegH264DecodeResult>> decodeTasks,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decodeTasks);
        var remaining = decodeTasks
            .Select((task, index) => (Task: task, Index: index))
            .ToList();
        while (remaining.Count > 0)
        {
            Task<FfmpegH264DecodeResult> completed =
                await Task.WhenAny(
                    remaining.Select(entry => entry.Task))
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            int remainingIndex = remaining.FindIndex(
                entry => ReferenceEquals(
                    entry.Task,
                    completed));
            (Task<FfmpegH264DecodeResult> Task, int Index) entry =
                remaining[remainingIndex];
            remaining.RemoveAt(remainingIndex);
            try
            {
                FfmpegH264DecodeResult result =
                    await entry.Task.ConfigureAwait(false);
                if (result.Status ==
                        FfmpegH264DecodeStatus.Frame &&
                    result.Bitmap is not null)
                {
                    return new FfmpegH264RaceWinner(
                        entry.Index,
                        result);
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private void ScheduleRaceLoserCleanup(
        IReadOnlyList<Task<FfmpegH264Decoder?>>
            candidateTasks,
        IReadOnlyList<Task<FfmpegH264DecodeResult>> decodeTasks,
        int winnerIndex)
    {
        if (winnerIndex > 0)
        {
            _ = Task.Run(() =>
                CleanupOwnRaceLoserAsync(
                    decodeTasks[0]));
        }

        for (int index = 0;
            index < candidateTasks.Count;
            index++)
        {
            int decodeIndex = index + 1;
            if (decodeIndex == winnerIndex)
            {
                continue;
            }

            Task<FfmpegH264Decoder?> candidateTask =
                candidateTasks[index];
            Task<FfmpegH264DecodeResult> decodeTask =
                decodeTasks[decodeIndex];
            _ = Task.Run(() =>
                CleanupCreatedCandidateAsync(
                    candidateTask,
                    decodeTask));
        }
    }

    private async Task CleanupOwnRaceLoserAsync(
        Task<FfmpegH264DecodeResult> decodeTask)
    {
        try
        {
            FfmpegH264DecodeResult result =
                await decodeTask.ConfigureAwait(false);
            result.Bitmap?.Dispose();
        }
        catch
        {
        }

        DisposeOwnBackendResources();
    }

    private static async Task CleanupCreatedCandidateAsync(
        Task<FfmpegH264Decoder?> candidateTask,
        Task<FfmpegH264DecodeResult> decodeTask)
    {
        try
        {
            FfmpegH264DecodeResult result =
                await decodeTask.ConfigureAwait(false);
            result.Bitmap?.Dispose();
        }
        catch
        {
        }

        FfmpegH264Decoder? candidate = null;
        try
        {
            candidate = await candidateTask.ConfigureAwait(false);
        }
        catch
        {
        }

        candidate?.Dispose();
    }

    private async Task<FfmpegH264DecodeResult> DecodeOwnBackendAsync(
        long submissionId,
        ReadOnlyMemory<byte> h264Bytes)
    {
        if (Volatile.Read(ref _disposeState) != 0 ||
            h264Bytes.IsEmpty)
        {
            return FfmpegH264DecodeResult.Failed(submissionId);
        }

        if (!IsOwnBackendRunning)
        {
            DesynchronizeBridge();
            return FfmpegH264DecodeResult.Failed(submissionId);
        }

        bool hasDecodedFrame = Volatile.Read(ref _hasDecodedFrame) != 0;
        TimeSpan waitTimeout = GetDecodeWaitTimeout(hasDecodedFrame, _frameSize);
        using var timeout = new CancellationTokenSource(waitTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            timeout.Token,
            _cancellationTokenSource.Token);

        try
        {
            FfmpegH264DecodeResult? result = await ExecuteDecodeTransactionAsync(
                _transactionGate,
                linked.Token,
                async cancellationToken =>
                {
                    if (!_pipeline.TrySubmit(submissionId))
                    {
                        throw new IOException(
                            "ffmpeg 待解码输入队列已满。");
                    }

                    await WriteAccessUnitAsync(
                        _process.StandardInput.BaseStream,
                        h264Bytes,
                        trimLeadingAud: _hasWrittenAccessUnit,
                        cancellationToken).ConfigureAwait(false);
                    _hasWrittenAccessUnit = true;
                },
                cancellationToken => _process.StandardInput.BaseStream.FlushAsync(cancellationToken),
                TakeDecodedFrameOrWaitAsync,
                DesynchronizeBridge).ConfigureAwait(false);
            if (result?.Status == FfmpegH264DecodeStatus.Frame)
            {
                Volatile.Write(ref _hasDecodedFrame, 1);
            }
            else if (timeout.IsCancellationRequested)
            {
                AppendDecodeTimeout(hasDecodedFrame, waitTimeout);
            }

            return result ?? FfmpegH264DecodeResult.Failed(submissionId);
        }
        catch (Exception ex) when (IsDecoderBridgeFailure(ex))
        {
            if (timeout.IsCancellationRequested)
            {
                AppendDecodeTimeout(hasDecodedFrame, waitTimeout);
            }

            DesynchronizeBridge();
            return FfmpegH264DecodeResult.Failed(submissionId);
        }
    }

    private void AppendDecodeTimeout(bool hasDecodedFrame, TimeSpan waitTimeout) =>
        AppendErrorText(
            $"ffmpeg {(hasDecodedFrame ? "画面" : "首帧启动")}解码超时" +
            $"（{waitTimeout.TotalMilliseconds:0} ms）。");

    internal static async Task<T?> ExecuteDecodeTransactionAsync<T>(
        SemaphoreSlim transactionGate,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task> writeAsync,
        Func<CancellationToken, Task> flushAsync,
        Func<CancellationToken, Task<T>> readAsync,
        Action desynchronizeBridge)
        where T : class
    {
        bool entered = false;
        try
        {
            await transactionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            await writeAsync(cancellationToken).ConfigureAwait(false);
            await flushAsync(cancellationToken).ConfigureAwait(false);
            return await readAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsDecoderBridgeFailure(ex))
        {
            desynchronizeBridge();
            return null;
        }
        finally
        {
            if (entered)
            {
                transactionGate.Release();
            }
        }
    }

    internal static async Task WriteAccessUnitAsync(
        Stream input,
        ReadOnlyMemory<byte> accessUnit,
        bool trimLeadingAud,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (trimLeadingAud)
        {
            accessUnit = TrimLeadingAud(accessUnit);
        }

        await input.WriteAsync(
            accessUnit,
            cancellationToken).ConfigureAwait(false);
        await input.WriteAsync(
            AccessUnitBoundary,
            cancellationToken).ConfigureAwait(false);
    }

    internal static ReadOnlyMemory<byte> TrimLeadingAud(
        ReadOnlyMemory<byte> accessUnit)
    {
        ReadOnlySpan<byte> bytes = accessUnit.Span;
        int startCodeLength = GetStartCodeLength(bytes);
        if (startCodeLength == 0 ||
            bytes.Length <= startCodeLength ||
            (bytes[startCodeLength] & 0x1F) != 9)
        {
            return accessUnit;
        }

        for (int index = startCodeLength + 1;
            index <= bytes.Length - 3;
            index++)
        {
            int nextStartCodeLength =
                GetStartCodeLength(bytes[index..]);
            if (nextStartCodeLength > 0)
            {
                return accessUnit[index..];
            }
        }

        return ReadOnlyMemory<byte>.Empty;
    }

    private static int GetStartCodeLength(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 4 &&
            bytes[0] == 0 &&
            bytes[1] == 0 &&
            bytes[2] == 0 &&
            bytes[3] == 1)
        {
            return 4;
        }

        return bytes.Length >= 3 &&
            bytes[0] == 0 &&
            bytes[1] == 0 &&
            bytes[2] == 1
                ? 3
                : 0;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(
                ref _disposeState,
                1) != 0)
        {
            return;
        }

        IReadOnlyList<Task<FfmpegH264Decoder?>>?
            candidateTasks = _raceCandidateTasks;
        _lifetimeCancellationTokenSource.Cancel();
        if (candidateTasks is not null)
        {
            foreach (Task<FfmpegH264Decoder?> candidateTask in
                candidateTasks)
            {
                _ = Task.Run(() =>
                    DisposeCreatedCandidateAsync(
                        candidateTask));
            }
        }

        DisposeOwnBackendResources();
        _selectionGate.Dispose();
        _lifetimeCancellationTokenSource.Dispose();
    }

    private static async Task DisposeCreatedCandidateAsync(
        Task<FfmpegH264Decoder?> candidateTask)
    {
        try
        {
            FfmpegH264Decoder? candidate =
                await candidateTask.ConfigureAwait(false);
            candidate?.Dispose();
        }
        catch
        {
        }
    }

    private void DisposeOwnBackendResources()
    {
        if (Interlocked.Exchange(
                ref _backendDisposeState,
                1) != 0)
        {
            return;
        }

        _pipeline.Reset();
        _cancellationTokenSource.Cancel();
        try
        {
            _process.StandardInput.Close();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException)
        {
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }

            _process.WaitForExit(DisposeWaitMilliseconds);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.ComponentModel.Win32Exception)
        {
        }

        WaitForBackgroundReadersToStop();
        _process.Dispose();
        _frameSignal.Dispose();
        _transactionGate.Dispose();
        _cancellationTokenSource.Dispose();
    }

    private void WaitForBackgroundReadersToStop()
    {
        try
        {
            Task.WaitAll([_readTask, _errorDrainTask], DisposeWaitMilliseconds);
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(IsBackgroundReaderStopException))
        {
        }
        catch (Exception ex) when (IsBackgroundReaderStopException(ex))
        {
        }
    }

    private static bool IsBackgroundReaderStopException(Exception ex)
    {
        return ex is OperationCanceledException or IOException or ObjectDisposedException;
    }

    private async Task<FfmpegH264DecodeResult> TakeDecodedFrameOrWaitAsync(
        CancellationToken cancellationToken)
    {
        if (TryTakeDecodedFrame(out FfmpegH264DecodeResult decodedFrame))
        {
            return decodedFrame;
        }

        while (true)
        {
            if (TryTakeDecodedFrame(out decodedFrame))
            {
                return decodedFrame;
            }

            if (Volatile.Read(ref _bridgeDesynchronized) != 0)
            {
                throw new IOException("ffmpeg 输出流已失步。");
            }

            await _frameSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private bool TryTakeDecodedFrame(
        out FfmpegH264DecodeResult result)
    {
        if (!_pipeline.TryTake(out FfmpegH264DecodedFrame decodedFrame))
        {
            result = null!;
            return false;
        }

        try
        {
            DrainAvailableFrameSignals(_frameSignal);
            result = FfmpegH264DecodeResult.Frame(
                decodedFrame.SubmissionId,
                decodedFrame.Bitmap);
            return true;
        }
        catch
        {
            decodedFrame.Bitmap.Dispose();
            throw;
        }
    }

    private void ReadOutputLoop()
    {
        try
        {
            if (_outputMode == FfmpegH264OutputMode.BgraRaw)
            {
                ReadRawBgraLoop();
            }
            else
            {
                ReadJpegLoop();
            }
        }
        catch (Exception ex) when (IsDecoderBridgeFailure(ex))
        {
            if (!_cancellationTokenSource.IsCancellationRequested)
            {
                AppendErrorText(
                    $"ffmpeg 输出读取失败：{ex.Message}");
            }
        }
        finally
        {
            if (Volatile.Read(ref _backendDisposeState) == 0 &&
                Volatile.Read(ref _bridgeDesynchronized) == 0)
            {
                DesynchronizeBridge();
            }
        }
    }

    private void ReadRawBgraLoop()
    {
        Stream output = _process.StandardOutput.BaseStream;
        byte[] rawFrame =
            ArrayPool<byte>.Shared.Rent(_rawFrameByteCount);
        try
        {
            while (!_cancellationTokenSource.IsCancellationRequested)
            {
                bool hasFrame = ReadExactlyOrEnd(
                    output,
                    rawFrame,
                    _rawFrameByteCount);
                if (!hasFrame)
                {
                    return;
                }

                Bitmap bitmap = CopyBgraToDetachedBitmap(
                    rawFrame,
                    _rawFrameByteCount,
                    _frameSize);
                PublishFrame(bitmap);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rawFrame);
        }
    }

    private static bool ReadExactlyOrEnd(
        Stream input,
        byte[] destination,
        int byteCount)
    {
        int totalRead = 0;
        while (totalRead < byteCount)
        {
            int read = input.Read(
                destination,
                totalRead,
                byteCount - totalRead);
            if (read <= 0)
            {
                if (totalRead == 0)
                {
                    return false;
                }

                throw new EndOfStreamException(
                    "ffmpeg BGRA 输出在完整帧之前结束。");
            }

            totalRead += read;
        }

        return true;
    }

    internal static async Task<bool> ReadExactlyOrEndAsync(
        Stream input,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        int totalRead = 0;
        while (totalRead < destination.Length)
        {
            int read = await input.ReadAsync(
                destination[totalRead..],
                cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                if (totalRead == 0)
                {
                    return false;
                }

                throw new EndOfStreamException(
                    "ffmpeg BGRA 输出在完整帧之前结束。");
            }

            totalRead += read;
        }

        return true;
    }

    internal static Bitmap CopyBgraToDetachedBitmap(
        byte[] bgraBytes,
        int byteCount,
        Size frameSize)
    {
        ArgumentNullException.ThrowIfNull(bgraBytes);
        int expectedByteCount =
            CalculateRawFrameByteCount(frameSize);
        if (byteCount != expectedByteCount ||
            byteCount > bgraBytes.Length)
        {
            throw new ArgumentException(
                "BGRA 帧长度与解码尺寸不一致。",
                nameof(byteCount));
        }

        var bitmap = new Bitmap(
            frameSize.Width,
            frameSize.Height,
            PixelFormat.Format32bppArgb);
        try
        {
            BitmapData bitmapData = bitmap.LockBits(
                new Rectangle(Point.Empty, frameSize),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                int rowBytes = checked(frameSize.Width * 4);
                if (Math.Abs((long)bitmapData.Stride) < rowBytes)
                {
                    throw new InvalidOperationException(
                        "Bitmap 行跨度小于 BGRA 帧行长度。");
                }

                if (bitmapData.Stride == rowBytes)
                {
                    Marshal.Copy(
                        bgraBytes,
                        0,
                        bitmapData.Scan0,
                        expectedByteCount);
                }
                else
                {
                    for (int row = 0;
                        row < frameSize.Height;
                        row++)
                    {
                        Marshal.Copy(
                            bgraBytes,
                            row * rowBytes,
                            GetBitmapRowPointer(
                                bitmapData.Scan0,
                                row,
                                bitmapData.Stride),
                            rowBytes);
                    }
                }
            }
            finally
            {
                bitmap.UnlockBits(bitmapData);
            }

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    internal static IntPtr GetBitmapRowPointer(
        IntPtr scan0,
        int row,
        int stride)
    {
        if (row < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(row));
        }

        return IntPtr.Add(
            scan0,
            checked(row * stride));
    }

    private void ReadJpegLoop()
    {
        var frameBytes = new ArrayBufferWriter<byte>(256 * 1024);
        byte[] buffer = new byte[16 * 1024];
        int previous = -1;
        bool inJpeg = false;

        Stream output = _process.StandardOutput.BaseStream;
        while (!_cancellationTokenSource.IsCancellationRequested)
        {
            int read = output.Read(
                buffer,
                0,
                buffer.Length);
            if (read <= 0)
            {
                return;
            }

            int segmentStart = 0;
            for (int index = 0; index < read; index++)
            {
                int current = buffer[index] & 0xFF;
                if (!inJpeg)
                {
                    if (previous == 0xFF && current == 0xD8)
                    {
                        inJpeg = true;
                        frameBytes.Clear();
                        frameBytes.Write(JpegStartMarker);
                        segmentStart = index + 1;
                    }
                }
                else if (previous == 0xFF && current == 0xD9)
                {
                    if (index >= segmentStart)
                    {
                        frameBytes.Write(buffer.AsSpan(
                            segmentStart,
                            index - segmentStart + 1));
                    }

                    if (!ShouldDropPartialJpegFrame(
                            frameBytes.WrittenCount))
                    {
                        Bitmap bitmap = DetachedBitmapLoader.Load(
                            frameBytes.WrittenSpan.ToArray(),
                            offset: 0,
                            count: frameBytes.WrittenCount);
                        PublishFrame(bitmap);
                    }

                    inJpeg = false;
                    frameBytes.Clear();
                    segmentStart = index + 1;
                }

                previous = current;
            }

            if (inJpeg && segmentStart < read)
            {
                frameBytes.Write(buffer.AsSpan(
                    segmentStart,
                    read - segmentStart));
                if (ShouldDropPartialJpegFrame(
                        frameBytes.WrittenCount))
                {
                    inJpeg = false;
                    frameBytes.Clear();
                    previous = -1;
                }
            }
        }
    }

    private void DrainError()
    {
        char[] buffer = new char[1024];
        try
        {
            while (!_cancellationTokenSource.IsCancellationRequested)
            {
                int read = _process.StandardError.Read(
                    buffer,
                    0,
                    buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                AppendErrorText(new string(buffer, 0, read));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }

    private void PublishFrame(Bitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if (!_pipeline.TryPublish(
                bitmap,
                out _))
        {
            bitmap.Dispose();
            DesynchronizeBridge();
            return;
        }

        try
        {
            _frameSignal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal static int DrainAvailableFrameSignals(SemaphoreSlim signal)
    {
        int drained = 0;
        while (signal.Wait(0))
        {
            drained++;
        }

        return drained;
    }

    private void DesynchronizeBridge()
    {
        if (Volatile.Read(ref _backendDisposeState) != 0 ||
            Interlocked.CompareExchange(ref _bridgeDesynchronized, 1, 0) != 0)
        {
            return;
        }

        _pipeline.Reset();

        try
        {
            _cancellationTokenSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static bool IsDecoderBridgeFailure(Exception ex)
    {
        return ex is OperationCanceledException or
            IOException or
            InvalidOperationException or
            ObjectDisposedException or
            System.ComponentModel.Win32Exception or
            ArgumentException or
            System.Runtime.InteropServices.ExternalException;
    }

    private void AppendErrorText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        lock (_errorLock)
        {
            _lastError = string.Concat(_lastError, text);
            if (_lastError.Length > ErrorTailLength)
            {
                _lastError = _lastError[^ErrorTailLength..];
            }
        }
    }

    internal static bool ShouldDropPartialJpegFrame(int byteCount)
    {
        return byteCount > MaxDecodedJpegBytes;
    }

    private static IReadOnlyList<string> FindFfmpegPaths()
    {
        return ResolveAvailableFfmpegPaths();
    }

    internal static IReadOnlyList<string> ResolveAvailableFfmpegPaths(
        string? appBaseDirectory = null,
        string? pathValue = null,
        Func<string, bool>? fileExists = null,
        Func<string, bool>? isUsable = null)
    {
        var candidates = new List<string>();
        string baseDirectory =
            string.IsNullOrWhiteSpace(appBaseDirectory)
                ? AppContext.BaseDirectory
                : appBaseDirectory;
        fileExists ??= File.Exists;
        isUsable ??= IsUsableFfmpeg;
        try
        {
            candidates.Add(
                Path.GetFullPath(
                    Path.Combine(
                        baseDirectory,
                        "ffmpeg.exe")));
        }
        catch (Exception ex) when (
            ex is ArgumentException or
                NotSupportedException or
                PathTooLongException)
        {
        }

        candidates.AddRange(WindowsFfmpegDependency.CompanionPaths());
        candidates.AddRange(
            EnumeratePathCandidates(
                "ffmpeg.exe",
                pathValue));
        candidates.AddRange(
            EnumeratePathCandidates(
                "ffmpeg",
                pathValue));
        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(
                candidate =>
                    fileExists(candidate) &&
                    isUsable(candidate))
            .ToArray();
    }

    private static bool IsUsableFfmpeg(string path)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = "-version",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            if (!process.Start())
            {
                return false;
            }

            if (!process.WaitForExit(ProbeTimeoutMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }

                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static IEnumerable<string> EnumeratePathCandidates(
        string fileName,
        string? pathValue = null)
    {
        pathValue ??=
            Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            yield break;
        }

        foreach (string directory in pathValue.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            string trimmed = directory.Trim('"');
            string candidate;
            try
            {
                candidate = Path.Combine(trimmed, fileName);
            }
            catch (ArgumentException)
            {
                continue;
            }

            yield return candidate;
        }
    }
}

internal static class DetachedBitmapLoader
{
    public static Bitmap Load(
        byte[] encodedBytes,
        int offset,
        int count,
        int? expectedWidth = null,
        int? expectedHeight = null)
    {
        Size encodedSize = ReadJpegDimensions(encodedBytes, offset, count);
        if (!RemoteMessageCodec.IsFrameDimensionsAllowed(
                encodedSize.Width,
                encodedSize.Height))
        {
            throw new InvalidDataException("JPEG 解码尺寸超出安全像素预算。");
        }

        if (expectedWidth.HasValue != expectedHeight.HasValue ||
            (expectedWidth.HasValue &&
             (encodedSize.Width != expectedWidth.Value ||
              encodedSize.Height != expectedHeight!.Value)))
        {
            throw new InvalidDataException("JPEG 实际尺寸与画面帧声明不一致。");
        }

        using var stream = new MemoryStream(
            encodedBytes,
            offset,
            count,
            writable: false,
            publiclyVisible: false);
        using var decoded = new Bitmap(stream);
        if (decoded.Size != encodedSize ||
            !RemoteMessageCodec.IsFrameDimensionsAllowed(
                decoded.Width,
                decoded.Height))
        {
            throw new InvalidDataException("JPEG 解码结果尺寸异常。");
        }

        return new Bitmap(decoded);
    }

    internal static Size ReadJpegDimensions(
        byte[] encodedBytes,
        int offset,
        int count)
    {
        ArgumentNullException.ThrowIfNull(encodedBytes);
        if (offset < 0 || count < 4 || offset > encodedBytes.Length - count)
        {
            throw new InvalidDataException("JPEG 数据范围无效。");
        }

        ReadOnlySpan<byte> jpeg = encodedBytes.AsSpan(offset, count);
        if (jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            throw new InvalidDataException("画面数据不是有效的 JPEG。");
        }

        int index = 2;
        while (index < jpeg.Length)
        {
            while (index < jpeg.Length && jpeg[index] != 0xFF)
            {
                index++;
            }
            while (index < jpeg.Length && jpeg[index] == 0xFF)
            {
                index++;
            }
            if (index >= jpeg.Length)
            {
                break;
            }

            byte marker = jpeg[index++];
            if (marker == 0x00 || marker == 0xD8 || marker == 0xD9 ||
                marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                continue;
            }
            if (index > jpeg.Length - 2)
            {
                break;
            }

            int segmentLength = BinaryPrimitives.ReadUInt16BigEndian(jpeg[index..]);
            if (segmentLength < 2 || index > jpeg.Length - segmentLength)
            {
                throw new InvalidDataException("JPEG 分段长度无效。");
            }

            if (IsStartOfFrameMarker(marker))
            {
                if (segmentLength < 7)
                {
                    throw new InvalidDataException("JPEG 尺寸分段不完整。");
                }

                int height = BinaryPrimitives.ReadUInt16BigEndian(jpeg[(index + 3)..]);
                int width = BinaryPrimitives.ReadUInt16BigEndian(jpeg[(index + 5)..]);
                if (width <= 0 || height <= 0)
                {
                    throw new InvalidDataException("JPEG 尺寸无效。");
                }

                return new Size(width, height);
            }

            if (marker == 0xDA)
            {
                break;
            }
            index += segmentLength;
        }

        throw new InvalidDataException("JPEG 未包含可识别的尺寸信息。");
    }

    private static bool IsStartOfFrameMarker(byte marker) =>
        marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or
            0xC5 or 0xC6 or 0xC7 or
            0xC9 or 0xCA or 0xCB or
            0xCD or 0xCE or 0xCF;
}
