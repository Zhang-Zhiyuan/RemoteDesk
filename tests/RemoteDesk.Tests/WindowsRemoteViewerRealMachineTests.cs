using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Reflection;
using System.Windows.Forms;
using Xunit;
using Xunit.Abstractions;

namespace RemoteDesk.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class RemoteDeskRealMachineFactAttribute : FactAttribute
{
    internal const string EnabledVariable = "REMOTEDESK_REAL_MACHINE_TESTS";

    public RemoteDeskRealMachineFactAttribute()
    {
        string? enabled = Environment.GetEnvironmentVariable(EnabledVariable);
        if (!string.Equals(enabled, "1", StringComparison.Ordinal) &&
            !string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase))
        {
            Skip = $"Set {EnabledVariable}=1 to run this networked real-machine test.";
        }
    }
}

[Collection(WindowsRealMachineCollection.Name)]
public sealed class WindowsRemoteViewerRealMachineTests
{
    private const string HostVariable =
        "REMOTEDESK_REAL_MACHINE_HOST";
    private static string Host
    {
        get
        {
            string? configured =
                Environment.GetEnvironmentVariable(HostVariable);
            return string.IsNullOrWhiteSpace(configured)
                ? throw new InvalidOperationException(
                    $"Set {HostVariable} to an explicitly authorized test host.")
                : configured.Trim();
        }
    }
    private const string PortVariable =
        "REMOTEDESK_REAL_MACHINE_PORT";
    private static int Port =>
        ReadPositiveEnvironmentInteger(
            PortVariable,
            fallback: Protocol.DefaultPort,
            maximum: 65535);
    private const string PasswordVariable = "REMOTEDESK_REAL_MACHINE_PASSWORD";
    private const string ClickXVariable =
        "REMOTEDESK_REAL_MACHINE_CLICK_X";
    private const string ClickYVariable =
        "REMOTEDESK_REAL_MACHINE_CLICK_Y";
    private const string TargetVariable =
        "REMOTEDESK_REAL_MACHINE_TARGET_ID";
    private const string ExpectedWidthVariable =
        "REMOTEDESK_REAL_MACHINE_EXPECTED_WIDTH";
    private const string ExpectedHeightVariable =
        "REMOTEDESK_REAL_MACHINE_EXPECTED_HEIGHT";
    private const string SourceWidthVariable =
        "REMOTEDESK_REAL_MACHINE_SOURCE_WIDTH";
    private const string SourceHeightVariable =
        "REMOTEDESK_REAL_MACHINE_SOURCE_HEIGHT";
    private const string ExpectedFpsVariable =
        "REMOTEDESK_REAL_MACHINE_EXPECTED_FPS";
    private const string MinimumFpsVariable =
        "REMOTEDESK_REAL_MACHINE_MINIMUM_FPS";
    private const string ObservationSecondsVariable =
        "REMOTEDESK_REAL_MACHINE_OBSERVATION_SECONDS";
    private const string EntityTargetId = @"\\.\DISPLAY5";
    private static TimeSpan ObservationDuration =>
        TimeSpan.FromSeconds(
            ReadPositiveEnvironmentInteger(
                ObservationSecondsVariable,
                fallback: 30,
                maximum: 3600));
    private static readonly TimeSpan MaximumHardwareReadyLatency =
        TimeSpan.FromSeconds(5);
    private static int ExpectedEntityFrameWidth =>
        ReadPositiveEnvironmentInteger(
            ExpectedWidthVariable,
            fallback: 3840,
            maximum: 8192);
    private static int ExpectedEntityFrameHeight =>
        ReadPositiveEnvironmentInteger(
            ExpectedHeightVariable,
            fallback: 2160,
            maximum: 8192);
    private static int ExpectedSourceWidth =>
        ReadPositiveEnvironmentInteger(
            SourceWidthVariable,
            fallback: 3840,
            maximum: 8192);
    private static int ExpectedSourceHeight =>
        ReadPositiveEnvironmentInteger(
            SourceHeightVariable,
            fallback: 2160,
            maximum: 8192);
    private static int ExpectedEntityFramesPerSecond =>
        ReadPositiveEnvironmentInteger(
            ExpectedFpsVariable,
            fallback: 30,
            maximum: 120);
    private static int MinimumNativeUltraHdFramesPerSecond =>
        ReadPositiveEnvironmentInteger(
            MinimumFpsVariable,
            fallback:
                ExpectedEntityFramesPerSecond > 30
                    ? 57
                    : 27,
            maximum: ExpectedEntityFramesPerSecond);
    private static readonly FieldInfo LowLatencyTransportField =
        GetRequiredField(typeof(RemoteViewerClient), "_lowLatencyVideoTransport");
    private static readonly FieldInfo LowLatencyFeaturesField =
        GetRequiredField(typeof(LowLatencyVideoViewerTransport), "_features");
    private static readonly FieldInfo LowLatencyReassemblerField =
        GetRequiredField(typeof(LowLatencyVideoViewerTransport), "_reassembler");
    private static readonly FieldInfo LowLatencyStateField =
        GetRequiredField(typeof(LowLatencyVideoViewerTransport), "_state");
    private static readonly FieldInfo LowLatencyHostEndpointField =
        GetRequiredField(
            typeof(LowLatencyVideoViewerTransport),
            "_hostEndpoint");
    private static readonly FieldInfo LowLatencySocketField =
        GetRequiredField(typeof(LowLatencyVideoViewerTransport), "_socket");

    private readonly ITestOutputHelper _output;

    public WindowsRemoteViewerRealMachineTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void FrameIntervalSummaryReportsTailStallsWithoutAveragingThemAway()
    {
        double ticksPerMillisecond =
            Stopwatch.Frequency / 1000d;
        double[] arrivalMilliseconds =
            [0, 10, 30, 60, 100, 200];
        long[] timestamps = arrivalMilliseconds
            .Select(milliseconds =>
                checked((long)Math.Round(
                    milliseconds *
                    ticksPerMillisecond)))
            .ToArray();

        FrameIntervalSummary summary =
            SummarizeFrameIntervals(
                timestamps,
                notBeforeTimestamp: timestamps[0]);

        Assert.Equal(5, summary.SampleCount);
        Assert.Equal(40, summary.AverageMilliseconds, 2);
        Assert.Equal(30, summary.P50Milliseconds, 2);
        Assert.Equal(100, summary.P95Milliseconds, 2);
        Assert.Equal(100, summary.P99Milliseconds, 2);
        Assert.Equal(100, summary.MaximumMilliseconds, 2);
        Assert.Equal(1, summary.GapsAbove50Milliseconds);
        Assert.Equal(0, summary.GapsAbove100Milliseconds);
    }

    [Fact]
    public void FrameIntervalSummaryExcludesArrivalsAfterObservationWindow()
    {
        long ticksPerMillisecond =
            Math.Max(1, Stopwatch.Frequency / 1000);
        long observationStartedAt = 100 * ticksPerMillisecond;
        long observationEndedAt = 140 * ticksPerMillisecond;
        long[] timestamps =
        [
            observationStartedAt,
            observationStartedAt + (10 * ticksPerMillisecond),
            observationStartedAt + (20 * ticksPerMillisecond),
            observationEndedAt,
            observationEndedAt + (200 * ticksPerMillisecond)
        ];

        FrameIntervalSummary summary =
            SummarizeFrameIntervals(
                timestamps,
                observationStartedAt,
                observationEndedAt);

        Assert.Equal(3, summary.SampleCount);
        Assert.Equal(20, summary.MaximumMilliseconds, 2);
        Assert.Equal(0, summary.GapsAbove50Milliseconds);
        Assert.Equal(0, summary.GapsAbove100Milliseconds);
    }

    [Fact]
    public void LargestFrameGapsReportObservationOffsetAndPeriodicPhase()
    {
        long ticksPerMillisecond =
            Math.Max(1, Stopwatch.Frequency / 1000);
        long observationStartedAt = 100 * ticksPerMillisecond;
        long observationEndedAt =
            observationStartedAt +
            (4200 * ticksPerMillisecond);
        long[] timestamps =
        [
            observationStartedAt,
            observationStartedAt + (33 * ticksPerMillisecond),
            observationStartedAt + (2090 * ticksPerMillisecond),
            observationStartedAt + (2123 * ticksPerMillisecond),
            observationStartedAt + (4180 * ticksPerMillisecond),
            observationEndedAt + (500 * ticksPerMillisecond)
        ];

        FrameGapDiagnostic[] gaps =
            FindLargestFrameGaps(
                timestamps,
                observationStartedAt,
                observationEndedAt,
                maximumCount: 2);

        Assert.Equal(2, gaps.Length);
        Assert.All(
            gaps,
            gap => Assert.Equal(2057, gap.IntervalMilliseconds, 1));
        Assert.Equal(2.090, gaps[0].EndOffsetSeconds, 3);
        Assert.Equal(0.090, gaps[0].TwoSecondPhaseSeconds, 3);
        Assert.Equal(4.180, gaps[1].EndOffsetSeconds, 3);
        Assert.Equal(0.180, gaps[1].TwoSecondPhaseSeconds, 3);
    }

