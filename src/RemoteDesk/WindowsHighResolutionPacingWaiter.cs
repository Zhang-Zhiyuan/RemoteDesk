using System.Runtime.InteropServices;

namespace RemoteDesk;

internal interface IWindowsHighResolutionWaitableTimerApi
{
    bool IsAvailable { get; }

    bool TryCreate(out nint timerHandle);

    bool TrySetRelative(nint timerHandle, long dueTime100Nanoseconds);

    uint Wait(nint timerHandle);

    bool TryClose(nint timerHandle);
}

/// <summary>
/// Provides short, best-effort high-resolution waits for UDP packet pacing.
/// Native timer failures deliberately fall back to Thread.Sleep so that a
/// platform timing optimization can never terminate the video transport.
/// </summary>
internal sealed class WindowsHighResolutionPacingWaiter : IDisposable
{
    internal static readonly TimeSpan MaximumWaitSlice =
        TimeSpan.FromMilliseconds(20);

    internal const uint WaitObject0 = 0;

    private static readonly IWindowsHighResolutionWaitableTimerApi NativeApi =
        new WindowsHighResolutionWaitableTimerApi();

    private readonly object _sync = new();
    private readonly IWindowsHighResolutionWaitableTimerApi _api;
    private readonly Action<int> _sleepMilliseconds;
    private nint _timerHandle;
    private bool _disposed;

    internal WindowsHighResolutionPacingWaiter()
        : this(NativeApi, Thread.Sleep)
    {
    }

    internal WindowsHighResolutionPacingWaiter(
        IWindowsHighResolutionWaitableTimerApi api,
        Action<int> sleepMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(sleepMilliseconds);

        _api = api;
        _sleepMilliseconds = sleepMilliseconds;
        _timerHandle = TryCreateTimer(api);
    }

    internal void Wait(
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        TimeSpan remaining = duration;
        while (remaining > TimeSpan.Zero)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan slice = remaining > MaximumWaitSlice
                ? MaximumWaitSlice
                : remaining;
            WaitOnce(slice, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            remaining -= slice;
        }
    }

    internal static long ToRelativeDueTime100Nanoseconds(
        TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        // One TimeSpan tick and one Windows waitable-timer unit are both
        // 100 ns. A negative due time denotes an interval relative to now.
        return -duration.Ticks;
    }

    internal static int ToFallbackSleepMilliseconds(
        TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return 0;
        }

        double roundedMilliseconds =
            Math.Ceiling(duration.TotalMilliseconds);
        return roundedMilliseconds >= int.MaxValue
            ? int.MaxValue
            : (int)roundedMilliseconds;
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
            DisableNativeTimer();
        }
    }

    private void WaitOnce(
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        bool completedWithNativeTimer = false;
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_disposed && _timerHandle != 0)
            {
                try
                {
                    long dueTime =
                        ToRelativeDueTime100Nanoseconds(duration);
                    if (_api.TrySetRelative(_timerHandle, dueTime) &&
                        _api.Wait(_timerHandle) == WaitObject0)
                    {
                        completedWithNativeTimer = true;
                    }
                    else
                    {
                        DisableNativeTimer();
                    }
                }
                catch (Exception)
                {
                    DisableNativeTimer();
                }
            }
        }

        if (completedWithNativeTimer)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        _sleepMilliseconds(
            ToFallbackSleepMilliseconds(duration));
    }

    private static nint TryCreateTimer(
        IWindowsHighResolutionWaitableTimerApi api)
    {
        try
        {
            if (api.IsAvailable &&
                api.TryCreate(out nint timerHandle) &&
                timerHandle != 0 &&
                timerHandle != new nint(-1))
            {
                return timerHandle;
            }
        }
        catch (Exception)
        {
        }

        return 0;
    }

    private void DisableNativeTimer()
    {
        nint timerHandle = _timerHandle;
        _timerHandle = 0;
        if (timerHandle == 0 ||
            timerHandle == new nint(-1))
        {
            return;
        }

        try
        {
            _api.TryClose(timerHandle);
        }
        catch (Exception)
        {
        }
    }

    private sealed class WindowsHighResolutionWaitableTimerApi :
        IWindowsHighResolutionWaitableTimerApi
    {
        private const uint CreateWaitableTimerHighResolution = 0x00000002;
        private const uint TimerModifyState = 0x00000002;
        private const uint Synchronize = 0x00100000;
        private const uint Infinite = 0xffffffff;

        public bool IsAvailable => OperatingSystem.IsWindows();

        public bool TryCreate(out nint timerHandle)
        {
            timerHandle = CreateWaitableTimerExW(
                timerAttributes: 0,
                timerName: null,
                CreateWaitableTimerHighResolution,
                TimerModifyState | Synchronize);
            return timerHandle != 0 &&
                timerHandle != new nint(-1);
        }

        public bool TrySetRelative(
            nint timerHandle,
            long dueTime100Nanoseconds) =>
            SetWaitableTimerEx(
                timerHandle,
                ref dueTime100Nanoseconds,
                periodMilliseconds: 0,
                completionRoutine: 0,
                completionArgument: 0,
                wakeContext: 0,
                tolerableDelayMilliseconds: 0);

        public uint Wait(nint timerHandle) =>
            WaitForSingleObject(timerHandle, Infinite);

        public bool TryClose(nint timerHandle) =>
            CloseHandle(timerHandle);

        [DllImport(
            "kernel32.dll",
            EntryPoint = "CreateWaitableTimerExW",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern nint CreateWaitableTimerExW(
            nint timerAttributes,
            string? timerName,
            uint flags,
            uint desiredAccess);

        [DllImport(
            "kernel32.dll",
            EntryPoint = "SetWaitableTimerEx",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWaitableTimerEx(
            nint timerHandle,
            ref long dueTime,
            int periodMilliseconds,
            nint completionRoutine,
            nint completionArgument,
            nint wakeContext,
            uint tolerableDelayMilliseconds);

        [DllImport(
            "kernel32.dll",
            EntryPoint = "WaitForSingleObject",
            SetLastError = true)]
        private static extern uint WaitForSingleObject(
            nint handle,
            uint milliseconds);

        [DllImport(
            "kernel32.dll",
            EntryPoint = "CloseHandle",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(nint handle);
    }
}
