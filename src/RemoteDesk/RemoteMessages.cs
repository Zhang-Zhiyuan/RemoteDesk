using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace RemoteDesk;

internal enum RemoteInputKind : byte
{
    MouseMove = 1,
    MouseDown = 2,
    MouseUp = 3,
    MouseWheel = 4,
    KeyDown = 5,
    KeyUp = 6,
    TextInput = 7,
    PinchZoom = 8
}

internal enum RemoteMouseButton : byte
{
    None = 0,
    Left = 1,
    Right = 2,
    Middle = 3
}

[Flags]
internal enum RemoteKeyboardFlags
{
    None = 0,
    HasScanCode = 1 << 0,
    Extended = 1 << 1
}

internal readonly record struct RemotePhysicalKey(
    int VirtualKey,
    int ScanCode,
    RemoteKeyboardFlags Flags);

[Flags]
internal enum RemoteVideoCodecs
{
    None = 0,
    Jpeg = 1 << 0,
    H264AnnexB = 1 << 1
}

internal enum RemoteFrameEncoding : byte
{
    Jpeg = 1,
    H264AnnexB = 2
}

[Flags]
internal enum RemoteFrameFlags
{
    None = 0,
    KeyFrame = 1 << 0,
    CodecConfig = 1 << 1
}

internal readonly record struct RemoteFrame(
    int Width,
    int Height,
    RemoteFrameEncoding Encoding,
    RemoteFrameFlags Flags,
    byte[] EncodedBuffer,
    int EncodedOffset,
    int EncodedLength,
    double CaptureMilliseconds,
    double EncodeMilliseconds,
    long ReceivedAtTimestamp = 0);

internal readonly record struct RemoteFrameMetadata(
    int Width,
    int Height,
    RemoteFrameEncoding Encoding,
    RemoteFrameFlags Flags,
    int EncodedLength,
    double CaptureMilliseconds,
    double EncodeMilliseconds,
    long ReceivedAtTimestamp)
{
    public static RemoteFrameMetadata FromFrame(
        RemoteFrame frame) =>
        new(
            frame.Width,
            frame.Height,
            frame.Encoding,
            frame.Flags,
            frame.EncodedLength,
            frame.CaptureMilliseconds,
            frame.EncodeMilliseconds,
            frame.ReceivedAtTimestamp);
}

internal enum RemoteControlKind : byte
{
    CaptureTargetList = 1,
    SelectCaptureTarget = 2,
    CaptureTargetChanged = 3,
    ClipboardGetText = 4,
    ClipboardSetText = 5,
    ClipboardText = 6,
    ClipboardStatus = 7,
    FileTransferStart = 8,
    FileTransferChunk = 9,
    FileTransferComplete = 10,
    FileTransferStatus = 11,
    DeviceInfo = 12,
    ViewerInfo = 13,
    VideoKeyFrameRequest = 14,
    FileDropPasteBegin = 15,
    FileDropPasteCommit = 16,
    FileDropPasteCancel = 17,
    FileTransferRequestClipboardFiles = 18,
    FileTransferChecksum = 19,
    ViewerCapabilities = 20,
    FileTransferCancel = 21,
    FileTransferClipboardFilesPreview = 22,
    FileTransferConfirmClipboardFiles = 23,
    FileTransferRejectClipboardFiles = 24,
    RemoteUpdateStart = 25,
    RemoteUpdatePackageRequest = 26,
    DeviceBuildInfo = 27,
    LowLatencyVideoOffer = 28,
    LowLatencyVideoReady = 29,
    LowLatencyVideoStop = 30,
    LowLatencyVideoStopped = 31,
    SessionRejected = 32,
    DeviceIdentityRequest = 33,
    DeviceIdentity = 34,
    FileTransferReceipt = 35,
    HostVideoDiagnosticsRequest = 36,
    HostVideoDiagnostics = 37
}

internal sealed record LowLatencyVideoOffer(
    int Port,
    int MaxDatagramBytes,
    int MaxFrameBytes,
    ulong ChannelId,
    uint Epoch,
    byte[] HostToViewerKey,
    byte[] ViewerToHostKey,
    byte[] HostNoncePrefix,
    byte[] ViewerNoncePrefix,
    byte[] Challenge);

internal sealed record CaptureTargetInfo(string Id, string DisplayName)
{
    public override string ToString() => DisplayName;
}

internal sealed record RemoteControlMessage(
    RemoteControlKind Kind,
    IReadOnlyList<CaptureTargetInfo> Targets,
    string? TargetId,
    string? DisplayName,
    string? Text = null,
    bool Success = false,
    string? StatusMessage = null,
    string? TransferId = null,
    string? FileName = null,
    long FileLength = 0,
    long FileOffset = 0,
    ReadOnlyMemory<byte> FileBytes = default,
    string? MachineName = null,
    string? Platform = null,
    RemoteDeviceCapabilities Capabilities = RemoteDeviceCapabilities.None,
    RemoteVideoCodecs SupportedVideoCodecs = RemoteVideoCodecs.None,
    string? ChecksumAlgorithm = null,
    string? ChecksumHex = null,
    string? BuildStamp = null,
    IReadOnlyList<FileTransferConfirmationItem>? FileTransferPreviewItems = null,
    LowLatencyVideoOffer? LowLatencyVideoOffer = null,
    ulong LowLatencyVideoChannelId = 0,
    uint LowLatencyVideoEpoch = 0,
    byte LowLatencyVideoStopReason = 0);

