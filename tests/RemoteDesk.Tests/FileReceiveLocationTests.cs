using System.Net;
using System.Net.Sockets;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class FileReceiveLocationTests
{
    [Theory]
    [InlineData(@"C:\Users\测试\Downloads\", @"C:\Users\测试\Downloads\中文😀.txt")]
    [InlineData("/home/user/Downloads/", "/home/user/Downloads/中文😀.txt")]
    [InlineData(@"\\server\share", @"\\server\share\中文😀.txt")]
    [InlineData("/", "/中文😀.txt")]
    public void JoinsRemotePathsWithoutUsingLocalPlatform(string directory, string expected)
    {
        Assert.Equal(expected, FileReceiveLocation.Join(directory, "中文😀.txt"));
        Assert.Contains(expected, new FileReceiveLocation(directory, "", "fixture").FormatFile("中文😀.txt"));
    }

    [Fact]
    public void UnknownAndCurrentWindowAreExplicitlyUnconfirmed()
    {
        var location = new FileReceiveLocation("", "", "fixture");
        Assert.Contains("位置未确认", location.FormatFile("test"));
        Assert.Contains("无法确认", location.FormatFile("test", true));
        Assert.Contains("接收副本", location.FormatFile("test", true));
    }

    [Theory]
    [InlineData(true, "/home/测试😀/Downloads", "")]
    [InlineData(false, "", "接收目录不可用")]
    public void WireRoundTripsExactLocationIncludingFailure(bool success, string directory, string note)
    {
        // Same golden bytes are checked by Python and Java (UTF-8 byte length, not character count).
        Assert.Equal(Convert.FromHexString("2702696401082fe4b8adf09f988000"),
            RemoteMessageCodec.EncodeFileReceiveLocation("id", true, "/中😀", ""));
        var request = RemoteMessageCodec.DecodeControl(RemoteMessageCodec.EncodeFileReceiveLocationRequest("fixture"));
        Assert.Equal((RemoteControlKind)38, request.Kind);
        Assert.Equal("fixture", request.TransferId);
        byte[] bytes = RemoteMessageCodec.EncodeFileReceiveLocation("fixture", success, directory, note);
        var reply = RemoteMessageCodec.DecodeControl(bytes);
        Assert.Equal((RemoteControlKind)39, reply.Kind);
        Assert.Equal("fixture", reply.TransferId);
        Assert.Equal(success, reply.Success);
        Assert.Equal(directory, reply.Text);
        Assert.Equal(note, reply.StatusMessage);
        Assert.Throws<InvalidDataException>(() => RemoteMessageCodec.DecodeControl(bytes.Concat(new byte[] { 0 }).ToArray()));
    }

    [Fact]
    public void OverlongLocationIsRejectedNotTruncated()
    {
        Assert.Throws<InvalidDataException>(() => RemoteMessageCodec.EncodeFileReceiveLocation("id", true,
            new string('x', 8193), ""));
    }

    [Fact]
    public void ResultKeepsEveryActualPathAndPartialFailure()
    {
        var result = new RemoteFilePasteResult(2, 1, 0, 1, true, 1, "disk full",
            [new("one.txt", true, @"C:\接收\one (1).txt"), new("folder", true, "/tmp/folder.zip"), new("failed.txt", false, "disk full")]);
        string details = FileTransferResultDialog.FormatDetails(result, "fixture", true);
        Assert.Contains(@"C:\接收\one (1).txt", details);
        Assert.Contains("/tmp/folder.zip", details);
        Assert.Contains("未完成：failed.txt", details);
        Assert.Contains("disk full", details);
        Assert.Contains("不会自动解压", details);
        Assert.Contains("无法确认", details);
    }

    [Fact]
    public void ReceiverReportsItsConfiguredDirectoryNotStaticDefault()
    {
        string directory = Path.Combine(Path.GetTempPath(), "RemoteDesk-location-fixture", Guid.NewGuid().ToString("N"));
        using var receiver = new FileTransferReceiver(_ => { }, () => directory);
        Assert.Equal(Path.GetFullPath(directory), receiver.GetConfiguredReceiveDirectory());
        Assert.False(Directory.Exists(directory)); // Preflight has no filesystem writes.
    }

    [Theory]
    [InlineData("success")]
    [InlineData("legacy")]
    [InlineData("rejected")]
    [InlineData("disconnect")]
    [InlineData("timeout")]
    [InlineData("queued-send")]
    [InlineData("stale-generation")]
    public async Task PreflightUsesAuthenticatedMatchingCurrentConnectionOnly(string scenario)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var stop = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, stop.Token);
        int queries = 0;
        async Task Host()
        {
            try {
                using TcpClient peer = await listener.AcceptTcpClientAsync(linked.Token);
                await using var stream = peer.GetStream();
                using var gate = new SemaphoreSlim(1, 1);
                var authentication = await Protocol.AuthenticateServerDetailedAsync(stream, "fixture", linked.Token);
                using var session = authentication.Session!;
                Task Write(byte[] payload) => Protocol.WriteMessageAsync(stream, MessageType.Control, payload, session, gate, linked.Token);
                var capabilities = RemoteDeviceCapabilities.RemoteDesktop | RemoteDeviceCapabilities.FileReceive;
                if (scenario != "legacy") capabilities |= RemoteDeviceCapabilities.FileReceiveLocation;
                await Write(RemoteMessageCodec.EncodeDeviceInfo(new RemoteDeviceDescriptor("fixture", "Linux", capabilities)));
                while (!linked.IsCancellationRequested) {
                    var message = await Protocol.ReadMessageAsync(stream, session, linked.Token);
                    if (message.Type != MessageType.Control) continue;
                    var control = RemoteMessageCodec.DecodeControl(message.PayloadMemory);
                    if (control.Kind != RemoteControlKind.FileReceiveLocationRequest) continue;
                    Interlocked.Increment(ref queries);
                    if (scenario == "disconnect") return;
                    if (scenario == "timeout") continue;
                    await Write(RemoteMessageCodec.EncodeFileReceiveLocation("wrong-id", true, "/wrong", "wrong"));
                    await Write(RemoteMessageCodec.EncodeFileReceiveLocation(control.TransferId!, scenario != "rejected",
                        scenario == "rejected" ? "" : "/home/测试😀/Downloads", scenario == "rejected" ? "disk unavailable" : ""));
                }
            } catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
        }
        Task host = Host();
        using var client = new RemoteViewerClient();
        try {
            await client.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, "fixture", ViewerVideoMode.StableJpeg, timeout.Token);
            Assert.True(await client.WaitForCurrentDeviceInfoAsync(TimeSpan.FromSeconds(5), timeout.Token));
            long generation = client.InputConnectionGeneration;
            if (scenario == "stale-generation") generation--;
            if (scenario == "queued-send") {
                var gate = (SemaphoreSlim)typeof(RemoteViewerClient).GetField("_fileTransferLock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(client)!;
                await gate.WaitAsync(timeout.Token);
                var plan = new RemoteFilePastePlan(["fixture.txt"], 0, 0, false);
                Task<RemoteFilePasteResult> queued = client.SendFilePastePlanToRemoteAsync(plan);
                Assert.False(queued.IsCompleted);
                // Simulate the generation change while the operation waits for the batch slot.
                typeof(RemoteViewerClient).GetField("_inputConnectionGeneration", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(client, generation + 1);
                gate.Release();
                Assert.Contains("原文件确认已失效", (await Assert.ThrowsAsync<IOException>(() => queued)).Message);
                await Assert.ThrowsAsync<IOException>(() => client.SendFilePastePlanToRemoteDropPasteAsync(plan, generation));
                Assert.Equal(0, queries);
                return;
            }
            if (scenario is "rejected" or "disconnect" or "stale-generation" or "timeout")
                await Assert.ThrowsAsync<IOException>(() => client.GetRemoteFileReceiveLocationAsync(generation));
            else {
                var location = await client.GetRemoteFileReceiveLocationAsync(generation);
                Assert.Equal(scenario == "success", location.IsKnown);
                Assert.Equal("fixture", location.Device);
                if (location.IsKnown) Assert.Equal("/home/测试😀/Downloads", location.Directory);
                else Assert.Contains("位置未确认", location.Note);
            }
            Assert.Equal(scenario is "legacy" or "stale-generation" ? 0 : 1, queries);
        } finally {
            stop.Cancel();
            await client.DisconnectAsync();
            await host;
        }
    }
}
