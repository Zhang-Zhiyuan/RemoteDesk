using System.Text;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class ClipboardSnapshotProtocolTests
{
    private const string EmptyRevision = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private const string Text = "中文😀";
    private const string Revision = "e973a1c1b41c5c9f4fbac31fcc311536dfffd003bfb914580455170a599953fa";

    [Fact]
    public void RequestWireUsesAssignedKindAndBinaryWriterStrings()
    {
        Assert.Equal(new byte[] { 40, 2, 0x72, 0x31, 0 },
            RemoteMessageCodec.EncodeClipboardSnapshotRequest("r1", string.Empty));
        byte[] known = RemoteMessageCodec.EncodeClipboardSnapshotRequest("r1", Revision);
        RemoteControlMessage decoded = RemoteMessageCodec.DecodeControl(known);
        Assert.Equal(RemoteControlKind.ClipboardSnapshotRequest, decoded.Kind);
        Assert.Equal("r1", decoded.TransferId);
        Assert.Equal(Revision, decoded.ClipboardRevision);
        Assert.Equal(Revision, RemoteMessageCodec.ComputeClipboardRevision(Text));
    }

    [Fact]
    public void ChangedUnicodeSnapshotUsesTheSharedWireFieldOrder()
    {
        byte[] payload = RemoteMessageCodec.EncodeClipboardSnapshot("r1", true, Revision, true, true, Text, "ok");
        Assert.Equal(RawSnapshot("r1", true, Revision, true, true, Text, "ok"), payload);
        RemoteControlMessage decoded = RemoteMessageCodec.DecodeControl(payload);
        Assert.Equal(RemoteControlKind.ClipboardSnapshot, decoded.Kind);
        Assert.Equal("r1", decoded.TransferId);
        Assert.True(decoded.Success);
        Assert.Equal(Revision, decoded.ClipboardRevision);
        Assert.True(decoded.ClipboardHasText);
        Assert.True(decoded.ClipboardChanged);
        Assert.Equal(Text, decoded.Text);
        Assert.Equal("ok", decoded.StatusMessage);
    }

    [Fact]
    public void UnchangedSnapshotCarriesOnlyRevisionAndPresenceNotCachedText()
    {
        RemoteControlMessage decoded = RemoteMessageCodec.DecodeControl(
            RemoteMessageCodec.EncodeClipboardSnapshot("unchanged", true, Revision, true, false, "", ""));
        Assert.True(decoded.Success);
        Assert.True(decoded.ClipboardHasText);
        Assert.False(decoded.ClipboardChanged);
        Assert.Equal("", decoded.Text);
        Assert.Equal(Revision, decoded.ClipboardRevision);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EmptyClipboardHasAnEmptyTextDigestAndNoText(bool changed)
    {
        RemoteControlMessage decoded = RemoteMessageCodec.DecodeControl(
            RemoteMessageCodec.EncodeClipboardSnapshot("empty", true, EmptyRevision, false, changed, "", ""));
        Assert.True(decoded.Success);
        Assert.False(decoded.ClipboardHasText);
        Assert.Equal(changed, decoded.ClipboardChanged);
        Assert.Equal(EmptyRevision, decoded.ClipboardRevision);
        Assert.Equal("", decoded.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData(EmptyRevision)]
    public void FailedSnapshotContainsNoTextOrChange(string revision)
    {
        RemoteControlMessage decoded = RemoteMessageCodec.DecodeControl(
            RemoteMessageCodec.EncodeClipboardSnapshot("error", false, revision, false, false, "", "请重试。"));
        Assert.False(decoded.Success);
        Assert.False(decoded.ClipboardHasText);
        Assert.False(decoded.ClipboardChanged);
        Assert.Equal("", decoded.Text);
        Assert.Equal("请重试。", decoded.StatusMessage);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    public void RequestIdentifierLengthIsValidatedOnEncodeAndDecode(int length)
    {
        string id = new('a', length);
        Assert.Throws<InvalidDataException>(() => RemoteMessageCodec.EncodeClipboardSnapshotRequest(id, ""));
        Assert.Throws<InvalidDataException>(() => RemoteMessageCodec.DecodeControl(RawRequest(id, "")));
    }

    [Fact]
    public void IdentifierLimitIsUtf16CharactersAcrossUtf8WireEncoding()
    {
        string id = string.Concat(Enumerable.Repeat("😀", 32));
        Assert.Equal(id, RemoteMessageCodec.DecodeControl(
            RemoteMessageCodec.EncodeClipboardSnapshotRequest(id, "")).TransferId);
        Assert.Throws<InvalidDataException>(() => RemoteMessageCodec.DecodeControl(RawRequest(id + "a", "")));
    }

    [Theory]
    [InlineData("x")]
    [InlineData("E973A1C1B41C5C9F4FBAC31FCC311536DFFFD003BFB914580455170A599953FA")]
    [InlineData("g973a1c1b41c5c9f4fbac31fcc311536dfffd003bfb914580455170a599953fa")]
    public void RevisionMustBeEmptyOr64LowercaseHexCharacters(string revision)
    {
        Assert.Throws<InvalidDataException>(() => RemoteMessageCodec.EncodeClipboardSnapshotRequest("r", revision));
        Assert.Throws<InvalidDataException>(() => RemoteMessageCodec.DecodeControl(RawRequest("r", revision)));
        Assert.Throws<InvalidDataException>(() => RemoteMessageCodec.DecodeControl(
            RawSnapshot("r", true, revision, true, false, "", "")));
    }

    public static IEnumerable<object[]> InconsistentResponses()
    {
        yield return [true, "", false, false, ""];
        yield return [false, "", true, false, ""];
        yield return [false, "", false, true, ""];
        yield return [false, "", false, false, "secret"];
        yield return [true, Revision, true, false, Text];
        yield return [true, Revision, false, true, Text];
        yield return [true, Revision, true, true, "wrong"];
        yield return [true, Revision, false, false, ""];
        yield return [true, EmptyRevision, true, false, ""];
        yield return [true, EmptyRevision, true, true, Text];
    }

    [Theory]
    [MemberData(nameof(InconsistentResponses))]
    public void InconsistentResponseFieldsAreRejectedOnBothSides(
        bool success, string revision, bool hasText, bool changed, string text)
    {
        Assert.Throws<InvalidDataException>(() => RemoteMessageCodec.EncodeClipboardSnapshot(
            "r", success, revision, hasText, changed, text, ""));
        Assert.Throws<InvalidDataException>(() => RemoteMessageCodec.DecodeControl(
            RawSnapshot("r", success, revision, hasText, changed, text, "")));
    }

    [Fact]
    public void MaximumUtf16TextLengthIsAcceptedAndOneMoreRejected()
    {
        string text = string.Concat(Enumerable.Repeat("中😀", 85_333)) + "a";
        Assert.Equal(256_000, text.Length);
        string revision = RemoteMessageCodec.ComputeClipboardRevision(text);
        Assert.Equal(text, RemoteMessageCodec.DecodeControl(RemoteMessageCodec.EncodeClipboardSnapshot(
            "r", true, revision, true, true, text, "")).Text);
        text += "a";
        revision = RemoteMessageCodec.ComputeClipboardRevision(text);
        Assert.Throws<InvalidDataException>(() => RemoteMessageCodec.EncodeClipboardSnapshot(
            "r", true, revision, true, true, text, ""));
        Assert.Throws<InvalidDataException>(() => RemoteMessageCodec.DecodeControl(
            RawSnapshot("r", true, revision, true, true, text, "")));
    }

    [Fact]
    public void StatusTextLimitIsValidatedOnEncodeAndDecode()
    {
        string status = new('中', 4096);
        Assert.Equal(status, RemoteMessageCodec.DecodeControl(RemoteMessageCodec.EncodeClipboardSnapshot(
            "r", false, "", false, false, "", status)).StatusMessage);
        Assert.Throws<InvalidDataException>(() => RemoteMessageCodec.EncodeClipboardSnapshot(
            "r", false, "", false, false, "", status + "x"));
        Assert.Throws<InvalidDataException>(() => RemoteMessageCodec.DecodeControl(
            RawSnapshot("r", false, "", false, false, "", status + "x")));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(69)]
    [InlineData(70)]
    public void SnapshotBooleansOnlyAcceptZeroOrOne(int offset)
    {
        byte[] payload = RawSnapshot("r", true, EmptyRevision, false, true, "", "");
        payload[offset] = 2;
        Assert.Throws<InvalidDataException>(() => RemoteMessageCodec.DecodeControl(payload));
    }

    [Fact]
    public void InvalidUtf8AndInvalidLengthPrefixesAreRejectedWithoutExposingBytes()
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            RemoteMessageCodec.DecodeControl(new byte[] { 40, 1, 0xff, 0 }));
        Assert.Null(error.InnerException);
        Assert.Throws<InvalidDataException>(() => RemoteMessageCodec.DecodeControl(
            new byte[] { 40, 0xff, 0xff, 0xff, 0xff, 0x7f }));
        Assert.Throws<InvalidDataException>(() => RemoteMessageCodec.DecodeControl(
            new byte[] { 40, 0x81, 0x02 }));
        Assert.Throws<EndOfStreamException>(() => RemoteMessageCodec.DecodeControl(
            new byte[] { 40, 2, 0x72 }));
    }

    [Fact]
    public void TrailingBytesAndTruncatedResponsesAreRejected()
    {
        byte[] request = RemoteMessageCodec.EncodeClipboardSnapshotRequest("r", "");
        Assert.Throws<InvalidDataException>(() => RemoteMessageCodec.DecodeControl(request.Concat(new byte[] { 0 }).ToArray()));
        byte[] response = RemoteMessageCodec.EncodeClipboardSnapshot("r", true, Revision, true, true, Text, "");
        Assert.Throws<EndOfStreamException>(() => RemoteMessageCodec.DecodeControl(response.AsMemory(0, response.Length - 1)));
        Assert.Throws<InvalidDataException>(() => RemoteMessageCodec.DecodeControl(response.Concat(new byte[] { 0 }).ToArray()));
    }

    private static byte[] RawRequest(string id, string revision)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8);
        writer.Write((byte)40);
        writer.Write(id);
        writer.Write(revision);
        return stream.ToArray();
    }

    private static byte[] RawSnapshot(string id, bool success, string revision, bool hasText,
        bool changed, string text, string status)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8);
        writer.Write((byte)41);
        writer.Write(id);
        writer.Write(success);
        writer.Write(revision);
        writer.Write(hasText);
        writer.Write(changed);
        writer.Write(text);
        writer.Write(status);
        return stream.ToArray();
    }
}
