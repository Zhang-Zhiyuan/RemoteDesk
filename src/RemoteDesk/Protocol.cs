using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace RemoteDesk;

internal enum MessageType : byte
{
    Frame = 1,
    Input = 2,
    Control = 3,
    Ping = 4,
    Pong = 5,
    VideoFrame = 6,
    NativeVideoFrame = 7,
    NativeDetailChunk = 8,
    NativeDetailRequest = 9,
    NativeDetailFeedback = 10,
    NativeDetailOffer = 11,
    NativeDetailUdpResume = 12,
    NativeDetailUdpResumeAck = 13
}

// Buffer is the dedicated decrypted message array returned by SecureSession.Decrypt; it is never
// returned to an ArrayPool. Decoders may safely keep ReadOnlyMemory slices for asynchronous message
// handling, and the slice itself keeps the backing array alive.
internal readonly record struct ProtocolMessage(MessageType Type, byte[] Buffer, int PayloadOffset, int PayloadLength)
{
    public ReadOnlyMemory<byte> PayloadMemory => Buffer.AsMemory(PayloadOffset, PayloadLength);

    public ReadOnlySpan<byte> PayloadSpan => Buffer.AsSpan(PayloadOffset, PayloadLength);
}

internal readonly record struct ServerAuthenticationResult(SecureSession? Session, bool IsIncomplete)
{
    public bool IsAuthenticated => Session is not null;
}

internal static class Protocol
{
    public const int DefaultPort = 56565;

    private const int HeaderLength = 5;
    private const int EncryptedHeaderLength = 4;
    private const int NonceLength = 32;
    private const int ProofLength = 32;
    private const int AesTagLength = 16;
    private const int MaxFramePayloadBytes = 32 * 1024 * 1024;
    private const int MaxEncryptedBytes = HeaderLength + MaxFramePayloadBytes + AesTagLength;

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("RDK1");
    private static readonly byte[] AuthMarker = Encoding.ASCII.GetBytes("AUTH");
    private static readonly byte[] SessionInfo = Encoding.ASCII.GetBytes("RemoteDesk session v1");
    private static readonly byte[] ClientToServerInfo = Encoding.ASCII.GetBytes("client->server");
    private static readonly byte[] ServerToClientInfo = Encoding.ASCII.GetBytes("server->client");

    public static async Task<SecureSession?> AuthenticateServerAsync(NetworkStream stream, string password, CancellationToken cancellationToken)
    {
        ServerAuthenticationResult result = await AuthenticateServerDetailedAsync(stream, password, cancellationToken);
        return result.Session;
    }

