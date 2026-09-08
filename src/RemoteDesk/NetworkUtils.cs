using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Buffers.Binary;

namespace RemoteDesk;

internal static class NetworkUtils
{
    private const int TcpKeepAliveTimeMilliseconds = 12_000;
    private const int TcpKeepAliveIntervalMilliseconds = 3_000;

    public static IReadOnlyList<string> GetLocalIPv4Addresses()
    {
        NetworkInterface[] adapters;
        try
        {
            adapters = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (Exception ex) when (ex is NetworkInformationException or SocketException or InvalidOperationException or ObjectDisposedException)
        {
            return Array.Empty<string>();
        }

        var addresses = new List<string>();
        foreach (NetworkInterface adapter in adapters)
        {
            try
            {
                if (adapter.OperationalStatus != OperationalStatus.Up ||
                    adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                addresses.AddRange(adapter.GetIPProperties()
                    .UnicastAddresses
                    .Where(address =>
                        address.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(address.Address))
                    .Select(address => address.Address.ToString()));
            }
            catch (Exception ex) when (ex is NetworkInformationException or SocketException or InvalidOperationException or ObjectDisposedException)
            {
            }
        }

        return addresses
            .Distinct()
            .OrderBy(address => address)
            .ToArray();
    }

    public static bool IsLikelyLocalEndpoint(
        string? address,
        string? machineName,
        IReadOnlySet<string> localAddresses)
    {
        if (!string.IsNullOrWhiteSpace(address))
        {
            string trimmedAddress = address.Trim();
            if (string.Equals(trimmedAddress, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (localAddresses.Contains(trimmedAddress))
            {
                return true;
            }

            if (IPAddress.TryParse(trimmedAddress, out IPAddress? parsedAddress) &&
                IPAddress.IsLoopback(parsedAddress))
            {
                return true;
            }
        }

        return !string.IsNullOrWhiteSpace(machineName) &&
            string.Equals(machineName.Trim(), Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    }

    public static void ConfigureLowLatencyTcpClient(
        TcpClient client,
        int receiveBufferSize,
        int sendBufferSize)
    {
        try
        {
            client.NoDelay = true;
        }
        catch (Exception ex) when (IsSocketConfigurationException(ex))
        {
        }

        TrySetBufferSize(client, receiveBufferSize, isReceiveBuffer: true);
        TrySetBufferSize(client, sendBufferSize, isReceiveBuffer: false);
        TryEnableKeepAlive(client);
    }

    private static void TrySetBufferSize(TcpClient client, int size, bool isReceiveBuffer)
    {
        if (size <= 0)
        {
            return;
        }

        try
        {
            if (isReceiveBuffer)
            {
                client.ReceiveBufferSize = size;
            }
            else
            {
                client.SendBufferSize = size;
            }
        }
        catch (Exception ex) when (IsSocketConfigurationException(ex))
        {
        }
    }

    private static void TryEnableKeepAlive(TcpClient client)
    {
        try
        {
            Socket socket = client.Client;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            TryConfigureWindowsKeepAlive(socket);
        }
        catch (Exception ex) when (IsSocketConfigurationException(ex))
        {
        }
    }

    private static void TryConfigureWindowsKeepAlive(Socket socket)
    {
        try
        {
            Span<byte> keepAliveValues = stackalloc byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(keepAliveValues[0..4], 1);
            BinaryPrimitives.WriteUInt32LittleEndian(keepAliveValues[4..8], TcpKeepAliveTimeMilliseconds);
            BinaryPrimitives.WriteUInt32LittleEndian(keepAliveValues[8..12], TcpKeepAliveIntervalMilliseconds);
            socket.IOControl(IOControlCode.KeepAliveValues, keepAliveValues.ToArray(), null);
        }
        catch (Exception ex) when (IsSocketConfigurationException(ex))
        {
        }
    }

    private static bool IsSocketConfigurationException(Exception ex)
    {
        return ex is SocketException or ObjectDisposedException or InvalidOperationException or
            PlatformNotSupportedException or NotSupportedException or NullReferenceException;
    }
}
