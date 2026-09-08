using System.Net.Sockets;

namespace RemoteDesk;

internal readonly record struct HostPortStartResult(
    int RequestedPort,
    int ListeningPort)
{
    public bool UsedAutomaticFallback =>
        RequestedPort != ListeningPort;
}

internal static class RemotePortPolicy
{
    // Keep 56565/56566 as the wire-compatible defaults. Windows may reserve
    // those ports after Hyper-V/WinNAT starts, so automatic recovery uses
    // stable user ports below the default dynamic range (49152+).
    public const int PreferredFallbackHostPort = 40565;
    public const int AlternateFallbackHostPort = 40567;
    public const int DefaultDiscoveryPort = 56566;
    public const int FallbackDiscoveryPort = 40566;

    private static readonly int[] AutomaticHostPorts =
    [
        Protocol.DefaultPort,
        PreferredFallbackHostPort,
        AlternateFallbackHostPort
    ];

    public static bool IsAccessDenied(SocketException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.SocketErrorCode == SocketError.AccessDenied ||
            exception.NativeErrorCode == 10013 ||
            exception.ErrorCode == 10013;
    }

    public static bool CanFallbackFromDiscoveryBind(
        SocketException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return IsAccessDenied(exception) ||
            exception.SocketErrorCode ==
                SocketError.AddressAlreadyInUse;
    }

    public static IReadOnlyList<int> GetCompatibleHostProbePorts(
        int requestedPort)
    {
        if (!AutomaticHostPorts.Contains(requestedPort))
        {
            return [requestedPort];
        }

        return
        [
            requestedPort,
            .. AutomaticHostPorts.Where(port => port != requestedPort)
        ];
    }

    public static IReadOnlyList<int> GetDiscoveryProbePorts(
        int requestedPort)
    {
        return requestedPort switch
        {
            DefaultDiscoveryPort =>
                [DefaultDiscoveryPort, FallbackDiscoveryPort],
            FallbackDiscoveryPort =>
                [FallbackDiscoveryPort, DefaultDiscoveryPort],
            _ => [requestedPort]
        };
    }

    public static bool AreCompatibleHostPorts(
        int firstPort,
        int secondPort)
    {
        return firstPort != secondPort &&
            AutomaticHostPorts.Contains(firstPort) &&
            AutomaticHostPorts.Contains(secondPort);
    }

    public static async Task<HostPortStartResult>
        StartHostWithAccessDeniedFallbackAsync(
            int requestedPort,
            Func<int, Task> startAsync)
    {
        ArgumentNullException.ThrowIfNull(startAsync);

        try
        {
            await startAsync(requestedPort).ConfigureAwait(true);
            return new HostPortStartResult(
                requestedPort,
                requestedPort);
        }
        catch (SocketException ex) when (IsAccessDenied(ex))
        {
            foreach (int fallbackPort in AutomaticHostPorts.Where(
                port =>
                    port < 49152 &&
                    port != requestedPort))
            {
                try
                {
                    await startAsync(fallbackPort)
                        .ConfigureAwait(true);
                    return new HostPortStartResult(
                        requestedPort,
                        fallbackPort);
                }
                catch (SocketException fallbackException) when (
                    IsAccessDenied(fallbackException) ||
                    fallbackException.SocketErrorCode ==
                        SocketError.AddressAlreadyInUse)
                {
                }
            }

            throw new InvalidOperationException(
                $"Windows 拒绝监听端口 {requestedPort}，且自动兼容端口 " +
                $"{PreferredFallbackHostPort}/{AlternateFallbackHostPort} " +
                "均不可用。请检查系统 TCP 排除范围或端口占用。",
                ex);
        }
    }
}
