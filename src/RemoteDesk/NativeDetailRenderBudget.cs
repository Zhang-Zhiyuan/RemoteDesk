using System.Diagnostics;

namespace RemoteDesk;

// Local renderer admission only; never trust a remote clock or grant. A caller
// must know that input/base presentation has spare budget. Default means NO
// enhancement. This prevents added waits, not a claim of zero GPU contention.
internal readonly record struct NativeDetailRenderBudget(
    long DeadlineTimestamp,
    bool InputPending = false,
    bool BaseFrameBacklogged = false,
    bool TransportCongested = false)
{
    internal const double MinimumHeadroomMilliseconds = 2;
    internal bool Allows(long nowTimestamp) => DeadlineTimestamp > 0 && nowTimestamp >= 0 &&
        !InputPending && !BaseFrameBacklogged && !TransportCongested &&
        DeadlineTimestamp > nowTimestamp &&
        (DeadlineTimestamp - nowTimestamp) / (double)Stopwatch.Frequency * 1000 >= MinimumHeadroomMilliseconds;
}