    internal static async Task<ServerAuthenticationResult> AuthenticateServerDetailedAsync(NetworkStream stream, string password, CancellationToken cancellationToken)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceLength);
        await stream.WriteAsync(Magic, cancellationToken);
        await stream.WriteAsync(nonce, cancellationToken);

        byte[] marker;
        try
        {
            marker = await ReadExactAsync(stream, AuthMarker.Length, cancellationToken);
        }
        catch (Exception ex) when (IsIncompleteAuthenticationRead(ex, cancellationToken))
        {
            return new ServerAuthenticationResult(null, IsIncomplete: true);
        }

        if (!marker.SequenceEqual(AuthMarker))
        {
            return new ServerAuthenticationResult(null, IsIncomplete: false);
        }

        byte[] proof;
        try
        {
            proof = await ReadExactAsync(stream, ProofLength, cancellationToken);
        }
        catch (Exception ex) when (IsIncompleteAuthenticationRead(ex, cancellationToken))
        {
            return new ServerAuthenticationResult(null, IsIncomplete: true);
        }

        byte[] expectedProof = ComputePasswordProof(password, nonce);
        bool authenticated = CryptographicOperations.FixedTimeEquals(proof, expectedProof);

        try
        {
            await stream.WriteAsync(new[] { authenticated ? (byte)1 : (byte)0 }, cancellationToken);
        }
        catch (Exception ex) when (!authenticated && IsIncompleteAuthenticationRead(ex, cancellationToken))
        {
            return new ServerAuthenticationResult(null, IsIncomplete: false);
        }

        return new ServerAuthenticationResult(
            authenticated ? CreateSession(password, nonce, isServer: true) : null,
            IsIncomplete: false);
    }

    public static async Task<SecureSession> AuthenticateClientAsync(NetworkStream stream, string password, CancellationToken cancellationToken)
    {
        byte[] magic = await ReadExactAsync(stream, Magic.Length, cancellationToken);
        if (!magic.SequenceEqual(Magic))
        {
            throw new InvalidDataException("目标不是兼容的 RemoteDesk 被控端。");
        }

        byte[] nonce = await ReadExactAsync(stream, NonceLength, cancellationToken);
        byte[] proof = ComputePasswordProof(password, nonce);

        await stream.WriteAsync(AuthMarker, cancellationToken);
        await stream.WriteAsync(proof, cancellationToken);

        byte[] result = await ReadExactAsync(stream, 1, cancellationToken);
        if (result[0] != 1)
        {
            throw new UnauthorizedAccessException("口令错误，或被控端拒绝连接。");
        }

        return CreateSession(password, nonce, isServer: false);
    }

    internal static async Task<bool> ReadServerMagicAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] magic = await ReadExactAsync(stream, Magic.Length, cancellationToken);
        return magic.SequenceEqual(Magic);
    }

    public static async Task WriteMessageAsync(
        NetworkStream stream,
        MessageType messageType,
        ReadOnlyMemory<byte> payload,
        SecureSession session,
        SemaphoreSlim writeLock,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(messageType))
        {
            throw new InvalidDataException("未知消息类型，已拒绝发送。");
        }

        if (payload.Length > GetMaxPayloadBytes(messageType))
        {
            throw new InvalidDataException("消息过大，已拒绝发送。");
        }

        int plainLength = HeaderLength + payload.Length;
        int encryptedLength = plainLength + AesTagLength;
        int packetLength = EncryptedHeaderLength + encryptedLength;
        byte[] plainBuffer = ArrayPool<byte>.Shared.Rent(plainLength);
        byte[] packetBuffer = ArrayPool<byte>.Shared.Rent(packetLength);
        try
        {
            plainBuffer[0] = (byte)messageType;
            BinaryPrimitives.WriteInt32LittleEndian(plainBuffer.AsSpan(1), payload.Length);
            payload.CopyTo(plainBuffer.AsMemory(HeaderLength, payload.Length));

            await writeLock.WaitAsync(cancellationToken);
            try
            {
                BinaryPrimitives.WriteInt32LittleEndian(
                    packetBuffer.AsSpan(0, EncryptedHeaderLength),
                    encryptedLength);
                session.Encrypt(
                    plainBuffer.AsSpan(0, plainLength),
                    packetBuffer.AsSpan(EncryptedHeaderLength, encryptedLength));
                await stream.WriteAsync(
                    packetBuffer.AsMemory(0, packetLength),
                    cancellationToken);
            }
            finally
            {
                writeLock.Release();
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBuffer.AsSpan(0, plainLength));
            ArrayPool<byte>.Shared.Return(plainBuffer);
            ArrayPool<byte>.Shared.Return(packetBuffer);
        }
    }

    // Native fragments are optional. Never enqueue a write-lock waiter behind
    // input/control/base work; admission and the send-counter publication are
    // checked under the same encryption lock before consuming a nonce.
    internal static async Task<bool> TryWriteNativeChunkAsync(NetworkStream stream,
        ReadOnlyMemory<byte> payload, SecureSession session, SemaphoreSlim writeLock,
        Func<bool> mayWrite, Action beforeWrite, CancellationToken cancellationToken)
    {
        if (payload.Length is <= 0 or > NativeDetailSessionProtocol.MaximumChunkPayloadBytes)
            throw new InvalidDataException("Invalid native fragment size.");
        if (!mayWrite() || !await writeLock.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return false;
        byte[]? plain = null, packet = null;
        int plainBytes = HeaderLength + payload.Length;
        int encryptedBytes = plainBytes + AesTagLength;
        try
        {
            if (!mayWrite()) return false;
            plain = ArrayPool<byte>.Shared.Rent(plainBytes);
            packet = ArrayPool<byte>.Shared.Rent(EncryptedHeaderLength + encryptedBytes);
            plain[0] = (byte)MessageType.NativeDetailChunk;
            BinaryPrimitives.WriteInt32LittleEndian(plain.AsSpan(1), payload.Length);
            payload.CopyTo(plain.AsMemory(HeaderLength));
            BinaryPrimitives.WriteInt32LittleEndian(packet, encryptedBytes);
            beforeWrite();
            session.Encrypt(plain.AsSpan(0, plainBytes), packet.AsSpan(EncryptedHeaderLength, encryptedBytes));
            await stream.WriteAsync(packet.AsMemory(0, EncryptedHeaderLength + encryptedBytes), cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            if (plain is not null) { CryptographicOperations.ZeroMemory(plain.AsSpan(0, plainBytes)); ArrayPool<byte>.Shared.Return(plain); }
            if (packet is not null) ArrayPool<byte>.Shared.Return(packet);
            writeLock.Release();
        }
    }

    public static async Task WriteInputMessagesAsync(
        NetworkStream stream,
        IReadOnlyList<RemoteInputCommand> commands,
        SecureSession session,
        SemaphoreSlim writeLock,
        CancellationToken cancellationToken,
        Func<RemoteInputCommand, bool>? shouldSend = null)
    {
        if (commands.Count == 0)
        {
            return;
        }

        await writeLock.WaitAsync(cancellationToken);
        try
        {
            const int inputPayloadLength = RemoteMessageCodec.InputPayloadLength;
            const int inputPlainLength = HeaderLength + inputPayloadLength;
            const int inputEncryptedLength = inputPlainLength + AesTagLength;
            const int inputPacketLength = EncryptedHeaderLength + inputEncryptedLength;
            byte[] plainMessage = ArrayPool<byte>.Shared.Rent(inputPlainLength);
            int batchPacketLength = checked(inputPacketLength * commands.Count);
            byte[] batchPacket = ArrayPool<byte>.Shared.Rent(batchPacketLength);
            try
            {
                plainMessage[0] = (byte)MessageType.Input;
                BinaryPrimitives.WriteInt32LittleEndian(plainMessage.AsSpan(1), inputPayloadLength);

                int encodedCount = 0;
                for (int index = 0; index < commands.Count; index++)
                {
                    RemoteInputCommand command = commands[index];
                    // Evaluate after taking the shared TCP write lock. This
                    // lets the caller invalidate a mouse move that was
                    // dequeued before an independent UDP input route became
                    // active, without dropping reliable events in the batch.
                    if (shouldSend?.Invoke(command) == false)
                    {
                        continue;
                    }

                    RemoteMessageCodec.WriteInputPayload(
                        command,
                        plainMessage.AsSpan(HeaderLength, inputPayloadLength));

                    int packetOffset = encodedCount * inputPacketLength;
                    BinaryPrimitives.WriteInt32LittleEndian(
                        batchPacket.AsSpan(packetOffset, EncryptedHeaderLength),
                        inputEncryptedLength);
                    session.Encrypt(
                        plainMessage.AsSpan(0, inputPlainLength),
                        batchPacket.AsSpan(packetOffset + EncryptedHeaderLength, inputEncryptedLength));
                    encodedCount++;
                }

                if (encodedCount > 0)
                {
                    await stream.WriteAsync(
                        batchPacket.AsMemory(
                            0,
                            checked(inputPacketLength * encodedCount)),
                        cancellationToken);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(batchPacket);
                ArrayPool<byte>.Shared.Return(plainMessage);
            }
        }
        finally
        {
            writeLock.Release();
        }
    }

    public static Task<ProtocolFrameWriteTimings> WriteFrameMessageAsync(
        NetworkStream stream,
        int width,
        int height,
        double captureMilliseconds,
        double encodeMilliseconds,
        ReadOnlyMemory<byte> jpegBytes,
        SecureSession session,
        SemaphoreSlim writeLock,
        CancellationToken cancellationToken)
    {
        return WriteFrameMessageCoreAsync(
            stream,
            MessageType.Frame,
            width,
            height,
            RemoteFrameEncoding.Jpeg,
            RemoteFrameFlags.KeyFrame,
            captureMilliseconds,
            encodeMilliseconds,
            jpegBytes,
            session,
            writeLock,
            cancellationToken);
    }

    public static Task<ProtocolFrameWriteTimings> WriteVideoFrameMessageAsync(
        NetworkStream stream,
        int width,
        int height,
        RemoteFrameEncoding encoding,
        RemoteFrameFlags flags,
        double captureMilliseconds,
        double encodeMilliseconds,
        ReadOnlyMemory<byte> encodedBytes,
        SecureSession session,
        SemaphoreSlim writeLock,
        CancellationToken cancellationToken)
    {
        return WriteFrameMessageCoreAsync(
            stream,
            MessageType.VideoFrame,
            width,
            height,
            encoding,
            flags,
            captureMilliseconds,
            encodeMilliseconds,
            encodedBytes,
            session,
            writeLock,
            cancellationToken);
    }

    private static async Task<ProtocolFrameWriteTimings> WriteFrameMessageCoreAsync(
        NetworkStream stream,
        MessageType messageType,
        int width,
        int height,
        RemoteFrameEncoding encoding,
        RemoteFrameFlags flags,
        double captureMilliseconds,
        double encodeMilliseconds,
        ReadOnlyMemory<byte> encodedBytes,
        SecureSession session,
        SemaphoreSlim writeLock,
        CancellationToken cancellationToken)
    {
        int frameHeaderLength = messageType switch
        {
            MessageType.Frame => RemoteMessageCodec.FrameHeaderLength,
            MessageType.VideoFrame => RemoteMessageCodec.VideoFrameHeaderLength,
            _ => throw new ArgumentOutOfRangeException(
                nameof(messageType),
                messageType,
                "消息类型不是画面帧。")
        };
        int maxPayloadLength = GetMaxPayloadBytes(messageType);
        if (encodedBytes.Length > maxPayloadLength - frameHeaderLength)
        {
            throw new InvalidDataException("消息过大，已拒绝发送。");
        }
        if (encodedBytes.IsEmpty)
        {
            throw new InvalidDataException("画面帧内容为空，已拒绝发送。");
        }

        long preparationStartedAt = Stopwatch.GetTimestamp();
        int payloadLength = frameHeaderLength + encodedBytes.Length;
        int plainLength = HeaderLength + payloadLength;
        int encryptedLength = plainLength + AesTagLength;
        int packetLength = EncryptedHeaderLength + encryptedLength;
        byte[] plainBuffer = ArrayPool<byte>.Shared.Rent(plainLength);
        byte[] encryptedPacketBuffer = ArrayPool<byte>.Shared.Rent(packetLength);

        try
        {
            plainBuffer[0] = (byte)messageType;
            BinaryPrimitives.WriteInt32LittleEndian(
                plainBuffer.AsSpan(1),
                payloadLength);
            if (messageType == MessageType.Frame)
            {
                RemoteMessageCodec.WriteFrameHeader(
                    plainBuffer.AsSpan(HeaderLength, frameHeaderLength),
                    width,
                    height,
                    captureMilliseconds,
                    encodeMilliseconds);
            }
            else
            {
                RemoteMessageCodec.WriteVideoFrameHeader(
                    plainBuffer.AsSpan(HeaderLength, frameHeaderLength),
                    width,
                    height,
                    encoding,
                    flags,
                    captureMilliseconds,
                    encodeMilliseconds);
            }

            encodedBytes.CopyTo(plainBuffer.AsMemory(
                HeaderLength + frameHeaderLength,
                encodedBytes.Length));

            long preparedAt = Stopwatch.GetTimestamp();
            await writeLock.WaitAsync(cancellationToken);
            try
            {
                long lockAcquiredAt = Stopwatch.GetTimestamp();
                BinaryPrimitives.WriteInt32LittleEndian(
                    encryptedPacketBuffer.AsSpan(0, EncryptedHeaderLength),
                    encryptedLength);
                session.Encrypt(
                    plainBuffer.AsSpan(0, plainLength),
                    encryptedPacketBuffer.AsSpan(
                        EncryptedHeaderLength,
                        encryptedLength));

                long writeStartedAt = Stopwatch.GetTimestamp();
                await stream.WriteAsync(
                    encryptedPacketBuffer.AsMemory(0, packetLength),
                    cancellationToken);
                long writtenAt = Stopwatch.GetTimestamp();
                return new ProtocolFrameWriteTimings(
                    Stopwatch.GetElapsedTime(preparationStartedAt, preparedAt).TotalMilliseconds,
                    Stopwatch.GetElapsedTime(preparedAt, lockAcquiredAt).TotalMilliseconds,
                    Stopwatch.GetElapsedTime(lockAcquiredAt, writeStartedAt).TotalMilliseconds,
                    Stopwatch.GetElapsedTime(writeStartedAt, writtenAt).TotalMilliseconds);
            }
            finally
            {
                writeLock.Release();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(plainBuffer);
            ArrayPool<byte>.Shared.Return(encryptedPacketBuffer);
        }
    }

    public static async Task<ProtocolMessage> ReadMessageAsync(
        NetworkStream stream,
        SecureSession session,
        CancellationToken cancellationToken)
    {
        byte[] encryptedHeader = ArrayPool<byte>.Shared.Rent(EncryptedHeaderLength);
        int encryptedLength;
        try
        {
            await ReadExactAsync(stream, encryptedHeader.AsMemory(0, EncryptedHeaderLength), cancellationToken);
            encryptedLength = BinaryPrimitives.ReadInt32LittleEndian(encryptedHeader.AsSpan(0, EncryptedHeaderLength));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(encryptedHeader);
        }

        if (encryptedLength < HeaderLength + AesTagLength || encryptedLength > MaxEncryptedBytes)
        {
            throw new InvalidDataException("收到的加密消息长度异常。");
        }

        byte[] encryptedBuffer = ArrayPool<byte>.Shared.Rent(encryptedLength);
        byte[] plainMessage;
        try
        {
            await ReadExactAsync(stream, encryptedBuffer.AsMemory(0, encryptedLength), cancellationToken);
            plainMessage = session.Decrypt(encryptedBuffer.AsSpan(0, encryptedLength));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(encryptedBuffer);
        }

        if (plainMessage.Length < HeaderLength)
        {
            throw new InvalidDataException("收到的消息头不完整。");
        }

        MessageType messageType = (MessageType)plainMessage[0];
        if (!Enum.IsDefined(messageType))
        {
            throw new InvalidDataException("收到未知消息类型。");
        }

        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(plainMessage.AsSpan(1));

        if (payloadLength < 0 ||
            payloadLength > GetMaxPayloadBytes(messageType) ||
            plainMessage.Length != HeaderLength + payloadLength)
        {
            throw new InvalidDataException("收到的消息长度异常。");
        }

        return new ProtocolMessage(messageType, plainMessage, HeaderLength, payloadLength);
    }

    private static int GetMaxPayloadBytes(MessageType messageType)
    {
        return messageType switch
        {
            MessageType.Frame or MessageType.VideoFrame => MaxFramePayloadBytes,
            MessageType.NativeVideoFrame => NativeDetailSessionProtocol.MaximumBasePayloadBytes,
            MessageType.NativeDetailChunk => NativeDetailSessionProtocol.MaximumChunkPayloadBytes,
            MessageType.NativeDetailRequest => NativeDetailSessionProtocol.RequestBytes,
            MessageType.NativeDetailFeedback => NativeDetailSessionProtocol.FeedbackBytes,
            MessageType.NativeDetailOffer => NativeDetailSessionProtocol.OfferBytes,
            MessageType.NativeDetailUdpResume or MessageType.NativeDetailUdpResumeAck => NativeDetailSessionProtocol.ResumeBytes,
            MessageType.Input => RemoteMessageCodec.InputPayloadLength,
            MessageType.Control => RemoteMessageCodec.MaxControlPayloadBytes,
            MessageType.Ping or MessageType.Pong => 0,
            _ => 0
        };
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int length, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[length];
        await ReadExactAsync(stream, buffer.AsMemory(), cancellationToken);
        return buffer;
    }

    private static async Task ReadExactAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int offset = 0;

        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("连接已关闭。");
            }

            offset += read;
        }
    }

    private static bool IsIncompleteAuthenticationRead(Exception ex, CancellationToken cancellationToken)
    {
        return !cancellationToken.IsCancellationRequested &&
            ex is EndOfStreamException or IOException or ObjectDisposedException;
    }

    private static byte[] ComputePasswordProof(string password, byte[] nonce)
    {
        byte[] key = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(nonce);
    }

    private static SecureSession CreateSession(string password, byte[] nonce, bool isServer)
    {
        byte[] passwordKey = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        using var sessionHmac = new HMACSHA256(passwordKey);
        byte[] sessionSeed = Join(SessionInfo, nonce);
        byte[] masterKey = sessionHmac.ComputeHash(sessionSeed);

        using var keyHmac = new HMACSHA256(masterKey);
        byte[] clientToServerKey = keyHmac.ComputeHash(ClientToServerInfo);
        byte[] serverToClientKey = keyHmac.ComputeHash(ServerToClientInfo);
        return new SecureSession(clientToServerKey, serverToClientKey, isServer);
    }

    private static byte[] Join(byte[] first, byte[] second)
    {
        byte[] joined = new byte[first.Length + second.Length];
        Buffer.BlockCopy(first, 0, joined, 0, first.Length);
        Buffer.BlockCopy(second, 0, joined, first.Length, second.Length);
        return joined;
    }
}

