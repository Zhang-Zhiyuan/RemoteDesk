using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class ProtocolBoundaryTests
{
    [Fact]
    public async Task WriteMessageAsyncRejectsOversizedControlPayloadBeforeWriting()
    {
        byte[] payload = new byte[RemoteMessageCodec.MaxControlPayloadBytes + 1];

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Protocol.WriteMessageAsync(
                null!,
                MessageType.Control,
                payload,
                null!,
                null!,
                CancellationToken.None));
    }

    [Fact]
    public async Task WriteMessageAsyncRejectsNonEmptyPingPayload()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Protocol.WriteMessageAsync(
                null!,
                MessageType.Ping,
                new byte[] { 1 },
                null!,
                null!,
                CancellationToken.None));
    }

    [Fact]
    public async Task WriteMessageAsyncRejectsUnknownMessageType()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Protocol.WriteMessageAsync(
                null!,
                (MessageType)255,
                ReadOnlyMemory<byte>.Empty,
                null!,
                null!,
                CancellationToken.None));
    }

    [Fact]
    public void DecodeInputRejectsUnexpectedPayloadLength()
    {
        Assert.Throws<InvalidDataException>(() =>
            RemoteMessageCodec.DecodeInput(new byte[RemoteMessageCodec.InputPayloadLength - 1]));
    }

    [Fact]
    public void DecodeControlRejectsOversizedPayload()
    {
        byte[] payload = new byte[RemoteMessageCodec.MaxControlPayloadBytes + 1];

        Assert.Throws<InvalidDataException>(() =>
            RemoteMessageCodec.DecodeControl(payload));
    }

    [Fact]
    public void DecodeControlRejectsTrailingBytes()
    {
        byte[] payload =
        [
            (byte)RemoteControlKind.ClipboardGetText,
            0
        ];

        Assert.Throws<InvalidDataException>(() =>
            RemoteMessageCodec.DecodeControl(payload));
    }

    [Fact]
    public void EncodeVideoKeyFrameRequestRoundTrips()
    {
        RemoteControlMessage control = RemoteMessageCodec.DecodeControl(
            RemoteMessageCodec.EncodeVideoKeyFrameRequest());

        Assert.Equal(RemoteControlKind.VideoKeyFrameRequest, control.Kind);
    }

    [Fact]
    public void EncodeSessionRejectedRoundTrips()
    {
        RemoteControlMessage control = RemoteMessageCodec.DecodeControl(
            RemoteMessageCodec.EncodeSessionRejected(
                "被控端已有查看端连接。"));

        Assert.Equal(RemoteControlKind.SessionRejected, control.Kind);
        Assert.Equal(
            "被控端已有查看端连接。",
            control.StatusMessage);
    }

    [Fact]
    public void EncodeFileTransferRequestClipboardFilesRoundTrips()
    {
        RemoteControlMessage control = RemoteMessageCodec.DecodeControl(
            RemoteMessageCodec.EncodeFileTransferRequestClipboardFiles());

        Assert.Equal(RemoteControlKind.FileTransferRequestClipboardFiles, control.Kind);
    }

    [Fact]
    public void EncodeFileTransferClipboardFilesPreviewRoundTrips()
    {
        var item = new FileTransferConfirmationItem(
            "文件",
            @"C:\source\a.txt",
            "a.txt",
            123,
            @"C:\Users\me\Downloads\RemoteDeskReceived\a.txt");

        RemoteControlMessage control = RemoteMessageCodec.DecodeControl(
            RemoteMessageCodec.EncodeFileTransferClipboardFilesPreview([item], "note"));

        Assert.Equal(RemoteControlKind.FileTransferClipboardFilesPreview, control.Kind);
        Assert.Equal("note", control.StatusMessage);
        FileTransferConfirmationItem decoded = Assert.Single(control.FileTransferPreviewItems!);
        Assert.Equal(item, decoded);
    }

    [Theory]
    [InlineData((int)RemoteControlKind.FileTransferConfirmClipboardFiles)]
    [InlineData((int)RemoteControlKind.FileTransferRejectClipboardFiles)]
    public void EncodeFileTransferPreviewDecisionRoundTrips(int expectedKindValue)
    {
        var expectedKind = (RemoteControlKind)expectedKindValue;
        byte[] payload = expectedKind == RemoteControlKind.FileTransferConfirmClipboardFiles
            ? RemoteMessageCodec.EncodeFileTransferConfirmClipboardFiles()
            : RemoteMessageCodec.EncodeFileTransferRejectClipboardFiles();

        RemoteControlMessage control = RemoteMessageCodec.DecodeControl(payload);

        Assert.Equal(expectedKind, control.Kind);
    }

    [Fact]
    public void EncodeFileTransferChecksumRoundTrips()
    {
        string checksum = Convert.ToHexString(SHA256.HashData([1, 2, 3])).ToLowerInvariant();

        RemoteControlMessage control = RemoteMessageCodec.DecodeControl(
            RemoteMessageCodec.EncodeFileTransferChecksum("transfer-1", checksum));

        Assert.Equal(RemoteControlKind.FileTransferChecksum, control.Kind);
        Assert.Equal("transfer-1", control.TransferId);
        Assert.Equal(RemoteMessageCodec.FileTransferChecksumAlgorithm, control.ChecksumAlgorithm);
        Assert.Equal(checksum, control.ChecksumHex);
    }

    [Fact]
    public void EncodeRemoteUpdateStartRoundTrips()
    {
        RemoteControlMessage control = RemoteMessageCodec.DecodeControl(
            RemoteMessageCodec.EncodeRemoteUpdateStart("update-transfer", "RemoteDesk.exe", 123456));

        Assert.Equal(RemoteControlKind.RemoteUpdateStart, control.Kind);
        Assert.Equal("update-transfer", control.TransferId);
        Assert.Equal("RemoteDesk.exe", control.FileName);
        Assert.Equal(123456, control.FileLength);
    }

    [Fact]
    public void EncodeRemoteUpdatePackageRequestRoundTrips()
    {
        RemoteControlMessage control = RemoteMessageCodec.DecodeControl(
            RemoteMessageCodec.EncodeRemoteUpdatePackageRequest());

        Assert.Equal(RemoteControlKind.RemoteUpdatePackageRequest, control.Kind);
    }

    [Fact]
    public void EncodeDeviceInfoDoesNotAppendBuildStamp()
    {
        RemoteControlMessage control = RemoteMessageCodec.DecodeControl(
            RemoteMessageCodec.EncodeDeviceInfo(new RemoteDeviceDescriptor(
                "Desktop",
                RemoteDevicePlatforms.Windows,
                RemoteDeviceCapabilities.RemoteDesktop,
                "20260623010203")));

        Assert.Equal(RemoteControlKind.DeviceInfo, control.Kind);
        Assert.Equal("Desktop", control.MachineName);
        Assert.Equal(RemoteDevicePlatforms.Windows, control.Platform);
        Assert.Null(control.BuildStamp);
    }

    [Fact]
    public void EncodeDeviceBuildInfoRoundTrips()
    {
        RemoteControlMessage control = RemoteMessageCodec.DecodeControl(
            RemoteMessageCodec.EncodeDeviceBuildInfo("20260623010203"));

        Assert.Equal(RemoteControlKind.DeviceBuildInfo, control.Kind);
        Assert.Equal("20260623010203", control.BuildStamp);
    }

    [Fact]
    public void DecodeLegacyDeviceInfoWithoutBuildStamp()
    {
        using var output = new MemoryStream();
        using (var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((byte)RemoteControlKind.DeviceInfo);
            writer.Write("Legacy");
            writer.Write(RemoteDevicePlatforms.Windows);
            writer.Write((int)RemoteDeviceCapabilities.RemoteDesktop);
        }

        RemoteControlMessage control = RemoteMessageCodec.DecodeControl(output.ToArray());

        Assert.Equal(RemoteControlKind.DeviceInfo, control.Kind);
        Assert.Equal("Legacy", control.MachineName);
        Assert.Null(control.BuildStamp);
    }

    [Fact]
    public void DecodeTransitionalDeviceInfoWithAppendedBuildStamp()
    {
        using var output = new MemoryStream();
        using (var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((byte)RemoteControlKind.DeviceInfo);
            writer.Write("Transitional");
            writer.Write(RemoteDevicePlatforms.Windows);
            writer.Write((int)RemoteDeviceCapabilities.RemoteDesktop);
            writer.Write("20260623010203");
        }

        RemoteControlMessage control = RemoteMessageCodec.DecodeControl(output.ToArray());

        Assert.Equal(RemoteControlKind.DeviceInfo, control.Kind);
        Assert.Equal("Transitional", control.MachineName);
        Assert.Equal("20260623010203", control.BuildStamp);
    }

    [Fact]
    public void EncodeViewerCapabilitiesRoundTrips()
    {
        RemoteControlMessage control = RemoteMessageCodec.DecodeControl(
            RemoteMessageCodec.EncodeViewerCapabilities(
                RemoteDeviceCapabilities.FileChecksum |
                RemoteDeviceCapabilities.FileTransferCancel));

        Assert.Equal(RemoteControlKind.ViewerCapabilities, control.Kind);
        Assert.True(control.Capabilities.HasFlag(RemoteDeviceCapabilities.FileChecksum));
        Assert.True(control.Capabilities.HasFlag(RemoteDeviceCapabilities.FileTransferCancel));
    }

    [Fact]
    public void EncodeFileTransferCancelRoundTrips()
    {
        RemoteControlMessage control = RemoteMessageCodec.DecodeControl(
            RemoteMessageCodec.EncodeFileTransferCancel("transfer-cancel", "local read failed"));

        Assert.Equal(RemoteControlKind.FileTransferCancel, control.Kind);
        Assert.Equal("transfer-cancel", control.TransferId);
        Assert.Equal("local read failed", control.StatusMessage);
    }

    [Fact]
    public void DecodeLinuxFileTransferControlPayloads()
    {
        const string transferId = "linux-transfer-1";
        const string checksum = "14feba111828019cfa60b7ce26e6999ba18fdc717bb2c20e47e191ba669a169c";

        RemoteControlMessage start = RemoteMessageCodec.DecodeControl(Convert.FromHexString(
            "08106c696e75782d7472616e736665722d310e6c696e75782d6e6f74652e7478740c00000000000000"));
        RemoteControlMessage chunk = RemoteMessageCodec.DecodeControl(Convert.FromHexString(
            "09106c696e75782d7472616e736665722d3100000000000000000c00000068656c6c6f2072656d6f7465"));
        RemoteControlMessage checksumMessage = RemoteMessageCodec.DecodeControl(Convert.FromHexString(
            "13106c696e75782d7472616e736665722d31065348413235364031346665626131313138323830313963666136306237636532366536393939626131386664633731376262326332306534376531393162613636396131363963"));
        RemoteControlMessage complete = RemoteMessageCodec.DecodeControl(Convert.FromHexString(
            "0a106c696e75782d7472616e736665722d31"));
        RemoteControlMessage cancel = RemoteMessageCodec.DecodeControl(Convert.FromHexString(
            "15106c696e75782d7472616e736665722d311463616e63656c6c65642066726f6d206c696e7578"));

        Assert.Equal(RemoteControlKind.FileTransferStart, start.Kind);
        Assert.Equal(transferId, start.TransferId);
        Assert.Equal("linux-note.txt", start.FileName);
        Assert.Equal(12, start.FileLength);

        Assert.Equal(RemoteControlKind.FileTransferChunk, chunk.Kind);
        Assert.Equal(transferId, chunk.TransferId);
        Assert.Equal(0, chunk.FileOffset);
        Assert.Equal([104, 101, 108, 108, 111, 32, 114, 101, 109, 111, 116, 101], chunk.FileBytes.ToArray());

        Assert.Equal(RemoteControlKind.FileTransferChecksum, checksumMessage.Kind);
        Assert.Equal(transferId, checksumMessage.TransferId);
        Assert.Equal(RemoteMessageCodec.FileTransferChecksumAlgorithm, checksumMessage.ChecksumAlgorithm);
        Assert.Equal(checksum, checksumMessage.ChecksumHex);

        Assert.Equal(RemoteControlKind.FileTransferComplete, complete.Kind);
        Assert.Equal(transferId, complete.TransferId);

        Assert.Equal(RemoteControlKind.FileTransferCancel, cancel.Kind);
        Assert.Equal(transferId, cancel.TransferId);
        Assert.Equal("cancelled from linux", cancel.StatusMessage);
    }

    [Fact]
    public void DecodeFileTransferChecksumRejectsInvalidHex()
    {
        Assert.Throws<InvalidDataException>(() =>
            RemoteMessageCodec.EncodeFileTransferChecksum("transfer-1", new string('x', 64)));
    }

    [Fact]
    public void EncodeFileTransferChunkRejectsChunkPastFileLimit()
    {
        Assert.Throws<InvalidDataException>(() =>
            RemoteMessageCodec.EncodeFileTransferChunk(
                "transfer-1",
                RemoteMessageCodec.MaxFileTransferBytes,
                new byte[] { 1 }));
    }

    [Fact]
    public void WriteFileTransferChunkPayloadMatchesAllocatedEncoder()
    {
        byte[] chunk = [1, 2, 3, 4, 5];
        byte[] expected = RemoteMessageCodec.EncodeFileTransferChunk("transfer-1", 64, chunk);
        byte[] destination = new byte[RemoteMessageCodec.GetFileTransferChunkPayloadLength("transfer-1", chunk.Length)];

        int written = RemoteMessageCodec.WriteFileTransferChunkPayload("transfer-1", 64, chunk, destination);

        Assert.Equal(expected.Length, written);
        Assert.Equal(expected, destination);
    }

    [Fact]
    public void DecodeFileTransferChunkRejectsChunkPastFileLimit()
    {
        byte[] payload = CreateFileTransferChunkPayload(
            RemoteMessageCodec.MaxFileTransferBytes,
            new byte[] { 1 });

        Assert.Throws<InvalidDataException>(() =>
            RemoteMessageCodec.DecodeControl(payload));
    }

    [Fact]
    public void DecodeFileTransferChunkSharesUnderlyingPayloadWithoutCopying()
    {
        byte[] fileBytes = Enumerable.Range(0, RemoteMessageCodec.FileTransferChunkBytes)
            .Select(index => (byte)index)
            .ToArray();
        byte[] payload = RemoteMessageCodec.EncodeFileTransferChunk("transfer-zero-copy", 23, fileBytes);

        RemoteControlMessage control = RemoteMessageCodec.DecodeControl(payload);

        Assert.True(MemoryMarshal.TryGetArray(control.FileBytes, out ArraySegment<byte> segment));
        Assert.Same(payload, segment.Array);
        Assert.Equal(payload.Length - fileBytes.Length, segment.Offset);
        Assert.Equal(fileBytes.Length, segment.Count);
        Assert.Equal(fileBytes, control.FileBytes.ToArray());
    }

    [Fact]
    public void DecodeFileTransferChunkPreservesInputSliceBoundaries()
    {
        byte[] fileBytes = [3, 1, 4, 1, 5];
        byte[] payload = RemoteMessageCodec.EncodeFileTransferChunk("transfer-slice", 0, fileBytes);
        const int prefixLength = 11;
        byte[] backing = Enumerable.Repeat((byte)0xcc, prefixLength + payload.Length + 13).ToArray();
        payload.CopyTo(backing, prefixLength);

        RemoteControlMessage control = RemoteMessageCodec.DecodeControl(
            backing.AsMemory(prefixLength, payload.Length));

        Assert.True(MemoryMarshal.TryGetArray(control.FileBytes, out ArraySegment<byte> segment));
        Assert.Same(backing, segment.Array);
        Assert.Equal(prefixLength + payload.Length - fileBytes.Length, segment.Offset);
        Assert.Equal(fileBytes.Length, segment.Count);
        Assert.Equal(fileBytes, control.FileBytes.ToArray());
    }

    [Fact]
    public void DecodeFileTransferChunkRejectsTruncatedChunkWithoutEscapingInputSlice()
    {
        byte[] payload = RemoteMessageCodec.EncodeFileTransferChunk(
            "transfer-truncated",
            0,
            new byte[] { 8, 9, 10, 11 });
        byte[] backing = new byte[payload.Length + 8];
        payload.CopyTo(backing, 0);

        Assert.Throws<EndOfStreamException>(() =>
            RemoteMessageCodec.DecodeControl(backing.AsMemory(0, payload.Length - 1)));
    }

    [Theory]
    [InlineData((int)RemoteControlKind.FileDropPasteBegin)]
    [InlineData((int)RemoteControlKind.FileDropPasteCommit)]
    [InlineData((int)RemoteControlKind.FileDropPasteCancel)]
    public void EncodeFileDropPasteControlMessagesRoundTrip(int expectedKindValue)
    {
        var expectedKind = (RemoteControlKind)expectedKindValue;
        byte[] payload = expectedKind switch
        {
            RemoteControlKind.FileDropPasteBegin => RemoteMessageCodec.EncodeFileDropPasteBegin(),
            RemoteControlKind.FileDropPasteCommit => RemoteMessageCodec.EncodeFileDropPasteCommit(),
            RemoteControlKind.FileDropPasteCancel => RemoteMessageCodec.EncodeFileDropPasteCancel(),
            _ => throw new ArgumentOutOfRangeException(nameof(expectedKind))
        };

        RemoteControlMessage control = RemoteMessageCodec.DecodeControl(payload);

        Assert.Equal(expectedKind, control.Kind);
    }

    [Fact]
    public void DecodeVideoFrameRejectsUnknownFlags()
    {
        byte[] payload = new byte[33];
        BitConverter.GetBytes((int)RemoteFrameEncoding.H264AnnexB).CopyTo(payload, 0);
        BitConverter.GetBytes(1280).CopyTo(payload, 4);
        BitConverter.GetBytes(720).CopyTo(payload, 8);
        BitConverter.GetBytes(1 << 8).CopyTo(payload, 12);

        Assert.Throws<InvalidDataException>(() =>
            RemoteMessageCodec.DecodeVideoFrame(payload));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FrameDecodersRejectDimensionsAbovePixelBudget(bool videoFrame)
    {
        int headerLength = videoFrame
            ? RemoteMessageCodec.VideoFrameHeaderLength
            : RemoteMessageCodec.FrameHeaderLength;
        byte[] payload = new byte[headerLength + 1];
        if (videoFrame)
        {
            BitConverter.GetBytes((int)RemoteFrameEncoding.Jpeg).CopyTo(payload, 0);
            BitConverter.GetBytes(8192).CopyTo(payload, 4);
            BitConverter.GetBytes(8192).CopyTo(payload, 8);
            BitConverter.GetBytes((int)RemoteFrameFlags.KeyFrame).CopyTo(payload, 12);
        }
        else
        {
            BitConverter.GetBytes(8192).CopyTo(payload, 0);
            BitConverter.GetBytes(8192).CopyTo(payload, 4);
        }

        Assert.Throws<InvalidDataException>(() =>
        {
            if (videoFrame)
            {
                RemoteMessageCodec.DecodeVideoFrame(payload);
            }
            else
            {
                RemoteMessageCodec.DecodeFrame(payload);
            }
        });
    }

    [Fact]
    public void WriteVideoFrameHeaderUsesExposedWireLayoutAndRoundTrips()
    {
        Assert.Equal(32, RemoteMessageCodec.VideoFrameHeaderLength);
        byte[] payload = new byte[
            RemoteMessageCodec.VideoFrameHeaderLength + 3];
        RemoteMessageCodec.WriteVideoFrameHeader(
            payload.AsSpan(0, RemoteMessageCodec.VideoFrameHeaderLength),
            width: 1920,
            height: 1080,
            RemoteFrameEncoding.H264AnnexB,
            RemoteFrameFlags.KeyFrame | RemoteFrameFlags.CodecConfig,
            captureMilliseconds: 1.25,
            encodeMilliseconds: 2.5);
        new byte[] { 0x00, 0x00, 0x01 }.CopyTo(
            payload,
            RemoteMessageCodec.VideoFrameHeaderLength);

        RemoteFrame frame = RemoteMessageCodec.DecodeVideoFrame(payload);

        Assert.Equal(1920, frame.Width);
        Assert.Equal(1080, frame.Height);
        Assert.Equal(RemoteFrameEncoding.H264AnnexB, frame.Encoding);
        Assert.Equal(
            RemoteFrameFlags.KeyFrame | RemoteFrameFlags.CodecConfig,
            frame.Flags);
        Assert.Equal(1.25, frame.CaptureMilliseconds);
        Assert.Equal(2.5, frame.EncodeMilliseconds);
        Assert.Equal(
            new byte[] { 0x00, 0x00, 0x01 },
            frame.EncodedBuffer.AsSpan(
                frame.EncodedOffset,
                frame.EncodedLength).ToArray());
    }

    [Fact]
    public void WriteVideoFrameHeaderRejectsShortDestination()
    {
        Assert.Throws<ArgumentException>(() =>
            RemoteMessageCodec.WriteVideoFrameHeader(
                new byte[RemoteMessageCodec.VideoFrameHeaderLength - 1],
                width: 1920,
                height: 1080,
                RemoteFrameEncoding.H264AnnexB,
                RemoteFrameFlags.KeyFrame,
                captureMilliseconds: 1,
                encodeMilliseconds: 2));
    }

    [Fact]
    public void FrameWritersRejectDimensionsAbovePixelBudget()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RemoteMessageCodec.WriteFrameHeader(
                new byte[RemoteMessageCodec.FrameHeaderLength],
                width: 8192,
                height: 8192,
                captureMilliseconds: 0,
                encodeMilliseconds: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RemoteMessageCodec.WriteVideoFrameHeader(
                new byte[RemoteMessageCodec.VideoFrameHeaderLength],
                width: 8192,
                height: 8192,
                RemoteFrameEncoding.H264AnnexB,
                RemoteFrameFlags.KeyFrame,
                captureMilliseconds: 0,
                encodeMilliseconds: 0));
    }

    [Fact]
    public void EncodeInputRoundTripsTextInput()
    {
        const int codePoint = 0x4F60;
        RemoteInputCommand command = RemoteInputCommand.TextInput(codePoint);

        RemoteInputCommand decoded = RemoteMessageCodec.DecodeInput(RemoteMessageCodec.EncodeInput(command));

        Assert.Equal(RemoteInputKind.TextInput, decoded.Kind);
        Assert.Equal(codePoint, decoded.Data);
    }

    [Fact]
    public void EncodeInputRoundTripsPhysicalKeyboardMetadata()
    {
        RemoteInputCommand command =
            RemoteInputCommand.KeyDown(
                virtualKey: 0xA3,
                scanCode: 0x1D,
                RemoteKeyboardFlags.HasScanCode |
                RemoteKeyboardFlags.Extended);

        RemoteInputCommand decoded =
            RemoteMessageCodec.DecodeInput(
                RemoteMessageCodec.EncodeInput(command));

        Assert.Equal(command, decoded);
    }

    [Fact]
    public void WriteInputPayloadMatchesAllocatedEncoder()
    {
        RemoteInputCommand command = RemoteInputCommand.MouseWheel(-120, 300, 400);
        byte[] expected = RemoteMessageCodec.EncodeInput(command);
        byte[] destination = new byte[RemoteMessageCodec.InputPayloadLength];

        RemoteMessageCodec.WriteInputPayload(command, destination);

        Assert.Equal(expected, destination);
    }

    [Fact]
    public void WriteInputPayloadRejectsWrongSizedDestination()
    {
        Assert.Throws<ArgumentException>(() =>
            RemoteMessageCodec.WriteInputPayload(
                RemoteInputCommand.KeyDown(65),
                new byte[RemoteMessageCodec.InputPayloadLength - 1]));
    }

    [Fact]
    public void FileTransferKeepsCompatibleReceiveLimitWithSmallerRecommendedSendChunks()
    {
        Assert.Equal(128 * 1024, RemoteMessageCodec.FileTransferChunkBytes);
        Assert.Equal(32 * 1024, RemoteMessageCodec.RecommendedFileTransferChunkBytes);
        Assert.True(
            RemoteMessageCodec.RecommendedFileTransferChunkBytes <
            RemoteMessageCodec.FileTransferChunkBytes);
    }

    [Fact]
    public async Task WriteInputMessagesAsyncWritesReadableInputBatch()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var senderClient = new TcpClient();
        Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
        await senderClient.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        using TcpClient receiverClient = await acceptTask.WaitAsync(timeout.Token);
        NetworkUtils.ConfigureLowLatencyTcpClient(senderClient, 32 * 1024, 32 * 1024);
        NetworkUtils.ConfigureLowLatencyTcpClient(receiverClient, 32 * 1024, 32 * 1024);

        await using NetworkStream sendStream = senderClient.GetStream();
        await using NetworkStream receiveStream = receiverClient.GetStream();
        using SecureSession writerSession = CreateClientSession();
        using SecureSession readerSession = CreateServerSession();
        using var writeLock = new SemaphoreSlim(1, 1);
        RemoteInputCommand[] commands =
        [
            RemoteInputCommand.MouseMove(30, 40),
            RemoteInputCommand.MouseDown(RemoteMouseButton.Left, 30, 40),
            RemoteInputCommand.KeyUp(65)
        ];

        Task<RemoteInputCommand[]> readTask = Task.Run(async () =>
        {
            var inputs = new RemoteInputCommand[commands.Length];
            for (int index = 0; index < inputs.Length; index++)
            {
                ProtocolMessage message = await Protocol.ReadMessageAsync(receiveStream, readerSession, timeout.Token);
                Assert.Equal(MessageType.Input, message.Type);
                inputs[index] = RemoteMessageCodec.DecodeInput(message.PayloadSpan);
            }

            return inputs;
        }, timeout.Token);

        await Protocol.WriteInputMessagesAsync(
            sendStream,
            commands,
            writerSession,
            writeLock,
            timeout.Token);

        Assert.Equal(commands, await readTask.WaitAsync(timeout.Token));
    }

    [Fact]
    public async Task WriteInputMessagesAsyncLateFilterPreservesReliableCommands()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener =
            new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port =
            ((IPEndPoint)listener.LocalEndpoint).Port;

        using var senderClient = new TcpClient();
        Task<TcpClient> acceptTask =
            listener.AcceptTcpClientAsync(timeout.Token)
                .AsTask();
        await senderClient.ConnectAsync(
            IPAddress.Loopback,
            port,
            timeout.Token);
        using TcpClient receiverClient =
            await acceptTask.WaitAsync(timeout.Token);

        await using NetworkStream sendStream =
            senderClient.GetStream();
        await using NetworkStream receiveStream =
            receiverClient.GetStream();
        using SecureSession writerSession =
            CreateClientSession();
        using SecureSession readerSession =
            CreateServerSession();
        using var writeLock = new SemaphoreSlim(0, 1);
        RemoteInputCommand[] commands =
        [
            RemoteInputCommand.MouseMove(30, 40),
            RemoteInputCommand.MouseDown(
                RemoteMouseButton.Left,
                30,
                40),
            RemoteInputCommand.KeyUp(65),
            RemoteInputCommand.MouseWheel(120, 30, 40)
        ];
        RemoteInputCommand[] expected =
        [
            commands[1],
            commands[2],
            commands[3]
        ];
        bool allowMouseMove = true;
        int filterCalls = 0;

        Task writeTask =
            Protocol.WriteInputMessagesAsync(
                sendStream,
                commands,
                writerSession,
                writeLock,
                timeout.Token,
                command =>
                {
                    Interlocked.Increment(
                        ref filterCalls);
                    return command.Kind !=
                            RemoteInputKind.MouseMove ||
                        allowMouseMove;
                });

        Assert.False(writeTask.IsCompleted);
        Assert.Equal(0, Volatile.Read(ref filterCalls));
        allowMouseMove = false;
        writeLock.Release();

        var actual =
            new List<RemoteInputCommand>();
        for (int index = 0;
            index < expected.Length;
            index++)
        {
            ProtocolMessage message =
                await Protocol.ReadMessageAsync(
                    receiveStream,
                    readerSession,
                    timeout.Token);
            Assert.Equal(
                MessageType.Input,
                message.Type);
            actual.Add(
                RemoteMessageCodec.DecodeInput(
                    message.PayloadSpan));
        }

        await writeTask.WaitAsync(timeout.Token);
        Assert.Equal(commands.Length, filterCalls);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task WriteInputMessagesAsyncWritesNothingWhenLateFilterRejectsAll()
    {
        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener =
            new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port =
            ((IPEndPoint)listener.LocalEndpoint).Port;

        using var senderClient = new TcpClient();
        Task<TcpClient> acceptTask =
            listener.AcceptTcpClientAsync(timeout.Token)
                .AsTask();
        await senderClient.ConnectAsync(
            IPAddress.Loopback,
            port,
            timeout.Token);
        using TcpClient receiverClient =
            await acceptTask.WaitAsync(timeout.Token);

        await using var sendStream =
            new CountingNetworkStream(
                senderClient.Client);
        using SecureSession writerSession =
            CreateClientSession();
        using var writeLock =
            new SemaphoreSlim(1, 1);

        await Protocol.WriteInputMessagesAsync(
            sendStream,
            [
                RemoteInputCommand.MouseMove(10, 20),
                RemoteInputCommand.MouseMove(30, 40)
            ],
            writerSession,
            writeLock,
            timeout.Token,
            _ => false);

        Assert.Equal(0, sendStream.WriteCallCount);
        Assert.Equal(0, senderClient.Available);
        Assert.Equal(0, receiverClient.Available);
    }

    [Fact]
    public void SecureSessionRequiresEncryptedMessagesToArriveInSendOrder()
    {
        byte[] firstPlain = [1, 0, 0, 0, 0];
        byte[] secondPlain = [3, 1, 0, 0, 0, (byte)RemoteControlKind.ClipboardGetText];

        using var sender = CreateClientSession();
        byte[] firstEncrypted = sender.Encrypt(firstPlain);
        byte[] secondEncrypted = sender.Encrypt(secondPlain);

        using (SecureSession outOfOrderReceiver = CreateServerSession())
        {
            Assert.ThrowsAny<CryptographicException>(() => outOfOrderReceiver.Decrypt(secondEncrypted));
        }

        using SecureSession receiver = CreateServerSession();
        Assert.Equal(firstPlain, receiver.Decrypt(firstEncrypted));
        Assert.Equal(secondPlain, receiver.Decrypt(secondEncrypted));
    }

    [Fact]
    public async Task WriteMessageAsyncWritesLargeEncryptedPacketWithOneNetworkWrite()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var senderClient = new TcpClient();
        Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
        await senderClient.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        using TcpClient receiverClient = await acceptTask.WaitAsync(timeout.Token);

        await using var sendStream = new CountingNetworkStream(senderClient.Client);
        await using NetworkStream receiveStream = receiverClient.GetStream();
        using SecureSession writerSession = CreateClientSession();
        using SecureSession readerSession = CreateServerSession();
        using var writeLock = new SemaphoreSlim(1, 1);
        byte[] payload = Enumerable.Range(0, 64 * 1024)
            .Select(index => (byte)(index % 251))
            .ToArray();

        Task<ProtocolMessage> readTask =
            Protocol.ReadMessageAsync(receiveStream, readerSession, timeout.Token);
        await Protocol.WriteMessageAsync(
            sendStream,
            MessageType.Control,
            payload,
            writerSession,
            writeLock,
            timeout.Token);

        ProtocolMessage received = await readTask.WaitAsync(timeout.Token);
        Assert.Equal(1, sendStream.WriteCallCount);
        Assert.Equal(payload.Length + 25, sendStream.LastWriteLength);
        Assert.Equal(MessageType.Control, received.Type);
        Assert.Equal(payload, received.PayloadMemory.ToArray());
    }

    [Fact]
    public async Task WriteVideoFrameMessageAsyncWritesReadableVideoFrameWithOneNetworkWrite()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var senderClient = new TcpClient();
        Task<TcpClient> acceptTask =
            listener.AcceptTcpClientAsync(timeout.Token).AsTask();
        await senderClient.ConnectAsync(
            IPAddress.Loopback,
            port,
            timeout.Token);
        using TcpClient receiverClient =
            await acceptTask.WaitAsync(timeout.Token);

        await using var sendStream =
            new CountingNetworkStream(senderClient.Client);
        await using NetworkStream receiveStream = receiverClient.GetStream();
        using SecureSession writerSession = CreateClientSession();
        using SecureSession readerSession = CreateServerSession();
        using var writeLock = new SemaphoreSlim(1, 1);
        byte[] encodedBytes =
        [
            0x00, 0x00, 0x00, 0x01,
            0x67, 0x64, 0x00, 0x29
        ];

        Task<ProtocolMessage> readTask =
            Protocol.ReadMessageAsync(
                receiveStream,
                readerSession,
                timeout.Token);
        ProtocolFrameWriteTimings timings = await Protocol.WriteVideoFrameMessageAsync(
            sendStream,
            width: 1920,
            height: 1080,
            RemoteFrameEncoding.H264AnnexB,
            RemoteFrameFlags.KeyFrame | RemoteFrameFlags.CodecConfig,
            captureMilliseconds: 1.5,
            encodeMilliseconds: 2.75,
            encodedBytes,
            writerSession,
            writeLock,
            timeout.Token);

        ProtocolMessage message = await readTask.WaitAsync(timeout.Token);
        Assert.True(timings.PreparationMilliseconds >= 0);
        Assert.True(timings.LockWaitMilliseconds >= 0);
        Assert.True(timings.EncryptionMilliseconds >= 0);
        Assert.True(timings.SocketWriteMilliseconds >= 0);
        Assert.Equal(1, sendStream.WriteCallCount);
        Assert.Equal(
            encodedBytes.Length +
                RemoteMessageCodec.VideoFrameHeaderLength +
                25,
            sendStream.LastWriteLength);
        Assert.Equal(MessageType.VideoFrame, message.Type);

        RemoteFrame frame =
            RemoteMessageCodec.DecodeVideoFrame(message.PayloadMemory);
        Assert.Equal(1920, frame.Width);
        Assert.Equal(1080, frame.Height);
        Assert.Equal(RemoteFrameEncoding.H264AnnexB, frame.Encoding);
        Assert.Equal(
            RemoteFrameFlags.KeyFrame | RemoteFrameFlags.CodecConfig,
            frame.Flags);
        Assert.Equal(1.5, frame.CaptureMilliseconds);
        Assert.Equal(2.75, frame.EncodeMilliseconds);
        Assert.Equal(
            encodedBytes,
            frame.EncodedBuffer.AsSpan(
                frame.EncodedOffset,
                frame.EncodedLength).ToArray());
    }

    [Fact]
    public async Task WriteVideoFrameMessageAsyncRejectsOversizedPayloadBeforeWriting()
    {
        const int maxFramePayloadBytes = 32 * 1024 * 1024;
        byte[] encodedBytes = new byte[
            maxFramePayloadBytes -
            RemoteMessageCodec.VideoFrameHeaderLength +
            1];

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Protocol.WriteVideoFrameMessageAsync(
                null!,
                width: 1920,
                height: 1080,
                RemoteFrameEncoding.H264AnnexB,
                RemoteFrameFlags.KeyFrame,
                captureMilliseconds: 1,
                encodeMilliseconds: 2,
                encodedBytes,
                null!,
                null!,
                CancellationToken.None));
    }

    [Fact]
    public async Task FrameWriteTimingsSeparateSharedLockWaitFromSocketWrite()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var sender = new TcpClient();
        Task<TcpClient> accepting = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
        await sender.ConnectAsync(IPAddress.Loopback,
            ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token);
        using TcpClient receiver = await accepting;
        using SecureSession writerSession = CreateClientSession();
        using SecureSession readerSession = CreateServerSession();
        using var writeLock = new SemaphoreSlim(0, 1);
        Task<ProtocolMessage> read = Protocol.ReadMessageAsync(
            receiver.GetStream(), readerSession, timeout.Token);
        Task<ProtocolFrameWriteTimings> write = Protocol.WriteFrameMessageAsync(
            sender.GetStream(), 1, 1, 0, 0, new byte[] { 1, 2, 3 },
            writerSession, writeLock, timeout.Token);
        // The async method runs synchronously up to the unavailable write lock.
        Assert.False(write.IsCompleted);
        await Task.Delay(80, timeout.Token);
        writeLock.Release();
        ProtocolFrameWriteTimings timings = await write.WaitAsync(timeout.Token);
        Assert.True(timings.LockWaitMilliseconds >= 50);
        Assert.Equal(1, writeLock.CurrentCount);
        Assert.Equal(MessageType.Frame, (await read.WaitAsync(timeout.Token)).Type);
    }

    [Fact]
    public async Task CancelledFrameWaitingForWriteLockDoesNotReleaseAnotherOwnersLock()
    {
        using var cancellation = new CancellationTokenSource();
        using var writeLock = new SemaphoreSlim(0, 1);
        Task<ProtocolFrameWriteTimings> write = Protocol.WriteFrameMessageAsync(
            null!, 1, 1, 0, 0, new byte[] { 1, 2, 3 },
            null!, writeLock, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        Assert.Equal(0, writeLock.CurrentCount);
    }

    [Fact]
    public void FrameWriteTimingWindowReportsOnlyRecordedTcpWritesAndResets()
    {
        var window = new ProtocolFrameWriteTimingsWindow();
        Assert.Equal(string.Empty, window.DescribeAverage());
        window.Record(new ProtocolFrameWriteTimings(1, 2, 3, 4));
        window.Record(new ProtocolFrameWriteTimings(3, 4, 5, 6));
        string summary = window.DescribeAverage();
        Assert.Contains("TCP 本地均值(2帧)", summary);
        Assert.Contains($"组包 {2d:F2}ms", summary);
        Assert.Contains($"等锁 {3d:F2}ms", summary);
        Assert.Contains($"加密 {4d:F2}ms", summary);
        Assert.Contains($"写入 {5d:F2}ms", summary);
        Assert.Contains("非RTT", summary);
        window.Reset();
        Assert.Equal(string.Empty, window.DescribeAverage());
    }

    [Fact]
    public async Task ConcurrentProtocolFrameAndControlWritesRemainReadable()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var senderClient = new TcpClient();
        Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
        await senderClient.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        using TcpClient receiverClient = await acceptTask.WaitAsync(timeout.Token);
        NetworkUtils.ConfigureLowLatencyTcpClient(senderClient, 256 * 1024, 256 * 1024);
        NetworkUtils.ConfigureLowLatencyTcpClient(receiverClient, 256 * 1024, 256 * 1024);

        await using NetworkStream sendStream = senderClient.GetStream();
        await using NetworkStream receiveStream = receiverClient.GetStream();
        using SecureSession writerSession = CreateClientSession();
        using SecureSession readerSession = CreateServerSession();
        using var writeLock = new SemaphoreSlim(1, 1);
        byte[] jpegBytes = Enumerable.Range(0, 128 * 1024)
            .Select(index => (byte)(index % 251))
            .ToArray();

        Task<ProtocolMessage[]> readTask = Task.Run(async () =>
        {
            ProtocolMessage first = await Protocol.ReadMessageAsync(receiveStream, readerSession, timeout.Token);
            ProtocolMessage second = await Protocol.ReadMessageAsync(receiveStream, readerSession, timeout.Token);
            return new[] { first, second };
        }, timeout.Token);

        Task frameTask = Task.Run(
            () => Protocol.WriteFrameMessageAsync(
                sendStream,
                width: 640,
                height: 360,
                captureMilliseconds: 2.5,
                encodeMilliseconds: 3.5,
                jpegBytes,
                writerSession,
                writeLock,
                timeout.Token),
            timeout.Token);
        Task controlTask = Task.Run(
            () => Protocol.WriteMessageAsync(
                sendStream,
                MessageType.Control,
                RemoteMessageCodec.EncodeClipboardStatus(true, "concurrent control"),
                writerSession,
                writeLock,
                timeout.Token),
            timeout.Token);

        await Task.WhenAll(frameTask, controlTask).WaitAsync(timeout.Token);

        ProtocolMessage[] messages = await readTask.WaitAsync(timeout.Token);
        Assert.Contains(messages, message => message.Type == MessageType.Frame);
        Assert.Contains(messages, message => message.Type == MessageType.Control);
    }

    private static SecureSession CreateClientSession()
    {
        return new SecureSession(CreateKey(seed: 11), CreateKey(seed: 97), isServer: false);
    }

    private static SecureSession CreateServerSession()
    {
        return new SecureSession(CreateKey(seed: 11), CreateKey(seed: 97), isServer: true);
    }

    private static byte[] CreateKey(byte seed)
    {
        byte[] key = new byte[32];
        for (int index = 0; index < key.Length; index++)
        {
            key[index] = (byte)(seed + index);
        }

        return key;
    }

    private sealed class CountingNetworkStream : NetworkStream
    {
        public CountingNetworkStream(Socket socket)
            : base(socket, ownsSocket: false)
        {
        }

        public int WriteCallCount { get; private set; }

        public int LastWriteLength { get; private set; }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            WriteCallCount++;
            LastWriteLength = buffer.Length;
            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    private static byte[] CreateFileTransferChunkPayload(string transferId, long offset, byte[] bytes)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8);
        writer.Write((byte)RemoteControlKind.FileTransferChunk);
        writer.Write(transferId);
        writer.Write(offset);
        writer.Write(bytes.Length);
        writer.Write(bytes);
        return output.ToArray();
    }

    private static byte[] CreateFileTransferChunkPayload(long offset, byte[] bytes) =>
        CreateFileTransferChunkPayload("transfer-1", offset, bytes);
}
