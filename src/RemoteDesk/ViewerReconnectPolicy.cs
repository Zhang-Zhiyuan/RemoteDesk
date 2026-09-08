using System.Net.Sockets;

namespace RemoteDesk;

internal sealed class ViewerConnectionSnapshot
{
    public ViewerConnectionSnapshot(
        string host,
        int port,
        string password,
        ViewerVideoMode videoMode,
        RelayConnectionOptions? relayRoute = null)
    {
        Host = host;
        Port = port;
        Password = password;
        VideoMode = videoMode;
        RelayRoute = relayRoute;
    }

    public string Host { get; }

    public int Port { get; }

    public string Password { get; }

    public ViewerVideoMode VideoMode { get; }

    public RelayConnectionOptions? RelayRoute { get; }
}

internal static class ViewerReconnectPolicy
{
    internal static readonly TimeSpan StableConnectionWindow =
        TimeSpan.FromSeconds(5);
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(10)
    ];

    public static TimeSpan GetRetryDelay(int failedAttemptCount)
    {
        int index = Math.Clamp(
            failedAttemptCount,
            0,
            RetryDelays.Length - 1);
        return RetryDelays[index];
    }

    public static int RecordAttemptResult(
        int failedAttemptCount,
        bool stableConnectionWindowCompleted)
    {
        if (stableConnectionWindowCompleted)
        {
            return 0;
        }

        return failedAttemptCount == int.MaxValue
            ? int.MaxValue
            : Math.Max(0, failedAttemptCount) + 1;
    }

    public static bool ShouldCompleteStableConnectionWindow(
        long expectedIntentGeneration,
        long currentIntentGeneration,
        long expectedConnectionGeneration,
        long currentConnectionGeneration,
        bool currentConnected) =>
        currentConnected &&
        expectedIntentGeneration == currentIntentGeneration &&
        expectedConnectionGeneration ==
            currentConnectionGeneration;

    public static bool IsRetryableConnectionFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is InvalidDataException or
            RelayAccessDeniedException or
            RemoteSessionRejectedException)
        {
            return false;
        }

        return exception is TimeoutException or IOException or SocketException;
    }

    public static bool ShouldApplyConnectionEvent(
        bool eventConnected,
        bool currentConnected)
    {
        return eventConnected == currentConnected;
    }

    public static bool ShouldStartReconnect(
        bool connectedEvent,
        bool currentConnected,
        bool hasSuccessfulIntent,
        bool sessionRejected = false)
    {
        return !connectedEvent &&
            !currentConnected &&
            hasSuccessfulIntent &&
            !sessionRejected;
    }
}