internal sealed class SecureSession : IDisposable
{
    private readonly AesGcm _sendCipher;
    private readonly AesGcm _receiveCipher;
    private long _sendSequence;
    private long _receiveSequence;

    public SecureSession(byte[] clientToServerKey, byte[] serverToClientKey, bool isServer)
    {
        byte[] sendKey = isServer ? serverToClientKey : clientToServerKey;
        byte[] receiveKey = isServer ? clientToServerKey : serverToClientKey;
        _sendCipher = new AesGcm(sendKey, 16);
        _receiveCipher = new AesGcm(receiveKey, 16);
    }

    public byte[] Encrypt(ReadOnlySpan<byte> plainMessage)
    {
        byte[] output = new byte[plainMessage.Length + 16];
        Encrypt(plainMessage, output);
        return output;
    }

    public void Encrypt(ReadOnlySpan<byte> plainMessage, Span<byte> encryptedMessage)
    {
        if (encryptedMessage.Length != plainMessage.Length + 16)
        {
            throw new ArgumentException("加密输出缓冲区长度不匹配。", nameof(encryptedMessage));
        }

        Span<byte> nonce = stackalloc byte[12];
        WriteNonce(nonce, Interlocked.Increment(ref _sendSequence) - 1);
        Span<byte> ciphertext = encryptedMessage[..plainMessage.Length];
        Span<byte> tag = encryptedMessage.Slice(plainMessage.Length, 16);
        _sendCipher.Encrypt(nonce, plainMessage, ciphertext, tag);
    }

    public byte[] Decrypt(ReadOnlySpan<byte> encryptedMessage)
    {
        if (encryptedMessage.Length < 16)
        {
            throw new InvalidDataException("加密消息不完整。");
        }

        int cipherTextLength = encryptedMessage.Length - 16;
        byte[] plainMessage = new byte[cipherTextLength];
        Span<byte> nonce = stackalloc byte[12];
        WriteNonce(nonce, Interlocked.Increment(ref _receiveSequence) - 1);
        ReadOnlySpan<byte> ciphertext = encryptedMessage[..cipherTextLength];
        ReadOnlySpan<byte> tag = encryptedMessage[cipherTextLength..];
        _receiveCipher.Decrypt(nonce, ciphertext, tag, plainMessage);
        return plainMessage;
    }

    public void Dispose()
    {
        _sendCipher.Dispose();
        _receiveCipher.Dispose();
    }

    private static void WriteNonce(Span<byte> nonce, long sequence)
    {
        nonce.Clear();
        BinaryPrimitives.WriteInt64LittleEndian(nonce[4..], sequence);
    }
}