internal readonly record struct RemoteInputCommand(
    RemoteInputKind Kind,
    RemoteMouseButton Button,
    int X,
    int Y,
    int Data)
{
    public static RemoteInputCommand MouseMove(int x, int y) =>
        new(RemoteInputKind.MouseMove, RemoteMouseButton.None, x, y, 0);

    public static RemoteInputCommand MouseDown(RemoteMouseButton button, int x, int y) =>
        new(RemoteInputKind.MouseDown, button, x, y, 0);

    public static RemoteInputCommand MouseUp(RemoteMouseButton button, int x, int y) =>
        new(RemoteInputKind.MouseUp, button, x, y, 0);

    public static RemoteInputCommand MouseWheel(int delta, int x, int y) =>
        new(RemoteInputKind.MouseWheel, RemoteMouseButton.None, x, y, delta);

    public static RemoteInputCommand KeyDown(int virtualKey) =>
        new(RemoteInputKind.KeyDown, RemoteMouseButton.None, 0, 0, virtualKey);

    public static RemoteInputCommand KeyDown(
        int virtualKey,
        int scanCode,
        RemoteKeyboardFlags flags) =>
        new(
            RemoteInputKind.KeyDown,
            RemoteMouseButton.None,
            scanCode,
            (int)flags,
            virtualKey);

    public static RemoteInputCommand KeyUp(int virtualKey) =>
        new(RemoteInputKind.KeyUp, RemoteMouseButton.None, 0, 0, virtualKey);

    public static RemoteInputCommand KeyUp(
        int virtualKey,
        int scanCode,
        RemoteKeyboardFlags flags) =>
        new(
            RemoteInputKind.KeyUp,
            RemoteMouseButton.None,
            scanCode,
            (int)flags,
            virtualKey);

    public static RemoteInputCommand TextInput(int codePoint) =>
        new(RemoteInputKind.TextInput, RemoteMouseButton.None, 0, 0, codePoint);

    public static RemoteInputCommand PinchZoom(int delta, int x, int y) =>
        new(RemoteInputKind.PinchZoom, RemoteMouseButton.None, x, y, delta);
}

internal static class RemoteMessageCodec
{
    internal const int FrameHeaderLength = 24;
    internal const int VideoFrameHeaderLength = 32;
    internal const int InputPayloadLength = 14;
    internal const int FileTransferChunkBytes = 128 * 1024;
    internal const int RecommendedFileTransferChunkBytes = 32 * 1024;
    internal const long MaxFileTransferBytes = 1024L * 1024L * 1024L;
    internal const int MaxControlPayloadBytes = 2 * 1024 * 1024;
    internal const string FileTransferChecksumAlgorithm = "SHA256";
    internal const int MaxFrameDimension = 32_768;
    internal const long MaxFramePixels = 16_777_216;
    private const int MaxControlItems = 64;
    private const int MaxClipboardTextChars = 256_000;
    private const int MaxControlStringChars = 4_096;
    private const int Sha256HexLength = 64;

    public static byte[] EncodeFrame(int width, int height, byte[] jpegBytes)
    {
        byte[] payload = new byte[FrameHeaderLength + jpegBytes.Length];
        WriteFrameHeader(payload, width, height, 0, 0);
        Buffer.BlockCopy(jpegBytes, 0, payload, FrameHeaderLength, jpegBytes.Length);
        return payload;
    }

