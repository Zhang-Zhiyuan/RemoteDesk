using System.Buffers.Binary;
using RemoteDesk.NativeDetail;

namespace RemoteDesk;

// The base and its content manifest are ONE authenticated record. A separately
// captured image or a nearby receive timestamp must never supply this identity.
internal sealed record NativeDetailFrameIdentity(DetailManifest Manifest, long? DecoderSampleTime = null)
{
    internal NativeDetailFrameIdentity BindDecoderSample(long sampleTime) => sampleTime >= 0
        ? this with { DecoderSampleTime = sampleTime }
        : throw new ArgumentOutOfRangeException(nameof(sampleTime));

    internal NativeDetailFrameIdentity? MatchDecoderOutput(long? explicitSampleTime) =>
        explicitSampleTime.HasValue && DecoderSampleTime == explicitSampleTime ? this : null;
}

internal sealed record NativeDetailRequest(DetailContext Context, DetailRect Viewport, bool Enabled);
internal sealed record NativeDetailOffer(int TargetGeneration, Size NativeSize, bool Available);
internal sealed record NativeDetailUdpResume(DetailContext Context, ulong ChannelId, uint Epoch, long MinimumFrameSequence);
internal sealed record NativeDetailFeedback(DetailContext Context, long ReceivedSequence,
    long PresentedSequence, long ReceivedWireBytes, ulong CachedTileMask = 0, int AcknowledgementDelayMilliseconds = 0)
{
    internal long LocalReceivedAt { get; init; }
}

internal static class NativeDetailSessionProtocol
{
    internal const int RequestBytes = 48;
    internal const int OfferBytes = 24;
    internal const int FeedbackBytes = 72;
    internal const int ResumeBytes = 56;
    internal const int MaximumManifestBytes = 44 + 2048 * 10;
    internal const int MaximumAccessUnitBytes = 8 * 1024 * 1024;
    internal const int MaximumBasePayloadBytes = 12 + MaximumManifestBytes +
        RemoteMessageCodec.VideoFrameHeaderLength + MaximumAccessUnitBytes;
    internal const int MaximumChunkPayloadBytes = DetailWire.ChunkHeaderBytes + DetailWire.ChunkBytes;
    private const uint BaseMagic = 0x3156444E; // NDV1
    private const uint RequestMagic = 0x3152444E; // NDR1

