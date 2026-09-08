using System.Runtime;

namespace RemoteDesk;

internal static class ManagedLatencyMode
{
    private static readonly object Sync = new();
    private static readonly IDisposable InactiveLease =
        new InactiveManagedLatencyLease();
    private static GCLatencyMode s_previousMode;
    private static int s_activeLeases;
    private static bool s_modeChanged;

    internal static IDisposable TryAcquireSustainedLowLatency()
    {
        lock (Sync)
        {
            if (s_activeLeases == 0)
            {
                try
                {
                    s_previousMode = GCSettings.LatencyMode;
                    GCSettings.LatencyMode =
                        GCLatencyMode.SustainedLowLatency;
                    s_modeChanged =
                        GCSettings.LatencyMode ==
                        GCLatencyMode.SustainedLowLatency;
                }
                catch (InvalidOperationException)
                {
                    s_modeChanged = false;
                }
            }

            if (!s_modeChanged)
            {
                return InactiveLease;
            }

            s_activeLeases++;
            return new ActiveManagedLatencyLease();
        }
    }

    private sealed class ActiveManagedLatencyLease : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            lock (Sync)
            {
                if (s_activeLeases <= 0 ||
                    --s_activeLeases != 0)
                {
                    return;
                }

                try
                {
                    GCSettings.LatencyMode =
                        s_previousMode;
                }
                catch (InvalidOperationException)
                {
                }
                finally
                {
                    s_modeChanged = false;
                }
            }
        }
    }

    private sealed class InactiveManagedLatencyLease : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
