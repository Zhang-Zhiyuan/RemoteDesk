using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Xunit;
using Xunit.Abstractions;

namespace RemoteDesk.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class RemoteDeskLocalHostInputFactAttribute : FactAttribute
{
    internal const string EnabledVariable =
        "REMOTEDESK_LOCAL_HOST_INPUT_SMOKE";

    public RemoteDeskLocalHostInputFactAttribute()
    {
        string? enabled =
            Environment.GetEnvironmentVariable(EnabledVariable);
        if (!string.Equals(enabled, "1", StringComparison.Ordinal) &&
            !string.Equals(
                enabled,
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            Skip =
                $"Set {EnabledVariable}=1 to run this interactive " +
                "loopback input smoke test against an external host.";
        }
    }
}

[Collection(WindowsRealMachineCollection.Name)]
public sealed class WindowsLocalHostInputSmokeTests
{
    private readonly ITestOutputHelper _output;

    public WindowsLocalHostInputSmokeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [RemoteDeskLocalHostInputFact]
    public async Task ExternalHostAppliesAndAcknowledgesUdpMouseMove()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(45));
        RemoteDeskSettings settings = new AppSettingsService().Load();
        string password =
            AppSettingsService.UnprotectSecret(
                settings.Host.ProtectedPassword);
        Assert.False(
            string.IsNullOrWhiteSpace(password),
            "The external loopback host has no saved access password.");

        ScreenCaptureTarget captureTarget =
            ScreenCaptureService.FindTargetById(
                settings.Host.CaptureTargetId ?? string.Empty);
        if (captureTarget.IsAllScreens)
        {
            captureTarget =
                ScreenCaptureService.ChooseLowLatencyStartupTarget(
                    captureTarget,
                    ScreenCaptureService.GetAvailableTargets());
        }

        using var cadenceStimulus =
            new CadenceStimulusWindow(captureTarget.Bounds);
        await cadenceStimulus.Ready.WaitAsync(timeout.Token);
        // The stimulus must be fully composed and then remain unchanged while
        // the Host starts WGC. Receiving the first H.264 frame below therefore
        // proves that a static startup recovery frame was actually handed off;
        // animation begins only after that gate has passed.
        await Task.Delay(
            TimeSpan.FromMilliseconds(500),
            timeout.Token);

        var logs = new ConcurrentQueue<string>();
        var udpReady =
            new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var h264Frame =
            new TaskCompletionSource<RemoteFrameMetadata>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var h264Arrivals = new ConcurrentQueue<long>();
        using var client = new RemoteViewerClient(allowLocalConnectionsForTesting: true);
        client.Log += message =>
        {
            logs.Enqueue(message);
            if (message.Contains(
                    "低延迟 UDP 画面握手完成",
                    StringComparison.Ordinal))
            {
                udpReady.TrySetResult();
            }
        };
        client.FrameReceived += frame =>
        {
            if (frame.Encoding == RemoteFrameEncoding.H264AnnexB)
            {
                h264Arrivals.Enqueue(Stopwatch.GetTimestamp());
                h264Frame.TrySetResult(
                    RemoteFrameMetadata.FromFrame(frame));
            }
        };