    public static void WriteFrameHeader(
        Span<byte> destination,
        int width,
        int height,
        double captureMilliseconds,
        double encodeMilliseconds)
    {
        if (destination.Length < FrameHeaderLength)
        {
            throw new ArgumentException(
                "画面帧头输出缓冲区长度异常。",
                nameof(destination));
        }
        if (!IsFrameDimensionsAllowed(width, height))
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                "画面帧尺寸超过协议安全上限。");
        }

        BinaryPrimitives.WriteInt32LittleEndian(destination, width);
        BinaryPrimitives.WriteInt32LittleEndian(destination[4..], height);
        BinaryPrimitives.WriteDoubleLittleEndian(
            destination[8..],
            captureMilliseconds);
        BinaryPrimitives.WriteDoubleLittleEndian(
            destination[16..],
            encodeMilliseconds);
    }

    public static void WriteVideoFrameHeader(
        Span<byte> destination,
        int width,
        int height,
        RemoteFrameEncoding encoding,
        RemoteFrameFlags flags,
        double captureMilliseconds,
        double encodeMilliseconds)
    {
        if (destination.Length < VideoFrameHeaderLength)
        {
            throw new ArgumentException(
                "视频帧头输出缓冲区长度异常。",
                nameof(destination));
        }
        if (!IsFrameDimensionsAllowed(width, height))
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                "视频帧尺寸超过协议安全上限。");
        }
        if (!Enum.IsDefined(encoding) ||
            (flags & ~(
                RemoteFrameFlags.KeyFrame |
                RemoteFrameFlags.CodecConfig)) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(encoding),
                "视频帧编码或标志不受支持。");
        }

        BinaryPrimitives.WriteInt32LittleEndian(
            destination,
            (int)encoding);
        BinaryPrimitives.WriteInt32LittleEndian(destination[4..], width);
        BinaryPrimitives.WriteInt32LittleEndian(destination[8..], height);
        BinaryPrimitives.WriteInt32LittleEndian(
            destination[12..],
            (int)flags);
        BinaryPrimitives.WriteDoubleLittleEndian(
            destination[16..],
            captureMilliseconds);
        BinaryPrimitives.WriteDoubleLittleEndian(
            destination[24..],
            encodeMilliseconds);
    }

    public static RemoteFrame DecodeFrame(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length < FrameHeaderLength)
        {
            throw new InvalidDataException("画面帧数据不完整。");
        }

        ReadOnlySpan<byte> payloadSpan = payload.Span;
        int width = BinaryPrimitives.ReadInt32LittleEndian(payloadSpan);
        int height = BinaryPrimitives.ReadInt32LittleEndian(payloadSpan[4..]);
        double captureMilliseconds = BinaryPrimitives.ReadDoubleLittleEndian(payloadSpan[8..]);
        double encodeMilliseconds = BinaryPrimitives.ReadDoubleLittleEndian(payloadSpan[16..]);
        int jpegLength = payload.Length - FrameHeaderLength;

        if (!IsFrameDimensionsAllowed(width, height) ||
            jpegLength <= 0)
        {
            throw new InvalidDataException("画面帧尺寸异常。");
        }

        if (MemoryMarshal.TryGetArray(payload, out ArraySegment<byte> segment) && segment.Array is not null)
        {
            return new RemoteFrame(
                width,
                height,
                RemoteFrameEncoding.Jpeg,
                RemoteFrameFlags.KeyFrame,
                segment.Array,
                segment.Offset + FrameHeaderLength,
                jpegLength,
                captureMilliseconds,
                encodeMilliseconds,
                Stopwatch.GetTimestamp());
        }

        byte[] jpegBytes = payload[FrameHeaderLength..].ToArray();
        return new RemoteFrame(
            width,
            height,
            RemoteFrameEncoding.Jpeg,
            RemoteFrameFlags.KeyFrame,
            jpegBytes,
            0,
            jpegBytes.Length,
            captureMilliseconds,
            encodeMilliseconds,
            Stopwatch.GetTimestamp());
    }

    public static RemoteFrame DecodeVideoFrame(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length < VideoFrameHeaderLength)
        {
            throw new InvalidDataException("视频帧数据不完整。");
        }

        ReadOnlySpan<byte> payloadSpan = payload.Span;
        var encoding = (RemoteFrameEncoding)BinaryPrimitives.ReadInt32LittleEndian(payloadSpan);
        int width = BinaryPrimitives.ReadInt32LittleEndian(payloadSpan[4..]);
        int height = BinaryPrimitives.ReadInt32LittleEndian(payloadSpan[8..]);
        var flags = (RemoteFrameFlags)BinaryPrimitives.ReadInt32LittleEndian(payloadSpan[12..]);
        double captureMilliseconds = BinaryPrimitives.ReadDoubleLittleEndian(payloadSpan[16..]);
        double encodeMilliseconds = BinaryPrimitives.ReadDoubleLittleEndian(payloadSpan[24..]);
        int encodedLength = payload.Length - VideoFrameHeaderLength;

        if (!Enum.IsDefined(encoding) ||
            (flags & ~(RemoteFrameFlags.KeyFrame | RemoteFrameFlags.CodecConfig)) != 0 ||
            !IsFrameDimensionsAllowed(width, height) ||
            encodedLength <= 0)
        {
            throw new InvalidDataException("视频帧格式异常。");
        }

        if (MemoryMarshal.TryGetArray(payload, out ArraySegment<byte> segment) && segment.Array is not null)
        {
            return new RemoteFrame(
                width,
                height,
                encoding,
                flags,
                segment.Array,
                segment.Offset + VideoFrameHeaderLength,
                encodedLength,
                captureMilliseconds,
                encodeMilliseconds,
                Stopwatch.GetTimestamp());
        }

        byte[] encodedBytes = payload[VideoFrameHeaderLength..].ToArray();
        return new RemoteFrame(
            width,
            height,
            encoding,
            flags,
            encodedBytes,
            0,
            encodedBytes.Length,
            captureMilliseconds,
            encodeMilliseconds,
            Stopwatch.GetTimestamp());
    }

    internal static bool IsFrameDimensionsAllowed(int width, int height) =>
        width > 0 &&
        height > 0 &&
        width <= MaxFrameDimension &&
        height <= MaxFrameDimension &&
        (long)width * height <= MaxFramePixels;

    public static byte[] EncodeInput(RemoteInputCommand command)
    {
        byte[] payload = new byte[InputPayloadLength];
        WriteInputPayload(command, payload);
        return payload;
    }

    public static void WriteInputPayload(RemoteInputCommand command, Span<byte> destination)
    {
        if (destination.Length != InputPayloadLength)
        {
            throw new ArgumentException("输入命令输出缓冲区长度异常。", nameof(destination));
        }

        destination[0] = (byte)command.Kind;
        destination[1] = (byte)command.Button;
        BinaryPrimitives.WriteInt32LittleEndian(destination[2..], command.X);
        BinaryPrimitives.WriteInt32LittleEndian(destination[6..], command.Y);
        BinaryPrimitives.WriteInt32LittleEndian(destination[10..], command.Data);
    }

    public static RemoteInputCommand DecodeInput(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != InputPayloadLength)
        {
            throw new InvalidDataException("输入命令数据长度异常。");
        }

        var kind = (RemoteInputKind)payload[0];
        var button = (RemoteMouseButton)payload[1];
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(button))
        {
            throw new InvalidDataException("输入命令类型异常。");
        }

        return new RemoteInputCommand(
            kind,
            button,
            BinaryPrimitives.ReadInt32LittleEndian(payload[2..]),
            BinaryPrimitives.ReadInt32LittleEndian(payload[6..]),
            BinaryPrimitives.ReadInt32LittleEndian(payload[10..]));
    }

    public static byte[] EncodeCaptureTargetList(IEnumerable<CaptureTargetInfo> targets)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8);
        writer.Write((byte)RemoteControlKind.CaptureTargetList);

        CaptureTargetInfo[] targetArray = targets.Take(MaxControlItems).ToArray();
        writer.Write(targetArray.Length);
        foreach (CaptureTargetInfo target in targetArray)
        {
            WriteBoundedString(writer, target.Id, "capture-target");
            WriteBoundedString(writer, target.DisplayName, "屏幕");
        }

        return output.ToArray();
    }

    public static byte[] EncodeDeviceInfo(RemoteDeviceDescriptor device)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8);
        writer.Write((byte)RemoteControlKind.DeviceInfo);
        WriteBoundedString(writer, device.MachineName, "RemoteDesk");
        WriteBoundedString(writer, device.Platform, RemoteDevicePlatforms.Unknown);
        writer.Write((int)device.Capabilities);
        return output.ToArray();
    }

    public static byte[] EncodeDeviceBuildInfo(string? buildStamp)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8);
        writer.Write((byte)RemoteControlKind.DeviceBuildInfo);
        WriteBoundedString(writer, RemoteDeskBuildInfo.NormalizeBuildStamp(buildStamp) ?? string.Empty, string.Empty);
        return output.ToArray();
    }

    public static byte[] EncodeDeviceIdentityRequest() => [(byte)RemoteControlKind.DeviceIdentityRequest];

    public static byte[] EncodeHostVideoDiagnosticsRequest() => [(byte)RemoteControlKind.HostVideoDiagnosticsRequest];

    public static byte[] EncodeHostVideoDiagnostics(string text)
    {
        if (text.Length > HostVideoDiagnostics.MaximumCharacters) throw new ArgumentException("Video diagnostic exceeds limit.");
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8);
        writer.Write((byte)RemoteControlKind.HostVideoDiagnostics);
        writer.Write(text);
        return output.ToArray();
    }

    public static byte[] EncodeDeviceIdentity(string deviceId)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8);
        writer.Write((byte)RemoteControlKind.DeviceIdentity);
        writer.Write(RemoteDeviceIdentity.Normalize(deviceId) ?? throw new ArgumentException("Invalid device ID"));
        return output.ToArray();
    }

    public static byte[] EncodeViewerInfo(RemoteVideoCodecs supportedVideoCodecs)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8);
        writer.Write((byte)RemoteControlKind.ViewerInfo);
        writer.Write((int)(supportedVideoCodecs == RemoteVideoCodecs.None ? RemoteVideoCodecs.Jpeg : supportedVideoCodecs));
        return output.ToArray();
    }

    public static byte[] EncodeViewerCapabilities(RemoteDeviceCapabilities capabilities)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8);
        writer.Write((byte)RemoteControlKind.ViewerCapabilities);
        writer.Write((int)capabilities);
        return output.ToArray();
    }

    public static byte[] EncodeLowLatencyVideoOffer(LowLatencyVideoOffer offer)
    {
        LowLatencyVideoProtocol.ValidateOffer(offer);
        const int payloadLength =
            1 + 1 + 2 + 2 + 4 + 8 + 4 +
            (LowLatencyVideoProtocol.KeyLength * 2) +
            (LowLatencyVideoProtocol.NoncePrefixLength * 2) +
            LowLatencyVideoProtocol.ChallengeLength;
        byte[] payload = new byte[payloadLength];
        int offset = 0;
        payload[offset++] = (byte)RemoteControlKind.LowLatencyVideoOffer;
        payload[offset++] = LowLatencyVideoProtocol.Version;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset), checked((ushort)offer.Port));
        offset += sizeof(ushort);
        BinaryPrimitives.WriteUInt16LittleEndian(
            payload.AsSpan(offset),
            checked((ushort)offer.MaxDatagramBytes));
        offset += sizeof(ushort);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset), offer.MaxFrameBytes);
        offset += sizeof(int);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(offset), offer.ChannelId);
        offset += sizeof(ulong);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset), offer.Epoch);
        offset += sizeof(uint);
        offer.HostToViewerKey.CopyTo(payload, offset);
        offset += LowLatencyVideoProtocol.KeyLength;
        offer.ViewerToHostKey.CopyTo(payload, offset);
        offset += LowLatencyVideoProtocol.KeyLength;
        offer.HostNoncePrefix.CopyTo(payload, offset);
        offset += LowLatencyVideoProtocol.NoncePrefixLength;
        offer.ViewerNoncePrefix.CopyTo(payload, offset);
        offset += LowLatencyVideoProtocol.NoncePrefixLength;
        offer.Challenge.CopyTo(payload, offset);
        Debug.Assert(offset + LowLatencyVideoProtocol.ChallengeLength == payload.Length);
        return payload;
    }

    public static byte[] EncodeLowLatencyVideoReady(ulong channelId, uint epoch)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8);
        writer.Write((byte)RemoteControlKind.LowLatencyVideoReady);
        writer.Write(channelId);
        writer.Write(epoch);
        return output.ToArray();
    }

    public static byte[] EncodeLowLatencyVideoStop(ulong channelId, uint epoch, byte reason)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8);
        writer.Write((byte)RemoteControlKind.LowLatencyVideoStop);
        writer.Write(channelId);
        writer.Write(epoch);
        writer.Write(reason);
        return output.ToArray();
    }

    public static byte[] EncodeLowLatencyVideoStopped(ulong channelId, uint epoch, byte reason)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8);
        writer.Write((byte)RemoteControlKind.LowLatencyVideoStopped);
        writer.Write(channelId);
        writer.Write(epoch);
        writer.Write(reason);
        return output.ToArray();
    }

    public static byte[] EncodeVideoKeyFrameRequest()
    {
        return [(byte)RemoteControlKind.VideoKeyFrameRequest];
    }

    public static byte[] EncodeFileDropPasteBegin()
    {
        return [(byte)RemoteControlKind.FileDropPasteBegin];
    }

    public static byte[] EncodeFileDropPasteCommit()
    {
        return [(byte)RemoteControlKind.FileDropPasteCommit];
    }

    public static byte[] EncodeFileDropPasteCancel()
    {
        return [(byte)RemoteControlKind.FileDropPasteCancel];
    }

    public static byte[] EncodeFileTransferRequestClipboardFiles()
    {
        return [(byte)RemoteControlKind.FileTransferRequestClipboardFiles];
    }

    public static byte[] EncodeFileTransferConfirmClipboardFiles()
    {
        return [(byte)RemoteControlKind.FileTransferConfirmClipboardFiles];
    }

    public static byte[] EncodeFileTransferRejectClipboardFiles()
    {
        return [(byte)RemoteControlKind.FileTransferRejectClipboardFiles];
    }

    public static byte[] EncodeFileTransferClipboardFilesPreview(
        IEnumerable<FileTransferConfirmationItem> items,
        string? note)
    {
        ArgumentNullException.ThrowIfNull(items);
        FileTransferConfirmationItem[] itemArray = items.Take(MaxControlItems).ToArray();

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8);
        writer.Write((byte)RemoteControlKind.FileTransferClipboardFilesPreview);
        writer.Write(itemArray.Length);
        foreach (FileTransferConfirmationItem item in itemArray)
        {
            WriteBoundedString(writer, item.Kind, "文件");
            WriteBoundedString(writer, item.SourcePath, "unknown");
            WriteBoundedString(writer, item.TransferName, "file");
            writer.Write(Math.Max(0, item.SizeBytes));
            WriteBoundedString(writer, item.DestinationPath, "控制端接收目录");
        }

        WriteBoundedString(writer, note, string.Empty);
        return output.ToArray();
    }

    public static byte[] EncodeSelectCaptureTarget(string targetId)
    {
        ValidateControlString(targetId);

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8);
        writer.Write((byte)RemoteControlKind.SelectCaptureTarget);
        writer.Write(targetId);
        return output.ToArray();
    }

    public static byte[] EncodeCaptureTargetChanged(CaptureTargetInfo target)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8);
        writer.Write((byte)RemoteControlKind.CaptureTargetChanged);
        WriteBoundedString(writer, target.Id, "capture-target");
        WriteBoundedString(writer, target.DisplayName, "屏幕");
        return output.ToArray();
    }

    public static byte[] EncodeClipboardGetText()
    {
        return [(byte)RemoteControlKind.ClipboardGetText];
    }

    public static byte[] EncodeClipboardSetText(string text)
    {
        ValidateClipboardText(text);
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8);
        writer.Write((byte)RemoteControlKind.ClipboardSetText);
        writer.Write(text);
        return output.ToArray();
    }

    public static byte[] EncodeClipboardText(string text)
    {
        ValidateClipboardText(text);
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8);
        writer.Write((byte)RemoteControlKind.ClipboardText);
        writer.Write(text);
        return output.ToArray();
    }

    public static byte[] EncodeClipboardStatus(bool success, string message)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8);
        writer.Write((byte)RemoteControlKind.ClipboardStatus);
        writer.Write(success);
        WriteBoundedString(writer, message, "剪贴板操作已完成。");
        return output.ToArray();
    }

    public static byte[] EncodeFileTransferStart(string transferId, string fileName, long fileLength)
    {
        return EncodeFileTransferStart(RemoteControlKind.FileTransferStart, transferId, fileName, fileLength);
    }

    public static byte[] EncodeRemoteUpdateStart(string transferId, string fileName, long fileLength)
    {
        return EncodeFileTransferStart(RemoteControlKind.RemoteUpdateStart, transferId, fileName, fileLength);
    }

    public static byte[] EncodeRemoteUpdatePackageRequest()
    {
        return [(byte)RemoteControlKind.RemoteUpdatePackageRequest];
    }

    private static byte[] EncodeFileTransferStart(RemoteControlKind kind, string transferId, string fileName, long fileLength)
    {
        ValidateControlString(transferId);
        ValidateControlString(fileName);
        ValidateFileLength(fileLength);

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8);
        writer.Write((byte)kind);
        writer.Write(transferId);
        writer.Write(fileName);
        writer.Write(fileLength);
        return output.ToArray();
    }

    public static byte[] EncodeFileTransferChunk(string transferId, long offset, ReadOnlyMemory<byte> bytes)
    {
        ValidateFileTransferChunk(transferId, offset, bytes.Length);

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8);
        writer.Write((byte)RemoteControlKind.FileTransferChunk);
        writer.Write(transferId);
        writer.Write(offset);
        writer.Write(bytes.Length);
        writer.Write(bytes.Span);
        return output.ToArray();
    }

    public static int GetFileTransferChunkPayloadLength(string transferId, int byteLength)
    {
        ValidateFileTransferChunk(transferId, offset: 0, byteLength);
        return 1 + GetEncodedStringByteCount(transferId) + sizeof(long) + sizeof(int) + byteLength;
    }

    public static int WriteFileTransferChunkPayload(
        string transferId,
        long offset,
        ReadOnlySpan<byte> bytes,
        Span<byte> destination)
    {
        ValidateFileTransferChunk(transferId, offset, bytes.Length);
        int requiredLength = 1 + GetEncodedStringByteCount(transferId) + sizeof(long) + sizeof(int) + bytes.Length;
        if (destination.Length < requiredLength)
        {
            throw new ArgumentException("文件分块输出缓冲区不足。", nameof(destination));
        }

        destination[0] = (byte)RemoteControlKind.FileTransferChunk;
        int written = 1;
        written += WriteEncodedString(destination[written..], transferId);
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(written, sizeof(long)), offset);
        written += sizeof(long);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(written, sizeof(int)), bytes.Length);
        written += sizeof(int);
        bytes.CopyTo(destination[written..]);
        return written + bytes.Length;
    }

    public static byte[] EncodeFileTransferComplete(string transferId)
    {
        ValidateControlString(transferId);

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8);
        writer.Write((byte)RemoteControlKind.FileTransferComplete);
        writer.Write(transferId);
        return output.ToArray();
    }

    public static byte[] EncodeFileTransferCancel(string transferId, string reason)
    {
        ValidateControlString(transferId);

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8);
        writer.Write((byte)RemoteControlKind.FileTransferCancel);
        writer.Write(transferId);
        WriteBoundedString(writer, reason, "文件传输已取消。");
        return output.ToArray();
    }

    public static byte[] EncodeFileTransferChecksum(string transferId, string checksumHex)
    {
        return EncodeFileTransferChecksum(transferId, FileTransferChecksumAlgorithm, checksumHex);
    }

    public static byte[] EncodeFileTransferChecksum(string transferId, string algorithm, string checksumHex)
    {
        ValidateControlString(transferId);
        ValidateChecksumAlgorithm(algorithm);
        ValidateSha256Hex(checksumHex);

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8);
        writer.Write((byte)RemoteControlKind.FileTransferChecksum);
        writer.Write(transferId);
        writer.Write(NormalizeChecksumAlgorithm(algorithm));
        writer.Write(checksumHex.ToLowerInvariant());
        return output.ToArray();
    }

    public static byte[] EncodeFileTransferStatus(bool success, string message)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8);
        writer.Write((byte)RemoteControlKind.FileTransferStatus);
        writer.Write(success);
        WriteBoundedString(writer, message, "文件传输状态已更新。");
        return output.ToArray();
    }

    public static byte[] EncodeFileTransferReceipt(string transferId, bool success, string message)
    {
        ValidateControlString(transferId);
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8);
        writer.Write((byte)RemoteControlKind.FileTransferReceipt);
        writer.Write(transferId);
        writer.Write(success);
        WriteBoundedString(writer, message, "文件保存结果已更新。");
        return output.ToArray();
    }

    public static byte[] EncodeSessionRejected(string message)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8);
        writer.Write((byte)RemoteControlKind.SessionRejected);
        WriteBoundedString(
            writer,
            message,
            "被控端当前无法接受新的查看连接。");
        return output.ToArray();
    }

    public static RemoteControlMessage DecodeControl(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length == 0 || payload.Length > MaxControlPayloadBytes)
        {
            throw new InvalidDataException("控制消息长度异常。");
        }

        using var input = CreateReadableStream(payload);
        using var reader = new BinaryReader(input, Encoding.UTF8);
        var kind = (RemoteControlKind)reader.ReadByte();

        RemoteControlMessage control = kind switch
        {
            RemoteControlKind.CaptureTargetList => DecodeCaptureTargetList(reader),
            RemoteControlKind.SelectCaptureTarget => new RemoteControlMessage(
                kind,
                Array.Empty<CaptureTargetInfo>(),
                ReadBoundedString(reader),
                null),
            RemoteControlKind.CaptureTargetChanged => new RemoteControlMessage(
                kind,
                Array.Empty<CaptureTargetInfo>(),
                ReadBoundedString(reader),
                ReadBoundedString(reader)),
            RemoteControlKind.ClipboardGetText => new RemoteControlMessage(
                kind,
                Array.Empty<CaptureTargetInfo>(),
                null,
                null),
            RemoteControlKind.ClipboardSetText => DecodeClipboardText(kind, reader),
            RemoteControlKind.ClipboardText => DecodeClipboardText(kind, reader),
            RemoteControlKind.ClipboardStatus => new RemoteControlMessage(
                kind,
                Array.Empty<CaptureTargetInfo>(),
                null,
                null,
                null,
                reader.ReadBoolean(),
                ReadBoundedString(reader)),
            RemoteControlKind.FileTransferStart => DecodeFileTransferStart(reader, kind),
            RemoteControlKind.RemoteUpdateStart => DecodeFileTransferStart(reader, kind),
            RemoteControlKind.FileTransferChunk => DecodeFileTransferChunk(reader, kind, payload),
            RemoteControlKind.FileTransferComplete => new RemoteControlMessage(
                kind,
                Array.Empty<CaptureTargetInfo>(),
                null,
                null,
                TransferId: ReadBoundedString(reader)),
            RemoteControlKind.FileTransferCancel => DecodeFileTransferCancel(reader, kind),
            RemoteControlKind.FileTransferStatus => new RemoteControlMessage(
                kind,
                Array.Empty<CaptureTargetInfo>(),
                null,
                null,
                Success: reader.ReadBoolean(),
                StatusMessage: ReadBoundedString(reader)),
            RemoteControlKind.FileTransferReceipt => new RemoteControlMessage(
                kind, [], null, null,
                TransferId: ReadBoundedString(reader),
                Success: reader.ReadBoolean(),
                StatusMessage: ReadBoundedString(reader)),
            RemoteControlKind.FileTransferChecksum => DecodeFileTransferChecksum(reader, kind),
            RemoteControlKind.DeviceInfo => DecodeDeviceInfo(reader, kind),
            RemoteControlKind.DeviceIdentityRequest => new RemoteControlMessage(kind, [], null, null),
            RemoteControlKind.HostVideoDiagnosticsRequest => new RemoteControlMessage(kind, [], null, null),
            RemoteControlKind.HostVideoDiagnostics => new RemoteControlMessage(kind, [], null, null,
                Text: ReadHostVideoDiagnostics(reader)),
            RemoteControlKind.DeviceIdentity => new RemoteControlMessage(kind, [], null, null,
                Text: RemoteDeviceIdentity.Normalize(ReadBoundedString(reader)) ??
                    throw new InvalidDataException("设备标识无效。")),
            RemoteControlKind.ViewerInfo => new RemoteControlMessage(
                kind,
                Array.Empty<CaptureTargetInfo>(),
                null,
                null,
                SupportedVideoCodecs: NormalizeVideoCodecs((RemoteVideoCodecs)reader.ReadInt32())),
            RemoteControlKind.ViewerCapabilities => new RemoteControlMessage(
                kind,
                Array.Empty<CaptureTargetInfo>(),
                null,
                null,
                Capabilities: (RemoteDeviceCapabilities)reader.ReadInt32()),
            RemoteControlKind.VideoKeyFrameRequest => new RemoteControlMessage(
                kind,
                Array.Empty<CaptureTargetInfo>(),
                null,
                null),
            RemoteControlKind.FileDropPasteBegin => new RemoteControlMessage(
                kind,
                Array.Empty<CaptureTargetInfo>(),
                null,
                null),
            RemoteControlKind.FileDropPasteCommit => new RemoteControlMessage(
                kind,
                Array.Empty<CaptureTargetInfo>(),
                null,
                null),
            RemoteControlKind.FileDropPasteCancel => new RemoteControlMessage(
                kind,
                Array.Empty<CaptureTargetInfo>(),
                null,
                null),
            RemoteControlKind.FileTransferRequestClipboardFiles => new RemoteControlMessage(
                kind,
                Array.Empty<CaptureTargetInfo>(),
                null,
                null),
            RemoteControlKind.FileTransferClipboardFilesPreview => DecodeFileTransferClipboardFilesPreview(reader, kind),
            RemoteControlKind.FileTransferConfirmClipboardFiles => new RemoteControlMessage(
                kind,
                Array.Empty<CaptureTargetInfo>(),
                null,
                null),
            RemoteControlKind.FileTransferRejectClipboardFiles => new RemoteControlMessage(
                kind,
                Array.Empty<CaptureTargetInfo>(),
                null,
                null),
            RemoteControlKind.RemoteUpdatePackageRequest => new RemoteControlMessage(
                kind,
                Array.Empty<CaptureTargetInfo>(),
                null,
                null),
            RemoteControlKind.DeviceBuildInfo => new RemoteControlMessage(
                kind,
                Array.Empty<CaptureTargetInfo>(),
                null,
                null,
                BuildStamp: RemoteDeskBuildInfo.NormalizeBuildStamp(ReadBoundedString(reader))),
            RemoteControlKind.LowLatencyVideoOffer => DecodeLowLatencyVideoOffer(reader, kind),
            RemoteControlKind.LowLatencyVideoReady => new RemoteControlMessage(
                kind,
                Array.Empty<CaptureTargetInfo>(),
                null,
                null,
                LowLatencyVideoChannelId: reader.ReadUInt64(),
                LowLatencyVideoEpoch: reader.ReadUInt32()),
            RemoteControlKind.LowLatencyVideoStop => new RemoteControlMessage(
                kind,
                Array.Empty<CaptureTargetInfo>(),
                null,
                null,
                LowLatencyVideoChannelId: reader.ReadUInt64(),
                LowLatencyVideoEpoch: reader.ReadUInt32(),
                LowLatencyVideoStopReason: reader.ReadByte()),
            RemoteControlKind.LowLatencyVideoStopped => new RemoteControlMessage(
                kind,
                Array.Empty<CaptureTargetInfo>(),
                null,
                null,
                LowLatencyVideoChannelId: reader.ReadUInt64(),
                LowLatencyVideoEpoch: reader.ReadUInt32(),
                LowLatencyVideoStopReason: reader.ReadByte()),
            RemoteControlKind.SessionRejected => new RemoteControlMessage(
                kind,
                Array.Empty<CaptureTargetInfo>(),
                null,
                null,
                StatusMessage: ReadBoundedString(reader)),
            _ => throw new InvalidDataException("未知控制消息。")
        };

        if (input.Position != input.Length)
        {
            throw new InvalidDataException("控制消息包含多余数据。");
        }

        return control;
    }

    private static RemoteControlMessage DecodeLowLatencyVideoOffer(
        BinaryReader reader,
        RemoteControlKind kind)
    {
        byte version = reader.ReadByte();
        if (version != LowLatencyVideoProtocol.Version)
        {
            throw new InvalidDataException("低延迟画面通道版本不受支持。");
        }

        var offer = new LowLatencyVideoOffer(
            reader.ReadUInt16(),
            reader.ReadUInt16(),
            reader.ReadInt32(),
            reader.ReadUInt64(),
            reader.ReadUInt32(),
            ReadFixedBytes(reader, LowLatencyVideoProtocol.KeyLength),
            ReadFixedBytes(reader, LowLatencyVideoProtocol.KeyLength),
            ReadFixedBytes(reader, LowLatencyVideoProtocol.NoncePrefixLength),
            ReadFixedBytes(reader, LowLatencyVideoProtocol.NoncePrefixLength),
            ReadFixedBytes(reader, LowLatencyVideoProtocol.ChallengeLength));
        LowLatencyVideoProtocol.ValidateOffer(offer);
        return new RemoteControlMessage(
            kind,
            Array.Empty<CaptureTargetInfo>(),
            null,
            null,
            LowLatencyVideoOffer: offer);
    }

    private static byte[] ReadFixedBytes(BinaryReader reader, int length)
    {
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new EndOfStreamException("低延迟画面控制消息不完整。");
        }

        return bytes;
    }

    private static RemoteControlMessage DecodeCaptureTargetList(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > MaxControlItems)
        {
            throw new InvalidDataException("屏幕列表数量异常。");
        }

        var targets = new List<CaptureTargetInfo>(count);
        for (int index = 0; index < count; index++)
        {
            targets.Add(new CaptureTargetInfo(ReadBoundedString(reader), ReadBoundedString(reader)));
        }

        return new RemoteControlMessage(RemoteControlKind.CaptureTargetList, targets, null, null);
    }

    private static RemoteControlMessage DecodeDeviceInfo(BinaryReader reader, RemoteControlKind kind)
    {
        string machineName = ReadBoundedString(reader);
        string platform = RemoteDevicePlatforms.Normalize(ReadBoundedString(reader));
        var capabilities = (RemoteDeviceCapabilities)reader.ReadInt32();
        string? buildStamp = reader.BaseStream.Position < reader.BaseStream.Length
            ? RemoteDeskBuildInfo.NormalizeBuildStamp(ReadBoundedString(reader))
            : null;

        return new RemoteControlMessage(
            kind,
            Array.Empty<CaptureTargetInfo>(),
            null,
            null,
            MachineName: machineName,
            Platform: platform,
            Capabilities: capabilities,
            BuildStamp: buildStamp);
    }

    private static RemoteControlMessage DecodeFileTransferStart(BinaryReader reader, RemoteControlKind kind)
    {
        string transferId = ReadBoundedString(reader);
        string fileName = ReadBoundedString(reader);
        long fileLength = reader.ReadInt64();
        ValidateFileLength(fileLength);
        return new RemoteControlMessage(
            kind,
            Array.Empty<CaptureTargetInfo>(),
            null,
            null,
            TransferId: transferId,
            FileName: fileName,
            FileLength: fileLength);
    }

    private static RemoteControlMessage DecodeFileTransferChunk(
        BinaryReader reader,
        RemoteControlKind kind,
        ReadOnlyMemory<byte> payload)
    {
        string transferId = ReadBoundedString(reader);
        long offset = reader.ReadInt64();
        int length = reader.ReadInt32();
        if (offset < 0 ||
            offset > MaxFileTransferBytes ||
            length <= 0 ||
            length > FileTransferChunkBytes ||
            offset > MaxFileTransferBytes - length)
        {
            throw new InvalidDataException("文件分块数据异常。");
        }

        long bytesOffset = reader.BaseStream.Position;
        if (bytesOffset < 0 || reader.BaseStream.Length - bytesOffset < length)
        {
            throw new EndOfStreamException("文件分块数据不完整。");
        }

        // The protocol message owns its decrypted payload array for the full duration of control
        // handling. Keep a view over that payload instead of allocating and copying another
        // 128 KiB array on the LOH for every file chunk.
        ReadOnlyMemory<byte> bytes = payload.Slice(checked((int)bytesOffset), length);
        reader.BaseStream.Position = bytesOffset + length;

        return new RemoteControlMessage(
            kind,
            Array.Empty<CaptureTargetInfo>(),
            null,
            null,
            TransferId: transferId,
            FileOffset: offset,
            FileBytes: bytes);
    }

    private static RemoteControlMessage DecodeFileTransferCancel(BinaryReader reader, RemoteControlKind kind)
    {
        string transferId = ReadBoundedString(reader);
        string reason = ReadBoundedString(reader);
        return new RemoteControlMessage(
            kind,
            Array.Empty<CaptureTargetInfo>(),
            null,
            null,
            StatusMessage: reason,
            TransferId: transferId);
    }

    private static RemoteControlMessage DecodeFileTransferChecksum(BinaryReader reader, RemoteControlKind kind)
    {
        string transferId = ReadBoundedString(reader);
        string algorithm = NormalizeChecksumAlgorithm(ReadBoundedString(reader));
        string checksumHex = ReadBoundedString(reader).ToLowerInvariant();
        ValidateChecksumAlgorithm(algorithm);
        ValidateSha256Hex(checksumHex);
        return new RemoteControlMessage(
            kind,
            Array.Empty<CaptureTargetInfo>(),
            null,
            null,
            TransferId: transferId,
            ChecksumAlgorithm: algorithm,
            ChecksumHex: checksumHex);
    }

    private static RemoteControlMessage DecodeFileTransferClipboardFilesPreview(BinaryReader reader, RemoteControlKind kind)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > MaxControlItems)
        {
            throw new InvalidDataException("文件预览列表数量异常。");
        }

        var items = new List<FileTransferConfirmationItem>(count);
        for (int index = 0; index < count; index++)
        {
            string itemKind = ReadBoundedString(reader);
            string sourcePath = ReadBoundedString(reader);
            string transferName = ReadBoundedString(reader);
            long sizeBytes = reader.ReadInt64();
            if (sizeBytes < 0)
            {
                throw new InvalidDataException("文件预览大小异常。");
            }

            string destinationPath = ReadBoundedString(reader);
            items.Add(new FileTransferConfirmationItem(itemKind, sourcePath, transferName, sizeBytes, destinationPath));
        }

        string note = ReadBoundedString(reader);
        return new RemoteControlMessage(
            kind,
            Array.Empty<CaptureTargetInfo>(),
            null,
            null,
            StatusMessage: note,
            FileTransferPreviewItems: items);
    }

    private static RemoteControlMessage DecodeClipboardText(RemoteControlKind kind, BinaryReader reader)
    {
        string text = reader.ReadString();
        ValidateClipboardText(text);
        return new RemoteControlMessage(kind, Array.Empty<CaptureTargetInfo>(), null, null, text);
    }

    private static string ReadHostVideoDiagnostics(BinaryReader reader)
    {
        string text = ReadBoundedString(reader);
        if (text.Length > HostVideoDiagnostics.MaximumCharacters) throw new InvalidDataException("Video diagnostic exceeds limit.");
        return text;
    }

    private static string ReadBoundedString(BinaryReader reader)
    {
        string value = reader.ReadString();
        if (value.Length > MaxControlStringChars)
        {
            throw new InvalidDataException("控制消息文本过长。");
        }

        return value;
    }

    private static void WriteBoundedString(BinaryWriter writer, string? value, string fallback)
    {
        string text = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        writer.Write(text.Length <= MaxControlStringChars ? text : text[..MaxControlStringChars]);
    }

    private static MemoryStream CreateReadableStream(ReadOnlyMemory<byte> payload)
    {
        if (MemoryMarshal.TryGetArray(payload, out ArraySegment<byte> segment) && segment.Array is not null)
        {
            return new MemoryStream(segment.Array, segment.Offset, segment.Count, writable: false, publiclyVisible: false);
        }

        return new MemoryStream(payload.ToArray(), writable: false);
    }

    private static void ValidateClipboardText(string text)
    {
        if (text.Length > MaxClipboardTextChars)
        {
            throw new InvalidDataException("剪贴板文本过大，已拒绝处理。");
        }
    }

    private static void ValidateControlString(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxControlStringChars)
        {
            throw new InvalidDataException("控制消息文本异常。");
        }
    }

    private static void ValidateFileTransferChunk(string transferId, long offset, int byteLength)
    {
        ValidateControlString(transferId);
        if (offset < 0 || offset > MaxFileTransferBytes)
        {
            throw new InvalidDataException("文件分块偏移异常。");
        }

        if (byteLength <= 0 || byteLength > FileTransferChunkBytes)
        {
            throw new InvalidDataException("文件分块大小异常。");
        }

        if (offset > MaxFileTransferBytes - byteLength)
        {
            throw new InvalidDataException("文件分块超出允许范围。");
        }
    }

    private static int GetEncodedStringByteCount(string value)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        return Get7BitEncodedIntByteCount(byteCount) + byteCount;
    }

    private static int WriteEncodedString(Span<byte> destination, string value)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        int written = Write7BitEncodedInt(destination, byteCount);
        return written + Encoding.UTF8.GetBytes(value.AsSpan(), destination[written..]);
    }

    private static int Get7BitEncodedIntByteCount(int value)
    {
        uint remaining = (uint)value;
        int count = 1;
        while (remaining >= 0x80)
        {
            remaining >>= 7;
            count++;
        }

        return count;
    }

    private static int Write7BitEncodedInt(Span<byte> destination, int value)
    {
        uint remaining = (uint)value;
        int written = 0;
        while (remaining >= 0x80)
        {
            destination[written++] = (byte)((remaining & 0x7F) | 0x80);
            remaining >>= 7;
        }

        destination[written++] = (byte)remaining;
        return written;
    }

    private static void ValidateFileLength(long fileLength)
    {
        if (fileLength < 0 || fileLength > MaxFileTransferBytes)
        {
            throw new InvalidDataException("文件大小超出允许范围。");
        }
    }

    private static void ValidateChecksumAlgorithm(string algorithm)
    {
        if (!string.Equals(NormalizeChecksumAlgorithm(algorithm), FileTransferChecksumAlgorithm, StringComparison.Ordinal))
        {
            throw new InvalidDataException("文件校验算法不受支持。");
        }
    }

    private static void ValidateSha256Hex(string checksumHex)
    {
        if (checksumHex.Length != Sha256HexLength)
        {
            throw new InvalidDataException("文件校验值长度异常。");
        }

        try
        {
            Convert.FromHexString(checksumHex);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("文件校验值格式异常。", ex);
        }
    }

    private static string NormalizeChecksumAlgorithm(string algorithm)
    {
        return algorithm.Trim().Replace("-", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
    }

    private static RemoteVideoCodecs NormalizeVideoCodecs(RemoteVideoCodecs codecs)
    {
        const RemoteVideoCodecs knownCodecs = RemoteVideoCodecs.Jpeg | RemoteVideoCodecs.H264AnnexB;
        RemoteVideoCodecs normalized = codecs & knownCodecs;
        return normalized == RemoteVideoCodecs.None ? RemoteVideoCodecs.Jpeg : normalized;
    }
}
