using System.Net;
using System.Net.Sockets;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class FileTransferReceiptTests
{
    [Theory]
    [InlineData("saved")]
    [InlineData("rejected")]
    [InlineData("timeout")]
    [InlineData("disconnect")]
    [InlineData("legacy")]
    [InlineData("account-package")]
    [InlineData("legacy-package")]
    public async Task UploadCompletesOnlyAfterMatchingReceipt(string outcome)
    {
        string directory = Directory.CreateTempSubdirectory("RemoteDesk-receipt-").FullName;
        string path = Path.Combine(directory, "中文😀.txt");
        await File.WriteAllTextAsync(path, "测试\r\n");
        bool sendingPackage = outcome is "account-package" or "legacy-package";
        bool legacy = outcome is "legacy" or "legacy-package";
        byte[] expected = [];
        AccountLoginPackage? package = null;
        using var uploaded = new MemoryStream();
        if (sendingPackage)
        {
            path = Path.Combine(directory, "演示账号.asxaccount");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "account-login-v1.asxaccount"), path);
            expected = await File.ReadAllBytesAsync(path);
            package = AccountLoginPackage.Read(path);
            await File.WriteAllTextAsync(path, "replaced after confirmation");
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var completeReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task host = Task.Run(async () => {
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = peer.GetStream(); using var writeLock = new SemaphoreSlim(1, 1);
            var authentication = await Protocol.AuthenticateServerDetailedAsync(stream, "receipt-fixture", timeout.Token);
            using var session = authentication.Session!;
            Task Write(byte[] payload) => Protocol.WriteMessageAsync(stream, MessageType.Control, payload, session, writeLock, timeout.Token);
            await Write(RemoteMessageCodec.EncodeDeviceInfo(new("fixture", "Windows", RemoteDeviceCapabilities.FileReceive |
                (legacy ? RemoteDeviceCapabilities.None : RemoteDeviceCapabilities.FileTransferReceipt))));
            while (true) {
                var message = await Protocol.ReadMessageAsync(stream, session, timeout.Token);
                if (message.Type != MessageType.Control) continue;
                var control = RemoteMessageCodec.DecodeControl(message.PayloadMemory);
                if (sendingPackage && control.Kind == RemoteControlKind.FileTransferChunk)
                    await uploaded.WriteAsync(control.FileBytes, timeout.Token);
                if (control.Kind != RemoteControlKind.FileTransferComplete) continue;
                // Neither generic progress nor an unrelated receipt can finish this upload.
                await Write(RemoteMessageCodec.EncodeFileTransferStatus(true, "receiving"));
                await Write(RemoteMessageCodec.EncodeFileTransferReceipt("unrelated-id", true, "wrong file"));
                completeReceived.TrySetResult();
                await release.Task.WaitAsync(timeout.Token);
                if (outcome == "disconnect") return;
                if (outcome is "saved" or "account-package" or "rejected")
                    await Write(RemoteMessageCodec.EncodeFileTransferReceipt(control.TransferId!, outcome != "rejected", outcome != "rejected" ? "saved fixture" : "disk full fixture"));
                await stop.Task.WaitAsync(timeout.Token);
                return;
            }
        }, timeout.Token);
        using var viewer = new RemoteViewerClient(allowLocalConnectionsForTesting: true);
        viewer.FileSaveConfirmationTimeout = TimeSpan.FromMilliseconds(180);
        try {
            await viewer.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, "receipt-fixture", ViewerVideoMode.StableJpeg);
            Assert.True(await viewer.WaitForCurrentDeviceInfoAsync(TimeSpan.FromSeconds(2), timeout.Token));
            var batch = package is null ? null : viewer.SendFilePastePlanToRemoteAsync(package.CreateSendPlan(),
                expectedGeneration: viewer.InputConnectionGeneration);
            Task send = (Task?)batch ?? viewer.SendFileToRemoteAsync(path);
            await completeReceived.Task.WaitAsync(timeout.Token);
            if (!legacy) Assert.False(send.IsCompleted);
            release.TrySetResult();
            if (legacy || outcome is "saved" or "account-package") await send.WaitAsync(timeout.Token);
            else {
                Exception? error = await Record.ExceptionAsync(() => send.WaitAsync(timeout.Token));
                Assert.NotNull(error);
                if (outcome == "rejected") Assert.Contains("disk full fixture", error.Message);
                if (outcome == "timeout") Assert.Contains("保存确认超时", error.Message);
            }
            if (batch is not null)
            {
                var result = await batch;
                Assert.Equal(1, result.SentFiles);
                Assert.Equal(!legacy, Assert.Single(result.Results!).SaveConfirmed);
                Assert.Equal(expected, uploaded.ToArray());
            }
        }
        finally { release.TrySetResult(); stop.TrySetResult(); await viewer.DisconnectAsync(); await host; Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void ReceiptWireKeepsIdSuccessAndUnicodeMessage()
    {
        var message = RemoteMessageCodec.DecodeControl(RemoteMessageCodec.EncodeFileTransferReceipt("fixture", false, "空间不足😀"));
        Assert.Equal(RemoteControlKind.FileTransferReceipt, message.Kind);
        Assert.Equal("fixture", message.TransferId); Assert.False(message.Success); Assert.Equal("空间不足😀", message.StatusMessage);
        Assert.Equal(35, (int)message.Kind);
        Assert.Equal(1 << 24, (int)RemoteDeviceCapabilities.FileTransferReceipt);
        Assert.ThrowsAny<IOException>(() => RemoteMessageCodec.DecodeControl(new byte[] { 35, 7, 1 }));
    }
}
