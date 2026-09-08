using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

internal static class Program
{
    private static readonly Guid GraphicsCaptureItemGuid =
        new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    [STAThread]
    private static int Main(string[] args)
    {
        bool animationOnly =
            args.FirstOrDefault() is string mode &&
            string.Equals(
                mode,
                "--animate-only",
                StringComparison.OrdinalIgnoreCase);
        int argumentOffset = animationOnly ? 1 : 0;
        string? requestedDevice =
            args.Skip(argumentOffset).FirstOrDefault();
        int seconds =
            args.Length >= argumentOffset + 2 &&
            int.TryParse(
                args[argumentOffset + 1],
                out int parsedSeconds)
                ? Math.Clamp(parsedSeconds, 2, 60)
                : 10;
        int animationIntervalMilliseconds =
            args.Length >= argumentOffset + 3 &&
            int.TryParse(
                args[argumentOffset + 2],
                out int parsedAnimationInterval)
                ? Math.Clamp(parsedAnimationInterval, 1, 1000)
                : 1;
        int requestedCaptureRate =
            args.Length >= argumentOffset + 4 &&
            int.TryParse(
                args[argumentOffset + 3],
                out int parsedCaptureRate)
                ? Math.Clamp(parsedCaptureRate, 0, 1000)
                : 0;
        string selectionMode =
            args.Length >= argumentOffset + 5
                ? args[argumentOffset + 4]
                : "all";
        if (selectionMode is not ("all" or "bucket60"))
        {
            Console.Error.WriteLine(
                "selection must be all or bucket60");
            return 4;
        }

        if (!GraphicsCaptureSession.IsSupported())
        {
            Console.Error.WriteLine("WGC_UNSUPPORTED");
            return 2;
        }

        MonitorTarget target =
            FindMonitor(requestedDevice) ??
            throw new InvalidOperationException(
                $"Monitor not found: {requestedDevice ?? "<primary>"}");

        using var animation = new AnimationWindow(
            target.Bounds,
            animationIntervalMilliseconds);
        animation.Start();
        if (animationOnly)
        {
            Thread.Sleep(TimeSpan.FromSeconds(seconds));
            animation.Stop();
            Console.WriteLine(
                $"ANIMATION_RESULT device={target.DeviceName} " +
                $"seconds={seconds} interval_ms=" +
                $"{animationIntervalMilliseconds} " +
                $"size={animation.Size.Width}x" +
                $"{animation.Size.Height} " +
                $"ticks={animation.TickCount}");
            return animation.TickCount > 0 ? 0 : 3;
        }

        using IDXGIAdapter1 adapter =
            FindAdapterForMonitor(target.Handle);
        D3D11.D3D11CreateDevice(
            adapter,
            DriverType.Unknown,
            DeviceCreationFlags.BgraSupport,
            [
                FeatureLevel.Level_11_1,
                FeatureLevel.Level_11_0,
                FeatureLevel.Level_10_1,
                FeatureLevel.Level_10_0
            ],
            out ID3D11Device? d3dDevice).CheckError();
        if (d3dDevice is null)
        {
            throw new InvalidOperationException(
                "D3D11CreateDevice returned no device.");
        }

        using (d3dDevice)
        {
        using IDXGIDevice dxgiDevice =
            d3dDevice.QueryInterface<IDXGIDevice>();
        IDirect3DDevice winrtDevice =
            CreateWinRtDevice(dxgiDevice.NativePointer);
        GraphicsCaptureItem item =
            CreateItemForMonitor(target.Handle);

        using Direct3D11CaptureFramePool framePool =
            Direct3D11CaptureFramePool.CreateFreeThreaded(
                winrtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                item.Size);
        using GraphicsCaptureSession session =
            framePool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;
        bool captureIntervalApplied =
            requestedCaptureRate > 0 &&
            TrySetMinUpdateInterval(
                session,
                requestedCaptureRate);

        int frames = 0;
        int wrongSize = 0;
        long firstFrameAt = 0;
        long lastFrameAt = 0;
        var arrivalTimestamps = new List<long>();
        var sourceTimestamps = new List<long>();
        var selectedSourceTimestamps = new List<long>();
        object timestampGate = new();
        object callbackGate = new();
        bool acceptingFrames = true;
        int callbacksInFlight = 0;
        long firstSourceTimestamp = long.MinValue;
        long lastSelectedBucket = long.MinValue;
        long startedAt = Stopwatch.GetTimestamp();
        framePool.FrameArrived += (sender, _) =>
        {
            lock (callbackGate)
            {
                if (!acceptingFrames)
                {
                    return;
                }

                callbacksInFlight++;
            }

            try
            {
                using Direct3D11CaptureFrame? frame =
                    sender.TryGetNextFrame();
                if (frame is null)
                {
                    return;
                }

                long now = Stopwatch.GetTimestamp();
                Interlocked.CompareExchange(
                    ref firstFrameAt,
                    now,
                    0);
                Volatile.Write(ref lastFrameAt, now);
                lock (timestampGate)
                {
                    arrivalTimestamps.Add(now);
                    long sourceTimestamp =
                        frame.SystemRelativeTime.Ticks;
                    sourceTimestamps.Add(sourceTimestamp);
                    if (firstSourceTimestamp == long.MinValue)
                    {
                        firstSourceTimestamp = sourceTimestamp;
                    }

                    long bucket = checked(
                        (sourceTimestamp - firstSourceTimestamp) *
                        60 /
                        TimeSpan.TicksPerSecond);
                    if (selectionMode == "all" ||
                        bucket > lastSelectedBucket)
                    {
                        selectedSourceTimestamps.Add(sourceTimestamp);
                        lastSelectedBucket = bucket;
                    }
                }
                if (frame.ContentSize.Width != item.Size.Width ||
                    frame.ContentSize.Height != item.Size.Height)
                {
                    Interlocked.Increment(ref wrongSize);
                }

                Interlocked.Increment(ref frames);
            }
            finally
            {
                lock (callbackGate)
                {
                    callbacksInFlight--;
                    if (!acceptingFrames &&
                        callbacksInFlight == 0)
                    {
                        Monitor.PulseAll(callbackGate);
                    }
                }
            }
        };

        session.StartCapture();
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        long stoppedAt;
        int frameCount;
        int wrongSizeCount;
        long capturedFirstFrameAt;
        long capturedLastFrameAt;
        long[] arrivals;
        long[] sources;
        long[] selectedSources;
        lock (callbackGate)
        {
            acceptingFrames = false;
            while (callbacksInFlight != 0)
            {
                Monitor.Wait(callbackGate);
            }

            stoppedAt = Stopwatch.GetTimestamp();
            frameCount = Volatile.Read(ref frames);
            wrongSizeCount = Volatile.Read(ref wrongSize);
            capturedFirstFrameAt = Volatile.Read(ref firstFrameAt);
            capturedLastFrameAt = Volatile.Read(ref lastFrameAt);
            lock (timestampGate)
            {
                arrivals = arrivalTimestamps.ToArray();
                sources = sourceTimestamps.ToArray();
                selectedSources = selectedSourceTimestamps.ToArray();
            }
        }
        animation.Stop();

        double elapsed =
            Stopwatch.GetElapsedTime(
                startedAt,
                stoppedAt).TotalSeconds;
        double fps = frameCount / elapsed;
        double firstMilliseconds =
            capturedFirstFrameAt == 0
                ? double.PositiveInfinity
                : Stopwatch.GetElapsedTime(
                    startedAt,
                    capturedFirstFrameAt).TotalMilliseconds;
        double trailingMilliseconds =
            capturedLastFrameAt == 0
                ? double.PositiveInfinity
                : Stopwatch.GetElapsedTime(
                    capturedLastFrameAt,
                    stoppedAt).TotalMilliseconds;
        double[] arrivalIntervals = GetIntervalsMilliseconds(
            arrivals,
            Stopwatch.Frequency);
        double[] sourceIntervals = GetIntervalsMilliseconds(
            sources,
            TimeSpan.TicksPerSecond);
        double[] selectedSourceIntervals =
            GetIntervalsMilliseconds(
                selectedSources,
                TimeSpan.TicksPerSecond);
        int distinctSourceTimestamps = sources
            .Distinct()
            .Count();

        Console.WriteLine(
            FormattableString.Invariant(
                $"WGC_RESULT device={target.DeviceName} adapter=\"{adapter.Description1.Description}\" size={item.Size.Width}x{item.Size.Height} requested_fps={requestedCaptureRate} min_interval_applied={captureIntervalApplied} selection={selectionMode} frames={frameCount} seconds={elapsed:F3} fps={fps:F2} selected_frames={selectedSources.Length} selected_fps={(selectedSources.Length / elapsed):F2} first_ms={firstMilliseconds:F2} tail_ms={trailingMilliseconds:F2} wrong_size={wrongSizeCount} feature={d3dDevice.FeatureLevel} source_time_distinct={distinctSourceTimestamps} arrival_p50_ms={Percentile(arrivalIntervals, 0.50):F2} arrival_p95_ms={Percentile(arrivalIntervals, 0.95):F2} arrival_p99_ms={Percentile(arrivalIntervals, 0.99):F2} source_p50_ms={Percentile(sourceIntervals, 0.50):F2} source_p95_ms={Percentile(sourceIntervals, 0.95):F2} source_p99_ms={Percentile(sourceIntervals, 0.99):F2} selected_p50_ms={Percentile(selectedSourceIntervals, 0.50):F2} selected_p95_ms={Percentile(selectedSourceIntervals, 0.95):F2} selected_p99_ms={Percentile(selectedSourceIntervals, 0.99):F2}"));
        Console.WriteLine(
            $"ANIMATION_TICKS={animation.TickCount}");
        return frameCount > 0 ? 0 : 3;
        }
    }