    internal static byte[] EncodeResume(NativeDetailUdpResume resume)
    {
        resume.Context.Validate();
        if (resume.ChannelId == 0 || resume.Epoch == 0 || resume.MinimumFrameSequence <= 0)
            throw new InvalidDataException("Invalid UDP resume boundary.");
        byte[] bytes = new byte[ResumeBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x3155444E);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), resume.Context.Epoch);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), resume.Context.Request);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), resume.Context.Width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), resume.Context.Height);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32), resume.ChannelId);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), resume.Epoch);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(48), resume.MinimumFrameSequence);
        return bytes;
    }

    internal static NativeDetailUdpResume DecodeResume(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != ResumeBytes || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0x3155444E ||
            BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]) != 0 || BinaryPrimitives.ReadInt32LittleEndian(bytes[44..]) != 0)
            throw new InvalidDataException("Invalid UDP resume message.");
        var resume = new NativeDetailUdpResume(new(BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[16..]), BinaryPrimitives.ReadInt32LittleEndian(bytes[24..]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[28..])), BinaryPrimitives.ReadUInt64LittleEndian(bytes[32..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[40..]), BinaryPrimitives.ReadInt64LittleEndian(bytes[48..]));
        _ = EncodeResume(resume);
        return resume;
    }

    internal static byte[] EncodeOffer(NativeDetailOffer offer)
    {
        if (offer.NativeSize.Width is < 48 or > 8192 || offer.NativeSize.Height is < 48 or > 8192 ||
            (long)offer.NativeSize.Width * offer.NativeSize.Height > 16_777_216)
            throw new InvalidDataException("Invalid native offer size.");
        byte[] bytes = new byte[OfferBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x314F444E);
        bytes[4] = offer.Available ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), offer.TargetGeneration);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), offer.NativeSize.Width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), offer.NativeSize.Height);
        return bytes;
    }

    internal static NativeDetailOffer DecodeOffer(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != OfferBytes || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0x314F444E ||
            bytes[4] > 1 || bytes[5] != 0 || bytes[6] != 0 || bytes[7] != 0 ||
            BinaryPrimitives.ReadInt32LittleEndian(bytes[20..]) != 0)
            throw new InvalidDataException("Invalid native offer.");
        var offer = new NativeDetailOffer(BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]),
            new(BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]), BinaryPrimitives.ReadInt32LittleEndian(bytes[16..])), bytes[4] == 1);
        _ = EncodeOffer(offer);
        return offer;
    }

    internal static byte[] EncodeFeedback(NativeDetailFeedback feedback)
    {
        feedback.Context.Validate();
        if (feedback.ReceivedSequence <= 0 || feedback.PresentedSequence < 0 ||
            feedback.PresentedSequence > feedback.ReceivedSequence || feedback.ReceivedWireBytes <= 0 ||
            (feedback.PresentedSequence == 0 && feedback.CachedTileMask != 0) || feedback.AcknowledgementDelayMilliseconds is < 0 or > 500)
            throw new InvalidDataException("Invalid native feedback counters.");
        byte[] bytes = new byte[FeedbackBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x3146444E);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), feedback.Context.Epoch);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), feedback.Context.Request);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), feedback.Context.Width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), feedback.Context.Height);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(32), feedback.ReceivedSequence);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(40), feedback.PresentedSequence);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(48), feedback.ReceivedWireBytes);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(56), feedback.CachedTileMask);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(64), feedback.AcknowledgementDelayMilliseconds);
        return bytes;
    }

    internal static NativeDetailFeedback DecodeFeedback(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != FeedbackBytes || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0x3146444E ||
            BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]) != 0 || BinaryPrimitives.ReadInt32LittleEndian(bytes[68..]) != 0)
            throw new InvalidDataException("Invalid native feedback.");
        var value = new NativeDetailFeedback(new(BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[16..]), BinaryPrimitives.ReadInt32LittleEndian(bytes[24..]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[28..])), BinaryPrimitives.ReadInt64LittleEndian(bytes[32..]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[40..]), BinaryPrimitives.ReadInt64LittleEndian(bytes[48..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[56..]), BinaryPrimitives.ReadInt32LittleEndian(bytes[64..]));
        _ = EncodeFeedback(value);
        return value;
    }

    internal static byte[] EncodeBase(DetailManifest manifest, RemoteFrame frame)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ValidateBase(manifest, frame);
        byte[] encodedManifest = DetailWire.EncodeManifest(manifest);
        int videoBytes = RemoteMessageCodec.VideoFrameHeaderLength + frame.EncodedLength;
        byte[] result = new byte[12 + encodedManifest.Length + videoBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(result, BaseMagic);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), encodedManifest.Length);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8), videoBytes);
        encodedManifest.CopyTo(result, 12);
        Span<byte> video = result.AsSpan(12 + encodedManifest.Length);
        RemoteMessageCodec.WriteVideoFrameHeader(video[..RemoteMessageCodec.VideoFrameHeaderLength],
            frame.Width, frame.Height, frame.Encoding, frame.Flags,
            frame.CaptureMilliseconds, frame.EncodeMilliseconds);
        frame.EncodedBuffer.AsSpan(frame.EncodedOffset, frame.EncodedLength)
            .CopyTo(video[RemoteMessageCodec.VideoFrameHeaderLength..]);
        return result;
    }

    internal static RemoteFrame DecodeBase(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length < 12 || payload.Length > MaximumBasePayloadBytes ||
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Span) != BaseMagic)
            throw new InvalidDataException("Invalid native base envelope.");
        int manifestBytes = BinaryPrimitives.ReadInt32LittleEndian(payload.Span[4..]);
        int videoBytes = BinaryPrimitives.ReadInt32LittleEndian(payload.Span[8..]);
        if (manifestBytes is < 54 or > MaximumManifestBytes ||
            videoBytes <= RemoteMessageCodec.VideoFrameHeaderLength ||
            videoBytes > RemoteMessageCodec.VideoFrameHeaderLength + MaximumAccessUnitBytes ||
            (long)12 + manifestBytes + videoBytes != payload.Length)
            throw new InvalidDataException("Invalid native base lengths.");
        DetailManifest manifest = DetailWire.DecodeManifest(payload.Span.Slice(12, manifestBytes));
        RemoteFrame frame = RemoteMessageCodec.DecodeVideoFrame(payload.Slice(12 + manifestBytes, videoBytes));
        ValidateBase(manifest, frame);
        return frame with { NativeDetails = new(manifest) };
    }

    private static void ValidateBase(DetailManifest manifest, RemoteFrame frame)
    {
        if (frame.Encoding != RemoteFrameEncoding.H264AnnexB ||
            frame.Flags != (RemoteFrameFlags.KeyFrame | RemoteFrameFlags.CodecConfig) ||
            frame.Width < 48 || frame.Height < 48 || (frame.Width & 1) != 0 || (frame.Height & 1) != 0 ||
            frame.Width > manifest.Context.Width || frame.Height > manifest.Context.Height ||
            frame.EncodedLength is <= 0 or > MaximumAccessUnitBytes || frame.EncodedBuffer is null ||
            frame.EncodedOffset < 0 || frame.EncodedOffset > frame.EncodedBuffer.Length - frame.EncodedLength ||
            !double.IsFinite(frame.CaptureMilliseconds) || frame.CaptureMilliseconds < 0 ||
            !double.IsFinite(frame.EncodeMilliseconds) || frame.EncodeMilliseconds < 0)
            throw new InvalidDataException("Native detail requires a bounded, independent H.264 base.");
    }

    internal static byte[] EncodeRequest(NativeDetailRequest request)
    {
        request.Context.Validate(); request.Viewport.Validate(request.Context);
        byte[] result = new byte[RequestBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(result, RequestMagic);
        result[4] = request.Enabled ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(8), request.Context.Epoch);
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(16), request.Context.Request);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(24), request.Context.Width);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(28), request.Context.Height);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(32), request.Viewport.X);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(36), request.Viewport.Y);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(40), request.Viewport.Width);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(44), request.Viewport.Height);
        return result;
    }

    internal static NativeDetailRequest DecodeRequest(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != RequestBytes || BinaryPrimitives.ReadUInt32LittleEndian(payload) != RequestMagic ||
            payload[4] > 1 || payload[5] != 0 || payload[6] != 0 || payload[7] != 0)
            throw new InvalidDataException("Invalid native detail request.");
        var context = new DetailContext(BinaryPrimitives.ReadInt64LittleEndian(payload[8..]),
            BinaryPrimitives.ReadInt64LittleEndian(payload[16..]),
            BinaryPrimitives.ReadInt32LittleEndian(payload[24..]), BinaryPrimitives.ReadInt32LittleEndian(payload[28..]));
        var viewport = new DetailRect(BinaryPrimitives.ReadInt32LittleEndian(payload[32..]),
            BinaryPrimitives.ReadInt32LittleEndian(payload[36..]),
            BinaryPrimitives.ReadInt32LittleEndian(payload[40..]), BinaryPrimitives.ReadInt32LittleEndian(payload[44..]));
        context.Validate(); viewport.Validate(context);
        return new(context, viewport, payload[4] == 1);
    }
}
