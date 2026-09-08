using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class RemoteHostServerTransferWireTests
{
    [Fact]
    public async Task ViewerFailureReceiptStopsActiveClipboardReturnWithoutStatusStorm()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            $"RemoteDesk-wire-receipt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        string firstPath = Path.Combine(tempDirectory, "first.bin");
        string secondPath = Path.Combine(tempDirectory, "second.bin");
        await File.WriteAllBytesAsync(firstPath, new byte[8 * 1024 * 1024]);
        await File.WriteAllBytesAsync(secondPath, new byte[8 * 1024 * 1024]);

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();

            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
            using var viewer = new TcpClient
            {
                NoDelay = true,
                ReceiveBufferSize = 4 * 1024
            };
            await viewer.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            using TcpClient host = await acceptTask;
            host.NoDelay = true;
            host.SendBufferSize = 4 * 1024;

            NetworkStream hostStream = host.GetStream();
            NetworkStream viewerStream = viewer.GetStream();
            const string password = "wire failure receipt password";
            Task<SecureSession?> hostAuthentication = Protocol.AuthenticateServerAsync(
                hostStream,
                password,
                timeout.Token);
            Task<SecureSession> viewerAuthentication = Protocol.AuthenticateClientAsync(
                viewerStream,
                password,
                timeout.Token);

            using SecureSession viewerSession = await viewerAuthentication;
            using SecureSession hostSession = Assert.IsType<SecureSession>(await hostAuthentication);
            using var viewerWriteLock = new SemaphoreSlim(1, 1);
            using var writePriority = new RemoteHostServer.SessionWritePriority();
            var viewerState = new RemoteHostServer.ViewerSessionState
            {
                Capabilities = RemoteDeviceCapabilities.FileTransferCancel
            };
            var plan = new RemoteFilePastePlan([firstPath, secondPath], 0, 0, false);
            var receiptHandled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var duplicateReceiptHandled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var logs = new ConcurrentQueue<string>();

            Task receiptTask = Task.Run(async () =>
            {
                for (int index = 0; index < 2; index++)
                {
                    ProtocolMessage receiptMessage = await Protocol.ReadMessageAsync(
                        hostStream,
                        hostSession,
                        timeout.Token);
                    Assert.Equal(MessageType.Control, receiptMessage.Type);
                    RemoteControlMessage receipt = RemoteMessageCodec.DecodeControl(receiptMessage.PayloadMemory);
                    Assert.Equal(RemoteControlKind.FileTransferStatus, receipt.Kind);
                    bool cancelled = RemoteHostServer.HandlePeerFileTransferStatus(
                        receipt,
                        viewerState,
                        logs.Enqueue);
                    (index == 0 ? receiptHandled : duplicateReceiptHandled).TrySetResult(cancelled);
                }
            }, CancellationToken.None);

            Assert.True(viewerState.TryStartClipboardFileReturn(
                operationCancellationToken => RemoteHostServer.RunClipboardFilesToViewerAsync(
                    plan,
                    hostStream,
                    hostSession,
                    writePriority,
                    logs.Enqueue,
                    viewerState.Capabilities,
                    operationCancellationToken,
                    timeout.Token),
                timeout.Token));

            bool failureSent = false;
            int chunksAfterFailureHandled = 0;
            int transferStarts = 0;
            int transferCancels = 0;
            int transferCompletes = 0;
            int failureStatuses = 0;
            while (true)
            {
                ProtocolMessage message = await Protocol.ReadMessageAsync(
                    viewerStream,
                    viewerSession,
                    timeout.Token);
                if (message.Type == MessageType.Ping)
                {
                    break;
                }

                Assert.Equal(MessageType.Control, message.Type);
                RemoteControlMessage control = RemoteMessageCodec.DecodeControl(message.PayloadMemory);
                switch (control.Kind)
                {
                    case RemoteControlKind.FileTransferStart:
                        transferStarts++;
                        Assert.Equal("first.bin", control.FileName);
                        if (!failureSent)
                        {
                            failureSent = true;
                            byte[] failure = RemoteMessageCodec.EncodeFileTransferStatus(
                                false,
                                "接收远端文件失败：回传清单绑定失败");
                            await Protocol.WriteMessageAsync(
                                viewerStream,
                                MessageType.Control,
                                failure,
                                viewerSession,
                                viewerWriteLock,
                                timeout.Token);
                            Assert.True(await receiptHandled.Task.WaitAsync(TimeSpan.FromSeconds(5)));

                            await Protocol.WriteMessageAsync(
                                viewerStream,
                                MessageType.Control,
                                failure,
                                viewerSession,
                                viewerWriteLock,
                                timeout.Token);
                        }

                        break;

                    case RemoteControlKind.FileTransferChunk:
                        if (receiptHandled.Task.IsCompleted)
                        {
                            chunksAfterFailureHandled++;
                        }

                        break;

                    case RemoteControlKind.FileTransferCancel:
                        transferCancels++;
                        break;

                    case RemoteControlKind.FileTransferComplete:
                        transferCompletes++;
                        break;

                    case RemoteControlKind.FileTransferStatus when !control.Success:
                        failureStatuses++;
                        Assert.Contains("已取消", control.StatusMessage, StringComparison.Ordinal);
                        await duplicateReceiptHandled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        await viewerState.CancelAndWaitForClipboardFileReturnAsync().WaitAsync(TimeSpan.FromSeconds(5));
                        await receiptTask.WaitAsync(TimeSpan.FromSeconds(5));
                        await Protocol.WriteMessageAsync(
                            hostStream,
                            MessageType.Ping,
                            ReadOnlyMemory<byte>.Empty,
                            hostSession,
                            writePriority.Lock,
                            timeout.Token);
                        break;
                }
            }

            Assert.True(failureSent);
            int completeFirstFileChunkCount = (int)Math.Ceiling(
                new FileInfo(firstPath).Length /
                (double)RemoteMessageCodec.RecommendedFileTransferChunkBytes);
            Assert.InRange(chunksAfterFailureHandled, 0, completeFirstFileChunkCount - 1);
            Assert.Equal(1, transferStarts);
            Assert.Equal(1, transferCancels);
            Assert.Equal(0, transferCompletes);
            Assert.Equal(1, failureStatuses);
            Assert.False(viewerState.HasActiveClipboardFileReturn);
            Assert.Equal(2, logs.Count(message => message.StartsWith("查看端文件状态异常：", StringComparison.Ordinal)));
        }
        finally
        {
            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public async Task PendingRejectWritesOneCanonicalStatusBeforeImmediateRetryPreview()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
        using var viewer = new TcpClient { NoDelay = true };
        await viewer.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        using TcpClient host = await acceptTask;
        host.NoDelay = true;

        NetworkStream hostStream = host.GetStream();
        NetworkStream viewerStream = viewer.GetStream();
        const string password = "wire pending reject barrier password";
        Task<SecureSession?> hostAuthentication = Protocol.AuthenticateServerAsync(
            hostStream,
            password,
            timeout.Token);
        Task<SecureSession> viewerAuthentication = Protocol.AuthenticateClientAsync(
            viewerStream,
            password,
            timeout.Token);

        using SecureSession viewerSession = await viewerAuthentication;
        using SecureSession hostSession = Assert.IsType<SecureSession>(await hostAuthentication);
        using var hostWriteLock = new SemaphoreSlim(1, 1);
        using var viewerWriteLock = new SemaphoreSlim(1, 1);
        var viewerState = new RemoteHostServer.ViewerSessionState();
        Assert.True(viewerState.TryReserveClipboardFileReturnPlan(
            new RemoteFilePastePlan([@"C:\pending.txt"], 0, 0, false)));

        var retryPreviewItem = new FileTransferConfirmationItem(
            "文件",
            @"C:\retry.txt",
            "retry.txt",
            1,
            @"C:\received\retry.txt");
        Task hostSequence = RunRejectThenImmediateRequestHostLoopAsync(
            hostStream,
            hostSession,
            hostWriteLock,
            viewerState,
            retryPreviewItem,
            expectedRejectCount: 2,
            timeout.Token);

        // Queue a duplicate reject and the retry request without waiting for any host response.
        // The real host input loop processes these encrypted controls serially.
        await Protocol.WriteMessageAsync(
            viewerStream,
            MessageType.Control,
            RemoteMessageCodec.EncodeFileTransferRejectClipboardFiles(),
            viewerSession,
            viewerWriteLock,
            timeout.Token);
        await Protocol.WriteMessageAsync(
            viewerStream,
            MessageType.Control,
            RemoteMessageCodec.EncodeFileTransferRejectClipboardFiles(),
            viewerSession,
            viewerWriteLock,
            timeout.Token);
        await Protocol.WriteMessageAsync(
            viewerStream,
            MessageType.Control,
            RemoteMessageCodec.EncodeFileTransferRequestClipboardFiles(),
            viewerSession,
            viewerWriteLock,
            timeout.Token);

        ProtocolMessage terminalMessage = await Protocol.ReadMessageAsync(
            viewerStream,
            viewerSession,
            timeout.Token);
        Assert.Equal(MessageType.Control, terminalMessage.Type);
        RemoteControlMessage terminal = RemoteMessageCodec.DecodeControl(terminalMessage.PayloadMemory);
        Assert.Equal(RemoteControlKind.FileTransferStatus, terminal.Kind);
        Assert.False(terminal.Success);
        Assert.Equal("远端文件回传已取消。", terminal.StatusMessage);

        ProtocolMessage retryMessage = await Protocol.ReadMessageAsync(
            viewerStream,
            viewerSession,
            timeout.Token);
        Assert.Equal(MessageType.Control, retryMessage.Type);
        RemoteControlMessage retry = RemoteMessageCodec.DecodeControl(retryMessage.PayloadMemory);
        Assert.Equal(RemoteControlKind.FileTransferClipboardFilesPreview, retry.Kind);
        Assert.DoesNotContain("进行中", retry.StatusMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.False(viewerState.HasPendingClipboardFileReturnPlan);
        Assert.False(viewerState.HasActiveClipboardFileReturn);
        await hostSequence.WaitAsync(timeout.Token);
    }

    [Fact]
    public async Task ActiveRejectWaitsForSingleCancelAndTerminalBeforeImmediateRetryPreview()
    {
        string sourcePath = Path.Combine(
            Path.GetTempPath(),
            $"RemoteDesk-wire-reject-barrier-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(sourcePath, new byte[8 * 1024 * 1024]);
        var viewerState = new RemoteHostServer.ViewerSessionState
        {
            Capabilities = RemoteDeviceCapabilities.FileTransferCancel
        };

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();

            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
            using var viewer = new TcpClient
            {
                NoDelay = true,
                ReceiveBufferSize = 4 * 1024
            };
            await viewer.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            using TcpClient host = await acceptTask;
            host.NoDelay = true;
            host.SendBufferSize = 4 * 1024;

            NetworkStream hostStream = host.GetStream();
            NetworkStream viewerStream = viewer.GetStream();
            const string password = "wire active reject barrier password";
            Task<SecureSession?> hostAuthentication = Protocol.AuthenticateServerAsync(
                hostStream,
                password,
                timeout.Token);
            Task<SecureSession> viewerAuthentication = Protocol.AuthenticateClientAsync(
                viewerStream,
                password,
                timeout.Token);

            using SecureSession viewerSession = await viewerAuthentication;
            using SecureSession hostSession = Assert.IsType<SecureSession>(await hostAuthentication);
            using var hostWritePriority = new RemoteHostServer.SessionWritePriority();
            using var viewerWriteLock = new SemaphoreSlim(1, 1);
            var plan = new RemoteFilePastePlan([sourcePath], 0, 0, false);
            Assert.True(viewerState.TryStartClipboardFileReturn(
                operationCancellationToken => RemoteHostServer.RunClipboardFilesToViewerAsync(
                    plan,
                    hostStream,
                    hostSession,
                    hostWritePriority,
                    _ => { },
                    viewerState.Capabilities,
                    operationCancellationToken,
                    timeout.Token),
                timeout.Token));

            var retryPreviewItem = new FileTransferConfirmationItem(
                "文件",
                @"C:\retry-after-active.txt",
                "retry-after-active.txt",
                1,
                @"C:\received\retry-after-active.txt");
            Task hostControlTask = RunRejectThenImmediateRequestHostLoopAsync(
                hostStream,
                hostSession,
                hostWritePriority.Lock,
                viewerState,
                retryPreviewItem,
                expectedRejectCount: 2,
                timeout.Token);
            bool rejectAndRetrySent = false;
            int transferStarts = 0;
            int transferCancels = 0;
            int cancellationStatuses = 0;
            int busyStatuses = 0;
            bool retryPreviewReceived = false;

            while (!retryPreviewReceived)
            {
                ProtocolMessage message = await Protocol.ReadMessageAsync(
                    viewerStream,
                    viewerSession,
                    timeout.Token);
                Assert.Equal(MessageType.Control, message.Type);
                RemoteControlMessage control = RemoteMessageCodec.DecodeControl(message.PayloadMemory);
                switch (control.Kind)
                {
                    case RemoteControlKind.FileTransferStart:
                        transferStarts++;
                        break;

                    case RemoteControlKind.FileTransferChunk when !rejectAndRetrySent:
                        rejectAndRetrySent = true;
                        await Protocol.WriteMessageAsync(
                            viewerStream,
                            MessageType.Control,
                            RemoteMessageCodec.EncodeFileTransferRejectClipboardFiles(),
                            viewerSession,
                            viewerWriteLock,
                            timeout.Token);
                        await Protocol.WriteMessageAsync(
                            viewerStream,
                            MessageType.Control,
                            RemoteMessageCodec.EncodeFileTransferRejectClipboardFiles(),
                            viewerSession,
                            viewerWriteLock,
                            timeout.Token);
                        await Protocol.WriteMessageAsync(
                            viewerStream,
                            MessageType.Control,
                            RemoteMessageCodec.EncodeFileTransferRequestClipboardFiles(),
                            viewerSession,
                            viewerWriteLock,
                            timeout.Token);
                        break;

                    case RemoteControlKind.FileTransferCancel:
                        transferCancels++;
                        break;

                    case RemoteControlKind.FileTransferStatus when !control.Success:
                        if ((control.StatusMessage ?? string.Empty).StartsWith(
                                "远端文件回传已取消",
                                StringComparison.Ordinal))
                        {
                            cancellationStatuses++;
                        }

                        if ((control.StatusMessage ?? string.Empty).Contains(
                                "进行中",
                                StringComparison.Ordinal))
                        {
                            busyStatuses++;
                        }

                        break;

                    case RemoteControlKind.FileTransferClipboardFilesPreview:
                        Assert.Equal(1, cancellationStatuses);
                        retryPreviewReceived = true;
                        break;
                }
            }

            Assert.True(rejectAndRetrySent);
            await hostControlTask.WaitAsync(timeout.Token);
            Assert.Equal(1, transferStarts);
            Assert.Equal(1, transferCancels);
            Assert.Equal(1, cancellationStatuses);
            Assert.Equal(0, busyStatuses);
            Assert.False(viewerState.HasActiveClipboardFileReturn);
        }
        finally
        {
            try
            {
                await viewerState.CancelAndWaitForClipboardFileReturnAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException or ObjectDisposedException)
            {
            }

            try
            {
                File.Delete(sourcePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public async Task CancelledFileSendLeavesWireAlignedForCancelAndNextEncryptedMessage()
    {
        string sourcePath = Path.Combine(
            Path.GetTempPath(),
            $"RemoteDesk-wire-cancel-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(sourcePath, new byte[8 * 1024 * 1024]);

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var operationCancellation = new CancellationTokenSource();
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();

            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
            using var receiver = new TcpClient
            {
                NoDelay = true,
                ReceiveBufferSize = 4 * 1024
            };
            await receiver.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            using TcpClient sender = await acceptTask;
            sender.NoDelay = true;
            sender.SendBufferSize = 4 * 1024;

            NetworkStream senderStream = sender.GetStream();
            NetworkStream receiverStream = receiver.GetStream();
            const string password = "wire cancellation password";
            Task<SecureSession?> senderAuthentication = Protocol.AuthenticateServerAsync(
                senderStream,
                password,
                timeout.Token);
            Task<SecureSession> receiverAuthentication = Protocol.AuthenticateClientAsync(
                receiverStream,
                password,
                timeout.Token);

            using SecureSession receiverSession = await receiverAuthentication;
            using SecureSession senderSession = Assert.IsType<SecureSession>(await senderAuthentication);
            using var senderWriteLock = new SemaphoreSlim(1, 1);

            Task sendTask = RemoteHostServer.SendFileToViewerAsync(
                sourcePath,
                senderStream,
                senderSession,
                senderWriteLock,
                operationCancellation.Token,
                sendChecksum: false,
                sendCancel: true,
                transferCancelNotificationToken: timeout.Token);

            string? transferId = null;
            int receivedChunkCount = 0;
            RemoteControlMessage? cancellation = null;
            while (cancellation is null)
            {
                ProtocolMessage message = await Protocol.ReadMessageAsync(
                    receiverStream,
                    receiverSession,
                    timeout.Token);
                Assert.Equal(MessageType.Control, message.Type);

                RemoteControlMessage control = RemoteMessageCodec.DecodeControl(message.PayloadMemory);
                switch (control.Kind)
                {
                    case RemoteControlKind.FileTransferStart:
                        transferId = control.TransferId;
                        break;

                    case RemoteControlKind.FileTransferChunk:
                        Assert.InRange(
                            control.FileBytes.Length,
                            1,
                            RemoteMessageCodec.RecommendedFileTransferChunkBytes);
                        receivedChunkCount++;
                        if (receivedChunkCount == 1)
                        {
                            operationCancellation.Cancel();
                        }

                        break;

                    case RemoteControlKind.FileTransferComplete:
                        Assert.Fail("传输在测试触发取消后仍然完成，未验证到取消线路。");
                        break;

                    case RemoteControlKind.FileTransferCancel:
                        cancellation = control;
                        break;
                }
            }

            Assert.True(receivedChunkCount >= 1);
            Assert.False(string.IsNullOrWhiteSpace(transferId));
            Assert.Equal(transferId, cancellation.TransferId);
            Assert.False(string.IsNullOrWhiteSpace(cancellation.StatusMessage));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await sendTask);

            await Protocol.WriteMessageAsync(
                senderStream,
                MessageType.Ping,
                ReadOnlyMemory<byte>.Empty,
                senderSession,
                senderWriteLock,
                timeout.Token);

            ProtocolMessage ping = await Protocol.ReadMessageAsync(
                receiverStream,
                receiverSession,
                timeout.Token);
            Assert.Equal(MessageType.Ping, ping.Type);
            Assert.Equal(0, ping.PayloadLength);
        }
        finally
        {
            try
            {
                File.Delete(sourcePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task RunRejectThenImmediateRequestHostLoopAsync(
        NetworkStream hostStream,
        SecureSession hostSession,
        SemaphoreSlim hostWriteLock,
        RemoteHostServer.ViewerSessionState viewerState,
        FileTransferConfirmationItem retryPreviewItem,
        int expectedRejectCount,
        CancellationToken cancellationToken)
    {
        int rejectCount = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            ProtocolMessage message = await Protocol.ReadMessageAsync(
                hostStream,
                hostSession,
                cancellationToken);
            Assert.Equal(MessageType.Control, message.Type);
            RemoteControlMessage control = RemoteMessageCodec.DecodeControl(message.PayloadMemory);
            switch (control.Kind)
            {
                case RemoteControlKind.FileTransferRejectClipboardFiles:
                    rejectCount++;
                    await RemoteHostServer.RejectClipboardFilesToViewerAsync(
                        hostStream,
                        hostSession,
                        hostWriteLock,
                        viewerState,
                        cancellationToken);
                    break;

                case RemoteControlKind.FileTransferRequestClipboardFiles:
                    Assert.Equal(expectedRejectCount, rejectCount);
                    if (viewerState.HasPendingClipboardFileReturnPlan ||
                        viewerState.HasActiveClipboardFileReturn)
                    {
                        await Protocol.WriteMessageAsync(
                            hostStream,
                            MessageType.Control,
                            RemoteMessageCodec.EncodeFileTransferStatus(
                                false,
                                "远端文件回传进行中，请等待完成或先取消当前请求。"),
                            hostSession,
                            hostWriteLock,
                            cancellationToken);
                    }
                    else
                    {
                        await Protocol.WriteMessageAsync(
                            hostStream,
                            MessageType.Control,
                            RemoteMessageCodec.EncodeFileTransferClipboardFilesPreview([retryPreviewItem], null),
                            hostSession,
                            hostWriteLock,
                            cancellationToken);
                    }

                    return;
            }
        }
    }
}