    private static bool TrySetMinUpdateInterval(
        GraphicsCaptureSession session,
        int framesPerSecond)
    {
        nint sessionPointer = Marshal.GetIUnknownForObject(session);
        try
        {
            Guid session5Guid =
                new("67C0EA62-1F85-5061-925A-239BE0AC09CB");
            int queryResult = Marshal.QueryInterface(
                sessionPointer,
                ref session5Guid,
                out nint session5Pointer);
            if (queryResult < 0 || session5Pointer == nint.Zero)
            {
                return false;
            }

            try
            {
                nint vtable = Marshal.ReadIntPtr(session5Pointer);
                nint putMethod = Marshal.ReadIntPtr(
                    vtable,
                    7 * IntPtr.Size);
                var putMinUpdateInterval =
                    Marshal.GetDelegateForFunctionPointer<
                        PutMinUpdateIntervalDelegate>(putMethod);
                long intervalTicks = checked(
                    (long)Math.Round(
                        TimeSpan.TicksPerSecond /
                        (double)framesPerSecond));
                return putMinUpdateInterval(
                    session5Pointer,
                    intervalTicks) >= 0;
            }
            finally
            {
                Marshal.Release(session5Pointer);
            }
        }
        finally
        {
            Marshal.Release(sessionPointer);
        }
    }

