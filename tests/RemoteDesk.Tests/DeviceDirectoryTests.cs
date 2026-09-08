using System.Net;
using System.Net.Sockets;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class DeviceDirectoryTests
{
    private const string MachineId = "00112233-4455-6677-8899-aabbccddeeff";

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("RemoteDesk.DeviceDirectory.Tests.").FullName;
        public static TemporaryDirectory Create() => new();
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    [Theory]
    [InlineData("pc-a.", "", "pc-a", 56565, false)]
    [InlineData("10.0.0.8:45678", "", "10.0.0.8", 45678, true)]
    [InlineData("[::1]:45678", "56565", "::1", 45678, true)]
    [InlineData("::1", "", "::1", 56565, false)]
    public void ParseEndpoint(string input, string port, string host, int expectedPort, bool explicitPort) =>
        Assert.Equal((host, expectedPort, explicitPort), MainForm.ParseDeviceEndpoint(input, port));

    [Theory]
    [InlineData(".", "")]
    [InlineData("http://pc", "")]
    [InlineData("pc", "65536")]
    [InlineData("pc:", "")]
    [InlineData("[::1]junk", "")]
    public void InvalidEndpointIsNotSaved(string host, string port) =>
        Assert.Throws<ArgumentException>(() => MainForm.ParseDeviceEndpoint(host, port));

    [Fact]
    public void AuthenticatedIdentityMergesChangedAddressesAndPreservesRemark()
    {
        var rows = new List<SavedRemoteDevice>
        {
            new() { Address = "10.0.0.8", Port = 56565, DeviceId = MachineId },
            new() { Address = "10.0.0.9", Port = 45678, DeviceId = MachineId, Remark = "工作机" },
            new() { Address = "10.0.0.10", Port = 56565, DeviceId = Guid.NewGuid().ToString(), MachineName = "PC" }
        };
        MainForm.UpsertRecentDevice(rows, new SavedRemoteDevice
        {
            Address = "10.0.0.11", Port = 45679, DeviceId = MachineId.ToUpperInvariant(),
            MachineName = "PC", ProtectedPassword = AppSettingsService.ProtectSecret("test-new-password")
        });
        Assert.Equal(2, rows.Count);
        Assert.Equal("工作机", rows[0].Remark);
        Assert.Equal("10.0.0.11", rows[0].Address);
        Assert.Equal(MachineId, rows[0].DeviceId);
        Assert.Equal("test-new-password", AppSettingsService.UnprotectSecret(rows[0].ProtectedPassword));
    }

    [Fact]
    public void SameNameOrKnownDifferentIdentitiesDoNotMerge()
    {
        var first = new SavedRemoteDevice { Address = "pc", Port = 56565, MachineName = "PC", DeviceId = MachineId };
        var second = new SavedRemoteDevice { Address = "pc", Port = 40565, MachineName = "PC", DeviceId = Guid.NewGuid().ToString() };
        Assert.False(MainForm.SameSavedMachine(first, second));
        second.Address = "another-pc"; second.DeviceId = null;
        Assert.False(MainForm.SameSavedMachine(first, second));
    }

    [Fact]
    public void SettingsRestartMergesIdentityAliasesAndPreservesEncryptedCredentials()
    {
        using var temp = TemporaryDirectory.Create();
        var service = new AppSettingsService(Path.Combine(temp.Path, "settings.json"));
        var settings = new RemoteDeskSettings();
        settings.Viewer.RecentDevices =
        [
            new() { Address = "old", Port = 40565, DeviceId = MachineId, Remark = "我的电脑", LastConnectedAt = DateTimeOffset.Now.AddDays(-1) },
            new() { Address = "new", Port = 45678, DeviceId = MachineId, ProtectedPassword = AppSettingsService.ProtectSecret("test-secret"), AutoDetectPort = false, LastConnectedAt = DateTimeOffset.Now }
        ];
        Assert.True(service.Save(settings).Success);
        SavedRemoteDevice saved = Assert.Single(service.Load().Viewer.RecentDevices);
        Assert.Equal("new", saved.Address); Assert.Equal("我的电脑", saved.Remark);
        Assert.False(saved.AutoDetectPort);
        Assert.Equal("test-secret", AppSettingsService.UnprotectSecret(saved.ProtectedPassword));
        Assert.DoesNotContain("test-secret", File.ReadAllText(Path.Combine(temp.Path, "settings.json")));
    }

    [Fact]
    public void IdentityProtocolIsStrictAndDeviceInfoRemainsWireCompatible()
    {
        byte[] encoded = RemoteMessageCodec.EncodeDeviceIdentity(MachineId.ToUpperInvariant());
        Assert.Equal(34, encoded[0]); Assert.Equal(36, encoded[1]);
        Assert.Equal(MachineId, RemoteMessageCodec.DecodeControl(encoded).Text);
        Assert.Equal(RemoteControlKind.DeviceIdentityRequest, RemoteMessageCodec.DecodeControl(new byte[] { 33 }).Kind);
        Assert.Throws<InvalidDataException>(() => RemoteMessageCodec.DecodeControl(new byte[] { 33, 0 }));
        Assert.Throws<InvalidDataException>(() => RemoteMessageCodec.DecodeControl(encoded.Concat(new byte[] { 0 }).ToArray()));
        Assert.Throws<ArgumentException>(() => RemoteMessageCodec.EncodeDeviceIdentity(Guid.Empty.ToString()));
        var oldInfo = new RemoteDeviceDescriptor("PC", "Android", RemoteDeviceCapabilities.RemoteDesktop);
        Assert.Equal(RemoteMessageCodec.EncodeDeviceInfo(oldInfo), RemoteMessageCodec.EncodeDeviceInfo(oldInfo with { DeviceId = MachineId }));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LoopbackRequestsIdentityOnlyWhenHostAdvertisesSupport(bool supported)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gotInfo = new TaskCompletionSource<RemoteDeviceDescriptor>(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        async Task Host()
        {
            using TcpClient peer = await listener.AcceptTcpClientAsync(timeout.Token);
            await using NetworkStream stream = peer.GetStream(); using var gate = new SemaphoreSlim(1, 1);
            var auth = await Protocol.AuthenticateServerDetailedAsync(stream, "test-password", timeout.Token);
            Assert.True(auth.IsAuthenticated); using SecureSession session = auth.Session!;
            await Protocol.WriteMessageAsync(stream, MessageType.Control,
                RemoteMessageCodec.EncodeDeviceInfo(new("PC", "Linux", supported ? RemoteDeviceCapabilities.DeviceIdentity : RemoteDeviceCapabilities.None)),
                session, gate, timeout.Token);
            while (!release.Task.IsCompleted)
            {
                Task<ProtocolMessage> read = Protocol.ReadMessageAsync(stream, session, timeout.Token);
                if (await Task.WhenAny(read, release.Task) != read) break;
                ProtocolMessage message = await read;
                if (message.Type != MessageType.Control) continue;
                if (RemoteMessageCodec.DecodeControl(message.PayloadMemory).Kind == RemoteControlKind.DeviceIdentityRequest)
                {
                    Interlocked.Increment(ref requests);
                    await Protocol.WriteMessageAsync(stream, MessageType.Control, RemoteMessageCodec.EncodeDeviceIdentity(MachineId), session, gate, timeout.Token);
                }
            }
        }
        Task host = Host(); using var client = new RemoteViewerClient();
        client.DeviceInfoReceived += info => { if (!supported || info.DeviceId is not null) gotInfo.TrySetResult(info); };
        try
        {
            await client.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, "test-password", ViewerVideoMode.StableJpeg, timeout.Token);
            var info = await gotInfo.Task.WaitAsync(timeout.Token);
            await Task.Delay(150, timeout.Token);
            Assert.Equal(supported ? MachineId : null, info.DeviceId);
            Assert.Equal(supported ? 1 : 0, Volatile.Read(ref requests));
        }
        finally
        {
            release.TrySetResult(); await host.WaitAsync(timeout.Token); await client.DisconnectAsync();
        }
    }
}