    [RemoteDeskRealMachineFact]
    [Trait("Category", "RealMachine")]
    public async Task AutomaticModeKeepsFramesAndAppliedMouseAcksAlive()
    {
        Assert.True(
            OperatingSystem.IsWindows(),
            "This RemoteViewerClient real-machine test requires Windows.");
        string password =
            Environment.GetEnvironmentVariable(PasswordVariable) ??
            throw new InvalidOperationException(
                $"{PasswordVariable} must be set when real-machine tests are enabled.");
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                $"{PasswordVariable} must not be empty when real-machine tests are enabled.");
        }

        using var client = new RemoteViewerClient();
        var deviceReceived = new TaskCompletionSource<RemoteDeviceDescriptor>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstFrameReceived = new TaskCompletionSource<Size>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var logs = new ConcurrentQueue<string>();
        long frames = 0;
        client.DeviceInfoReceived += device =>
            deviceReceived.TrySetResult(device);
        client.FrameReceived += frame =>
        {
            Interlocked.Increment(ref frames);
            if (frame.Width > 0 && frame.Height > 0)
            {
                firstFrameReceived.TrySetResult(
                    new Size(frame.Width, frame.Height));
            }
        };
        client.Log += message =>
            logs.Enqueue(RedactSecret(message, password));

        try
        {
            await client.ConnectAsync(
                Host,
                Port,
                password,
                ViewerVideoMode.Automatic);
            RemoteDeviceDescriptor device =
                await deviceReceived.Task.WaitAsync(
                    TimeSpan.FromSeconds(10));
            Size frameSize =
                await firstFrameReceived.Task.WaitAsync(
                    TimeSpan.FromSeconds(20));

            var inputExercise = Stopwatch.StartNew();
            long sent = 0;
            while (client.IsConnected &&
                inputExercise.Elapsed < TimeSpan.FromSeconds(5))
            {
                await client.SendInputAsync(
                    RemoteInputCommand.MouseMove(
                        frameSize.Width / 2 + (int)(sent & 1),
                        frameSize.Height / 2));
                sent++;
                await Task.Delay(8);
            }

            await Task.Delay(500);
            UdpViewerSnapshot udp = CaptureUdpSnapshot(client);
            LowLatencyMouseInputLatencySnapshot inputLatency =
                client.CollectUdpMouseInputLatencySnapshot();
            _output.WriteLine(
                "automatic: endpoint={0}:{1}; device={2}; build={3}; frame={4}x{5}; frames={6}; connected={7}; udpActive={8}; mouseSent={9}; mouseAck={10}; matched={11}; latestRtt={12:F2}ms; maxRtt={13:F2}ms",
                Host,
                Port,
                device.MachineName,
                device.BuildStamp,
                frameSize.Width,
                frameSize.Height,
                Volatile.Read(ref frames),
                client.IsConnected,
                udp.IsActive,
                inputLatency.SentMouseMoveCount,
                inputLatency.AcknowledgedMouseMoveCount,
                inputLatency.MatchedLatencySampleCount,
                inputLatency.LatestRoundTripMilliseconds,
                inputLatency.MaximumRoundTripMilliseconds);
            foreach (string message in logs.TakeLast(20))
            {
                _output.WriteLine("client-log: {0}", message);
            }

            Assert.True(
                client.IsConnected,
                "The automatic-mode session disconnected after its first frame.");
            Assert.True(
                Volatile.Read(ref frames) > 0,
                "Automatic mode did not receive a frame.");
            Assert.True(sent > 0, "No safe mouse movement was submitted.");
            if (device.Capabilities.HasFlag(
                    RemoteDeviceCapabilities
                        .LowLatencyUdpMouseInputAppliedAck))
            {
                Assert.True(
                    udp.MouseInputAppliedAckNegotiated,
                    "The host advertised applied mouse ACKs but negotiation did not complete.");
                Assert.True(
                    inputLatency.SentMouseMoveCount > 0 &&
                        inputLatency.AcknowledgedMouseMoveCount > 0 &&
                        inputLatency.MatchedLatencySampleCount > 0,
                    "The host advertised applied mouse ACKs but returned none.");
            }
        }
        finally
        {
            await client.DisconnectAsync();
        }
    }

    [RemoteDeskRealMachineFact]
    [Trait("Category", "RealMachine")]
    public async Task NewestAuthenticatedViewerReplacesExistingSession()
    {
        Assert.True(
            OperatingSystem.IsWindows(),
            "This RemoteViewerClient real-machine test requires Windows.");
        string password =
            Environment.GetEnvironmentVariable(PasswordVariable) ??
            throw new InvalidOperationException(
                $"{PasswordVariable} must be set when real-machine tests are enabled.");
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                $"{PasswordVariable} must not be empty when real-machine tests are enabled.");
        }

        using var first = new RemoteViewerClient();
        using var second = new RemoteViewerClient();
        var firstFrame = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondFrame = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstDisconnected = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        first.FrameReceived += frame =>
        {
            if (frame.Width > 0 && frame.Height > 0)
            {
                firstFrame.TrySetResult(null);
            }
        };
        second.FrameReceived += frame =>
        {
            if (frame.Width > 0 && frame.Height > 0)
            {
                secondFrame.TrySetResult(null);
            }
        };
        first.ConnectedChanged += connected =>
        {
            if (!connected)
            {
                firstDisconnected.TrySetResult(null);
            }
        };

        try
        {
            await first.ConnectAsync(
                Host,
                Port,
                password,
                ViewerVideoMode.Automatic);
            await firstFrame.Task.WaitAsync(
                TimeSpan.FromSeconds(20));

            await second.ConnectAsync(
                Host,
                Port,
                password,
                ViewerVideoMode.Automatic);
            await Task.WhenAll(
                secondFrame.Task.WaitAsync(
                    TimeSpan.FromSeconds(20)),
                firstDisconnected.Task.WaitAsync(
                    TimeSpan.FromSeconds(10)));

            _output.WriteLine(
                "takeover: endpoint={0}:{1}; firstConnected={2}; secondConnected={3}",
                Host,
                Port,
                first.IsConnected,
                second.IsConnected);
            Assert.False(
                first.IsConnected,
                "The superseded viewer remained connected after the newest authenticated viewer took ownership.");
            Assert.True(
                second.IsConnected,
                "The newest authenticated viewer did not retain the host session.");
        }
        finally
        {
            await second.DisconnectAsync();
            await first.DisconnectAsync();
        }
    }

    [RemoteDeskRealMachineFact]
    [Trait("Category", "RealMachine")]
    public async Task ReliableClickIsAppliedOrReturnsAnActionableFailure()
    {
        Assert.True(
            OperatingSystem.IsWindows(),
            "This RemoteViewerClient real-machine test requires Windows.");
        string password =
            Environment.GetEnvironmentVariable(PasswordVariable) ??
            throw new InvalidOperationException(
                $"{PasswordVariable} must be set when real-machine tests are enabled.");
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                $"{PasswordVariable} must not be empty when real-machine tests are enabled.");
        }

        using var client = new RemoteViewerClient();
        var firstFrameReceived = new TaskCompletionSource<Size>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var statuses = new ConcurrentQueue<string>();
        client.FrameReceived += frame =>
        {
            if (frame.Width > 0 && frame.Height > 0)
            {
                firstFrameReceived.TrySetResult(
                    new Size(frame.Width, frame.Height));
            }
        };
        client.ClipboardStatusReceived += statuses.Enqueue;

        try
        {
            await client.ConnectAsync(
                Host,
                Port,
                password,
                ViewerVideoMode.Automatic);
            Size frameSize =
                await firstFrameReceived.Task.WaitAsync(
                    TimeSpan.FromSeconds(20));
            int x = ReadOptionalCoordinate(
                ClickXVariable,
                frameSize.Width / 2,
                frameSize.Width);
            int y = ReadOptionalCoordinate(
                ClickYVariable,
                frameSize.Height / 2,
                frameSize.Height);
            _output.WriteLine(
                "reliable-click: endpoint={0}:{1}; coordinate={2},{3}; frame={4}x{5}",
                Host,
                Port,
                x,
                y,
                frameSize.Width,
                frameSize.Height);
            await client.SendInputsAsync(
            [
                RemoteInputCommand.MouseDown(
                    RemoteMouseButton.Left,
                    x,
                    y),
                RemoteInputCommand.MouseUp(
                    RemoteMouseButton.Left,
                    x,
                    y)
            ]);
            await client.FlushInputAsync(
                new CancellationTokenSource(
                    TimeSpan.FromSeconds(5)).Token);
            await Task.Delay(750);

            foreach (string status in statuses)
            {
                _output.WriteLine("host-status: {0}", status);
            }

            string[] inputFailures = statuses
                .Where(status =>
                    status.Contains(
                        "远程输入失败",
                        StringComparison.Ordinal))
                .ToArray();
            Assert.All(
                inputFailures,
                status => Assert.True(
                    status.Contains(
                        "管理员重启",
                        StringComparison.Ordinal) ||
                    status.Contains(
                        "安全桌面",
                        StringComparison.Ordinal) ||
                    status.Contains(
                        "暂不可用",
                        StringComparison.Ordinal),
                    "Input was rejected without an actionable recovery message: " +
                    status));
        }
        finally
        {
            await client.DisconnectAsync();
        }
    }

    [RemoteDeskRealMachineFact]
    [Trait("Category", "RealMachine")]
    public async Task ForceH264MaintainsUdpFeedbackAndFecNegotiationForThirtySeconds()
    {
        Assert.True(
            OperatingSystem.IsWindows(),
            "This RemoteViewerClient real-machine test requires Windows.");
        using IDisposable timerResolution =
            WindowsTimerResolution.TryAcquireInteractive();
        using IDisposable managedLatency =
            ManagedLatencyMode
                .TryAcquireSustainedLowLatency();
        string password =
            Environment.GetEnvironmentVariable(PasswordVariable) ??
            throw new InvalidOperationException(
                $"{PasswordVariable} must be set when real-machine tests are enabled.");
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                $"{PasswordVariable} must not be empty when real-machine tests are enabled.");
        }

        using var client = new RemoteViewerClient();
        string requestedTarget =
            Environment.GetEnvironmentVariable(TargetVariable) ??
            EntityTargetId;
        var deviceReceived = new TaskCompletionSource<RemoteDeviceDescriptor>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var targetsReceived =
            new TaskCompletionSource<IReadOnlyList<CaptureTargetInfo>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var targetChanged =
            new TaskCompletionSource<CaptureTargetInfo>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var logs = new ConcurrentQueue<string>();
        var roundTrips = new ConcurrentQueue<double>();
        var h264ArrivalTimestamps = new ConcurrentQueue<long>();
        var h264FrameSamples =
            new ConcurrentQueue<EntityFrameSample>();
        var nonH264ArrivalTimestamps =
            new ConcurrentQueue<long>();
        long totalFrames = 0;
        long h264Frames = 0;
        long udpH264Frames = 0;
        long recoveryFrames = 0;
        long dependentFrames = 0;
        long invalidH264Frames = 0;
        long invalidH264FlagFrames = 0;
        long firstH264ArrivedAt = 0;
        long latestFrameGeometry = 0;
        int udpActiveForFrameSampling = 0;
        RealViewerWindowHost? viewerWindowHost = null;

        client.DeviceInfoReceived += device => deviceReceived.TrySetResult(device);
        client.CaptureTargetsReceived += targets =>
            targetsReceived.TrySetResult(targets);
        client.CaptureTargetChanged += target =>
        {
            if (string.Equals(
                    requestedTarget,
                    target.Id,
                    StringComparison.Ordinal))
            {
                targetChanged.TrySetResult(target);
            }
        };
        client.RoundTripUpdated += roundTrip =>
            roundTrips.Enqueue(roundTrip.TotalMilliseconds);
        client.Log += message =>
            logs.Enqueue(RedactSecret(message, password));
        client.FrameReceived += frame =>
        {
            Interlocked.Increment(ref totalFrames);
            long arrivedAt = Stopwatch.GetTimestamp();
            if (frame.Encoding != RemoteFrameEncoding.H264AnnexB)
            {
                nonH264ArrivalTimestamps.Enqueue(arrivedAt);
                return;
            }

            Interlocked.Increment(ref h264Frames);
            Interlocked.CompareExchange(
                ref firstH264ArrivedAt,
                arrivedAt,
                0);
            h264ArrivalTimestamps.Enqueue(arrivedAt);
            // Do not reflect over the transport and format both endpoints on
            // the UDP receive thread for every 4K frame. The observation loop
            // refreshes this state at 10 Hz, which is sufficient for route
            // attribution without injecting allocations or GC stalls into
            // the latency distribution being measured.
            bool isUdpActive =
                Volatile.Read(
                    ref udpActiveForFrameSampling) != 0;
            h264FrameSamples.Enqueue(
                new EntityFrameSample(
                    arrivedAt,
                    frame.Width,
                    frame.Height,
                    frame.EncodedLength,
                    frame.Flags,
                    isUdpActive));
            Interlocked.Exchange(
                ref latestFrameGeometry,
                PackFrameGeometry(
                    frame.Width,
                    frame.Height));
            if (frame.Width <= 0 ||
                frame.Height <= 0 ||
                frame.EncodedLength <= 0 ||
                frame.EncodedOffset < 0 ||
                frame.EncodedOffset > frame.EncodedBuffer.Length -
                    frame.EncodedLength)
            {
                Interlocked.Increment(ref invalidH264Frames);
            }

            const RemoteFrameFlags recoveryFlags =
                RemoteFrameFlags.KeyFrame | RemoteFrameFlags.CodecConfig;
            if (frame.Flags == recoveryFlags)
            {
                Interlocked.Increment(ref recoveryFrames);
            }
            else if (frame.Flags == RemoteFrameFlags.None)
            {
                Interlocked.Increment(ref dependentFrames);
            }
            else
            {
                Interlocked.Increment(
                    ref invalidH264FlagFrames);
            }

            if (isUdpActive)
            {
                Interlocked.Increment(ref udpH264Frames);
            }
        };

        try
        {
            long startupStartedAt =
                Stopwatch.GetTimestamp();
            await client.ConnectAsync(
                Host,
                Port,
                password,
                ViewerVideoMode.ForceH264);
            RemoteDeviceDescriptor device =
                await deviceReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            IReadOnlyList<CaptureTargetInfo> captureTargets =
                await targetsReceived.Task.WaitAsync(
                    TimeSpan.FromSeconds(10));
            CaptureTargetInfo advertisedTarget =
                Assert.Single(
                    captureTargets,
                    target => string.Equals(
                        target.Id,
                        requestedTarget,
                        StringComparison.Ordinal));
            Assert.Contains(
                $"{ExpectedSourceWidth}x{ExpectedSourceHeight}",
                advertisedTarget.DisplayName,
                StringComparison.Ordinal);
            await client.SelectCaptureTargetAsync(requestedTarget);
            CaptureTargetInfo selectedTarget =
                await targetChanged.Task.WaitAsync(
                    TimeSpan.FromSeconds(10));
            Assert.Equal(
                requestedTarget,
                selectedTarget.Id);
            Assert.Contains(
                $"{ExpectedSourceWidth}x{ExpectedSourceHeight}",
                selectedTarget.DisplayName,
                StringComparison.Ordinal);
            _output.WriteLine(
                "selectedTarget={0}; displayName={1}",
                selectedTarget.Id,
                selectedTarget.DisplayName);

            // DeviceInfo proves the host can send the optional protected GOP2
            // tier. Production Windows viewers deliberately leave that tier
            // unnegotiated and require independently decodable GOP1 below.
            RemoteDeviceCapabilities requiredCapabilities =
                RemoteDeviceCapabilities.LowLatencyUdpVideo |
                RemoteDeviceCapabilities.UdpVideoCongestionFeedback |
                RemoteDeviceCapabilities.LowLatencyUdpVideoXorFec |
                RemoteDeviceCapabilities.LowLatencyUdpMouseInput |
                RemoteDeviceCapabilities
                    .LowLatencyUdpMouseInputAppliedAck |
                RemoteDeviceCapabilities.ShortGopH264;
            if (ExpectedEntityFramesPerSecond > 30)
            {
                requiredCapabilities |=
                    RemoteDeviceCapabilities
                        .HighFrameRateH264;
            }
            Assert.Equal(
                requiredCapabilities,
                device.Capabilities & requiredCapabilities);
            viewerWindowHost =
                await RealViewerWindowHost.StartAsync(client);

            UdpViewerSnapshot readySnapshot = default;
            RemoteViewerRenderTelemetrySnapshot
                readyRenderSnapshot = default;
            long directFramesAtNativeSourceArrival = -1;
            while (Stopwatch.GetElapsedTime(
                       startupStartedAt) <
                   TimeSpan.FromSeconds(15))
            {
                readySnapshot = CaptureUdpSnapshot(client);
                Volatile.Write(
                    ref udpActiveForFrameSampling,
                    readySnapshot.IsActive ? 1 : 0);
                readyRenderSnapshot =
                    viewerWindowHost
                        .CollectRenderTelemetrySnapshot();
                Size latestSourceSize =
                    UnpackFrameGeometry(
                        Interlocked.Read(
                            ref latestFrameGeometry));
                bool nativeSourceReady =
                    latestSourceSize.Width ==
                        ExpectedEntityFrameWidth &&
                    latestSourceSize.Height ==
                        ExpectedEntityFrameHeight;
                if (nativeSourceReady &&
                    directFramesAtNativeSourceArrival < 0)
                {
                    directFramesAtNativeSourceArrival =
                        readyRenderSnapshot
                            .DirectHardwarePresentedFrames;
                }

                if (client.IsConnected &&
                    readySnapshot.IsActive &&
                    readySnapshot.CongestionFeedbackNegotiated &&
                    readySnapshot.XorFecNegotiated &&
                    !readySnapshot.ShortGopNegotiated &&
                    readySnapshot.MouseInputAppliedAckNegotiated &&
                    Volatile.Read(ref udpH264Frames) > 0 &&
                    nativeSourceReady &&
                    directFramesAtNativeSourceArrival >= 0 &&
                    readyRenderSnapshot
                        .DirectHardwarePresentedFrames >
                            directFramesAtNativeSourceArrival &&
                    readyRenderSnapshot
                        .DirectPresentationQualificationSuccesses > 0 &&
                    readyRenderSnapshot
                        .DirectPresentationQualificationFailures == 0)
                {
                    break;
                }

                await Task.Delay(100);
            }

            TimeSpan hardwareReadyLatency =
                Stopwatch.GetElapsedTime(
                    startupStartedAt);
            long firstH264Timestamp =
                Volatile.Read(ref firstH264ArrivedAt);
            double firstH264Milliseconds =
                firstH264Timestamp == 0
                    ? double.NaN
                    : Stopwatch.GetElapsedTime(
                        startupStartedAt,
                        firstH264Timestamp)
                        .TotalMilliseconds;
            _output.WriteLine(
                "startup: firstH264Ms={0:F1}; exactHardwareReadyMs={1:F1}; budgetMs={2:F0}",
                firstH264Milliseconds,
                hardwareReadyLatency.TotalMilliseconds,
                MaximumHardwareReadyLatency
                    .TotalMilliseconds);
            Size readyFrameSize =
                UnpackFrameGeometry(
                    Interlocked.Read(
                        ref latestFrameGeometry));

            if (!readySnapshot.IsActive ||
                !readySnapshot.CongestionFeedbackNegotiated ||
                !readySnapshot.XorFecNegotiated ||
                readySnapshot.ShortGopNegotiated ||
                !readySnapshot.MouseInputAppliedAckNegotiated ||
                Volatile.Read(ref udpH264Frames) == 0 ||
                readyFrameSize.Width !=
                    ExpectedEntityFrameWidth ||
                readyFrameSize.Height !=
                    ExpectedEntityFrameHeight ||
                directFramesAtNativeSourceArrival < 0 ||
                readyRenderSnapshot
                    .DirectHardwarePresentedFrames <=
                        directFramesAtNativeSourceArrival)
            {
                WriteDiagnostics(
                    "readiness-failed",
                    device,
                    readySnapshot,
                    totalFrames,
                    h264Frames,
                    udpH264Frames,
                    recoveryFrames,
                    dependentFrames,
                    invalidH264Frames,
                    invalidH264FlagFrames,
                    roundTrips,
                    logs);
                _output.WriteLine(
                    "readiness-failed: exactViewer presented={0}; occluded={1}; skipped={2}; failures={3}; qualification={4}/{5}/{6}; disabled={7}; backend={8}; lastFailure={9}",
                    readyRenderSnapshot
                        .DirectHardwarePresentedFrames,
                    readyRenderSnapshot
                        .DirectHardwareOccludedFrames,
                    readyRenderSnapshot
                        .DirectHardwareSkippedFrames,
                    readyRenderSnapshot
                        .DirectHardwarePresentationFailures,
                    readyRenderSnapshot
                        .DirectPresentationQualificationAttempts,
                    readyRenderSnapshot
                        .DirectPresentationQualificationSuccesses,
                    readyRenderSnapshot
                        .DirectPresentationQualificationFailures,
                    readyRenderSnapshot
                        .DirectHardwarePathDisabled,
                    readyRenderSnapshot
                        .LastRenderedDecoderBackend,
                    readyRenderSnapshot
                        .LastDirectHardwareFailureDetail);
            }

            Assert.True(
                readySnapshot.IsActive &&
                    readySnapshot.CongestionFeedbackNegotiated &&
                    readySnapshot.XorFecNegotiated &&
                    !readySnapshot.ShortGopNegotiated &&
                    readySnapshot.MouseInputAppliedAckNegotiated &&
                    Volatile.Read(ref udpH264Frames) > 0 &&
                    readyFrameSize.Width ==
                        ExpectedEntityFrameWidth &&
                    readyFrameSize.Height ==
                        ExpectedEntityFrameHeight &&
                    directFramesAtNativeSourceArrival >= 0 &&
                    readyRenderSnapshot
                        .DirectHardwarePresentedFrames >
                            directFramesAtNativeSourceArrival &&
                    readyRenderSnapshot
                        .DirectPresentationQualificationSuccesses > 0 &&
                    readyRenderSnapshot
                        .DirectPresentationQualificationFailures == 0,
                BuildFailureContext(
                    "H.264 over low-latency UDP and the exact MF/D3D11 viewer presentation path did not become ready.",
                    readySnapshot,
                    logs));
            Assert.True(
                hardwareReadyLatency <=
                    MaximumHardwareReadyLatency,
                BuildFailureContext(
                    "The exact H.264 hardware presentation path became ready, but exceeded the interactive startup latency budget.",
                    readySnapshot,
                    logs));

            long initialH264Frames = Volatile.Read(ref h264Frames);
            long initialUdpH264Frames = Volatile.Read(ref udpH264Frames);
            long initialCompletedFrames = readySnapshot.CompletedFrames;
            long initialAbandonedFrames =
                readySnapshot.AbandonedIncompleteFrames;
            RemoteViewerRenderTelemetrySnapshot
                initialRenderSnapshot =
                    viewerWindowHost
                        .CollectRenderTelemetrySnapshot(
                            includeLatencyDistributions: true);
            long observationStartedAt = Stopwatch.GetTimestamp();
            long observationEndedAt = checked(
                observationStartedAt +
                (long)Math.Round(
                    ObservationDuration.TotalSeconds *
                    Stopwatch.Frequency));
            int disconnectedSamples = 0;
            int inactiveUdpSamples = 0;
            UdpViewerSnapshot finalSnapshot = readySnapshot;
            Task<long> inputExercise = ExerciseMouseInputAsync(
                client,
                readyFrameSize.Width,
                readyFrameSize.Height,
                ObservationDuration);

            // The host disables UDP after one second without receiver feedback. Keeping state 2
            // while UDP frames grow for 30 seconds proves FeedbackV2 reaches the host end to end.
            var observation = Stopwatch.StartNew();
            while (observation.Elapsed < ObservationDuration)
            {
                if (!client.IsConnected)
                {
                    disconnectedSamples++;
                }

                finalSnapshot = CaptureUdpSnapshot(client);
                Volatile.Write(
                    ref udpActiveForFrameSampling,
                    finalSnapshot.IsActive ? 1 : 0);
                if (!finalSnapshot.IsActive)
                {
                    inactiveUdpSamples++;
                }

                await Task.Delay(100);
            }

            long exercisedMouseMoves = await inputExercise;
            finalSnapshot = CaptureUdpSnapshot(client);
            LowLatencyMouseInputLatencySnapshot inputLatency =
                client.CollectUdpMouseInputLatencySnapshot();
            RemoteViewerRenderTelemetrySnapshot
                finalRenderSnapshot =
                    viewerWindowHost
                        .CollectRenderTelemetrySnapshot(
                            includeLatencyDistributions: true);
            long observedH264Frames =
                Volatile.Read(ref h264Frames) - initialH264Frames;
            long observedUdpH264Frames =
                Volatile.Read(ref udpH264Frames) - initialUdpH264Frames;
            long observedCompletedFrames =
                finalSnapshot.CompletedFrames - initialCompletedFrames;
            long observedAbandonedFrames =
                finalSnapshot.AbandonedIncompleteFrames -
                initialAbandonedFrames;
            EntityFrameSample[] observedFrameSamples =
                h264FrameSamples
                    .Where(sample =>
                        sample.ArrivedAt >= observationStartedAt &&
                        sample.ArrivedAt <= observationEndedAt)
                    .ToArray();
            const RemoteFrameFlags recoveryFlags =
                RemoteFrameFlags.KeyFrame |
                RemoteFrameFlags.CodecConfig;
            long observedRecoveryFrames =
                observedFrameSamples.LongCount(sample =>
                    sample.Flags == recoveryFlags);
            long observedDependentFrames =
                observedFrameSamples.LongCount(sample =>
                    sample.Flags == RemoteFrameFlags.None);
            long observedInvalidFlagFrames =
                observedFrameSamples.LongLength -
                observedRecoveryFrames -
                observedDependentFrames;
            long wrongSizeFrames =
                observedFrameSamples.LongCount(sample =>
                    sample.Width != ExpectedEntityFrameWidth ||
                    sample.Height != ExpectedEntityFrameHeight);
            long observedUdpNativeFrames =
                observedFrameSamples.LongCount(sample =>
                    sample.IsUdpActive &&
                    sample.Width == ExpectedEntityFrameWidth &&
                    sample.Height == ExpectedEntityFrameHeight);
            long observedNonH264Frames =
                nonH264ArrivalTimestamps.LongCount(timestamp =>
                    timestamp >= observationStartedAt &&
                    timestamp <= observationEndedAt);
            long observedEncodedBytes =
                observedFrameSamples.Sum(sample =>
                    (long)Math.Max(0, sample.EncodedLength));
            double encodedMegabitsPerSecond =
                observedEncodedBytes * 8d /
                ObservationDuration.TotalSeconds /
                1_000_000d;
            double encodedBitsPerPixelPerFrame =
                observedFrameSamples.Length == 0
                    ? 0
                    : observedEncodedBytes * 8d /
                        observedFrameSamples.Length /
                        ExpectedEntityFrameWidth /
                        ExpectedEntityFrameHeight;
            double[] encodedFrameKilobytes =
                observedFrameSamples
                    .Select(sample =>
                        sample.EncodedLength / 1024d)
                    .Order()
                    .ToArray();
            double maximumEncodedFrameKilobytes =
                encodedFrameKilobytes.Length == 0
                    ? 0
                    : encodedFrameKilobytes[^1];
            Size observedFrameSize =
                UnpackFrameGeometry(
                    Interlocked.Read(
                        ref latestFrameGeometry));
            string h264SizeDistribution =
                string.Join(
                    ", ",
                    observedFrameSamples
                        .GroupBy(sample =>
                            $"{sample.Width}x{sample.Height}")
                        .OrderBy(group => group.Key)
                        .Select(group =>
                            $"{group.Key}={group.LongCount()}"));

            WriteDiagnostics(
                "final",
                device,
                finalSnapshot,
                totalFrames,
                h264Frames,
                udpH264Frames,
                recoveryFrames,
                dependentFrames,
                invalidH264Frames,
                invalidH264FlagFrames,
                roundTrips,
                logs);
            _output.WriteLine(
                "monitor: disconnectedSamples={0}; inactiveUdpSamples={1}; h264Frames={2}; udpH264Frames={3}; completedUdpFrames={4}",
                disconnectedSamples,
                inactiveUdpSamples,
                observedH264Frames,
                observedUdpH264Frames,
                observedCompletedFrames);
            _output.WriteLine(
                "monitor: latestFrameSize={0}x{1}; sampledFrames={2}; wrongSizeFrames={3}; nonH264Frames={4}; udpNativeFrames={5}; encodedMbps={6:F2}; encodedBppFrame={7:F4}; sizes=[{8}]",
                observedFrameSize.Width,
                observedFrameSize.Height,
                observedFrameSamples.Length,
                wrongSizeFrames,
                observedNonH264Frames,
                observedUdpNativeFrames,
                encodedMegabitsPerSecond,
                encodedBitsPerPixelPerFrame,
                h264SizeDistribution);
            _output.WriteLine(
                "monitor: encodedFrameKiB p50={0:F1}; p95={1:F1}; p99={2:F1}; max={3:F1}",
                Percentile(encodedFrameKilobytes, 0.50),
                Percentile(encodedFrameKilobytes, 0.95),
                Percentile(encodedFrameKilobytes, 0.99),
                maximumEncodedFrameKilobytes);
            _output.WriteLine(
                "monitor: GOP1 recoveryIDR={0}; dependentP={1}; invalidFlags={2}",
                observedRecoveryFrames,
                observedDependentFrames,
                observedInvalidFlagFrames);
            FrameIntervalSummary frameIntervals =
                SummarizeFrameIntervals(
                    h264ArrivalTimestamps,
                    observationStartedAt,
                    observationEndedAt);
            _output.WriteLine(
                "monitor: interFrame samples={0}; avg={1:F2}ms; p50={2:F2}ms; p95={3:F2}ms; p99={4:F2}ms; max={5:F2}ms; gaps>50ms={6}; gaps>100ms={7}; abandoned={8}",
                frameIntervals.SampleCount,
                frameIntervals.AverageMilliseconds,
                frameIntervals.P50Milliseconds,
                frameIntervals.P95Milliseconds,
                frameIntervals.P99Milliseconds,
                frameIntervals.MaximumMilliseconds,
                frameIntervals.GapsAbove50Milliseconds,
                frameIntervals.GapsAbove100Milliseconds,
                observedAbandonedFrames);
            foreach (FrameGapDiagnostic gap in
                FindLargestFrameGaps(
                    h264ArrivalTimestamps,
                    observationStartedAt,
                    observationEndedAt,
                    maximumCount: 20)
                    .Where(gap =>
                        gap.IntervalMilliseconds > 45))
            {
                _output.WriteLine(
                    "monitor: longGap endOffset={0:F3}s; interval={1:F2}ms; phaseWithin2s={2:F3}s",
                    gap.EndOffsetSeconds,
                    gap.IntervalMilliseconds,
                    gap.TwoSecondPhaseSeconds);
            }
            _output.WriteLine(
                "monitor: mouseMoves={0}; ackNegotiated={1}; sent={2}; acknowledged={3}; matched={4}; latestSequence={5}/{6}; applyAckRtt latest={7:F2}ms; ema={8:F2}ms; p95={9:F2}ms; p99={10:F2}ms; max={11:F2}ms@seq{12}; tails>25/50/100/150ms={13}/{14}/{15}/{16}",
                exercisedMouseMoves,
                finalSnapshot.MouseInputAppliedAckNegotiated,
                inputLatency.SentMouseMoveCount,
                inputLatency.AcknowledgedMouseMoveCount,
                inputLatency.MatchedLatencySampleCount,
                inputLatency.LatestAcknowledgedSequence,
                inputLatency.LatestSentSequence,
                inputLatency.LatestRoundTripMilliseconds,
                inputLatency.SmoothedRoundTripMilliseconds,
                inputLatency.P95RoundTripMilliseconds,
                inputLatency.P99RoundTripMilliseconds,
                inputLatency.MaximumRoundTripMilliseconds,
                inputLatency.MaximumRoundTripSequence,
                inputLatency.RoundTripsAbove25Milliseconds,
                inputLatency.RoundTripsAbove50Milliseconds,
                inputLatency.RoundTripsAbove100Milliseconds,
                inputLatency.RoundTripsAbove150Milliseconds);
            long directPresentedFrames =
                finalRenderSnapshot
                    .DirectHardwarePresentedFrames -
                initialRenderSnapshot
                    .DirectHardwarePresentedFrames;
            long directPresentationFailures =
                finalRenderSnapshot
                    .DirectHardwarePresentationFailures -
                initialRenderSnapshot
                    .DirectHardwarePresentationFailures;
            double totalDirectDecodeMilliseconds =
                finalRenderSnapshot
                    .TotalDirectHardwareDecodeMilliseconds -
                initialRenderSnapshot
                    .TotalDirectHardwareDecodeMilliseconds;
            double totalDirectDecodeToPresentMilliseconds =
                finalRenderSnapshot
                    .TotalDirectHardwareDecodeToPresentMilliseconds -
                initialRenderSnapshot
                    .TotalDirectHardwareDecodeToPresentMilliseconds;
            double totalDirectReceiveToPresentMilliseconds =
                finalRenderSnapshot
                    .TotalDirectHardwareReceiveToPresentMilliseconds -
                initialRenderSnapshot
                    .TotalDirectHardwareReceiveToPresentMilliseconds;
            double averageDirectDecodeMilliseconds =
                directPresentedFrames > 0
                    ? totalDirectDecodeMilliseconds /
                        directPresentedFrames
                    : double.PositiveInfinity;
            double averageDirectDecodeToPresentMilliseconds =
                directPresentedFrames > 0
                    ? totalDirectDecodeToPresentMilliseconds /
                        directPresentedFrames
                    : double.PositiveInfinity;
            double averageDirectReceiveToPresentMilliseconds =
                directPresentedFrames > 0
                    ? totalDirectReceiveToPresentMilliseconds /
                        directPresentedFrames
                    : double.PositiveInfinity;
            FixedLatencyHistogramSummary
                directDecodeLatency =
                    finalRenderSnapshot
                        .DirectHardwareDecodeLatency
                        .SummarizeSince(
                            initialRenderSnapshot
                                .DirectHardwareDecodeLatency);
            FixedLatencyHistogramSummary
                directDecodeToPresentLatency =
                    finalRenderSnapshot
                        .DirectHardwareDecodeToPresentLatency
                        .SummarizeSince(
                            initialRenderSnapshot
                                .DirectHardwareDecodeToPresentLatency);
            FixedLatencyHistogramSummary
                directReceiveToPresentLatency =
                    finalRenderSnapshot
                        .DirectHardwareReceiveToPresentLatency
                        .SummarizeSince(
                            initialRenderSnapshot
                                .DirectHardwareReceiveToPresentLatency);
            _output.WriteLine(
                "monitor: exactViewer MF/D3D11 presented={0}; failures={1}; qualification={2}/{3}/{4}; avgDecode={5:F2}ms; avgDecodeToPresent={6:F2}ms; avgReceiveToPresent={7:F2}ms; startupIncludedMaxReceiveToPresent={8:F2}ms; disabled={9}; backend={10}; lastFailure={11}",
                directPresentedFrames,
                directPresentationFailures,
                finalRenderSnapshot
                    .DirectPresentationQualificationAttempts,
                finalRenderSnapshot
                    .DirectPresentationQualificationSuccesses,
                finalRenderSnapshot
                    .DirectPresentationQualificationFailures,
                averageDirectDecodeMilliseconds,
                averageDirectDecodeToPresentMilliseconds,
                averageDirectReceiveToPresentMilliseconds,
                finalRenderSnapshot
                    .MaximumDirectHardwareReceiveToPresentMilliseconds,
                finalRenderSnapshot
                    .DirectHardwarePathDisabled,
                finalRenderSnapshot
                    .LastRenderedDecoderBackend,
                finalRenderSnapshot
                    .LastDirectHardwareFailureDetail);
            _output.WriteLine(
                "monitor: exactViewer MF/D3D11 latency decode samples={0}; p50={1:F2}ms; p95={2:F2}ms; p99={3:F2}ms; maxUpper={4:F2}ms",
                directDecodeLatency.SampleCount,
                directDecodeLatency.P50Milliseconds,
                directDecodeLatency.P95Milliseconds,
                directDecodeLatency.P99Milliseconds,
                directDecodeLatency.MaximumMilliseconds);
            _output.WriteLine(
                "monitor: exactViewer MF/D3D11 latency decodeToPresent samples={0}; p50={1:F2}ms; p95={2:F2}ms; p99={3:F2}ms; maxUpper={4:F2}ms",
                directDecodeToPresentLatency.SampleCount,
                directDecodeToPresentLatency.P50Milliseconds,
                directDecodeToPresentLatency.P95Milliseconds,
                directDecodeToPresentLatency.P99Milliseconds,
                directDecodeToPresentLatency.MaximumMilliseconds);
            _output.WriteLine(
                "monitor: exactViewer MF/D3D11 latency receiveToPresent samples={0}; p50={1:F2}ms; p95={2:F2}ms; p99={3:F2}ms; maxUpper={4:F2}ms",
                directReceiveToPresentLatency.SampleCount,
                directReceiveToPresentLatency.P50Milliseconds,
                directReceiveToPresentLatency.P95Milliseconds,
                directReceiveToPresentLatency.P99Milliseconds,
                directReceiveToPresentLatency.MaximumMilliseconds);

            Assert.True(
                client.IsConnected,
                BuildFailureContext(
                    "The viewer disconnected during the 30-second window.",
                    finalSnapshot,
                    logs));
            Assert.Equal(0, disconnectedSamples);
            Assert.Equal(0, inactiveUdpSamples);
            Assert.True(finalSnapshot.IsActive);
            Assert.True(finalSnapshot.CongestionFeedbackNegotiated);
            Assert.True(finalSnapshot.MouseInputAppliedAckNegotiated);
            // FEC parity is adaptive and may remain idle on a clean LAN. Negotiation is the
            // stable assertion; recovered fragments are still reported when loss activates it.
            Assert.True(finalSnapshot.XorFecNegotiated);
            Assert.False(
                finalSnapshot.ShortGopNegotiated,
                "The production Windows viewer unexpectedly negotiated ShortGopH264.");
            Assert.True(
                observedH264Frames > 0,
                "No H.264 frame arrived during the observation window.");
            Assert.True(
                observedUdpH264Frames > 0,
                "No H.264 frame was published while UDP was active.");
            Assert.True(
                observedCompletedFrames > 0,
                "The UDP reassembler did not complete a frame during the observation window.");
            Assert.Equal(
                ExpectedEntityFrameWidth,
                observedFrameSize.Width);
            Assert.Equal(
                ExpectedEntityFrameHeight,
                observedFrameSize.Height);
            Assert.NotEmpty(observedFrameSamples);
            Assert.Equal(0, wrongSizeFrames);
            Assert.Equal(0, observedNonH264Frames);
            Assert.InRange(
                encodedMegabitsPerSecond,
                0.01,
                FfmpegDesktopH264Capture
                    .MaximumBitrateBitsPerSecond /
                    1_000_000d *
                    1.10d);
            Assert.InRange(
                maximumEncodedFrameKilobytes,
                0.01,
                LowLatencyVideoCongestionController
                    .MaximumFrameBudgetBytes /
                    1024d);
            long minimumInteractiveFrames =
                checked((long)Math.Floor(
                    ObservationDuration.TotalSeconds *
                    MinimumNativeUltraHdFramesPerSecond));
            Assert.True(
                observedFrameSamples.LongLength >=
                    minimumInteractiveFrames,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Sustained input delivered only {0} native 4K H.264 frames in {1:F0}s; expected at least {2} ({3} FPS) while preserving the configured spatial resolution.",
                    observedFrameSamples.LongLength,
                    ObservationDuration.TotalSeconds,
                    minimumInteractiveFrames,
                    MinimumNativeUltraHdFramesPerSecond));
            Assert.True(
                observedUdpNativeFrames >=
                    minimumInteractiveFrames,
                $"Only {observedUdpNativeFrames} native 4K H.264 frames arrived over the active UDP route; expected at least {minimumInteractiveFrames}.");
            Assert.True(
                observedCompletedFrames >=
                    minimumInteractiveFrames,
                $"The UDP reassembler completed only {observedCompletedFrames} frames; expected at least {minimumInteractiveFrames}.");
            Assert.True(
                frameIntervals.SampleCount > 0,
                "No consecutive H.264 frame intervals were observed.");
            bool highFrameRateProfile =
                ExpectedEntityFramesPerSecond > 30;
            Assert.InRange(
                frameIntervals.P50Milliseconds,
                0.01,
                highFrameRateProfile ? 20 : 45);
            Assert.InRange(
                frameIntervals.P95Milliseconds,
                0.01,
                highFrameRateProfile ? 20 : 45);
            Assert.InRange(
                frameIntervals.P99Milliseconds,
                0.01,
                // Keep P50/P95 below one 50 Hz period to prove sustained
                // high-frame-rate delivery. P99 may span two 60 Hz periods:
                // the entity WLAN has an independently reproduced driver/RF
                // tail, while the explicit long-gap and minimum-frame-count
                // gates below still reject an application-side 60 -> 30 FPS
                // collapse.
                highFrameRateProfile ? 35 : 60);
            Assert.InRange(
                frameIntervals.MaximumMilliseconds,
                0.01,
                highFrameRateProfile ? 120 : 150);
            Assert.InRange(
                frameIntervals.GapsAbove50Milliseconds,
                0,
                Math.Max(
                    1,
                    checked((int)Math.Ceiling(
                        frameIntervals.SampleCount * 0.01))));
            Assert.InRange(
                frameIntervals.GapsAbove100Milliseconds,
                0,
                2);
            Assert.InRange(
                observedAbandonedFrames,
                0,
                2);
            Assert.True(
                directPresentedFrames >=
                    checked((long)Math.Floor(
                        ObservationDuration.TotalSeconds *
                        MinimumNativeUltraHdFramesPerSecond)),
                $"The exact RemoteViewerWindow MF/D3D11 path presented only {directPresentedFrames} frames during sustained interaction.");
            Assert.Equal(
                0,
                directPresentationFailures);
            Assert.True(
                finalRenderSnapshot
                    .DirectPresentationQualificationAttempts >=
                        RemoteViewerWindow
                            .DirectPresentationQualificationFrames);
            Assert.True(
                finalRenderSnapshot
                    .DirectPresentationQualificationSuccesses > 0);
            Assert.Equal(
                0,
                finalRenderSnapshot
                    .DirectPresentationQualificationFailures);
            // Include bounded driver/MFT scheduling jitter rather than only
            // GPU execution. Keep that internal stage below one 60 Hz frame
            // and enforce the stricter user-visible receive-to-present budget
            // separately.
            Assert.InRange(
                averageDirectDecodeMilliseconds,
                0.01,
                12);
            Assert.InRange(
                averageDirectDecodeToPresentMilliseconds,
                0.01,
                8);
            Assert.InRange(
                averageDirectReceiveToPresentMilliseconds,
                0.01,
                15);
            Assert.True(
                directDecodeLatency.SampleCount > 0 &&
                    directDecodeToPresentLatency.SampleCount > 0 &&
                    directReceiveToPresentLatency.SampleCount > 0,
                "The exact MF/D3D11 path did not retain direct-presentation latency samples.");
            Assert.True(
                double.IsFinite(
                    directDecodeLatency.P95Milliseconds) &&
                    double.IsFinite(
                        directDecodeLatency.P99Milliseconds) &&
                    double.IsFinite(
                        directDecodeLatency.MaximumMilliseconds) &&
                    double.IsFinite(
                        directDecodeToPresentLatency
                            .P95Milliseconds) &&
                    double.IsFinite(
                        directDecodeToPresentLatency
                            .P99Milliseconds) &&
                    double.IsFinite(
                        directDecodeToPresentLatency
                            .MaximumMilliseconds),
                "The exact MF/D3D11 decoder/presenter latency distribution is incomplete.");
            Assert.InRange(
                directReceiveToPresentLatency
                    .P95Milliseconds,
                0.01,
                20);
            Assert.InRange(
                directReceiveToPresentLatency
                    .P99Milliseconds,
                0.01,
                35);
            Assert.InRange(
                directReceiveToPresentLatency
                    .MaximumMilliseconds,
                0.01,
                120);
            Assert.True(
                exercisedMouseMoves > 0 &&
                    inputLatency.SentMouseMoveCount > 0,
                "The real-machine mouse exercise did not enter the UDP input route.");
            Assert.True(
                inputLatency.AcknowledgedMouseMoveCount > 0 &&
                    inputLatency.MatchedLatencySampleCount >= 300,
                "The host did not return enough applied UDP mouse acknowledgements for a stable latency distribution.");
            Assert.InRange(
                inputLatency.SmoothedRoundTripMilliseconds,
                0.01,
                10);
            Assert.InRange(
                inputLatency.P95RoundTripMilliseconds,
                0.01,
                15);
            Assert.InRange(
                inputLatency.P99RoundTripMilliseconds,
                0.01,
                25);
            Assert.InRange(
                inputLatency.MaximumRoundTripMilliseconds,
                0.01,
                150);
            ulong pendingMouseAcknowledgements =
                inputLatency.LatestSentSequence >=
                    inputLatency.LatestAcknowledgedSequence
                    ? inputLatency.LatestSentSequence -
                        inputLatency.LatestAcknowledgedSequence
                    : 0;
            Assert.InRange(
                pendingMouseAcknowledgements,
                0UL,
                32UL);
            Assert.Equal(0, Volatile.Read(ref invalidH264Frames));
            Assert.Equal(
                0,
                Volatile.Read(
                    ref invalidH264FlagFrames));
            Assert.True(
                Volatile.Read(ref recoveryFrames) > 0,
                "No independently recoverable H.264 access unit was observed.");
            Assert.Equal(
                0,
                observedInvalidFlagFrames);
            Assert.Equal(
                0,
                Volatile.Read(ref dependentFrames));
            Assert.Equal(
                0,
                observedDependentFrames);
            Assert.Equal(
                observedFrameSamples.LongLength,
                observedRecoveryFrames);
            Assert.Equal(
                Volatile.Read(ref h264Frames),
                Volatile.Read(ref recoveryFrames));
            Assert.Contains(
                logs,
                message => message.Contains(
                    "低延迟 UDP 画面握手完成",
                    StringComparison.Ordinal));
            Assert.DoesNotContain(
                logs,
                message => message.Contains("回退 TCP", StringComparison.Ordinal) ||
                    message.Contains("握手超时", StringComparison.Ordinal));
            Assert.NotEmpty(roundTrips);
        }
        finally
        {
            if (viewerWindowHost is not null)
            {
                await viewerWindowHost.DisposeAsync();
            }

            await client.DisconnectAsync();
        }
    }

    private void WriteDiagnostics(
        string stage,
        RemoteDeviceDescriptor device,
        UdpViewerSnapshot udp,
        long totalFrames,
        long h264Frames,
        long udpH264Frames,
        long recoveryFrames,
        long dependentFrames,
        long invalidH264Frames,
        long invalidH264FlagFrames,
        ConcurrentQueue<double> roundTrips,
        ConcurrentQueue<string> logs)
    {
        double[] rtt = roundTrips.ToArray();
        string rttSummary = rtt.Length == 0
            ? "n/a"
            : string.Format(
                CultureInfo.InvariantCulture,
                "avg={0:F2}ms/max={1:F2}ms/samples={2}",
                rtt.Average(),
                rtt.Max(),
                rtt.Length);
        _output.WriteLine(
            "{0}: endpoint={1}:{2}; device={3}; platform={4}; build={5}; duration={6:F0}s",
            stage,
            Host,
            Port,
            device.MachineName,
            device.Platform,
            device.BuildStamp ?? "unknown",
            ObservationDuration.TotalSeconds);
        _output.WriteLine(
            "{0}: frames={1}; h264={2}; udpH264={3}; recoveryIDR={4}; dependentP={5}; invalidH264={6}; invalidFlags={7}; rtt={8}",
            stage,
            Volatile.Read(ref totalFrames),
            Volatile.Read(ref h264Frames),
            Volatile.Read(ref udpH264Frames),
            Volatile.Read(ref recoveryFrames),
            Volatile.Read(ref dependentFrames),
            Volatile.Read(ref invalidH264Frames),
            Volatile.Read(ref invalidH264FlagFrames),
            rttSummary);
        _output.WriteLine(
            "{0}: udpInstalled={1}; state={2}; active={3}; remote={4}; local={5}; FeedbackV2={6}; XorFec={7}; ShortGop={8}; MouseAppliedAck={9}; completed={10}; abandoned={11}; recoveredFecFragments={12}",
            stage,
            udp.IsInstalled,
            udp.State,
            udp.IsActive,
            udp.HostEndpoint,
            udp.LocalEndpoint,
            udp.CongestionFeedbackNegotiated,
            udp.XorFecNegotiated,
            udp.ShortGopNegotiated,
            udp.MouseInputAppliedAckNegotiated,
            udp.CompletedFrames,
            udp.AbandonedIncompleteFrames,
            udp.RecoveredFecFragments);
        foreach (string message in logs.TakeLast(20))
        {
            _output.WriteLine("client-log: {0}", message);
        }
    }

    private static string BuildFailureContext(
        string message,
        UdpViewerSnapshot udp,
        ConcurrentQueue<string> logs)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0} UDP installed={1}, state={2}, remote={3}, local={4}, feedback={5}, FEC={6}, shortGop={7}, mouseAck={8}, completed={9}, abandoned={10}, recovered={11}. Logs: {12}",
            message,
            udp.IsInstalled,
            udp.State,
            udp.HostEndpoint,
            udp.LocalEndpoint,
            udp.CongestionFeedbackNegotiated,
            udp.XorFecNegotiated,
            udp.ShortGopNegotiated,
            udp.MouseInputAppliedAckNegotiated,
            udp.CompletedFrames,
            udp.AbandonedIncompleteFrames,
            udp.RecoveredFecFragments,
            string.Join(" | ", logs.TakeLast(8)));
    }

    private static UdpViewerSnapshot CaptureUdpSnapshot(
        RemoteViewerClient client)
    {
        if (LowLatencyTransportField.GetValue(client) is not
            LowLatencyVideoViewerTransport transport)
        {
            return default;
        }

        var features =
            (LowLatencyVideoFeatures)(LowLatencyFeaturesField.GetValue(transport) ??
                LowLatencyVideoFeatures.None);
        var reassembler =
            (LowLatencyVideoAdjacentFrameReassembler)
                (LowLatencyReassemblerField.GetValue(transport) ??
                    throw new InvalidOperationException(
                        "Low-latency UDP reassembler is unavailable."));
        int state =
            (int)(LowLatencyStateField.GetValue(transport) ??
                throw new InvalidOperationException(
                    "Low-latency UDP state is unavailable."));
        string hostEndpoint =
            LowLatencyHostEndpointField.GetValue(transport)?.ToString() ??
            "unavailable";
        string localEndpoint = GetLocalEndpoint(transport);
        return new UdpViewerSnapshot(
            IsInstalled: true,
            State: state,
            IsActive: state == 2,
            HostEndpoint: hostEndpoint,
            LocalEndpoint: localEndpoint,
            CongestionFeedbackNegotiated:
                features.HasFlag(LowLatencyVideoFeatures.CongestionFeedback),
            XorFecNegotiated:
                features.HasFlag(LowLatencyVideoFeatures.XorFec),
            ShortGopNegotiated:
                features.HasFlag(
                    LowLatencyVideoFeatures.ShortGopH264),
            MouseInputAppliedAckNegotiated:
                features.HasFlag(
                    LowLatencyVideoFeatures
                        .UdpMouseInputAppliedAck),
            CompletedFrames: reassembler.CompletedFrameCount,
            AbandonedIncompleteFrames:
                reassembler.AbandonedIncompleteFrameCount,
            RecoveredFecFragments: reassembler.RecoveredFragmentCount);
    }

    private static string GetLocalEndpoint(
        LowLatencyVideoViewerTransport transport)
    {
        try
        {
            return (LowLatencySocketField.GetValue(transport) as
                    System.Net.Sockets.Socket)?.LocalEndPoint?.ToString() ??
                "unavailable";
        }
        catch (ObjectDisposedException)
        {
            return "disposed";
        }
    }

    private static FieldInfo GetRequiredField(Type owner, string fieldName)
    {
        return owner.GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new MissingFieldException(owner.FullName, fieldName);
    }

    private static string RedactSecret(string message, string secret)
    {
        return message.Replace(secret, "<redacted>", StringComparison.Ordinal);
    }

    private static int ReadOptionalCoordinate(
        string variable,
        int fallback,
        int exclusiveMaximum)
    {
        string? value = Environment.GetEnvironmentVariable(variable);
        return int.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int parsed)
            ? Math.Clamp(parsed, 0, exclusiveMaximum - 1)
            : fallback;
    }

    private static async Task<long> ExerciseMouseInputAsync(
        RemoteViewerClient client,
        int frameWidth,
        int frameHeight,
        TimeSpan duration)
    {
        Assert.True(frameWidth > 1 && frameHeight > 0);
        int centerX = frameWidth / 2;
        int centerY = frameHeight / 2;
        long sent = 0;
        var elapsed = Stopwatch.StartNew();
        while (client.IsConnected &&
            elapsed.Elapsed < duration)
        {
            int x = centerX + (int)(sent & 1);
            await client.SendInputAsync(
                RemoteInputCommand.MouseMove(
                    x,
                    centerY));
            sent++;
            await Task.Delay(8);
        }

        return sent;
    }

    private sealed class RealViewerWindowHost :
        IAsyncDisposable
    {
        private readonly TaskCompletionSource<
            RemoteViewerWindow> _ready =
                new(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stopped =
            new(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        private readonly Thread _thread;
        private RemoteViewerWindow? _window;
        private int _disposed;

        private RealViewerWindowHost(
            RemoteViewerClient client)
        {
            _thread = new Thread(
                () => RunMessageLoop(client))
            {
                IsBackground = true,
                Name =
                    "RemoteDesk real-machine exact viewer"
            };
            _thread.SetApartmentState(
                ApartmentState.STA);
            _thread.Start();
        }

        public static async Task<
            RealViewerWindowHost> StartAsync(
                RemoteViewerClient client)
        {
            var host =
                new RealViewerWindowHost(client);
            try
            {
                await host._ready.Task.WaitAsync(
                    TimeSpan.FromSeconds(10));
                return host;
            }
            catch
            {
                await host.DisposeAsync();
                throw;
            }
        }

        public RemoteViewerRenderTelemetrySnapshot
            CollectRenderTelemetrySnapshot(
                bool includeLatencyDistributions = false)
        {
            RemoteViewerWindow? window =
                Volatile.Read(ref _window);
            return window is not null &&
                !window.IsDisposed
                    ? window
                        .CollectRenderTelemetrySnapshot(
                            includeLatencyDistributions)
                    : default;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(
                    ref _disposed,
                    1) != 0)
            {
                return;
            }

            RemoteViewerWindow? window =
                Volatile.Read(ref _window);
            if (window is not null &&
                !window.IsDisposed)
            {
                try
                {
                    window.BeginInvoke(
                        new Action(window.Close));
                }
                catch (Exception ex) when (
                    ex is InvalidOperationException or
                        ObjectDisposedException)
                {
                }
            }

            await _stopped.Task.WaitAsync(
                TimeSpan.FromSeconds(10));
            _thread.Join(
                TimeSpan.FromSeconds(1));
        }

        private void RunMessageLoop(
            RemoteViewerClient client)
        {
            try
            {
                using var window =
                    new RemoteViewerWindow(
                        client,
                        "RemoteDesk 实体低延迟显示验证",
                        inputEnabled: false,
                        clipboardTextEnabled: false,
                        filePasteEnabled: false,
                        fileDropPasteEnabled: false,
                        remoteFilePullEnabled: false,
                        isAndroidRemote: false)
                    {
                        ShowInTaskbar = false
                    };
                window.SetRemotePlatform(
                    RemoteDevicePlatforms.Windows);
                Volatile.Write(
                    ref _window,
                    window);
                window.Shown += (_, _) =>
                    _ready.TrySetResult(window);
                Application.Run(window);
            }
            catch (Exception ex)
            {
                _ready.TrySetException(ex);
            }
            finally
            {
                Volatile.Write(
                    ref _window,
                    null);
                _stopped.TrySetResult();
            }
        }
    }

    internal static FrameIntervalSummary SummarizeFrameIntervals(
        IEnumerable<long> timestamps,
        long notBeforeTimestamp = 0,
        long notAfterTimestamp = long.MaxValue)
    {
        if (notAfterTimestamp < notBeforeTimestamp)
        {
            throw new ArgumentOutOfRangeException(
                nameof(notAfterTimestamp));
        }

        long[] ordered = timestamps
            .Where(timestamp =>
                timestamp >= notBeforeTimestamp &&
                timestamp <= notAfterTimestamp)
            .Order()
            .ToArray();
        if (ordered.Length < 2)
        {
            return default;
        }

        double timestampToMilliseconds =
            1000d / Stopwatch.Frequency;
        double[] intervals = new double[ordered.Length - 1];
        for (int index = 1; index < ordered.Length; index++)
        {
            intervals[index - 1] =
                Math.Max(
                    0,
                    (ordered[index] - ordered[index - 1]) *
                        timestampToMilliseconds);
        }

        Array.Sort(intervals);
        return new FrameIntervalSummary(
            SampleCount: intervals.Length,
            AverageMilliseconds: intervals.Average(),
            P50Milliseconds: Percentile(intervals, 0.50),
            P95Milliseconds: Percentile(intervals, 0.95),
            P99Milliseconds: Percentile(intervals, 0.99),
            MaximumMilliseconds: intervals[^1],
            GapsAbove50Milliseconds:
                intervals.Count(interval => interval > 50),
            GapsAbove100Milliseconds:
                intervals.Count(interval => interval > 100));
    }

    internal static FrameGapDiagnostic[] FindLargestFrameGaps(
        IEnumerable<long> timestamps,
        long observationStartedAt,
        long observationEndedAt,
        int maximumCount)
    {
        if (observationEndedAt < observationStartedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(observationEndedAt));
        }

        if (maximumCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumCount));
        }

        long[] ordered = timestamps
            .Where(timestamp =>
                timestamp >= observationStartedAt &&
                timestamp <= observationEndedAt)
            .Order()
            .ToArray();
        if (ordered.Length < 2 || maximumCount == 0)
        {
            return [];
        }

        double timestampToMilliseconds =
            1000d / Stopwatch.Frequency;
        return Enumerable
            .Range(1, ordered.Length - 1)
            .Select(index =>
            {
                double endOffsetSeconds =
                    (ordered[index] - observationStartedAt) /
                    (double)Stopwatch.Frequency;
                return new FrameGapDiagnostic(
                    EndOffsetSeconds: endOffsetSeconds,
                    IntervalMilliseconds:
                        Math.Max(
                            0,
                            (ordered[index] - ordered[index - 1]) *
                                timestampToMilliseconds),
                    TwoSecondPhaseSeconds:
                        endOffsetSeconds % 2d);
            })
            .OrderByDescending(gap =>
                gap.IntervalMilliseconds)
            .Take(maximumCount)
            .OrderBy(gap =>
                gap.EndOffsetSeconds)
            .ToArray();
    }

    private static double Percentile(
        IReadOnlyList<double> orderedValues,
        double percentile)
    {
        if (orderedValues.Count == 0)
        {
            return 0;
        }

        int index = (int)Math.Ceiling(
            Math.Clamp(percentile, 0, 1) *
                orderedValues.Count) - 1;
        return orderedValues[
            Math.Clamp(index, 0, orderedValues.Count - 1)];
    }

    private static int ReadPositiveEnvironmentInteger(
        string variableName,
        int fallback,
        int maximum)
    {
        if (fallback <= 0 || maximum < fallback)
        {
            throw new ArgumentOutOfRangeException(
                maximum < fallback
                    ? nameof(maximum)
                    : nameof(fallback));
        }

        string? value =
            Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (!int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int parsed) ||
            parsed <= 0 ||
            parsed > maximum)
        {
            throw new InvalidOperationException(
                $"{variableName} must be an integer between 1 and " +
                $"{maximum}.");
        }

        return parsed;
    }

    private static long PackFrameGeometry(
        int width,
        int height) =>
        ((long)(uint)width << 32) |
        (uint)height;

    private static Size UnpackFrameGeometry(
        long geometry) =>
        new(
            unchecked((int)(uint)(
                (ulong)geometry >> 32)),
            unchecked((int)(uint)geometry));

    internal readonly record struct FrameIntervalSummary(
        int SampleCount,
        double AverageMilliseconds,
        double P50Milliseconds,
        double P95Milliseconds,
        double P99Milliseconds,
        double MaximumMilliseconds,
        int GapsAbove50Milliseconds,
        int GapsAbove100Milliseconds);

    internal readonly record struct FrameGapDiagnostic(
        double EndOffsetSeconds,
        double IntervalMilliseconds,
        double TwoSecondPhaseSeconds);

    private readonly record struct EntityFrameSample(
        long ArrivedAt,
        int Width,
        int Height,
        int EncodedLength,
        RemoteFrameFlags Flags,
        bool IsUdpActive);

    private readonly record struct UdpViewerSnapshot(
        bool IsInstalled,
        int State,
        bool IsActive,
        string? HostEndpoint,
        string? LocalEndpoint,
        bool CongestionFeedbackNegotiated,
        bool XorFecNegotiated,
        bool ShortGopNegotiated,
        bool MouseInputAppliedAckNegotiated,
        long CompletedFrames,
        long AbandonedIncompleteFrames,
        long RecoveredFecFragments);
}
