using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Net;
using System.Net.Sockets;

namespace RemoteDesk;

internal enum FfmpegDesktopCaptureBackend
{
    DesktopDuplicationOutput0,
    GdiGrabBounds,
    WindowsGraphicsCaptureMonitor
}

internal enum FfmpegH264Encoder
{
    NvidiaNvenc,
    MediaFoundation,
    IntelQuickSync,
    AmdAmf
}

internal sealed record FfmpegDesktopH264CaptureOptions(
    FfmpegDesktopCaptureBackend Backend,
    Rectangle TargetBounds,
    Size OutputSize,
    int FramesPerSecond,
    int GopLength = 1,
    int? BitrateFramesPerSecond = null,
    WindowsGraphicsCaptureTarget? GraphicsCaptureTarget = null,
    WindowsDesktopDuplicationTarget? DesktopDuplicationTarget = null,
    bool AllowStaticFrameSilence = false);

internal sealed class FfmpegDesktopH264Frame : IDisposable
{
    private H264FrameBufferLease? _buffer;

    internal FfmpegDesktopH264Frame(
        int width,
        int height,
        RemoteFrameFlags flags,
        H264FrameBufferLease buffer,
        long producedAtTimestamp)
    {
        Width = width;
        Height = height;
        Flags = flags;
        _buffer =
            buffer ??
            throw new ArgumentNullException(nameof(buffer));
        ProducedAtTimestamp = producedAtTimestamp;
    }

    public int Width { get; }

    public int Height { get; }

    public RemoteFrameFlags Flags { get; }

    public ReadOnlyMemory<byte> AnnexBBytes =>
        GetBuffer().Memory;

    public long ProducedAtTimestamp { get; }

    public TimeSpan Age =>
        Stopwatch.GetElapsedTime(ProducedAtTimestamp);

    internal bool OwnsPooledBuffer =>
        Volatile.Read(ref _buffer)?.IsPooled == true;

    internal bool IsDisposed =>
        Volatile.Read(ref _buffer) is null;

    public void Dispose()
    {
        Interlocked.Exchange(
                ref _buffer,
                null)
            ?.Dispose();
    }

    private H264FrameBufferLease GetBuffer()
    {
        return Volatile.Read(ref _buffer) ??
            throw new ObjectDisposedException(
                nameof(FfmpegDesktopH264Frame));
    }
}

internal sealed record FfmpegDesktopH264CaptureStartResult(
    FfmpegDesktopH264Capture? Capture,
    string FailureDetail,
    bool UsedVerifiedEncoderFastPath = false,
    bool StartupDeadlineExpired = false)
{
    public bool Started => Capture is not null;
}

internal sealed record FfmpegGraphicsCaptureCapability(
    bool IsAvailable,
    string Detail);

internal interface IFfmpegDesktopH264Process : IDisposable
{
    Stream StandardOutput { get; }

    TextReader StandardError { get; }

    Task Completion { get; }

    bool HasExited { get; }

    int? ExitCode { get; }

    bool TryRequestGracefulExit();

    void KillEntireProcessTree();

    bool WaitForExit(int milliseconds);
}

internal delegate IFfmpegDesktopH264Process FfmpegDesktopH264ProcessFactory(
    string executablePath,
    IReadOnlyList<string> arguments);

internal interface IFfmpegDesktopH264RtpReceiver : IDisposable
{
    int Port { get; }

    bool HasPendingDatagrams { get; }

    ValueTask<int> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken);
}

internal delegate IFfmpegDesktopH264RtpReceiver
    FfmpegDesktopH264RtpReceiverFactory();

internal static class WindowsKillOnCloseJob
{
    internal const uint KillOnJobCloseLimit = 0x00002000;