        Point originalCursor = Cursor.Position;
        Point originalPhysicalCursor = GetPhysicalCursorPosition();
        try
        {
            await client.ConnectAsync(
                "127.0.0.1",
                settings.Host.Port,
                password,
                ViewerVideoMode.Automatic);
            await udpReady.Task.WaitAsync(timeout.Token);
            RemoteFrameMetadata frame =
                await h264Frame.Task.WaitAsync(timeout.Token);

            // Keep the stimulus completely static beyond both historical
            // failure thresholds: the viewer used to abandon UDP after
            // 2.5 seconds without a complete frame, while the host used to
            // kill a healthy change-driven WGC source after 6 seconds. The
            // later UDP mouse ACK and >=45 FPS animation together prove that
            // the same low-latency route and WGC source survived this idle
            // window rather than silently falling back to TCP/GDI.
            await Task.Delay(
                TimeSpan.FromSeconds(7),
                timeout.Token);
            Assert.True(
                client.IsConnected,
                "The external host disconnected during a static desktop.");
            Assert.DoesNotContain(
                logs,
                message => message.Contains(
                    "已回退 TCP",
                    StringComparison.Ordinal) ||
                    message.Contains(
                        "继续使用 TCP",
                        StringComparison.Ordinal));

            Rectangle physicalBounds =
                GetPhysicalDisplayBounds(captureTarget.Id);
            Point desired = ChooseDifferentPoint(
                physicalBounds,
                originalPhysicalCursor);
            int commandX = ScaleCoordinateInverse(
                desired.X - physicalBounds.Left,
                physicalBounds.Width,
                frame.Width);
            int commandY = ScaleCoordinateInverse(
                desired.Y - physicalBounds.Top,
                physicalBounds.Height,
                frame.Height);
            Point expected = new(
                physicalBounds.Left + ScaleCoordinate(
                    commandX,
                    frame.Width,
                    physicalBounds.Width),
                physicalBounds.Top + ScaleCoordinate(
                    commandY,
                    frame.Height,
                    physicalBounds.Height));

            LowLatencyMouseInputLatencySnapshot before =
                client.CollectUdpMouseInputLatencySnapshot();
            await client.SendInputAsync(
                RemoteInputCommand.MouseMove(commandX, commandY));

            await WaitUntilAsync(
                () => client
                    .CollectUdpMouseInputLatencySnapshot()
                    .AcknowledgedMouseMoveCount >
                    before.AcknowledgedMouseMoveCount,
                timeout.Token);
            Point appliedCursor = GetPhysicalCursorPosition();
            Assert.InRange(
                Math.Abs(appliedCursor.X - expected.X),
                0,
                64);
            Assert.InRange(
                Math.Abs(appliedCursor.Y - expected.Y),
                0,
                64);

            LowLatencyMouseInputLatencySnapshot after =
                client.CollectUdpMouseInputLatencySnapshot();
            Assert.True(
                after.SentMouseMoveCount > before.SentMouseMoveCount,
                "The mouse move did not use the authenticated UDP route.");
            Assert.Equal(
                after.LatestSentSequence,
                after.LatestAcknowledgedSequence);
            Assert.DoesNotContain(
                logs,
                message => message.Contains(
                    "输入失败",
                    StringComparison.Ordinal));

            FrameCadence cadence = await MeasureInteractiveCadenceAsync(
                cadenceStimulus,
                h264Arrivals,
                timeout.Token);
            Assert.True(
                cadence.FramesPerSecond >= 45,
                $"Expected the WGC/H.264 loopback path to sustain at " +
                $"least 45 FPS, got {cadence.FramesPerSecond:F1} FPS " +
                $"from {cadence.FrameCount} frames.");

            _output.WriteLine(
                $"frame={frame.Width}x{frame.Height}; " +
                $"cursorExpected={expected.X},{expected.Y}; " +
                $"cursorObserved={appliedCursor.X},{appliedCursor.Y}; " +
                $"udpAck={after.LatestRoundTripMilliseconds:F2}ms; " +
                $"cadence={cadence.FramesPerSecond:F1}fps; " +
                $"p95={cadence.P95IntervalMilliseconds:F2}ms");
        }
        finally
        {
            await client.DisconnectAsync();
            Cursor.Position = originalCursor;
        }
    }

    private static async Task<FrameCadence> MeasureInteractiveCadenceAsync(
        CadenceStimulusWindow stimulus,
        ConcurrentQueue<long> arrivals,
        CancellationToken cancellationToken)
    {
        while (arrivals.TryDequeue(out _))
        {
        }

        long startedAt = Stopwatch.GetTimestamp();
        long durationTicks = checked(3 * Stopwatch.Frequency);
        stimulus.StartAnimating();
        try
        {
            while (Stopwatch.GetTimestamp() - startedAt < durationTicks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(
                    TimeSpan.FromMilliseconds(25),
                    cancellationToken);
            }
        }
        finally
        {
            stimulus.StopAnimating();
        }

        await Task.Delay(
            TimeSpan.FromMilliseconds(250),
            cancellationToken);
        stimulus.ThrowIfFaulted();
        long[] snapshot = arrivals
            .Where(timestamp => timestamp >= startedAt)
            .Order()
            .ToArray();
        Assert.True(
            snapshot.Length >= 2,
            "The external WGC/H.264 host produced fewer than two frames.");

        double[] intervals = snapshot
            .Zip(
                snapshot.Skip(1),
                (previous, current) =>
                    (current - previous) *
                    1000d /
                    Stopwatch.Frequency)
            .ToArray();
        double observationSeconds =
            (snapshot[^1] - snapshot[0]) /
            (double)Stopwatch.Frequency;
        double framesPerSecond =
            intervals.Length /
            observationSeconds;
        Array.Sort(intervals);
        int p95Index = Math.Clamp(
            (int)Math.Ceiling(intervals.Length * 0.95) - 1,
            0,
            intervals.Length - 1);
        return new FrameCadence(
            snapshot.Length,
            framesPerSecond,
            intervals[p95Index]);
    }

    private static Point ChooseDifferentPoint(
        Rectangle bounds,
        Point current)
    {
        int insetX = Math.Min(32, Math.Max(1, bounds.Width / 4));
        int insetY = Math.Min(32, Math.Max(1, bounds.Height / 4));
        var first = new Point(
            bounds.Left + insetX,
            bounds.Top + insetY);
        if (first != current)
        {
            return first;
        }

        return new Point(
            Math.Max(bounds.Left, bounds.Right - insetX - 1),
            Math.Max(bounds.Top, bounds.Bottom - insetY - 1));
    }

    private static Point GetPhysicalCursorPosition()
    {
        Assert.True(
            GetPhysicalCursorPos(out NativePoint point),
            $"GetPhysicalCursorPos failed with Win32 error " +
            $"{Marshal.GetLastWin32Error()}.");
        return new Point(point.X, point.Y);
    }

    private static Rectangle GetPhysicalDisplayBounds(
        string deviceName)
    {
        const int devModeSize = 220;
        nint mode = Marshal.AllocHGlobal(devModeSize);
        try
        {
            Marshal.Copy(new byte[devModeSize], 0, mode, devModeSize);
            Marshal.WriteInt16(
                mode,
                68,
                checked((short)devModeSize));
            Assert.True(
                EnumDisplaySettingsW(
                    deviceName,
                    EnumCurrentSettings,
                    mode),
                $"EnumDisplaySettingsW failed for {deviceName} with " +
                $"Win32 error {Marshal.GetLastWin32Error()}.");
            int left = Marshal.ReadInt32(mode, 76);
            int top = Marshal.ReadInt32(mode, 80);
            int width = Marshal.ReadInt32(mode, 172);
            int height = Marshal.ReadInt32(mode, 176);
            Assert.True(
                width > 0 && height > 0,
                $"Display {deviceName} returned invalid physical bounds " +
                $"{width}x{height} at ({left},{top}).");
            return new Rectangle(left, top, width, height);
        }
        finally
        {
            Marshal.FreeHGlobal(mode);
        }
    }

    private static int ScaleCoordinateInverse(
        int coordinate,
        int targetLength,
        int sourceLength)
    {
        if (sourceLength <= 1 || targetLength <= 1)
        {
            return 0;
        }

        int clamped = Math.Clamp(coordinate, 0, targetLength - 1);
        return Math.Clamp(
            (int)Math.Round(
                clamped *
                (sourceLength - 1) /
                (double)(targetLength - 1)),
            0,
            sourceLength - 1);
    }

    private static int ScaleCoordinate(
        int coordinate,
        int sourceLength,
        int targetLength)
    {
        if (sourceLength <= 1 || targetLength <= 1)
        {
            return 0;
        }

        int clamped = Math.Clamp(coordinate, 0, sourceLength - 1);
        return Math.Clamp(
            (int)Math.Round(
                clamped *
                (targetLength - 1) /
                (double)(sourceLength - 1)),
            0,
            targetLength - 1);
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        CancellationToken cancellationToken)
    {
        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(
                TimeSpan.FromMilliseconds(10),
                cancellationToken);
        }
    }

    private const int EnumCurrentSettings = -1;

    [DllImport(
        "user32.dll",
        EntryPoint = "GetPhysicalCursorPos",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalCursorPos(
        out NativePoint point);

    [DllImport(
        "user32.dll",
        EntryPoint = "EnumDisplaySettingsW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsW(
        string deviceName,
        int modeNumber,
        nint deviceMode);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint
    {
        public readonly int X;
        public readonly int Y;
    }

    private sealed class CadenceStimulusWindow : IDisposable
    {
        private readonly Rectangle _captureBounds;
        private readonly Thread _thread;
        private readonly TaskCompletionSource _ready = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private Exception? _failure;
        private int _position;
        private int _animating;
        private int _stopRequested;
        private int _disposed;

        public CadenceStimulusWindow(Rectangle captureBounds)
        {
            _captureBounds = captureBounds;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "RemoteDesk cadence stimulus"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public Task Ready => _ready.Task;

        public void StartAnimating()
        {
            ThrowIfFaulted();
            Volatile.Write(ref _animating, 1);
        }

        public void StopAnimating()
        {
            Volatile.Write(ref _animating, 0);
        }

        public void ThrowIfFaulted()
        {
            Exception? failure = Volatile.Read(ref _failure);
            if (failure is not null)
            {
                throw new InvalidOperationException(
                    "The cadence stimulus window failed.",
                    failure);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Volatile.Write(ref _stopRequested, 1);
            if (!_thread.Join(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException(
                    "The cadence stimulus window did not stop.");
            }

            ThrowIfFaulted();
        }

        private void Run()
        {
            nint window = nint.Zero;
            try
            {
                int width = Math.Min(
                    800,
                    Math.Max(240, _captureBounds.Width / 3));
                int height = Math.Min(
                    480,
                    Math.Max(160, _captureBounds.Height / 3));
                int baseX = _captureBounds.Left + 96;
                int baseY = _captureBounds.Top + 96;
                window = CreateWindowExW(
                    WsExTopmost | WsExToolWindow,
                    "STATIC",
                    "RemoteDesk cadence stimulus",
                    WsPopup | WsVisible | SsBlackRect,
                    baseX,
                    baseY,
                    width,
                    height,
                    nint.Zero,
                    nint.Zero,
                    nint.Zero,
                    nint.Zero);
                if (window == nint.Zero)
                {
                    throw new InvalidOperationException(
                        $"CreateWindowExW failed with Win32 error " +
                        $"{Marshal.GetLastWin32Error()}.");
                }

                ShowWindow(window, SwShowNoActivate);
                UpdateWindow(window);
                _ready.TrySetResult();

                while (Volatile.Read(ref _stopRequested) == 0)
                {
                    while (PeekMessageW(
                               out NativeMessage message,
                               nint.Zero,
                               0,
                               0,
                               PmRemove))
                    {
                        TranslateMessage(ref message);
                        DispatchMessageW(ref message);
                    }

                    if (Volatile.Read(ref _animating) != 0)
                    {
                        int position =
                            Interlocked.Increment(ref _position) & 63;
                        if (!SetWindowPos(
                                window,
                                nint.Zero,
                                baseX + position,
                                baseY,
                                0,
                                0,
                                SwpNoSize |
                                SwpNoZOrder |
                                SwpNoActivate))
                        {
                            throw new InvalidOperationException(
                                $"SetWindowPos failed with Win32 error " +
                                $"{Marshal.GetLastWin32Error()}.");
                        }
                    }

                    Thread.Sleep(1);
                }
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _failure, ex);
                _ready.TrySetException(ex);
            }
            finally
            {
                if (window != nint.Zero)
                {
                    DestroyWindow(window);
                }
            }
        }

        private const uint WsExTopmost = 0x00000008;
        private const uint WsExToolWindow = 0x00000080;
        private const uint WsPopup = 0x80000000;
        private const uint WsVisible = 0x10000000;
        private const uint SsBlackRect = 0x00000004;
        private const int SwShowNoActivate = 4;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;
        private const uint PmRemove = 0x0001;

        [DllImport(
            "user32.dll",
            EntryPoint = "CreateWindowExW",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern nint CreateWindowExW(
            uint extendedStyle,
            string className,
            string windowName,
            uint style,
            int x,
            int y,
            int width,
            int height,
            nint parent,
            nint menu,
            nint instance,
            nint parameter);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(nint window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(nint window, int command);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UpdateWindow(nint window);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(
            nint window,
            nint insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [DllImport(
            "user32.dll",
            EntryPoint = "PeekMessageW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PeekMessageW(
            out NativeMessage message,
            nint window,
            uint minimum,
            uint maximum,
            uint removeMessage);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TranslateMessage(
            ref NativeMessage message);

        [DllImport(
            "user32.dll",
            EntryPoint = "DispatchMessageW")]
        private static extern nint DispatchMessageW(
            ref NativeMessage message);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMessage
        {
            public nint Window;
            public uint Message;
            public nuint WParam;
            public nint LParam;
            public uint Time;
            public Point Point;
        }
    }

    private readonly record struct FrameCadence(
        int FrameCount,
        double FramesPerSecond,
        double P95IntervalMilliseconds);
}
