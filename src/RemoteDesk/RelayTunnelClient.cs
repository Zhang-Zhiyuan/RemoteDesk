using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json;

namespace RemoteDesk;

internal static class RelayTunnelClient
{
    private static readonly ConcurrentDictionary<Guid, Task>
        ActiveBridges = new();

    public static async Task ConnectViewerIntoAsync(
        TcpClient viewerClient,
        RelayConnectionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewerClient);
        options = options.Validate();
        (TcpClient relayClient, SslStream relayStream) =
            await RelayTls.ConnectAsync(options, cancellationToken)
                .ConfigureAwait(false);
        TcpListener? loopbackListener = null;
        TcpClient? bridgeClient = null;
        try
        {
            await RelayTls.WriteJsonAsync(
                    relayStream,
                    new
                    {
                        version = 1,
                        role = "viewer",
                        token = options.AccessToken,
                        deviceId = options.DeviceId
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            using JsonDocument response = await RelayTls.ReadJsonAsync(
                    relayStream,
                    cancellationToken)
                .ConfigureAwait(false);
            RelayTls.EnsureSuccess(response.RootElement);

            loopbackListener = new TcpListener(IPAddress.Loopback, 0);
            loopbackListener.Start(1);
            int port = ((IPEndPoint)loopbackListener.LocalEndpoint).Port;
            Task<TcpClient> acceptTask =
                loopbackListener.AcceptTcpClientAsync(cancellationToken)
                    .AsTask();
            await viewerClient.ConnectAsync(
                    IPAddress.Loopback,
                    port,
                    cancellationToken)
                .ConfigureAwait(false);
            bridgeClient = await acceptTask.ConfigureAwait(false);
            NetworkUtils.ConfigureLowLatencyTcpClient(
                bridgeClient,
                RemoteViewerClient.FrameReceiveBufferBytes,
                32 * 1024);

            Guid bridgeId = Guid.NewGuid();
            Task bridge = RelayStreamBridge.RunAsync(
                bridgeClient.GetStream(),
                relayStream,
                bridgeClient,
                relayClient,
                CancellationToken.None);
            ActiveBridges[bridgeId] = bridge;
            _ = bridge.ContinueWith(
                static (completed, state) =>
                {
                    var id = (Guid)state!;
                    ActiveBridges.TryRemove(id, out _);
                    if (completed.IsFaulted)
                    {
                        _ = completed.Exception;
                    }
                },
                bridgeId,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            bridgeClient = null;
            relayClient = null!;
        }
        catch
        {
            bridgeClient?.Dispose();
            relayStream.Dispose();
            relayClient?.Dispose();
            throw;
        }
        finally
        {
            loopbackListener?.Stop();
        }
    }

    public static async Task<IReadOnlyList<RelayOnlineDevice>>
        ListDevicesAsync(
            RelayConnectionOptions options,
            CancellationToken cancellationToken,
            TimeSpan? requestTimeout = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(requestTimeout ?? TimeSpan.FromSeconds(12));
        try
        {
            return await ListDevicesCoreAsync(options, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("读取在线设备超时，请检查中继端口和云安全组。");
        }
    }

    private static async Task<IReadOnlyList<RelayOnlineDevice>>
        ListDevicesCoreAsync(
            RelayConnectionOptions options,
            CancellationToken cancellationToken)
    {
        options = options.Validate();
        (TcpClient relayClient, SslStream relayStream) =
            await RelayTls.ConnectAsync(options, cancellationToken)
                .ConfigureAwait(false);
        using (relayClient)
        using (relayStream)
        {
            await RelayTls.WriteJsonAsync(
                    relayStream,
                    new
                    {
                        version = 1,
                        role = "directory",
                        token = options.AccessToken
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            using JsonDocument response = await RelayTls.ReadJsonAsync(
                    relayStream,
                    cancellationToken)
                .ConfigureAwait(false);
            JsonElement root = response.RootElement;
            RelayTls.EnsureSuccess(root);
            if (!root.TryGetProperty(
                    "devices",
                    out JsonElement devices) ||
                devices.ValueKind != JsonValueKind.Array)
            {
                throw new RelayProtocolException("中继返回的在线设备列表无效。");
            }

            var result = new List<RelayOnlineDevice>();
            foreach (JsonElement device in devices.EnumerateArray())
            {
                if (device.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                string? deviceId = GetString(device, "deviceId");
                if (!Guid.TryParse(deviceId, out Guid parsedDeviceId))
                {
                    continue;
                }

                result.Add(new RelayOnlineDevice(
                    parsedDeviceId.ToString("D"),
                    GetString(device, "machineName") ?? "未命名设备",
                    GetString(device, "platform") ?? "未知",
                    GetString(device, "buildStamp"),
                    GetBoolean(device, "busy"),
                    Math.Clamp(GetInteger(device, "lastSeenSeconds"), 0, 3600)));
            }

            return result;
        }
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool GetBoolean(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) &&
        value.ValueKind == JsonValueKind.True;

    private static int GetInteger(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out int parsed)
            ? parsed
            : 0;
}