    public static SafeFileHandle? TryCreateAndAssign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return TryCreateAndAssignCore(
                process.Handle,
                CreateJobObject,
                ConfigureKillOnClose,
                AssignProcess);
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or
                ObjectDisposedException or
                PlatformNotSupportedException or
                DllNotFoundException or
                EntryPointNotFoundException or
                System.ComponentModel.Win32Exception)
        {
            // A restricted parent job can reject nested assignment. Process
            // shutdown still has the verified graceful/forced fallback.
            return null;
        }
    }

    internal static SafeFileHandle? TryCreateAndAssignCore(
        nint processHandle,
        Func<SafeFileHandle?> createJob,
        Func<SafeFileHandle, bool> configureJob,
        Func<SafeFileHandle, nint, bool> assignProcess)
    {
        if (processHandle == nint.Zero ||
            processHandle == new nint(-1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(processHandle));
        }

        ArgumentNullException.ThrowIfNull(createJob);
        ArgumentNullException.ThrowIfNull(configureJob);
        ArgumentNullException.ThrowIfNull(assignProcess);

        SafeFileHandle? job = createJob();
        if (job is null || job.IsInvalid || job.IsClosed)
        {
            job?.Dispose();
            return null;
        }

        try
        {
            if (!configureJob(job) ||
                !assignProcess(job, processHandle))
            {
                job.Dispose();
                return null;
            }

            return job;
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    private static SafeFileHandle? CreateJobObject() =>
        CreateJobObjectW(
            jobAttributes: nint.Zero,
            name: null);

    private static bool ConfigureKillOnClose(
        SafeFileHandle job)
    {
        var information =
            new JobObjectExtendedLimitInformation();
        information.BasicLimitInformation.LimitFlags =
            KillOnJobCloseLimit;
        return SetInformationJobObject(
            job,
            JobObjectInformationClass.ExtendedLimitInformation,
            ref information,
            (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>());
    }

    private static bool AssignProcess(
        SafeFileHandle job,
        nint processHandle) =>
        AssignProcessToJobObject(job, processHandle);

    private enum JobObjectInformationClass
    {
        ExtendedLimitInformation = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectIoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation
            BasicLimitInformation;
        public JobObjectIoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(
        nint jobAttributes,
        string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job,
        JobObjectInformationClass informationClass,
        ref JobObjectExtendedLimitInformation information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(
        SafeFileHandle job,
        nint processHandle);
}

internal sealed class FfmpegDesktopH264Capture : IDisposable
{
    internal static readonly TimeSpan DefaultStartupTimeout =
        TimeSpan.FromMilliseconds(750);
    // A D3D11-aware Media Foundation transform takes about one second to
    // enumerate and initialize on the measured AMD display adapter. A healthy
    // DDA -> scale_d3d11 -> h264_mf GOP1 path produced its first complete
    // recovery AU in 0.7-0.9 seconds. Keep the deadline tight: a driver/output
    // that opens but never yields its first duplicated frame cannot be fixed
    // by waiting longer, and the GDI/NVENC compatibility path should remain
    // available promptly while a background DDA recovery probe stays eligible.
    internal static readonly TimeSpan
        DesktopDuplicationAmdStartupTimeout =
            TimeSpan.FromMilliseconds(1500);
    internal static readonly TimeSpan
        DesktopDuplicationMediaFoundationStartupTimeout =
            TimeSpan.FromMilliseconds(6000);
    internal static readonly TimeSpan
        DesktopDuplicationMediaFoundationAllIndependentStartupTimeout =
            TimeSpan.FromMilliseconds(1500);
    internal static readonly TimeSpan
        DesktopDuplicationNvidiaStartupTimeout =
            TimeSpan.FromMilliseconds(400);
    // Warm gfxcapture -> h264_nvenc starts in about 734 ms, while the first
    // process after a cold driver load can take longer than 1.5 seconds.
    // Leave one bounded cold-start allowance; the host skips the remaining
    // WGC candidates after a real timeout and immediately falls back to DDA.
    internal static readonly TimeSpan
        WindowsGraphicsCaptureNvidiaStartupTimeout =
            TimeSpan.FromMilliseconds(2500);
    internal static readonly TimeSpan
        VerifiedReconnectMinimumStartupTimeout =
            TimeSpan.FromMilliseconds(1500);
    internal static readonly TimeSpan
        VerifiedReconnectMaximumStartupTimeout =
            TimeSpan.FromMilliseconds(2500);
    internal static readonly TimeSpan DefaultStallTimeout =
        TimeSpan.FromSeconds(2);
    // WGC deliberately throttles a fully static desktop to roughly 2 FPS.
    // A two-second no-frame watchdog can therefore race normal compositor
    // idling. This applies only after the first frame; startup remains under
    // the encoder-specific first-access-unit deadlines above.
    internal static readonly TimeSpan
        WindowsGraphicsCaptureStallTimeout =
            TimeSpan.FromSeconds(6);
    internal const int MinimumBitrateBitsPerSecond = 2_000_000;
    // Full-HD and QHD remain efficient at 0.18 bit/pixel/frame. Native
    // Ultra-HD GOP1 gets extra intra-frame budget at <= 30 FPS to keep small
    // desktop text and one-pixel edges crisp. At high frame rate, 0.24 bpp
    // made the measured 4K60 stream consume about 112 Mbps before encrypted
    // UDP fragmentation; one ordinary Wi-Fi stall then forced a lasting
    // 60 -> 30 FPS fallback. Keep native 4K geometry while smoothly
    // interpolating the UHD budget from 0.24 bpp at 30 FPS to 0.16 bpp at
    // 60 FPS. Blending the policy between QHD and UHD also prevents a bitrate
    // drop when either FPS or an even output dimension crosses a threshold.
    // The endpoints remain 59.7 Mbps at 4K30 and 79.6 Mbps at 4K60, leaving
    // RF airtime for feedback and input. The UDP controller separately
    // budgets datagram/FEC overhead.
    internal const int MaximumBitrateBitsPerSecond = 160_000_000;
    // Desktop capture is dominated by sharp, independently decodable refresh
    // frames. A one-frame VBV limited a 1080p30 IDR to roughly 46 KiB even
    // after a long static pause, while the same desktop needed about 180 KiB
    // as a quality-85 JPEG. Keep the average/peak bitrate bounded, but allow
    // the rate controller to spend four frame budgets on a detailed IDR. With
    // no lookahead, no B-frames and the explicit zero-latency encoder options,
    // this is burst capacity rather than four frames of presentation delay.
    internal const int DesktopVbvFrameCapacity = 4;
    internal const int RtpPayloadType = 96;
    // This RTP hop never leaves 127.0.0.1. A 16 KiB payload reduced the
    // measured native-4K packet rate from about 5,700 to 465 datagrams/s.
    // The separately encrypted LAN transport continues to use its own
    // 1200-byte MTU and fragments these completed access units as before.
    internal const int LoopbackRtpPacketSizeBytes = 16 * 1024;
    internal const int ShortGopLength = 2;
    // The measured AMD Media Foundation encoder emits about 1.4 MiB across
    // nine IDR frames when its DDA pipeline first becomes ready. Preserve that
    // cold-start burst in the kernel while the reader starts, but keep the
    // window bounded because the userspace mailbox remains latest-only.
    internal const int MinimumRtpReceiveBufferBytes = 2 * 1024 * 1024;
    internal const int MaximumRtpReceiveBufferBytes = 4 * 1024 * 1024;

    private static readonly RemoteFrameFlags IndependentFrameFlags =
        RemoteFrameFlags.KeyFrame | RemoteFrameFlags.CodecConfig;
    internal const uint AmdVendorId = 0x1002;
    internal const uint IntelVendorId = 0x8086;
    internal const uint NvidiaVendorId = 0x10DE;
    private const long QuadHdPixelCount = 2560L * 1440L;
    private const long NativeUltraHdPixelCount = 3840L * 2160L;
    // On the measured 4K WGC output, requesting exactly 60 FPS quantized
    // source delivery to roughly 53 FPS. A 160 FPS request still yielded only
    // about 134 source frames/s and a 25 ms selected-frame P95; requesting
    // 240 FPS reached the compositor's roughly 160 Hz ceiling and reduced the
    // selected P95 to 18.75 ms. Retain at most one source frame per 60 Hz
    // bucket without manufacturing duplicates. Keep this narrowly scoped to
    // native landscape UHD so lower resolutions, scaling paths and portrait
    // displays retain their cheaper direct capture behavior.
    private const int WindowsGraphicsCaptureUltraHdSamplingFps = 240;
    private static readonly ConcurrentDictionary<
        string,
        Lazy<Task<FfmpegGraphicsCaptureCapability>>>
        GraphicsCaptureCapabilityCache =
            new(StringComparer.OrdinalIgnoreCase);
    private static EncoderPreference? s_windowsGraphicsCapturePreference;
    private static EncoderPreference? s_desktopDuplicationPreference;
    private static EncoderPreference? s_gdiGrabPreference;
    private const double StandardBitsPerPixelPerFrame = 0.18d;
    private const double NativeUltraHdBitsPerPixelPerFrame = 0.24d;
    private const double
        NativeUltraHdHighFrameRateBitsPerPixelPerFrame = 0.16d;
    private const int ErrorTailLength = 4096;
    private const int ReaderBufferBytes = 32 * 1024;
    private const int MaximumUdpDatagramBytes = ushort.MaxValue;
    private static readonly TimeSpan GraphicsCaptureProbeTimeout =
        TimeSpan.FromSeconds(2);
    internal const int GracefulExitWaitMilliseconds = 1000;
    internal const int ForcedExitWaitMilliseconds = 2000;
    internal const int FailedCaptureExitWaitMilliseconds = 500;
    internal const int UnconfirmedExitReaperRetryMilliseconds = 1000;
    private const int ReaderShutdownWaitMilliseconds = 750;
    internal static ProcessPriorityClass ProductionProcessPriority =>
        ProcessPriorityClass.Normal;
    // This worker blocks on loopback RTP between short packet bursts. Keeping
    // only this frame-boundary hot path at Highest avoids 50 ms read batches
    // under desktop load without raising the whole FFmpeg/host process and
    // competing with input acknowledgement.
    internal const ThreadPriority RtpReaderThreadPriority =
        ThreadPriority.Highest;

    private readonly FfmpegDesktopH264CaptureOptions _options;
    private readonly IFfmpegDesktopH264Process _process;
    private readonly IFfmpegDesktopH264RtpReceiver? _rtpReceiver;
    private readonly ArrayPool<byte> _accessUnitPool;
    private readonly TimeSpan _stallTimeout;
    private readonly CancellationTokenSource _captureCancellation = new();
    private readonly LatestFrameMailbox<FfmpegDesktopH264Frame> _mailbox = new();
    private readonly SemaphoreSlim _frameSignal = new(0);
    private readonly TaskCompletionSource<bool> _firstFrameReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _failureLock = new();
    private readonly object _errorLock = new();
    private readonly object _publishLock = new();
    private readonly Task _videoOutputTask;
    private readonly Task _stdoutDrainTask;
    private readonly Task _stderrTask;
    private readonly Task _watchdogTask;
    private readonly long _processStartedAt;

    private string _failureReason = string.Empty;
    private string _stderrTail = string.Empty;
    private long _lastFrameAt;
    private long _lastRtpFrameAt;
    private long _pendingRecoveryTimestamp;
    private bool _dependentFrameAllowed;
    private bool _hasPublishedRecoveryFrame;
    private RawFallbackParserState? _rawFallbackParserState;
    private long _rawFallbackParserGeneration;
    private int _startupDeadlineExpired;
    private int _rtpReceiverDisposed;
    private int _shutdownRequested;
    private int _disposed;

    private FfmpegDesktopH264Capture(
        FfmpegDesktopH264CaptureOptions options,
        FfmpegH264Encoder encoder,
        string executablePath,
        IFfmpegDesktopH264Process process,
        IFfmpegDesktopH264RtpReceiver? rtpReceiver,
        ArrayPool<byte>? accessUnitPool,
        TimeSpan stallTimeout)
    {
        _options = options;
        _process = process;
        _rtpReceiver = rtpReceiver;
        _accessUnitPool =
            accessUnitPool ??
            ArrayPool<byte>.Shared;
        _stallTimeout = stallTimeout;
        BackendName = GetBackendName(options);
        EncoderName = GetEncoderName(encoder);
        ExecutablePath = executablePath;
        _processStartedAt = Stopwatch.GetTimestamp();
        if (rtpReceiver is null)
        {
            _videoOutputTask = Task.Run(ReadRawAnnexBOutputAsync);
        }
        else
        {
            // The loopback RTP marker is the lowest-latency frame boundary,
            // but some Media Foundation MFT/muxer combinations have been
            // observed to encode valid Annex-B output without producing a
            // usable RTP recovery AU. The tee's stdout branch is therefore a
            // hot fallback: it starts the candidate when RTP is silent and is
            // suppressed while RTP is delivering healthy frames.
            _videoOutputTask = Task.WhenAll(
                LowLatencyDedicatedThread.Start(
                    "RemoteDesk FFmpeg RTP reader",
                    ReadRtpOutput,
                    RtpReaderThreadPriority),
                Task.Run(ReadRawAnnexBOutputAsync));
        }

        _stdoutDrainTask = Task.CompletedTask;
        // Begin draining synchronously up to the first asynchronous read so a
        // fast encoder failure cannot outrun thread-pool scheduling and lose
        // its diagnostic before the next candidate starts.
        _stderrTask = DrainStandardErrorAsync();
        _watchdogTask = Task.Run(WatchdogAsync);
    }

    public string BackendName { get; }

    public string EncoderName { get; }

    public string ExecutablePath { get; }

    public FfmpegDesktopCaptureBackend Backend =>
        _options.Backend;

    public int FramesPerSecond =>
        _options.FramesPerSecond;

    public Task Completion => _completion.Task;

    public bool IsRunning =>
        Volatile.Read(ref _disposed) == 0 &&
        !HasFailure &&
        !_process.HasExited;

    internal bool StartupDeadlineExpired =>
        Volatile.Read(
            ref _startupDeadlineExpired) != 0;

    internal bool RawFallbackParserActive =>
        Volatile.Read(
            ref _rawFallbackParserState) is not null;

    internal long RawFallbackParserGeneration =>
        Volatile.Read(
            ref _rawFallbackParserGeneration);

    public bool IsStalled
    {
        get
        {
            long lastFrameAt = Volatile.Read(ref _lastFrameAt);
            long reference = lastFrameAt == 0
                ? _processStartedAt
                : lastFrameAt;
            return Stopwatch.GetElapsedTime(reference) >= _stallTimeout;
        }
    }

    public string FailureDetail
    {
        get
        {
            string failureReason;
            string stderrTail;
            lock (_failureLock)
            {
                failureReason = _failureReason.Trim();
            }

            lock (_errorLock)
            {
                stderrTail = _stderrTail.Trim();
            }

            if (string.IsNullOrWhiteSpace(failureReason) &&
                Volatile.Read(ref _disposed) == 0 &&
                _process.HasExited)
            {
                failureReason = _process.ExitCode is int exitCode
                    ? $"ffmpeg exited with code {exitCode}."
                    : "ffmpeg exited.";
            }

            if (string.IsNullOrWhiteSpace(failureReason))
            {
                return stderrTail;
            }

            return string.IsNullOrWhiteSpace(stderrTail)
                ? failureReason
                : $"{failureReason} ffmpeg: {stderrTail}";
        }
    }

    public static async Task<FfmpegDesktopH264CaptureStartResult> TryStartAsync(
        FfmpegDesktopH264CaptureOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<string> ffmpegPaths =
            FfmpegH264Decoder.AvailablePaths;
        if (ffmpegPaths.Count == 0)
        {
            return new FfmpegDesktopH264CaptureStartResult(
                Capture: null,
                "ffmpeg is unavailable.");
        }

        var failures = new List<string>();
        bool useEncoderPreferenceCache = true;
        bool startupDeadlineExpired = false;
        foreach (string ffmpegPath in ffmpegPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (options.Backend ==
                FfmpegDesktopCaptureBackend
                    .WindowsGraphicsCaptureMonitor)
            {
                FfmpegGraphicsCaptureCapability capability =
                    await GetGraphicsCaptureCapabilityAsync(
                            ffmpegPath,
                            cancellationToken)
                        .ConfigureAwait(false);
                if (!capability.IsAvailable)
                {
                    failures.Add(
                        $"{ffmpegPath}: {capability.Detail}");
                    continue;
                }
            }

            FfmpegDesktopH264CaptureStartResult start =
                await TryStartLoopbackRtpAsync(
                        options,
                        ffmpegPath,
                        SystemFfmpegDesktopH264Process.Start,
                        () => SystemLoopbackRtpReceiver.Create(
                            CalculateLoopbackRtpReceiveBufferBytes(
                                options)),
                        DefaultStartupTimeout,
                        GetProductionStallTimeout(options.Backend),
                        cancellationToken,
                        useEncoderPreferenceCache,
                        useEncoderSpecificStartupTimeouts: true)
                    .ConfigureAwait(false);
            if (start.Started)
            {
                return start;
            }

            failures.Add(
                $"{ffmpegPath}: {start.FailureDetail}");
            startupDeadlineExpired |=
                start.StartupDeadlineExpired;
            // A preference learned with one FFmpeg build must not prevent a
            // later PATH candidate from exploring all of its encoder
            // implementations.
            useEncoderPreferenceCache = false;
        }

        return new FfmpegDesktopH264CaptureStartResult(
            Capture: null,
            failures.Count == 0
                ? "No usable FFmpeg capture candidate was available."
                : string.Join(" | ", failures),
            StartupDeadlineExpired:
                startupDeadlineExpired);
    }

    internal static Task<FfmpegDesktopH264CaptureStartResult> TryStartAsync(
        FfmpegDesktopH264CaptureOptions options,
        string? ffmpegPath,
        FfmpegDesktopH264ProcessFactory processFactory,
        TimeSpan startupTimeout,
        TimeSpan stallTimeout,
        CancellationToken cancellationToken,
        bool useEncoderPreferenceCache = false)
    {
        return TryStartCoreAsync(
            options,
            ffmpegPath,
            processFactory,
            rtpReceiverFactory: null,
            startupTimeout,
            stallTimeout,
            cancellationToken,
            useEncoderPreferenceCache,
            useEncoderSpecificStartupTimeouts: false,
            accessUnitPool: null);
    }

    internal static Task<FfmpegDesktopH264CaptureStartResult>
        TryStartLoopbackRtpAsync(
            FfmpegDesktopH264CaptureOptions options,
            string? ffmpegPath,
            FfmpegDesktopH264ProcessFactory processFactory,
            FfmpegDesktopH264RtpReceiverFactory rtpReceiverFactory,
            TimeSpan startupTimeout,
            TimeSpan stallTimeout,
            CancellationToken cancellationToken,
            bool useEncoderPreferenceCache = false,
            bool useEncoderSpecificStartupTimeouts = false,
            ArrayPool<byte>? accessUnitPool = null)
    {
        ArgumentNullException.ThrowIfNull(rtpReceiverFactory);
        return TryStartCoreAsync(
            options,
            ffmpegPath,
            processFactory,
            rtpReceiverFactory,
            startupTimeout,
            stallTimeout,
            cancellationToken,
            useEncoderPreferenceCache,
            useEncoderSpecificStartupTimeouts,
            accessUnitPool);
    }

    internal static bool ContainsGraphicsCaptureFilter(
        string? filterListing)
    {
        if (string.IsNullOrWhiteSpace(filterListing))
        {
            return false;
        }

        foreach (string line in filterListing.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = line.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries);
            if (fields.Any(
                    field => string.Equals(
                        field,
                        "gfxcapture",
                        StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    internal static void ResetGraphicsCaptureCapabilityCacheForTests()
    {
        GraphicsCaptureCapabilityCache.Clear();
    }

    private static async Task<FfmpegGraphicsCaptureCapability>
        GetGraphicsCaptureCapabilityAsync(
            string ffmpegPath,
            CancellationToken cancellationToken)
    {
        string cacheKey;
        try
        {
            cacheKey = Path.GetFullPath(ffmpegPath);
        }
        catch (Exception ex) when (
            ex is ArgumentException or
                NotSupportedException or
                PathTooLongException)
        {
            cacheKey = ffmpegPath;
        }

        try
        {
            var file = new FileInfo(ffmpegPath);
            if (file.Exists)
            {
                cacheKey = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{cacheKey}|{file.Length}|" +
                    $"{file.LastWriteTimeUtc.Ticks}");
            }
        }
        catch (Exception ex) when (
            ex is IOException or
                UnauthorizedAccessException or
                NotSupportedException)
        {
        }

        Lazy<Task<FfmpegGraphicsCaptureCapability>> capability =
            GraphicsCaptureCapabilityCache.GetOrAdd(
                cacheKey,
                _ => new Lazy<Task<FfmpegGraphicsCaptureCapability>>(
                    () => ProbeGraphicsCaptureCapabilityAsync(
                        ffmpegPath),
                    LazyThreadSafetyMode.ExecutionAndPublication));
        return await capability.Value
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<FfmpegGraphicsCaptureCapability>
        ProbeGraphicsCaptureCapabilityAsync(string ffmpegPath)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.StartInfo.ArgumentList.Add("-hide_banner");
            process.StartInfo.ArgumentList.Add("-filters");
            if (!process.Start())
            {
                return new FfmpegGraphicsCaptureCapability(
                    IsAvailable: false,
                    "ffmpeg gfxcapture capability probe did not start.");
            }

            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            using var timeoutCancellation =
                new CancellationTokenSource(
                    GraphicsCaptureProbeTimeout);
            try
            {
                await process.WaitForExitAsync(
                        timeoutCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (timeoutCancellation.IsCancellationRequested)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    _ = process.WaitForExit(500);
                }
                catch (Exception ex) when (
                    ex is InvalidOperationException or
                        System.ComponentModel.Win32Exception or
                        NotSupportedException)
                {
                }

                return new FfmpegGraphicsCaptureCapability(
                    IsAvailable: false,
                    "ffmpeg gfxcapture capability probe timed out.");
            }

            string listing =
                (await stdout.ConfigureAwait(false)) +
                Environment.NewLine +
                (await stderr.ConfigureAwait(false));
            if (process.ExitCode != 0)
            {
                return new FfmpegGraphicsCaptureCapability(
                    IsAvailable: false,
                    $"ffmpeg gfxcapture capability probe exited with " +
                    $"code {process.ExitCode}.");
            }

            return ContainsGraphicsCaptureFilter(listing)
                ? new FfmpegGraphicsCaptureCapability(
                    IsAvailable: true,
                    "ffmpeg exposes the gfxcapture source filter.")
                : new FfmpegGraphicsCaptureCapability(
                    IsAvailable: false,
                    "The selected ffmpeg build does not expose the " +
                    "gfxcapture source filter required for Windows " +
                    "Graphics Capture.");
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or
                IOException or
                UnauthorizedAccessException or
                System.ComponentModel.Win32Exception or
                NotSupportedException)
        {
            return new FfmpegGraphicsCaptureCapability(
                IsAvailable: false,
                $"ffmpeg gfxcapture capability probe failed: {ex.Message}");
        }
    }

    private static async Task<FfmpegDesktopH264CaptureStartResult>
        TryStartCoreAsync(
            FfmpegDesktopH264CaptureOptions options,
            string? ffmpegPath,
            FfmpegDesktopH264ProcessFactory processFactory,
            FfmpegDesktopH264RtpReceiverFactory? rtpReceiverFactory,
            TimeSpan startupTimeout,
            TimeSpan stallTimeout,
            CancellationToken cancellationToken,
            bool useEncoderPreferenceCache,
            bool useEncoderSpecificStartupTimeouts,
            ArrayPool<byte>? accessUnitPool)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(processFactory);
        cancellationToken.ThrowIfCancellationRequested();

        string? validationFailure = ValidateOptions(options);
        if (validationFailure is not null)
        {
            return new FfmpegDesktopH264CaptureStartResult(
                Capture: null,
                validationFailure);
        }

        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            return new FfmpegDesktopH264CaptureStartResult(
                Capture: null,
                "ffmpeg is unavailable.");
        }

        if (startupTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(startupTimeout));
        }

        if (stallTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(stallTimeout));
        }

        EncoderPreference? preferredEncoder =
            useEncoderPreferenceCache
                ? ReadEncoderPreference(options.Backend)
                : null;
        if (preferredEncoder is not null &&
            !PathsEqual(
                preferredEncoder.ExecutablePath,
                ffmpegPath))
        {
            preferredEncoder = null;
        }
        bool usedVerifiedEncoderFastPath =
            preferredEncoder is not null &&
            preferredEncoder.Options == options &&
            IsGpuSurfaceCaptureBackend(options.Backend);
        var failures = new List<string>();
        bool startupDeadlineExpired = false;
        foreach (FfmpegH264Encoder encoder in
                 GetStartupEncoderCandidates(
                     options,
                     preferredEncoder,
                     usedVerifiedEncoderFastPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IFfmpegDesktopH264Process? process = null;
            IFfmpegDesktopH264RtpReceiver? rtpReceiver = null;
            FfmpegDesktopH264Capture? capture = null;
            bool candidateFailed = false;
            bool candidateStartupDeadlineExpired =
                false;
            long candidateStartedAt = Stopwatch.GetTimestamp();
            try
            {
                rtpReceiver = rtpReceiverFactory?.Invoke();
                IReadOnlyList<string> arguments = rtpReceiver is null
                    ? BuildArguments(options, encoder)
                    : BuildLoopbackRtpArguments(
                        options,
                        encoder,
                        rtpReceiver.Port);
                process = processFactory(ffmpegPath, arguments);
                capture = new FfmpegDesktopH264Capture(
                    options,
                    encoder,
                    ffmpegPath,
                    process,
                    rtpReceiver,
                    accessUnitPool,
                    stallTimeout);
                process = null;
                rtpReceiver = null;

                TimeSpan candidateStartupTimeout =
                    usedVerifiedEncoderFastPath &&
                    preferredEncoder?.Encoder == encoder
                        ? CalculateVerifiedReconnectStartupTimeout(
                            preferredEncoder
                                .SuccessfulStartupDuration)
                        : GetStartupTimeout(
                            options.Backend,
                            encoder,
                            startupTimeout,
                            useEncoderSpecificStartupTimeouts,
                            options.GopLength);
                if (await capture.WaitForStartupAsync(
                        candidateStartupTimeout,
                        cancellationToken).ConfigureAwait(false))
                {
                    if (useEncoderPreferenceCache)
                    {
                        RememberEncoderPreference(
                            options.Backend,
                            encoder,
                            ffmpegPath,
                            options,
                            Stopwatch.GetElapsedTime(
                                candidateStartedAt));
                    }

                    FfmpegDesktopH264Capture startedCapture = capture;
                    capture = null;
                    return new FfmpegDesktopH264CaptureStartResult(
                        startedCapture,
                        FailureDetail: string.Empty,
                        usedVerifiedEncoderFastPath);
                }

                failures.Add(
                    $"hardware encoder {GetEncoderName(encoder)}: " +
                    NormalizeFailure(capture.FailureDetail));
                candidateStartupDeadlineExpired =
                    capture.StartupDeadlineExpired;
                startupDeadlineExpired |=
                    candidateStartupDeadlineExpired;
                candidateFailed = true;
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                capture?.Fail(
                    "ffmpeg H.264 startup was cancelled before a " +
                    "recovery access unit arrived.");
                throw;
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or
                    IOException or
                    UnauthorizedAccessException or
                    System.ComponentModel.Win32Exception or
                    NotSupportedException or
                    ArgumentException)
            {
                failures.Add(
                    $"hardware encoder {GetEncoderName(encoder)}: " +
                    ex.Message);
                candidateFailed = true;
            }
            finally
            {
                capture?.Dispose();
                process?.Dispose();
                rtpReceiver?.Dispose();
            }

            if (candidateFailed &&
                preferredEncoder?.Encoder == encoder)
            {
                if (usedVerifiedEncoderFastPath)
                {
                    // This encoder already produced a recovery AU on the
                    // exact DDA backend. Keep it as the last-known-good
                    // half-open candidate and let the host switch directly
                    // to GDI hardware encoding instead of serially probing
                    // unverified adapter combinations on every reconnect.
                    break;
                }

                ClearEncoderPreferenceIfUnchanged(
                    options.Backend,
                    preferredEncoder);
            }

            if (ShouldStopDdaDiscoveryAfterStartupDeadline(
                    options.Backend,
                    useEncoderSpecificStartupTimeouts,
                    candidateStartupDeadlineExpired))
            {
                // Every encoder candidate reads from the same DDA source.
                // Once that source consumes its real-frame deadline without
                // yielding a frame, changing encoders cannot unblock
                // AcquireNextFrame. Switch to hardware-encoded gdigrab
                // instead of stacking more native cleanup delays. Encoders
                // that fail fast still allow the next DDA candidate to be
                // discovered in this same session.
                break;
            }
        }

        return new FfmpegDesktopH264CaptureStartResult(
            Capture: null,
            failures.Count == 0
                ? "No FFmpeg hardware H.264 encoder candidate was available."
                : string.Join(" | ", failures),
            usedVerifiedEncoderFastPath,
            startupDeadlineExpired);
    }

    public bool TryReadLatestFrame(
        out FfmpegDesktopH264Frame? frame)
    {
        frame = null;
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        try
        {
            if (!_frameSignal.Wait(0))
            {
                return false;
            }
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        return TryTakeSignaledFrame(out frame);
    }

    public async ValueTask<FfmpegDesktopH264Frame?> ReadLatestFrameAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            if (TryReadLatestFrame(out FfmpegDesktopH264Frame? frame))
            {
                return frame;
            }

            if (Completion.IsCompleted)
            {
                return null;
            }

            try
            {
                await _frameSignal.WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
                when (Completion.IsCompleted)
            {
                return null;
            }

            if (TryTakeSignaledFrame(out frame))
            {
                return frame;
            }

            if (Completion.IsCompleted)
            {
                return null;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _shutdownRequested, 1);
        bool failed = HasFailure;
        bool exited = _process.HasExited;
        if (!failed &&
            !exited &&
            ShouldRequestGracefulExit(_options.Backend) &&
            _process.TryRequestGracefulExit())
        {
            exited = _process.WaitForExit(
                GracefulExitWaitMilliseconds);
        }

        if (!exited)
        {
            TryKillProcess();
            exited = _process.WaitForExit(
                failed
                    ? FailedCaptureExitWaitMilliseconds
                    : ForcedExitWaitMilliseconds);
        }

        _captureCancellation.Cancel();
        _firstFrameReady.TrySetResult(false);
        _mailbox.Close()?.Dispose();
        DisposeRtpReceiver();
        WaitForBackgroundTasks(
            ReaderShutdownWaitMilliseconds);
        _completion.TrySetResult();
        SignalFrameReader();
        _frameSignal.Dispose();
        if (exited)
        {
            _process.Dispose();
        }
        else
        {
            // Process.Dispose alone does not terminate an OS process. Keep
            // the native handle alive until the forced exit becomes
            // observable instead of racing the next DDA candidate against
            // delayed driver cleanup.
            _ = ReapUnconfirmedProcessExitAsync();
        }

        _captureCancellation.Dispose();
    }

    internal static bool ShouldRequestGracefulExit(
        FfmpegDesktopCaptureBackend backend)
    {
        // The Windows graphics-capture plug-in currently faults in
        // graphicscapture.dll_unloaded while processing FFmpeg's interactive
        // "q" shutdown. Terminating this isolated child process skips that
        // unstable plug-in teardown and avoids a misleading Application
        // Error on every otherwise clean disconnect. DDA and GDI do not use
        // the plug-in and retain the ordinary graceful-exit path.
        return backend !=
            FfmpegDesktopCaptureBackend
                .WindowsGraphicsCaptureMonitor;
    }

    internal static IReadOnlyList<FfmpegH264Encoder> GetEncoderCandidates(
        FfmpegDesktopCaptureBackend backend)
    {
        return backend switch
        {
            // gfxcapture produces a BGRA D3D11 surface. AMF and NVENC can
            // consume that surface directly on their native adapter; MF and
            // QSV remain fail-fast compatibility candidates for drivers that
            // expose a suitable D3D11 input type.
            FfmpegDesktopCaptureBackend
                .WindowsGraphicsCaptureMonitor =>
                [
                    FfmpegH264Encoder.AmdAmf,
                    FfmpegH264Encoder.NvidiaNvenc,
                    FfmpegH264Encoder.MediaFoundation,
                    FfmpegH264Encoder.IntelQuickSync
                ],
            // The entity AMD-display/NVIDIA-render target repeatedly starts
            // DDA -> h264_mf in about 400 ms. Probing a vendor encoder first
            // can retain the duplication device long enough for that proven
            // path to time out and fall all the way back to GDI. Prefer the
            // adapter-neutral Media Foundation path, then try native vendor
            // encoders. The preference cache keeps any proven path first on
            // reconnect.
            FfmpegDesktopCaptureBackend.DesktopDuplicationOutput0 =>
                [
                    FfmpegH264Encoder.MediaFoundation,
                    FfmpegH264Encoder.AmdAmf,
                    FfmpegH264Encoder.NvidiaNvenc,
                    FfmpegH264Encoder.IntelQuickSync
                ],
            FfmpegDesktopCaptureBackend.GdiGrabBounds =>
                [
                    FfmpegH264Encoder.NvidiaNvenc,
                    FfmpegH264Encoder.MediaFoundation,
                    FfmpegH264Encoder.IntelQuickSync,
                    FfmpegH264Encoder.AmdAmf
                ],
            _ => throw new ArgumentOutOfRangeException(nameof(backend))
        };
    }

    internal static IReadOnlyList<FfmpegH264Encoder> GetEncoderCandidates(
        FfmpegDesktopH264CaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Backend !=
            FfmpegDesktopCaptureBackend
                .WindowsGraphicsCaptureMonitor)
        {
            return GetEncoderCandidates(options.Backend);
        }

        return options.GraphicsCaptureTarget?.AdapterVendorId switch
        {
            AmdVendorId =>
                [
                    // Move to the next resolved adapter (or DDA) immediately
                    // when the native AMF path cannot consume the WGC surface.
                    // Serial MF probing on the measured AMD owner can spend
                    // six seconds without ever negotiating BGRA.
                    FfmpegH264Encoder.AmdAmf
                ],
            NvidiaVendorId =>
                [
                    FfmpegH264Encoder.NvidiaNvenc
                ],
            IntelVendorId =>
                [
                    FfmpegH264Encoder.IntelQuickSync
                ],
            _ => GetEncoderCandidates(options.Backend)
        };
    }

    internal static TimeSpan GetProductionStallTimeout(
        FfmpegDesktopCaptureBackend backend)
    {
        return backend ==
            FfmpegDesktopCaptureBackend
                .WindowsGraphicsCaptureMonitor
                    ? WindowsGraphicsCaptureStallTimeout
                    : DefaultStallTimeout;
    }

    internal static TimeSpan GetStartupTimeout(
        FfmpegDesktopCaptureBackend backend,
        FfmpegH264Encoder encoder,
        TimeSpan defaultTimeout,
        bool useEncoderSpecificStartupTimeouts = true,
        int gopLength = ShortGopLength)
    {
        if (defaultTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(defaultTimeout));
        }

        if (!useEncoderSpecificStartupTimeouts ||
            !IsGpuSurfaceCaptureBackend(backend))
        {
            return defaultTimeout;
        }

        if (backend ==
                FfmpegDesktopCaptureBackend
                    .WindowsGraphicsCaptureMonitor &&
            encoder == FfmpegH264Encoder.NvidiaNvenc)
        {
            return defaultTimeout <
                WindowsGraphicsCaptureNvidiaStartupTimeout
                    ? WindowsGraphicsCaptureNvidiaStartupTimeout
                    : defaultTimeout;
        }

        return encoder switch
        {
            FfmpegH264Encoder.AmdAmf =>
                defaultTimeout <
                    DesktopDuplicationAmdStartupTimeout
                        ? DesktopDuplicationAmdStartupTimeout
                        : defaultTimeout,
            FfmpegH264Encoder.MediaFoundation =>
                gopLength == 1
                    ? DesktopDuplicationMediaFoundationAllIndependentStartupTimeout
                    :
                defaultTimeout <
                    DesktopDuplicationMediaFoundationStartupTimeout
                        ? DesktopDuplicationMediaFoundationStartupTimeout
                        : defaultTimeout,
            FfmpegH264Encoder.NvidiaNvenc =>
                defaultTimeout >
                    DesktopDuplicationNvidiaStartupTimeout
                        ? DesktopDuplicationNvidiaStartupTimeout
                        : defaultTimeout,
            _ => defaultTimeout
        };
    }

    internal static bool
        ShouldStopDdaDiscoveryAfterStartupDeadline(
            FfmpegDesktopCaptureBackend backend,
            bool useProductionStartupPolicy,
            bool startupDeadlineExpired)
    {
        return useProductionStartupPolicy &&
            startupDeadlineExpired &&
            backend ==
                FfmpegDesktopCaptureBackend
                    .DesktopDuplicationOutput0;
    }

    private static bool IsGpuSurfaceCaptureBackend(
        FfmpegDesktopCaptureBackend backend)
    {
        return backend is
            FfmpegDesktopCaptureBackend.WindowsGraphicsCaptureMonitor or
            FfmpegDesktopCaptureBackend.DesktopDuplicationOutput0;
    }

    internal static TimeSpan
        CalculateVerifiedReconnectStartupTimeout(
            TimeSpan successfulStartupDuration)
    {
        double learnedMilliseconds = Math.Max(
            0,
            successfulStartupDuration.TotalMilliseconds);
        double timeoutMilliseconds = Math.Clamp(
            learnedMilliseconds * 2.5d,
            VerifiedReconnectMinimumStartupTimeout
                .TotalMilliseconds,
            VerifiedReconnectMaximumStartupTimeout
                .TotalMilliseconds);
        return TimeSpan.FromMilliseconds(
            timeoutMilliseconds);
    }

    internal static void ResetEncoderPreferenceCacheForTests()
    {
        Interlocked.Exchange(
            ref s_windowsGraphicsCapturePreference,
            null);
        Interlocked.Exchange(
            ref s_desktopDuplicationPreference,
            null);
        Interlocked.Exchange(
            ref s_gdiGrabPreference,
            null);
    }

    private static IReadOnlyList<FfmpegH264Encoder>
        GetStartupEncoderCandidates(
            FfmpegDesktopH264CaptureOptions options,
            EncoderPreference? preference,
            bool restrictToVerifiedEncoder)
    {
        IReadOnlyList<FfmpegH264Encoder> candidates =
            GetEncoderCandidates(options);
        if (preference is null)
        {
            return candidates;
        }

        if (restrictToVerifiedEncoder)
        {
            return [preference.Encoder];
        }

        int preferredIndex = -1;
        for (int index = 0; index < candidates.Count; index++)
        {
            if (candidates[index] == preference.Encoder)
            {
                preferredIndex = index;
                break;
            }
        }

        if (preferredIndex <= 0)
        {
            return candidates;
        }

        var reordered =
            new FfmpegH264Encoder[candidates.Count];
        reordered[0] = preference.Encoder;
        int destination = 1;
        for (int index = 0; index < candidates.Count; index++)
        {
            if (index != preferredIndex)
            {
                reordered[destination++] = candidates[index];
            }
        }

        return reordered;
    }

    private static EncoderPreference? ReadEncoderPreference(
        FfmpegDesktopCaptureBackend backend)
    {
        return backend switch
        {
            FfmpegDesktopCaptureBackend
                .WindowsGraphicsCaptureMonitor =>
                Volatile.Read(
                    ref s_windowsGraphicsCapturePreference),
            FfmpegDesktopCaptureBackend.DesktopDuplicationOutput0 =>
                Volatile.Read(
                    ref s_desktopDuplicationPreference),
            FfmpegDesktopCaptureBackend.GdiGrabBounds =>
                Volatile.Read(ref s_gdiGrabPreference),
            _ => null
        };
    }

    private static void RememberEncoderPreference(
        FfmpegDesktopCaptureBackend backend,
        FfmpegH264Encoder encoder,
        string executablePath,
        FfmpegDesktopH264CaptureOptions options,
        TimeSpan successfulStartupDuration)
    {
        var preference = new EncoderPreference(
            encoder,
            executablePath,
            options,
            successfulStartupDuration);
        switch (backend)
        {
            case FfmpegDesktopCaptureBackend
                .WindowsGraphicsCaptureMonitor:
                Interlocked.Exchange(
                    ref s_windowsGraphicsCapturePreference,
                    preference);
                break;
            case FfmpegDesktopCaptureBackend
                .DesktopDuplicationOutput0:
                Interlocked.Exchange(
                    ref s_desktopDuplicationPreference,
                    preference);
                break;
            case FfmpegDesktopCaptureBackend.GdiGrabBounds:
                Interlocked.Exchange(
                    ref s_gdiGrabPreference,
                    preference);
                break;
        }
    }

    private static void ClearEncoderPreferenceIfUnchanged(
        FfmpegDesktopCaptureBackend backend,
        EncoderPreference preference)
    {
        switch (backend)
        {
            case FfmpegDesktopCaptureBackend
                .WindowsGraphicsCaptureMonitor:
                Interlocked.CompareExchange(
                    ref s_windowsGraphicsCapturePreference,
                    null,
                    preference);
                break;
            case FfmpegDesktopCaptureBackend
                .DesktopDuplicationOutput0:
                Interlocked.CompareExchange(
                    ref s_desktopDuplicationPreference,
                    null,
                    preference);
                break;
            case FfmpegDesktopCaptureBackend.GdiGrabBounds:
                Interlocked.CompareExchange(
                    ref s_gdiGrabPreference,
                    null,
                    preference);
                break;
        }
    }

    private static bool PathsEqual(
        string left,
        string right)
    {
        try
        {
            left = Path.GetFullPath(left);
            right = Path.GetFullPath(right);
        }
        catch (Exception ex) when (
            ex is ArgumentException or
                NotSupportedException or
                PathTooLongException)
        {
        }

        return string.Equals(
            left,
            right,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    internal static IReadOnlyList<string> BuildArguments(
        FfmpegDesktopH264CaptureOptions options,
        FfmpegH264Encoder encoder)
    {
        return BuildArgumentsCore(
            options,
            encoder,
            rtpPort: null);
    }

    internal static IReadOnlyList<string> BuildLoopbackRtpArguments(
        FfmpegDesktopH264CaptureOptions options,
        FfmpegH264Encoder encoder,
        int rtpPort)
    {
        if (rtpPort is < 1 or > IPEndPoint.MaxPort)
        {
            throw new ArgumentOutOfRangeException(nameof(rtpPort));
        }

        return BuildArgumentsCore(
            options,
            encoder,
            rtpPort);
    }

    private static IReadOnlyList<string> BuildArgumentsCore(
        FfmpegDesktopH264CaptureOptions options,
        FfmpegH264Encoder encoder,
        int? rtpPort)
    {
        string? validationFailure = ValidateOptions(options);
        if (validationFailure is not null)
        {
            throw new ArgumentException(
                validationFailure,
                nameof(options));
        }

        int bitrateFramesPerSecond =
            options.BitrateFramesPerSecond ??
            options.FramesPerSecond;
        int bitrate = CalculateBitrateBitsPerSecond(
            options.OutputSize,
            bitrateFramesPerSecond);
        int vbvBuffer = CalculateVbvBufferBits(
            bitrate,
            bitrateFramesPerSecond);
        var arguments = new List<string>
        {
            "-hide_banner",
            "-loglevel",
            "error"
        };

        // The viewer keeps its WinForms cursor local, so compositing the host
        // cursor into a 30 FPS encoded frame only adds a delayed duplicate and
        // makes pointer motion feel laggy. Keep every Windows capture backend
        // cursor-free while input remains on the independent control path.
        if (options.Backend ==
            FfmpegDesktopCaptureBackend
                .WindowsGraphicsCaptureMonitor)
        {
            WindowsGraphicsCaptureTarget target =
                options.GraphicsCaptureTarget!;
            string resizeOptions =
                options.OutputSize == target.Bounds.Size
                    ? "output_fmt=bgra:resize_mode=crop"
                    : string.Create(
                        CultureInfo.InvariantCulture,
                        $"width={options.OutputSize.Width}:height={options.OutputSize.Height}:output_fmt=bgra:resize_mode=scale_aspect:scale_mode=bicubic");
            bool recoverNativeUltraHd60Cadence =
                options.FramesPerSecond == 60 &&
                options.OutputSize == target.Bounds.Size &&
                options.OutputSize.Width >= options.OutputSize.Height &&
                (long)options.OutputSize.Width *
                    options.OutputSize.Height >=
                    NativeUltraHdPixelCount;
            int captureFramesPerSecond =
                recoverNativeUltraHd60Cadence
                    ? WindowsGraphicsCaptureUltraHdSamplingFps
                    : options.FramesPerSecond;
            string cadenceFilter = string.Empty;
            if (recoverNativeUltraHd60Cadence)
            {
                // Select, rather than synthesize, the first source frame in
                // each 60 Hz bucket. Rebasing the selected sequence before
                // fps declares the encoder rate while preserving one input
                // surface per output frame even after a static WGC pause.
                cadenceFilter =
                    ",setpts=PTS-STARTPTS," +
                    "select='isnan(prev_selected_t)+" +
                    "gt(floor(t*60),floor(prev_selected_t*60))'," +
                    "settb=expr=1/60000,setpts=N*1000," +
                    "fps=fps=60:start_time=0:round=near";
            }
            arguments.AddRange(
            [
                // Force gfxcapture, its D3D11 frame pool and the encoder onto
                // this resolver-selected encode adapter. On the measured
                // AMD-display/NVIDIA-render machine, selecting the non-owner
                // NVIDIA adapter is the proven direct BGRA -> NVENC path.
                "-init_hw_device",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"d3d11va=wgc:{target.AdapterIndex}"),
                "-filter_hw_device",
                "wgc",
                "-filter_complex",
                // gfxcapture owns a two-frame WGC/D3D11 pool and emits BGRA
                // D3D11 surfaces. Native mode is passed through unchanged;
                // the explicit 1440p ceiling uses gfxcapture's own GPU
                // scaler and still reaches AMF/NVENC/QSV without a CPU
                // download or an extra software scale filter.
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"gfxcapture=monitor_idx={target.MonitorIndex}:capture_cursor=0:display_border=0:max_framerate={captureFramesPerSecond}:{resizeOptions}{cadenceFilter}")
            ]);
        }
        else if (options.Backend ==
            FfmpegDesktopCaptureBackend.DesktopDuplicationOutput0)
        {
            WindowsDesktopDuplicationTarget target =
                options.DesktopDuplicationTarget!;
            arguments.AddRange(
            [
                // output_idx is local to the D3D11 adapter selected here; it
                // is not the global EnumDisplayMonitors index. Binding both
                // values prevents a secondary display on another GPU from
                // silently capturing adapter 0/output 0 by accident.
                "-init_hw_device",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"d3d11va=dda:{target.AdapterIndex}"),
                "-filter_hw_device",
                "dda",
                "-filter_complex",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"ddagrab=output_idx={target.OutputIndex}:draw_mouse=0:framerate={options.FramesPerSecond}:dup_frames=1,scale_d3d11=width={options.OutputSize.Width}:height={options.OutputSize.Height}:format=nv12")
            ]);
        }
        else
        {
            string conversionFilter =
                options.OutputSize == options.TargetBounds.Size
                    ? "format=nv12"
                    : string.Create(
                        CultureInfo.InvariantCulture,
                        $"scale={options.OutputSize.Width}:{options.OutputSize.Height}:flags=bicubic+accurate_rnd,format=nv12");
            arguments.AddRange(
            [
                "-f",
                "gdigrab",
                "-draw_mouse",
                "0",
                "-framerate",
                FormatInvariant(options.FramesPerSecond),
                "-offset_x",
                FormatInvariant(options.TargetBounds.Left),
                "-offset_y",
                FormatInvariant(options.TargetBounds.Top),
                "-video_size",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{options.TargetBounds.Width}x{options.TargetBounds.Height}"),
                "-i",
                "desktop",
                "-vf",
                conversionFilter
            ]);
            if (rtpPort is not null)
            {
                // Unlike the unlabeled filter_complex output used by DDA,
                // tee does not auto-select a simple input stream. Map the GDI
                // video explicitly or FFmpeg rejects the fallback candidate
                // with "Output file does not contain any stream".
                arguments.AddRange(["-map", "0:v:0"]);
            }
        }

        AddEncoderArguments(
            arguments,
            encoder,
            bitrate,
            vbvBuffer,
            options.GopLength);
        arguments.AddRange(
        [
            "-an",
            "-sn",
            "-dn",
            // Preserve the DDA source timestamps instead of allowing the
            // automatic output synchronizer to batch/drop live frames.
            "-fps_mode",
            "passthrough",
            // Keep tee/RTP and pipe writes direct and packet-flushed so the
            // host reader observes each encoded access unit immediately.
            "-avioflags",
            "direct",
            "-bsf:v",
            // Repeat SPS/PPS on every recovery point. In negotiated GOP=2
            // mode this makes each IDR/P pair independently disposable, while
            // legacy GOP=1 peers retain the previous all-IDR contract.
            "dump_extra=freq=keyframe,h264_metadata=aud=insert",
            "-flush_packets",
            "1"
        ]);
        if (rtpPort is int port)
        {
            arguments.AddRange(
            [
                "-f",
                "tee",
                // Bind the private RTP sender to loopback explicitly. FFmpeg
                // otherwise lets Winsock choose 0.0.0.0, which can trigger a
                // Windows Firewall consent dialog even though the receiver
                // and destination are both local to this process.
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"[f=rtp:payload_type={RtpPayloadType}:rtpflags=skip_rtcp:onfail=ignore]rtp://127.0.0.1:{port}?pkt_size={LoopbackRtpPacketSizeBytes}&connect=1&localaddr=127.0.0.1|[f=h264]pipe:1")
            ]);
        }
        else
        {
            arguments.AddRange(
            [
                "-f",
                "h264",
                "pipe:1"
            ]);
        }

        return arguments;
    }

    internal static int CalculateBitrateBitsPerSecond(
        Size outputSize,
        int framesPerSecond)
    {
        if (outputSize.Width <= 0 || outputSize.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputSize));
        }

        if (framesPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        }

        long pixelCount = checked(
            (long)outputSize.Width *
            outputSize.Height);
        double ultraHdBitsPerPixelPerFrame;
        if (framesPerSecond <= 30)
        {
            ultraHdBitsPerPixelPerFrame =
                NativeUltraHdBitsPerPixelPerFrame;
        }
        else if (framesPerSecond >= 60)
        {
            ultraHdBitsPerPixelPerFrame =
                NativeUltraHdHighFrameRateBitsPerPixelPerFrame;
        }
        else
        {
            double highFrameRateBlend =
                (framesPerSecond - 30d) / 30d;
            ultraHdBitsPerPixelPerFrame =
                NativeUltraHdBitsPerPixelPerFrame +
                (NativeUltraHdHighFrameRateBitsPerPixelPerFrame -
                    NativeUltraHdBitsPerPixelPerFrame) *
                highFrameRateBlend;
        }

        double ultraHdResolutionBlend = Math.Clamp(
            (pixelCount - QuadHdPixelCount) /
                (double)(NativeUltraHdPixelCount - QuadHdPixelCount),
            0d,
            1d);
        double bitsPerPixelPerFrame =
            StandardBitsPerPixelPerFrame +
            (ultraHdBitsPerPixelPerFrame -
                StandardBitsPerPixelPerFrame) *
            ultraHdResolutionBlend;
        double calculated =
            pixelCount *
            framesPerSecond *
            bitsPerPixelPerFrame;
        int bounded = (int)Math.Clamp(
            calculated,
            MinimumBitrateBitsPerSecond,
            MaximumBitrateBitsPerSecond);
        return Math.Max(
            MinimumBitrateBitsPerSecond,
            checked((bounded / 100_000) * 100_000));
    }

    internal static int CalculateVbvBufferBits(
        int bitrateBitsPerSecond,
        int framesPerSecond)
    {
        if (bitrateBitsPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bitrateBitsPerSecond));
        }

        if (framesPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        }

        // A bounded four-frame reservoir lets a static desktop refresh retain
        // small text and one-pixel edges. It remains below the transport's
        // frame limit and does not add reordering or lookahead latency.
        return Math.Max(
            64_000,
            checked(
                (int)Math.Ceiling(
                    bitrateBitsPerSecond /
                    (double)framesPerSecond *
                    DesktopVbvFrameCapacity)));
    }

    internal static int CalculateLoopbackRtpReceiveBufferBytes(
        FfmpegDesktopH264CaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        string? validationFailure = ValidateOptions(options);
        if (validationFailure is not null)
        {
            throw new ArgumentException(
                validationFailure,
                nameof(options));
        }

        int bitrateFramesPerSecond =
            options.BitrateFramesPerSecond ??
            options.FramesPerSecond;
        int bitrate = CalculateBitrateBitsPerSecond(
            options.OutputSize,
            bitrateFramesPerSecond);
        int oneFrameVbvBytes = checked(
            (int)Math.Ceiling(
                CalculateVbvBufferBits(
                    bitrate,
                    bitrateFramesPerSecond) /
                8d));
        // Two bounded VBV reservoirs size ordinary startup traffic. The clamp also
        // reserves enough space for the measured multi-frame MF startup burst.
        long requestedBytes = checked(
            (long)oneFrameVbvBytes * 2);
        return (int)Math.Clamp(
            requestedBytes,
            MinimumRtpReceiveBufferBytes,
            MaximumRtpReceiveBufferBytes);
    }

    private static void AddEncoderArguments(
        List<string> arguments,
        FfmpegH264Encoder encoder,
        int bitrate,
        int vbvBuffer,
        int gopLength)
    {
        string bitrateText = FormatInvariant(bitrate);
        string vbvText = FormatInvariant(vbvBuffer);
        string gopLengthText = FormatInvariant(gopLength);
        switch (encoder)
        {
            case FfmpegH264Encoder.NvidiaNvenc:
                arguments.AddRange(
                [
                    "-c:v", "h264_nvenc",
                    // P4 and spatial AQ materially improve desktop text and
                    // edge retention while the ULL/single-surface settings retain the
                    // same no-lookahead, one-frame pipeline.
                    "-preset", "p4",
                    "-tune", "ull",
                    // NVENC CBR emits filler NAL units even for a nearly
                    // static desktop. Capped VBR preserves the same target,
                    // peak rate and bounded VBV, but stops spending Wi-Fi
                    // airtime on padding that carries no picture data.
                    "-rc", "vbr",
                    "-spatial-aq", "1",
                    // A mild strength retains thin UI strokes. Aggressive AQ
                    // favors textured regions and can soften flat desktop text.
                    "-aq-strength", "1",
                    "-b:v", bitrateText,
                    "-maxrate", bitrateText,
                    "-bufsize", vbvText,
                    "-g", gopLengthText,
                    "-bf", "0",
                    "-rc-lookahead", "0",
                    // One in-flight NVENC surface is sufficient for the
                    // synchronous GOP=1/GOP=2 low-latency contract.
                    "-surfaces", "1",
                    "-delay", "0",
                    "-zerolatency", "1",
                    "-forced-idr", "1",
                    "-aud", "1"
                ]);
                break;
            case FfmpegH264Encoder.MediaFoundation:
                arguments.AddRange(
                [
                    "-c:v", "h264_mf",
                    "-hw_encoding", "1",
                    // CODECAPI_AVLowLatencyMode is only exposed by newer
                    // FFmpeg builds. FFmpeg 8.0 still exposes the Windows
                    // low-delay VBR mode, which avoids the MFT's ordinary CBR
                    // output batching while the bounded VBV and maxrate
                    // continue to bound each interactive burst.
                    "-rate_control", "ld_vbr",
                    "-scenario", "display_remoting",
                    "-b:v", bitrateText,
                    "-maxrate", bitrateText,
                    "-bufsize", vbvText,
                    "-g", gopLengthText,
                    "-bf", "0",
                    "-flags", "+low_delay"
                ]);
                break;
            case FfmpegH264Encoder.IntelQuickSync:
                arguments.AddRange(
                [
                    "-c:v", "h264_qsv",
                    "-preset", "veryfast",
                    "-async_depth", "1",
                    "-low_delay_brc", "1",
                    "-low_power", "1",
                    "-scenario", "remotegaming",
                    "-vcm", "1",
                    "-max_dec_frame_buffering", "1",
                    "-look_ahead", "0",
                    "-forced_idr", "1",
                    "-b:v", bitrateText,
                    "-maxrate", bitrateText,
                    "-bufsize", vbvText,
                    "-g", gopLengthText,
                    "-bf", "0",
                    "-aud", "1"
                ]);
                break;
            case FfmpegH264Encoder.AmdAmf:
                arguments.AddRange(
                [
                    "-c:v", "h264_amf",
                    "-usage", "ultralowlatency",
                    "-latency", "1",
                    "-quality", "balanced",
                    "-rc", "cbr",
                    "-b:v", bitrateText,
                    "-maxrate", bitrateText,
                    "-bufsize", vbvText,
                    "-g", gopLengthText,
                    "-bf", "0",
                    "-async_depth", "1",
                    "-frame_skipping", "0",
                    "-preanalysis", "0",
                    "-forced_idr", "1",
                    "-aud", "1"
                ]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(encoder));
        }
    }

    private async Task<bool> WaitForStartupAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Task timeoutTask = Task.Delay(timeout, cancellationToken);
        Task completed = await Task.WhenAny(
            _firstFrameReady.Task,
            _process.Completion,
            timeoutTask).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (ReferenceEquals(completed, _process.Completion))
        {
            await WaitForStartupDiagnosticsAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            Fail(FormatUnexpectedProcessStop(
                "ffmpeg H.264 process stopped during startup."));
            return false;
        }

        if (ReferenceEquals(completed, timeoutTask))
        {
            Volatile.Write(
                ref _startupDeadlineExpired,
                1);
            Fail(
                $"No independently decodable H.264 access unit arrived " +
                $"within {timeout.TotalMilliseconds:0} ms.");
            return false;
        }

        bool ready = await _firstFrameReady.Task.ConfigureAwait(false);
        if (!ready)
        {
            await WaitForStartupDiagnosticsAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            return false;
        }

        await Task.Yield();
        if (IsRunning)
        {
            return true;
        }

        if (_process.HasExited)
        {
            Fail(FormatUnexpectedProcessStop(
                "ffmpeg H.264 process stopped during startup."));
        }

        await WaitForStartupDiagnosticsAsync(
                cancellationToken)
            .ConfigureAwait(false);
        return false;
    }

    private async Task WaitForStartupDiagnosticsAsync(
        CancellationToken cancellationToken)
    {
        if (_stderrTask.IsCompleted)
        {
            await ObserveExpectedReaderCompletionAsync(
                    _stderrTask)
                .ConfigureAwait(false);
            return;
        }

        Task diagnosticDeadline = Task.Delay(
            TimeSpan.FromMilliseconds(100),
            cancellationToken);
        Task completed = await Task.WhenAny(
                _stderrTask,
                diagnosticDeadline)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (ReferenceEquals(completed, _stderrTask))
        {
            await ObserveExpectedReaderCompletionAsync(
                    _stderrTask)
                .ConfigureAwait(false);
        }
    }

    private static async Task ObserveExpectedReaderCompletionAsync(
        Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (
            IsExpectedReaderStopException(ex))
        {
        }
    }

    private async Task ReadRawAnnexBOutputAsync()
    {
        byte[] buffer = new byte[ReaderBufferBytes];
        AnnexBH264AccessUnitParser? rawOnlyParser =
            _rtpReceiver is null
                ? new AnnexBH264AccessUnitParser()
                : null;
        try
        {
            while (!_captureCancellation.IsCancellationRequested)
            {
                int read = await _process.StandardOutput.ReadAsync(
                    buffer.AsMemory(),
                    _captureCancellation.Token).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                RawFallbackParserState? fallbackState =
                    _rtpReceiver is null
                        ? null
                        : GetOrCreateRawFallbackParserState();
                AnnexBH264AccessUnitParser? parser =
                    rawOnlyParser ??
                    fallbackState?.Parser;
                if (parser is null)
                {
                    // The RTP marker path is healthy. The tee branch must
                    // still be drained so FFmpeg cannot block on stdout, but
                    // avoid scanning and allocating every duplicate 4K AU.
                    continue;
                }

                if (!PublishRawAccessUnits(
                        parser.Append(
                            buffer.AsSpan(0, read)),
                        fallbackState))
                {
                    return;
                }
            }

            if (!_captureCancellation.IsCancellationRequested)
            {
                RawFallbackParserState? fallbackState =
                    _rtpReceiver is null
                        ? null
                        : GetOrCreateRawFallbackParserState();
                AnnexBH264AccessUnitParser? parser =
                    rawOnlyParser ??
                    fallbackState?.Parser;
                if (parser is not null &&
                    !PublishRawAccessUnits(
                        parser.Complete(),
                        fallbackState))
                {
                    return;
                }

                Fail(FormatUnexpectedProcessStop(
                    "ffmpeg H.264 stdout ended."));
            }
        }
        catch (Exception ex) when (
            ex is OperationCanceledException or
                IOException or
                ObjectDisposedException or
                InvalidDataException)
        {
            if (!_captureCancellation.IsCancellationRequested)
            {
                Fail($"ffmpeg H.264 output failed: {ex.Message}");
            }
        }
    }

    private bool PublishRawAccessUnits(
        IReadOnlyList<AnnexBH264AccessUnit> accessUnits,
        RawFallbackParserState? fallbackState)
    {
        bool keepPublishing = true;
        int accessUnitIndex = 0;
        try
        {
            for (;
                 accessUnitIndex < accessUnits.Count;
                 accessUnitIndex++)
            {
                AnnexBH264AccessUnit accessUnit =
                    accessUnits[accessUnitIndex];
                using (accessUnit)
                {
                    // Dispose every unit even after a prior publication closed
                    // the capture. A parser call can return several completed
                    // units at once, and future parser implementations may pool
                    // those buffers as the RTP path does.
                    if (!keepPublishing)
                    {
                        continue;
                    }

                    if (fallbackState?.NeedsRecovery == true)
                    {
                        if (!IsRecoveryAccessUnit(accessUnit))
                        {
                            continue;
                        }

                        fallbackState.NeedsRecovery = false;
                    }

                    if (!PublishAccessUnit(
                            accessUnit,
                            fromRtp: false,
                            rawFallback:
                                _rtpReceiver is not null))
                    {
                        keepPublishing = false;
                    }
                }
            }
        }
        finally
        {
            // The current item is safe to dispose twice. This loop matters
            // when validation or publication throws before the for-loop can
            // visit the remaining units returned in the same parser batch.
            for (;
                 accessUnitIndex < accessUnits.Count;
                 accessUnitIndex++)
            {
                accessUnits[accessUnitIndex].Dispose();
            }
        }

        return keepPublishing;
    }

    private RawFallbackParserState?
        GetOrCreateRawFallbackParserState()
    {
        long lastRtpFrameAt =
            Volatile.Read(ref _lastRtpFrameAt);
        long observedAt = Stopwatch.GetTimestamp();
        if (!ShouldParseRawAnnexBFallback(
                lastRtpFrameAt,
                observedAt,
                _options.FramesPerSecond))
        {
            ReleaseRawFallbackParser();
            return null;
        }

        RawFallbackParserState? state =
            Volatile.Read(
                ref _rawFallbackParserState);
        if (state is null)
        {
            var created = new RawFallbackParserState(
                needsRecovery:
                    lastRtpFrameAt != 0);
            state = Interlocked.CompareExchange(
                    ref _rawFallbackParserState,
                    created,
                    null) ??
                created;
            if (ReferenceEquals(state, created))
            {
                Interlocked.Increment(
                    ref _rawFallbackParserGeneration);
            }
        }

        // RTP can recover between the first timestamp check and parser
        // creation. Recheck after publication has had a chance to atomically
        // clear the field so a stale stdout read cannot resurrect the parser.
        lastRtpFrameAt =
            Volatile.Read(ref _lastRtpFrameAt);
        observedAt = Stopwatch.GetTimestamp();
        if (ShouldParseRawAnnexBFallback(
                lastRtpFrameAt,
                observedAt,
                _options.FramesPerSecond))
        {
            return state;
        }

        Interlocked.CompareExchange(
            ref _rawFallbackParserState,
            null,
            state);
        return null;
    }

    private void ReleaseRawFallbackParser()
    {
        Interlocked.Exchange(
            ref _rawFallbackParserState,
            null);
    }

    private void ReadRtpOutput()
    {
        IFfmpegDesktopH264RtpReceiver receiver =
            _rtpReceiver ??
            throw new InvalidOperationException(
                "Loopback RTP receiver is unavailable.");
        byte[] datagram = new byte[MaximumUdpDatagramBytes];
        var assembler = new H264RtpAccessUnitAssembler(
            accessUnitPool: _accessUnitPool);
        AnnexBH264AccessUnit? pendingAccessUnit = null;
        bool firstRtpAccessUnitPublished = false;
        try
        {
            while (!_captureCancellation.IsCancellationRequested)
            {
                // Keep the receive and AU publication continuation on this
                // dedicated high-priority thread. Blocking on the socket's
                // ValueTask avoids an IOCP continuation hopping through the
                // shared thread pool and releasing several already-arrived
                // frames as one visible burst.
                int received = receiver.ReceiveAsync(
                        datagram.AsMemory(),
                        _captureCancellation.Token)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
                if (received <= 0)
                {
                    Fail(
                        "ffmpeg loopback RTP receiver ended unexpectedly.");
                    return;
                }

                if (received > LoopbackRtpPacketSizeBytes)
                {
                    Fail(
                        $"ffmpeg loopback RTP datagram exceeded the " +
                        $"{LoopbackRtpPacketSizeBytes}-byte private " +
                        "loopback limit.");
                    return;
                }

                AnnexBH264AccessUnit? accessUnit =
                    assembler.AppendPacket(
                        datagram.AsSpan(0, received));
                if (accessUnit is not null)
                {
                    if (firstRtpAccessUnitPublished ||
                        IsRecoveryAccessUnit(accessUnit))
                    {
                        pendingAccessUnit?.Dispose();
                        pendingAccessUnit = accessUnit;
                    }
                    else
                    {
                        accessUnit.Dispose();
                    }
                }

                // During hardware cold start, the kernel can already contain
                // a burst of old complete AUs. Drain only that initial backlog
                // before publishing the first recovery point. Once live,
                // publish every complete AU immediately; the existing
                // latest-only mailbox replaces a genuinely stale frame
                // without suppressing a sustained high-frame-rate stream just
                // because the next RTP packet is already queued.
                if (pendingAccessUnit is null ||
                    (!firstRtpAccessUnitPublished &&
                     receiver.HasPendingDatagrams))
                {
                    continue;
                }

                AnnexBH264AccessUnit readyAccessUnit =
                    pendingAccessUnit;
                pendingAccessUnit = null;
                using (readyAccessUnit)
                {
                    if (!PublishAccessUnit(
                            readyAccessUnit,
                            fromRtp: true,
                            rawFallback: false))
                    {
                        return;
                    }
                }

                firstRtpAccessUnitPublished = true;
            }
        }
        catch (Exception ex) when (
            ex is OperationCanceledException or
                IOException or
                ObjectDisposedException or
                SocketException or
                InvalidDataException)
        {
            if (!_captureCancellation.IsCancellationRequested)
            {
                Fail(
                    $"ffmpeg loopback RTP output failed: {ex.Message}");
            }
        }
        finally
        {
            pendingAccessUnit?.Dispose();
        }
    }

    private async Task DrainStandardOutputAsync()
    {
        byte[] buffer = new byte[ReaderBufferBytes];
        try
        {
            while (!_captureCancellation.IsCancellationRequested)
            {
                int read = await _process.StandardOutput.ReadAsync(
                    buffer.AsMemory(),
                    _captureCancellation.Token).ConfigureAwait(false);
                if (read <= 0)
                {
                    if (!_captureCancellation.IsCancellationRequested &&
                        _process.HasExited)
                    {
                        Fail(FormatUnexpectedProcessStop(
                            "ffmpeg H.264 process stdout ended."));
                    }

                    break;
                }
            }
        }
        catch (Exception ex) when (
            ex is OperationCanceledException or
                IOException or
                ObjectDisposedException)
        {
        }
    }

    private async Task DrainStandardErrorAsync()
    {
        char[] buffer = new char[1024];
        try
        {
            // stdout EOF / a failed first frame cancels video reading before
            // stderr has necessarily delivered the encoder's actual error.
            // Drain that separate pipe to EOF, including buffered tail chunks.
            // Fail/Dispose terminate the owned process; diagnostics waiting and
            // reader shutdown remain bounded and disposal closes the pipe.
            while (true)
            {
                int read = await _process.StandardError.ReadAsync(
                    buffer.AsMemory()).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                AppendErrorText(new string(buffer, 0, read));
            }
        }
        catch (Exception ex) when (
            ex is OperationCanceledException or
                IOException or
                ObjectDisposedException)
        {
        }
    }

    private async Task WatchdogAsync()
    {
        TimeSpan pollInterval = TimeSpan.FromMilliseconds(
            Math.Clamp(
                _stallTimeout.TotalMilliseconds / 4d,
                25d,
                250d));
        try
        {
            while (!_captureCancellation.IsCancellationRequested)
            {
                await Task.Delay(
                    pollInterval,
                    _captureCancellation.Token).ConfigureAwait(false);
                if (_process.HasExited)
                {
                    Fail(FormatUnexpectedProcessStop(
                        "ffmpeg H.264 process stopped."));
                    return;
                }

                if (Volatile.Read(ref _lastFrameAt) != 0 &&
                    IsStalled &&
                    !_options.AllowStaticFrameSilence)
                {
                    Fail(
                        $"ffmpeg H.264 output stalled for at least " +
                        $"{_stallTimeout.TotalMilliseconds:0} ms.");
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool PublishAccessUnit(
        AnnexBH264AccessUnit accessUnit,
        bool fromRtp = false,
        bool rawFallback = false)
    {
        long producedAtTimestamp = Stopwatch.GetTimestamp();
        lock (_publishLock)
        {
            if (fromRtp)
            {
                Volatile.Write(
                    ref _lastRtpFrameAt,
                    producedAtTimestamp);
                ReleaseRawFallbackParser();
            }
            else if (rawFallback)
            {
                long lastRtpFrameAt =
                    Volatile.Read(ref _lastRtpFrameAt);
                if (lastRtpFrameAt != 0 &&
                    Stopwatch.GetElapsedTime(
                        lastRtpFrameAt,
                        producedAtTimestamp) <
                        CalculateRtpFallbackSilence(
                            _options.FramesPerSecond))
                {
                    return true;
                }
            }

            if (!ValidateAccessUnitSequence(accessUnit))
            {
                return false;
            }

            bool recovery = IsRecoveryAccessUnit(accessUnit);
            if (!recovery &&
                Volatile.Read(
                    ref _pendingRecoveryTimestamp) != 0)
            {
                // Never let the P half of a GOP replace its recovery half in
                // the latest-only mailbox. The next GOP arrives within two
                // source periods, so dropping this P is both safe and fresh.
                return true;
            }

            H264FrameBufferLease frameBuffer =
                accessUnit.DetachBufferOwnership();
            FfmpegDesktopH264Frame frame;
            try
            {
                frame = new FfmpegDesktopH264Frame(
                    _options.OutputSize.Width,
                    _options.OutputSize.Height,
                    accessUnit.Flags,
                    frameBuffer,
                    producedAtTimestamp);
            }
            catch
            {
                frameBuffer.Dispose();
                throw;
            }

            LatestFrameOffer<FfmpegDesktopH264Frame> offer =
                _mailbox.Offer(frame);
            if (!offer.Accepted)
            {
                frame.Dispose();
                return false;
            }

            offer.Replaced?.Dispose();
            if (recovery)
            {
                Volatile.Write(
                    ref _pendingRecoveryTimestamp,
                    producedAtTimestamp);
                _hasPublishedRecoveryFrame = true;
            }

            Volatile.Write(ref _lastFrameAt, producedAtTimestamp);
            if (offer.ShouldSchedule)
            {
                SignalFrameReader();
            }

            if (recovery)
            {
                _firstFrameReady.TrySetResult(true);
            }

            return true;
        }
    }

    internal static TimeSpan CalculateRtpFallbackSilence(
        int framesPerSecond)
    {
        if (framesPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(framesPerSecond));
        }

        // Raw Annex-B framing completes at the following AUD, so it normally
        // trails the RTP marker by roughly one frame. Allow two frame periods
        // (and at least 50 ms) before treating stdout as the authoritative
        // fallback, avoiding duplicate publication on a healthy RTP path.
        return TimeSpan.FromMilliseconds(
            Math.Max(
                50d,
                2_000d / framesPerSecond));
    }

    internal static bool ShouldParseRawAnnexBFallback(
        long lastRtpFrameAt,
        long observedAt,
        int framesPerSecond)
    {
        if (lastRtpFrameAt == 0)
        {
            return true;
        }

        // A concurrently published RTP frame can have a timestamp newer than
        // the stdout reader's observation. Treat it as healthy rather than
        // passing a reversed interval to Stopwatch.
        return observedAt > lastRtpFrameAt &&
            Stopwatch.GetElapsedTime(
                lastRtpFrameAt,
                observedAt) >=
                CalculateRtpFallbackSilence(
                    framesPerSecond);
    }

    private bool ValidateAccessUnitSequence(
        AnnexBH264AccessUnit accessUnit)
    {
        bool recovery = IsRecoveryAccessUnit(accessUnit);
        if (_options.GopLength == 1)
        {
            if (recovery)
            {
                return true;
            }

            Fail(
                "ffmpeg produced a dependent H.264 frame despite the " +
                "negotiated GOP=1 all-IDR contract.");
            return false;
        }

        if (recovery)
        {
            _dependentFrameAllowed = true;
            return true;
        }

        if (_hasPublishedRecoveryFrame &&
            _dependentFrameAllowed &&
            !accessUnit.IsIdr &&
            !accessUnit.Flags.HasFlag(
                RemoteFrameFlags.KeyFrame))
        {
            _dependentFrameAllowed = false;
            return true;
        }

        Fail(
            "ffmpeg violated the negotiated GOP=2 IDR/P contract.");
        return false;
    }

    private static bool IsRecoveryAccessUnit(
        AnnexBH264AccessUnit accessUnit) =>
        accessUnit.IsIdr &&
        (accessUnit.Flags & IndependentFrameFlags) ==
            IndependentFrameFlags;

    private sealed class RawFallbackParserState
    {
        public RawFallbackParserState(
            bool needsRecovery)
        {
            NeedsRecovery = needsRecovery;
        }

        public AnnexBH264AccessUnitParser Parser { get; } =
            new();

        public bool NeedsRecovery { get; set; }
    }

    private bool TryTakeSignaledFrame(
        out FfmpegDesktopH264Frame? frame)
    {
        frame = _mailbox.TakeLatest();
        if (frame is not null &&
            (frame.Flags & IndependentFrameFlags) ==
                IndependentFrameFlags)
        {
            Interlocked.CompareExchange(
                ref _pendingRecoveryTimestamp,
                0,
                frame.ProducedAtTimestamp);
        }

        bool needsFollowUp = _mailbox.CompleteDispatch();
        if (needsFollowUp)
        {
            SignalFrameReader();
        }

        return frame is not null;
    }

    private void Fail(string reason)
    {
        if (Volatile.Read(ref _shutdownRequested) != 0)
        {
            _completion.TrySetResult();
            SignalFrameReader();
            return;
        }

        bool firstFailure = false;
        lock (_failureLock)
        {
            if (string.IsNullOrWhiteSpace(_failureReason))
            {
                _failureReason = reason;
                firstFailure = true;
            }
        }

        if (!firstFailure)
        {
            return;
        }

        _mailbox.Close()?.Dispose();
        _firstFrameReady.TrySetResult(false);
        _captureCancellation.Cancel();
        DisposeRtpReceiver();
        TryKillProcess();
        _completion.TrySetResult();
        SignalFrameReader();
    }

    private bool HasFailure
    {
        get
        {
            lock (_failureLock)
            {
                return !string.IsNullOrWhiteSpace(_failureReason);
            }
        }
    }

    private void AppendErrorText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        lock (_errorLock)
        {
            _stderrTail = string.Concat(_stderrTail, text);
            if (_stderrTail.Length > ErrorTailLength)
            {
                _stderrTail = _stderrTail[^ErrorTailLength..];
            }
        }
    }

    private string FormatUnexpectedProcessStop(string prefix)
    {
        return _process.ExitCode is int exitCode
            ? $"{prefix} Exit code: {exitCode}."
            : prefix;
    }

    private void TryKillProcess()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.KillEntireProcessTree();
            }
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or
                ObjectDisposedException or
                System.ComponentModel.Win32Exception or
                NotSupportedException)
        {
        }
    }

    private async Task ReapUnconfirmedProcessExitAsync()
    {
        while (!_process.HasExited)
        {
            // Keep the process and Job handles alive until exit is positively
            // observed. Re-closing a still-running Process object would lose
            // the only reliable way to reap a driver-stalled FFmpeg process.
            TryKillProcess();
            Task retryDelay = Task.Delay(
                UnconfirmedExitReaperRetryMilliseconds);
            Task completed = await Task.WhenAny(
                    _process.Completion,
                    retryDelay)
                .ConfigureAwait(false);
            if (ReferenceEquals(
                    completed,
                    _process.Completion) &&
                !_process.HasExited)
            {
                // A faulted/early completion must not create a busy retry
                // loop while the native process is still observable.
                await retryDelay.ConfigureAwait(false);
            }
        }

        _process.Dispose();
    }

    private void DisposeRtpReceiver()
    {
        if (_rtpReceiver is null ||
            Interlocked.Exchange(
                ref _rtpReceiverDisposed,
                1) != 0)
        {
            return;
        }

        try
        {
            _rtpReceiver.Dispose();
        }
        catch (Exception ex) when (
            ex is IOException or
                ObjectDisposedException or
                SocketException)
        {
        }
    }

    private void SignalFrameReader()
    {
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

    private void WaitForBackgroundTasks(int milliseconds)
    {
        try
        {
            Task.WaitAll(
                [
                    _videoOutputTask,
                    _stdoutDrainTask,
                    _stderrTask,
                    _watchdogTask
                ],
                milliseconds);
        }
        catch (AggregateException ex) when (
            ex.InnerExceptions.All(IsExpectedReaderStopException))
        {
        }
        catch (Exception ex) when (IsExpectedReaderStopException(ex))
        {
        }
    }

    private static bool IsExpectedReaderStopException(Exception ex)
    {
        return ex is OperationCanceledException or
            IOException or
            ObjectDisposedException;
    }

    private static string? ValidateOptions(
        FfmpegDesktopH264CaptureOptions options)
    {
        if (!Enum.IsDefined(options.Backend))
        {
            return "Unknown FFmpeg desktop capture backend.";
        }

        if (options.Backend ==
            FfmpegDesktopCaptureBackend
                .WindowsGraphicsCaptureMonitor)
        {
            WindowsGraphicsCaptureTarget? graphicsTarget =
                options.GraphicsCaptureTarget;
            if (graphicsTarget is null)
            {
                return "Windows Graphics Capture requires a resolved " +
                    "monitor and DXGI adapter.";
            }

            if (graphicsTarget.MonitorIndex < 0 ||
                graphicsTarget.AdapterIndex < 0)
            {
                return "Windows Graphics Capture monitor and adapter " +
                    "indices must be non-negative.";
            }

            if (string.IsNullOrWhiteSpace(
                    graphicsTarget.DeviceName))
            {
                return "Windows Graphics Capture requires a physical " +
                    "display device name.";
            }

            if (graphicsTarget.Bounds != options.TargetBounds)
            {
                return "Windows Graphics Capture target bounds no longer " +
                    "match the selected display.";
            }

            if (options.OutputSize.Width >
                    graphicsTarget.Bounds.Width ||
                options.OutputSize.Height >
                    graphicsTarget.Bounds.Height)
            {
                return "Windows Graphics Capture output dimensions cannot " +
                    "upscale beyond the selected display.";
            }
        }

        if (options.AllowStaticFrameSilence &&
            options.Backend !=
                FfmpegDesktopCaptureBackend
                    .WindowsGraphicsCaptureMonitor)
        {
            return "Static frame silence is supported only by Windows " +
                "Graphics Capture.";
        }

        if (options.Backend ==
            FfmpegDesktopCaptureBackend
                .DesktopDuplicationOutput0)
        {
            WindowsDesktopDuplicationTarget? duplicationTarget =
                options.DesktopDuplicationTarget;
            if (duplicationTarget is null)
            {
                return "DXGI Desktop Duplication requires a resolved " +
                    "adapter and per-adapter output.";
            }

            if (duplicationTarget.AdapterIndex < 0 ||
                duplicationTarget.OutputIndex < 0)
            {
                return "DXGI Desktop Duplication adapter and output " +
                    "indices must be non-negative.";
            }

            if (string.IsNullOrWhiteSpace(
                    duplicationTarget.DeviceName))
            {
                return "DXGI Desktop Duplication requires a physical " +
                    "display device name.";
            }

            if (duplicationTarget.Bounds != options.TargetBounds)
            {
                return "DXGI Desktop Duplication target bounds no longer " +
                    "match the selected display.";
            }
        }

        if (options.TargetBounds.Width <= 0 ||
            options.TargetBounds.Height <= 0)
        {
            return "Capture bounds must be non-empty.";
        }

        if (options.OutputSize.Width <= 0 ||
            options.OutputSize.Height <= 0 ||
            options.OutputSize.Width > 8192 ||
            options.OutputSize.Height > 8192)
        {
            return "H.264 output size must be between 1 and 8192 pixels.";
        }

        if ((options.OutputSize.Width & 1) != 0 ||
            (options.OutputSize.Height & 1) != 0)
        {
            return "H.264 NV12 output dimensions must be even.";
        }

        if (options.FramesPerSecond is < 1 or > 120)
        {
            return "H.264 frame rate must be between 1 and 120 FPS.";
        }

        if (options.GopLength is not (1 or ShortGopLength))
        {
            return "H.264 GOP length must be 1 or 2.";
        }

        if (options.BitrateFramesPerSecond is int bitrateFps &&
            (bitrateFps < 1 ||
             bitrateFps > options.FramesPerSecond))
        {
            return "H.264 bitrate frame rate must be between 1 and the capture frame rate.";
        }

        return null;
    }

    private static string GetBackendName(
        FfmpegDesktopH264CaptureOptions options)
    {
        return options.Backend switch
        {
            FfmpegDesktopCaptureBackend
                .WindowsGraphicsCaptureMonitor =>
                options.GraphicsCaptureTarget is { } target
                    ? string.Create(
                        CultureInfo.InvariantCulture,
                        $"gfxcapture/monitor{target.MonitorIndex}/adapter{target.AdapterIndex}")
                    : "gfxcapture/unresolved",
            FfmpegDesktopCaptureBackend.DesktopDuplicationOutput0 =>
                options.DesktopDuplicationTarget is { } target
                    ? string.Create(
                        CultureInfo.InvariantCulture,
                        $"ddagrab/adapter{target.AdapterIndex}/output{target.OutputIndex}")
                    : "ddagrab/unresolved",
            FfmpegDesktopCaptureBackend.GdiGrabBounds =>
                "gdigrab/bounds",
            _ => options.Backend.ToString()
        };
    }

    private static string GetEncoderName(FfmpegH264Encoder encoder)
    {
        return encoder switch
        {
            FfmpegH264Encoder.NvidiaNvenc => "h264_nvenc",
            FfmpegH264Encoder.MediaFoundation => "h264_mf",
            FfmpegH264Encoder.IntelQuickSync => "h264_qsv",
            FfmpegH264Encoder.AmdAmf => "h264_amf",
            _ => encoder.ToString()
        };
    }

    private static string FormatInvariant(int value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static string NormalizeFailure(string failure)
    {
        return string.IsNullOrWhiteSpace(failure)
            ? "startup failed without diagnostic output."
            : failure.Trim();
    }

    private sealed record EncoderPreference(
        FfmpegH264Encoder Encoder,
        string ExecutablePath,
        FfmpegDesktopH264CaptureOptions Options,
        TimeSpan SuccessfulStartupDuration);

    private sealed class SystemLoopbackRtpReceiver :
        IFfmpegDesktopH264RtpReceiver
    {
        private readonly Socket _socket;
        private readonly EndPoint _anySender =
            new IPEndPoint(IPAddress.Any, IPEndPoint.MinPort);
        private int _disposed;

        private SystemLoopbackRtpReceiver(
            Socket socket,
            int port)
        {
            _socket = socket;
            Port = port;
        }

        public int Port { get; }

        public bool HasPendingDatagrams =>
            _socket.Available > 0;

        public static IFfmpegDesktopH264RtpReceiver Create(
            int receiveBufferBytes)
        {
            if (receiveBufferBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(receiveBufferBytes));
            }

            Socket? socket = null;
            try
            {
                socket = new Socket(
                    AddressFamily.InterNetwork,
                    SocketType.Dgram,
                    ProtocolType.Udp);
                socket.Bind(
                    new IPEndPoint(
                        IPAddress.Loopback,
                        IPEndPoint.MinPort));
                try
                {
                    // Cover the measured Media Foundation cold-start burst.
                    // The RTP reader drains all visible datagrams and its
                    // mailbox publishes only the newest complete access unit,
                    // so this kernel headroom does not add display backlog.
                    socket.ReceiveBufferSize =
                        receiveBufferBytes;
                }
                catch (SocketException)
                {
                }

                int port =
                    ((IPEndPoint)socket.LocalEndPoint!).Port;
                var receiver = new SystemLoopbackRtpReceiver(
                    socket,
                    port);
                socket = null;
                return receiver;
            }
            finally
            {
                socket?.Dispose();
            }
        }

        public async ValueTask<int> ReceiveAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            SocketReceiveFromResult result =
                await _socket.ReceiveFromAsync(
                    buffer,
                    SocketFlags.None,
                    _anySender,
                    cancellationToken).ConfigureAwait(false);
            return result.ReceivedBytes;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _socket.Dispose();
            }
        }
    }

    private sealed class SystemFfmpegDesktopH264Process :
        IFfmpegDesktopH264Process
    {
        private readonly Process _process;
        private readonly Task _completion;
        private readonly SafeFileHandle? _killOnCloseJob;
        private int _disposed;

        private SystemFfmpegDesktopH264Process(
            Process process,
            SafeFileHandle? killOnCloseJob)
        {
            _process = process;
            _killOnCloseJob = killOnCloseJob;
            _completion = process.WaitForExitAsync();
        }

        public Stream StandardOutput =>
            _process.StandardOutput.BaseStream;

        public TextReader StandardError =>
            _process.StandardError;

        public Task Completion => _completion;

        public bool HasExited
        {
            get
            {
                try
                {
                    return _process.HasExited;
                }
                catch (Exception ex) when (
                    ex is InvalidOperationException or
                        ObjectDisposedException)
                {
                    return true;
                }
            }
        }

        public int? ExitCode
        {
            get
            {
                try
                {
                    return _process.HasExited
                        ? _process.ExitCode
                        : null;
                }
                catch (Exception ex) when (
                    ex is InvalidOperationException or
                        ObjectDisposedException)
                {
                    return null;
                }
            }
        }

        public static IFfmpegDesktopH264Process Start(
            string executablePath,
            IReadOnlyList<string> arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = false
            };
            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException(
                        "ffmpeg process did not start.");
                }
            }
            catch
            {
                process.Dispose();
                throw;
            }

            SafeFileHandle? killOnCloseJob =
                WindowsKillOnCloseJob.TryCreateAndAssign(
                    process);
            try
            {
                // Keep the capture process at the normal base priority. The
                // RemoteDesk input, RTP and mouse-ack loops use AboveNormal
                // threads, so even a CPU-heavy GDI compatibility encoder
                // cannot starve the interaction path. The direct WGC/NVENC
                // entity path reaches its frame target at normal priority.
                process.PriorityClass = ProductionProcessPriority;
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or
                    System.ComponentModel.Win32Exception or
                    NotSupportedException)
            {
            }

            try
            {
                return new SystemFfmpegDesktopH264Process(
                    process,
                    killOnCloseJob);
            }
            catch
            {
                // Closing a successfully assigned kill-on-close Job also
                // handles the narrow failure window after Process.Start.
                killOnCloseJob?.Dispose();
                if (killOnCloseJob is null)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                        }
                    }
                    catch (Exception ex) when (
                        ex is InvalidOperationException or
                            System.ComponentModel.Win32Exception or
                            NotSupportedException)
                    {
                    }
                }

                process.Dispose();
                throw;
            }
        }

        public void KillEntireProcessTree()
        {
            _process.Kill(entireProcessTree: true);
        }

        public bool TryRequestGracefulExit()
        {
            try
            {
                if (_process.HasExited)
                {
                    return true;
                }

                _process.StandardInput.WriteLine("q");
                _process.StandardInput.Flush();
                return true;
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or
                    IOException or
                    ObjectDisposedException)
            {
                return false;
            }
        }

        public bool WaitForExit(int milliseconds)
        {
            try
            {
                return _process.WaitForExit(milliseconds);
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or
                    ObjectDisposedException or
                    System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            // The Job handle is deliberately owned for the entire child
            // lifetime. Windows closes it even on Environment.Exit or a crash,
            // providing the last-resort child cleanup that Process.Dispose
            // alone cannot guarantee.
            _killOnCloseJob?.Dispose();
            _process.Dispose();
        }
    }
}
