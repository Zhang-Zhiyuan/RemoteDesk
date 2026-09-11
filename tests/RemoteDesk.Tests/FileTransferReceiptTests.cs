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
    public async Task UploadCompletesOnlyAfterMatchingReceipt(string outcome)
    {
        string directory = Directory.CreateTempSubdirectory("RemoteDesk-receipt-").FullName;
        string path = Path.Combine(directory, "中文😀.txt");
        await File.WriteAllTextAsync(path, "测试\r\n");
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
                (outcome == "legacy" ? RemoteDeviceCapabilities.None : RemoteDeviceCapabilities.FileTransferReceipt))));
            while (true) {
                var message = await Protocol.ReadMessageAsync(stream, session, timeout.Token);
                if (message.Type != MessageType.Control) continue;
                var control = RemoteMessageCodec.DecodeControl(message.PayloadMemory);
                if (control.Kind != RemoteControlKind.FileTransferComplete) continue;
                // Neither generic progress nor an unrelated receipt can finish this upload.
                await Write(RemoteMessageCodec.EncodeFileTransferStatus(true, "receiving"));
                await Write(RemoteMessageCodec.EncodeFileTransferReceipt("unrelated-id", true, "wrong file"));
                completeReceived.TrySetResult();
                await release.Task.WaitAsync(timeout.Token);
                if (outcome == "disconnect") return;
                if (outcome is "saved" or "rejected")
                    await Write(RemoteMessageCodec.EncodeFileTransferReceipt(control.TransferId!, outcome == "saved", outcome == "saved" ? "saved fixture" : "disk full fixture"));
                await stop.Task.WaitAsync(timeout.Token);
                return;
            }
        }, timeout.Token);
        using var viewer = new RemoteViewerClient();
        viewer.FileSaveConfirmationTimeout = TimeSpan.FromMilliseconds(180);
        try {
            await viewer.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, "receipt-fixture", ViewerVideoMode.StableJpeg);
            Assert.True(await viewer.WaitForCurrentDeviceInfoAsync(TimeSpan.FromSeconds(2), timeout.Token));
            Task send = viewer.SendFileToRemoteAsync(path);
            await completeReceived.Task.WaitAsync(timeout.Token);
            if (outcome != "legacy") Assert.False(send.IsCompleted);
            release.TrySetResult();
            if (outcome is "saved" or "legacy") await send.WaitAsync(timeout.Token);
            else {
                Exception? error = await Record.ExceptionAsync(() => send.WaitAsync(timeout.Token));
                Assert.NotNull(error);
                if (outcome == "rejected") Assert.Contains("disk full fixture", error.Message);
                if (outcome == "timeout") Assert.Contains("保存确认超时", error.Message);
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