    private static double[] GetIntervalsMilliseconds(
        IReadOnlyList<long> timestamps,
        long ticksPerSecond)
    {
        if (timestamps.Count < 2)
        {
            return [];
        }

        var intervals = new double[timestamps.Count - 1];
        for (int index = 1; index < timestamps.Count; index++)
        {
            intervals[index - 1] =
                (timestamps[index] - timestamps[index - 1]) *
                1000d /
                ticksPerSecond;
        }

        Array.Sort(intervals);
        return intervals;
    }

    private static double Percentile(
        IReadOnlyList<double> sortedValues,
        double percentile)
    {
        if (sortedValues.Count == 0)
        {
            return double.NaN;
        }

        int index = Math.Clamp(
            (int)Math.Ceiling(percentile * sortedValues.Count) - 1,
            0,
            sortedValues.Count - 1);
        return sortedValues[index];
    }

    private static IDirect3DDevice CreateWinRtDevice(
        nint dxgiDevice)
    {
        int hresult =
            CreateDirect3D11DeviceFromDXGIDevice(
                dxgiDevice,
                out nint inspectable);
        Marshal.ThrowExceptionForHR(hresult);
        try
        {
            return MarshalInterface<IDirect3DDevice>
                .FromAbi(inspectable);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    private static GraphicsCaptureItem CreateItemForMonitor(
        nint monitor)
    {
        IGraphicsCaptureItemInterop interop =
            GraphicsCaptureItem
                .As<IGraphicsCaptureItemInterop>();
        nint itemPointer =
            interop.CreateForMonitor(
                monitor,
                GraphicsCaptureItemGuid);
        try
        {
            return GraphicsCaptureItem
                .FromAbi(itemPointer);
        }
        finally
        {
            Marshal.Release(itemPointer);
        }
    }

    private static MonitorTarget? FindMonitor(
        string? requestedDevice)
    {
        MonitorTarget? primary = null;
        MonitorTarget? selected = null;
        _ = EnumDisplayMonitors(
            nint.Zero,
            nint.Zero,
            (monitor, _, _, _) =>
            {
                var info = new MonitorInfoEx
                {
                    Size = Marshal.SizeOf<MonitorInfoEx>()
                };
                if (!GetMonitorInfo(monitor, ref info))
                {
                    return true;
                }

                var target = new MonitorTarget(
                    monitor,
                    info.DeviceName,
                    Rectangle.FromLTRB(
                        info.Monitor.Left,
                        info.Monitor.Top,
                        info.Monitor.Right,
                        info.Monitor.Bottom));
                if ((info.Flags & 1) != 0)
                {
                    primary = target;
                }

                if (!string.IsNullOrWhiteSpace(requestedDevice) &&
                    string.Equals(
                        requestedDevice,
                        info.DeviceName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    selected = target;
                }

                return true;
            },
            nint.Zero);
        return string.IsNullOrWhiteSpace(requestedDevice)
            ? primary
            : selected;
    }

    private static IDXGIAdapter1 FindAdapterForMonitor(
        nint monitor)
    {
        using IDXGIFactory1 factory =
            DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint adapterIndex = 0; ; adapterIndex++)
        {
            SharpGen.Runtime.Result adapterResult =
                factory.EnumAdapters1(
                    adapterIndex,
                    out IDXGIAdapter1 adapter);
            if (adapterResult.Failure)
            {
                break;
            }

            bool matched = false;
            try
            {
                for (uint outputIndex = 0; ; outputIndex++)
                {
                    SharpGen.Runtime.Result outputResult =
                        adapter.EnumOutputs(
                            outputIndex,
                            out IDXGIOutput output);
                    if (outputResult.Failure)
                    {
                        break;
                    }

                    using (output)
                    {
                        if (output.Description.Monitor == monitor)
                        {
                            matched = true;
                            return adapter;
                        }
                    }
                }
            }
            finally
            {
                if (!matched)
                {
                    adapter.Dispose();
                }
            }
        }

        throw new InvalidOperationException(
            "No DXGI adapter owns the selected monitor.");
    }

    [DllImport(
        "d3d11.dll",
        ExactSpelling = true)]
    private static extern int
        CreateDirect3D11DeviceFromDXGIDevice(
            nint dxgiDevice,
            out nint graphicsDevice);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(
        nint deviceContext,
        nint clipRectangle,
        MonitorEnumProc callback,
        nint data);

    [DllImport(
        "user32.dll",
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(
        nint monitor,
        ref MonitorInfoEx info);

    private delegate bool MonitorEnumProc(
        nint monitor,
        nint deviceContext,
        nint monitorRectangle,
        nint data);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PutMinUpdateIntervalDelegate(
        nint session,
        long intervalTicks);

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow(
            nint window,
            in Guid iid);

        nint CreateForMonitor(
            nint monitor,
            in Guid iid);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(
        LayoutKind.Sequential,
        CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public NativeRectangle Monitor;
        public NativeRectangle Work;
        public uint Flags;

        [MarshalAs(
            UnmanagedType.ByValTStr,
            SizeConst = 32)]
        public string DeviceName;
    }

    private sealed record MonitorTarget(
        nint Handle,
        string DeviceName,
        Rectangle Bounds);

    private sealed class AnimationWindow : IDisposable
    {
        private readonly Rectangle _bounds;
        private readonly int _intervalMilliseconds;
        private readonly ManualResetEventSlim _ready = new();
        private Thread? _thread;
        private Form? _form;
        private int _tickCount;
        private bool _timerResolutionRaised;

        public AnimationWindow(
            Rectangle bounds,
            int intervalMilliseconds)
        {
            _bounds = bounds;
            _intervalMilliseconds = intervalMilliseconds;
        }

        public void Start()
        {
            if (_intervalMilliseconds < 16 &&
                timeBeginPeriod(1) == 0)
            {
                _timerResolutionRaised = true;
            }

            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "WGC probe animation"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            if (!_ready.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException(
                    "Animation window did not start.");
            }
        }

        public int TickCount =>
            Volatile.Read(ref _tickCount);

        public Size Size { get; private set; }

        public void Stop()
        {
            Form? form = Volatile.Read(ref _form);
            if (form is not null &&
                form.IsHandleCreated)
            {
                form.BeginInvoke(form.Close);
            }

            _thread?.Join(
                TimeSpan.FromSeconds(3));
            if (_timerResolutionRaised)
            {
                _ = timeEndPeriod(1);
                _timerResolutionRaised = false;
            }
        }

        public void Dispose()
        {
            Stop();
            _ready.Dispose();
        }

        private void Run()
        {
            using var form = new Form
            {
                FormBorderStyle =
                    FormBorderStyle.FixedToolWindow,
                StartPosition =
                    FormStartPosition.Manual,
                Bounds = new Rectangle(
                    _bounds.Left + 20,
                    _bounds.Top + 20,
                    Math.Min(480, _bounds.Width / 2),
                    Math.Min(320, _bounds.Height / 2)),
                ShowInTaskbar = false,
                TopMost = true,
                Text = "RemoteDesk WGC probe"
            };
            Size = form.Size;
            int phase = 0;
            Action renderFrame = () =>
            {
                phase++;
                Interlocked.Increment(ref _tickCount);
                form.BackColor = Color.FromArgb(
                    255,
                    phase & 255,
                    (phase * 3) & 255,
                    (phase * 7) & 255);
                form.Text =
                    $"RemoteDesk WGC probe {phase}";
                form.Left =
                    _bounds.Left +
                    20 +
                    phase % 120;
                form.Refresh();
            };
            using var animationStopped =
                new ManualResetEventSlim();
            Thread? animationThread = null;
            form.Shown += (_, _) =>
            {
                Volatile.Write(ref _form, form);
                animationThread = new Thread(
                    () =>
                    {
                        while (!animationStopped.IsSet)
                        {
                            try
                            {
                                form.Invoke(renderFrame);
                            }
                            catch (InvalidOperationException)
                            {
                                break;
                            }

                            if (animationStopped.Wait(
                                    _intervalMilliseconds))
                            {
                                break;
                            }
                        }
                    })
                {
                    IsBackground = true,
                    Name = "WGC probe animation driver"
                };
                animationThread.Start();
                _ready.Set();
            };
            form.FormClosed += (_, _) =>
                animationStopped.Set();
            Application.Run(form);
            animationStopped.Set();
            animationThread?.Join(
                TimeSpan.FromSeconds(1));
            Volatile.Write(ref _form, null);
        }
    }
}
