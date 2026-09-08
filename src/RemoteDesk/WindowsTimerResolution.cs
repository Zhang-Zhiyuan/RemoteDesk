using System.Runtime.InteropServices;

namespace RemoteDesk;

internal static class WindowsTimerResolution
{
    internal const uint InteractivePeriodMilliseconds = 1;
    private const uint TimerNoError = 0;
    private const uint ProcessPowerThrottlingCurrentVersion = 1;
    private const uint ProcessPowerThrottlingExecutionSpeed = 0x1;
    private const uint ProcessPowerThrottlingIgnoreTimerResolution = 0x4;
    private const int ProcessPowerThrottling = 4;
    private static readonly IDisposable InactiveLease =
        new InactiveTimerResolutionLease();

    internal static IDisposable TryAcquireInteractive()
    {
        if (!OperatingSystem.IsWindows())
        {
            return InactiveLease;
        }

        // Windows 11 can otherwise ignore a timer-resolution request after a
        // window-owning process is minimized or hidden in the notification area.
        // RemoteDesk remains latency-sensitive in that state while it is hosting.
        TryDisableLatencyPowerThrottling();

        return TryAcquire(
            InteractivePeriodMilliseconds,
            timeBeginPeriod,
            timeEndPeriod);
    }

    internal static IDisposable TryAcquire(
        uint periodMilliseconds,
        Func<uint, uint> beginPeriod,
        Func<uint, uint> endPeriod)
    {
        if (periodMilliseconds == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(periodMilliseconds));
        }

        ArgumentNullException.ThrowIfNull(beginPeriod);
        ArgumentNullException.ThrowIfNull(endPeriod);

        try
        {
            if (beginPeriod(periodMilliseconds) != TimerNoError)
            {
                return InactiveLease;
            }
        }
        catch (Exception ex) when (
            ex is DllNotFoundException or
                EntryPointNotFoundException or
                BadImageFormatException)
        {
            return InactiveLease;
        }

        return new ActiveTimerResolutionLease(
            periodMilliseconds,
            endPeriod);
    }

    private static void TryDisableLatencyPowerThrottling()
    {
        var state = new ProcessPowerThrottlingState
        {
            Version = ProcessPowerThrottlingCurrentVersion,
            ControlMask =
                ProcessPowerThrottlingExecutionSpeed |
                ProcessPowerThrottlingIgnoreTimerResolution,
            StateMask = 0
        };

        try
        {
            _ = SetProcessInformation(
                GetCurrentProcess(),
                ProcessPowerThrottling,
                ref state,
                Marshal.SizeOf<ProcessPowerThrottlingState>());
        }
        catch (Exception ex) when (
            ex is DllNotFoundException or
                EntryPointNotFoundException or
                BadImageFormatException)
        {
            // This tuning is best-effort on older Windows versions. The
            // timeBeginPeriod request below remains independently useful.
        }
    }

    private sealed class ActiveTimerResolutionLease : IDisposable
    {
        private readonly uint _periodMilliseconds;
        private Func<uint, uint>? _endPeriod;

        internal ActiveTimerResolutionLease(
            uint periodMilliseconds,
            Func<uint, uint> endPeriod)
        {
            _periodMilliseconds = periodMilliseconds;
            _endPeriod = endPeriod;
        }

        public void Dispose()
        {
            Func<uint, uint>? endPeriod =
                Interlocked.Exchange(ref _endPeriod, null);
            if (endPeriod is null)
            {
                return;
            }

            try
            {
                _ = endPeriod(_periodMilliseconds);
            }
            catch (Exception ex) when (
                ex is DllNotFoundException or
                    EntryPointNotFoundException or
                    BadImageFormatException)
            {
            }
        }
    }

    private sealed class InactiveTimerResolutionLease : IDisposable
    {
        public void Dispose()
        {
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessPowerThrottlingState
    {
        internal uint Version;
        internal uint ControlMask;
        internal uint StateMask;
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint periodMilliseconds);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint periodMilliseconds);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(
        nint process,
        int processInformationClass,
        ref ProcessPowerThrottlingState processInformation,
        int processInformationSize);
}
