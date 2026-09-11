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
        CancellationToken cancellationToken,
        Action<string>? diagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(viewerClient);
        options = options.Validate();
        (TcpClient relayClient, SslStream relayStream) =
            await RelayTls.ConnectAsync(options, cancellationToken, dataTunnel: true)
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

            try
            {
                diagnostic?.Invoke("中继本地端点：" +
                    RelayNetworkPathSelector.DescribeLocalEndpoint(relayClient.Client.LocalEndPoint));
            }
            catch { /* Optional diagnostics must not prevent a tunnel from connecting. */ }

            loopbackListener = new TcpListener(IPAddress.Loopback, 0);
            loopbackListener.Server.ReceiveBufferSize = RelayLoopbackPolicy.BufferBytes;
            loopbackListener.Server.SendBufferSize = RelayLoopbackPolicy.BufferBytes;
            loopbackListener.Start(1);
            int port = ((IPEndPoint)loopbackListener.LocalEndpoint).Port;
            Task<TcpClient> acceptTask =
                loopbackListener.AcceptTcpClientAsync(cancellationToken)
                    .AsTask();
            RelayLoopbackPolicy.Configure(viewerClient);
            await viewerClient.ConnectAsync(
                    IPAddress.Loopback,
                    port,
                    cancellationToken)
                .ConfigureAwait(false);
            bridgeClient = await acceptTask.ConfigureAwait(false);
            RelayLoopbackPolicy.Configure(bridgeClient);

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
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw RelayTls.CreateConnectionDeadlineError(error, "读取在线设备超时，请检查中继端口和云安全组。");
        }
    }

    private static async Task<IReadOnlyList<RelayOnlineDevice>>
        ListDevicesCoreAsync(
            RelayConnectionOptions options,
            CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, RelayOnlineDevice>(StringComparer.Ordinal);
        int offset = 0;
        for (int page = 0; page < 16; page++)
        {
            var response = await ListDevicesPageAsync(options, offset, cancellationToken).ConfigureAwait(false);
            foreach (var device in response.Devices) result[device.DeviceId] = device;
            if (result.Count > 512) throw new RelayProtocolException("中继在线设备过多。");
            if (response.NextOffset is null) return result.Values.ToArray();
            if (response.NextOffset != offset + 32 || response.NextOffset >= 512)
                throw new RelayProtocolException("中继目录分页无效。");
            offset = response.NextOffset.Value;
        }
        throw new RelayProtocolException("中继目录分页过多。");
    }

    public static async Task RenameDeviceAsync(RelayConnectionOptions options, string name,
        CancellationToken cancellationToken, TimeSpan? requestTimeout = null)
    {
        options = options.Validate();
        name = RelayDeviceName.Normalize(name);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(requestTimeout ?? TimeSpan.FromSeconds(12));
        try
        {
            (TcpClient client, SslStream stream) = await RelayTls.ConnectAsync(options, timeout.Token).ConfigureAwait(false);
            using (client)
            using (stream)
            {
                await RelayTls.WriteJsonAsync(stream, new { version = 1, role = "rename-device",
                    token = options.AccessToken, deviceId = options.DeviceId, name }, timeout.Token).ConfigureAwait(false);
                using JsonDocument response = await RelayTls.ReadJsonAsync(stream, timeout.Token).ConfigureAwait(false);
                if (!GetBoolean(response.RootElement, "ok"))
                {
                    if ((GetString(response.RootElement, "error") ?? "").Contains("访问密钥"))
                        RelayTls.EnsureSuccess(response.RootElement);
                    throw new RelayProtocolException(RelayDeviceName.ErrorMessage(GetString(response.RootElement, "error") ?? ""));
                }
                if (GetString(response.RootElement, "deviceId") != options.DeviceId ||
                    GetString(response.RootElement, "sharedName") != name)
                    throw new RelayProtocolException("中继未确认此设备的名称，请刷新在线列表核对后重试。");
            }
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw RelayTls.CreateConnectionDeadlineError(error, "保存名称超时，请刷新在线列表核对后重试。");
        }
    }

    private static async Task<(IReadOnlyList<RelayOnlineDevice> Devices, int? NextOffset)> ListDevicesPageAsync(
        RelayConnectionOptions options, int offset, CancellationToken cancellationToken)
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
                        token = options.AccessToken,
                        pageSize = 32,
                        offset
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
                devices.ValueKind != JsonValueKind.Array || devices.GetArrayLength() > 512)
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

                var report = RelayAddressReport.Parse(device);
                result.Add(new RelayOnlineDevice(
                    parsedDeviceId.ToString("D"),
                    GetString(device, "machineName") ?? "未命名设备",
                    GetString(device, "platform") ?? "未知",
                    GetString(device, "buildStamp"),
                    GetBoolean(device, "busy"),
                    Math.Clamp(GetInteger(device, "lastSeenSeconds"), 0, 3600))
                {
                    DirectAddresses = report.Addresses, DirectPort = report.Port,
                    SharedName = GetString(device, "sharedName") ?? "",
                    OriginalMachineName = GetString(device, "originalMachineName") ?? GetString(device, "machineName") ?? "",
                    CanRename = GetBoolean(root, "deviceNaming"),
                    NamingUnavailableReason = string.IsNullOrEmpty(GetString(root, "deviceNamingError"))
                        ? RelayDeviceName.UnsupportedMessage : RelayDeviceName.ErrorMessage(GetString(root, "deviceNamingError")!)
                });
            }

            int? next = null;
            if (root.TryGetProperty("nextOffset", out var nextValue) && nextValue.ValueKind != JsonValueKind.Null)
            {
                if (nextValue.ValueKind != JsonValueKind.Number || !nextValue.TryGetInt32(out int number))
                    throw new RelayProtocolException("中继目录分页无效。");
                next = number;
            }
            return (result, next);
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
