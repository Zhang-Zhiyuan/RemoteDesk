using Xunit;

namespace RemoteDesk.Tests;

public sealed class AnnexBH264AccessUnitParserTests
{
    [Fact]
    public void ConstructorRejectsLimitThatCannotIncludeDelimiterLookahead()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AnnexBH264AccessUnitParser(int.MaxValue));
    }

    [Fact]
    public void AppendSplitsMixedStartCodesAcrossSingleByteReads()
    {
        byte[] stream = Join(
            BuildIdrAccessUnit(fourByteAud: true, includeConfig: true, 0x11),
            BuildIdrAccessUnit(fourByteAud: false, includeConfig: true, 0x22),
            BuildIdrAccessUnit(fourByteAud: true, includeConfig: true, 0x33));
        var parser = new AnnexBH264AccessUnitParser();
        var units = new List<AnnexBH264AccessUnit>();

        foreach (byte value in stream)
        {
            units.AddRange(parser.Append([value]));
        }

        units.AddRange(parser.Complete());

        Assert.Equal(3, units.Count);
        Assert.All(units, AssertIndependentIdr);
        Assert.Equal(0x11, GetLastPayloadByte(units[0].Bytes));
        Assert.Equal(0x22, GetLastPayloadByte(units[1].Bytes));
        Assert.Equal(0x33, GetLastPayloadByte(units[2].Bytes));
    }

    [Fact]
    public void AppendReturnsCompletedUnitOnlyAfterNextAud()
    {
        byte[] first = BuildIdrAccessUnit(
            fourByteAud: true,
            includeConfig: true,
            0x41);
        byte[] secondAud = Nal(9, fourByteStartCode: false, 0xF0);
        var parser = new AnnexBH264AccessUnitParser();

        Assert.Empty(parser.Append(first));
        AnnexBH264AccessUnit unit =
            Assert.Single(parser.Append(secondAud));

        AssertIndependentIdr(unit);
        Assert.Equal(0x41, GetLastPayloadByte(unit.Bytes));
        Assert.Empty(parser.Complete());
    }

    [Fact]
    public void CompleteFlushesFinalAccessUnitExactlyOnce()
    {
        var parser = new AnnexBH264AccessUnitParser();
        parser.Append(BuildIdrAccessUnit(
            fourByteAud: true,
            includeConfig: true,
            0x51));

        AnnexBH264AccessUnit unit = Assert.Single(parser.Complete());

        AssertIndependentIdr(unit);
        Assert.Empty(parser.Complete());
        Assert.Throws<InvalidOperationException>(
            () => parser.Append([0x00]));
    }

    [Fact]
    public void ParameterSetsBeforeFirstAudAreCachedAndPrepended()
    {
        byte[] prefix = Join(
            Nal(7, fourByteStartCode: true, 0x64, 0x00, 0x1F),
            Nal(8, fourByteStartCode: false, 0xEE, 0x06));
        byte[] idr = Join(
            Nal(9, fourByteStartCode: true, 0xF0),
            Nal(5, fourByteStartCode: false, 0x61));
        var parser = new AnnexBH264AccessUnitParser();

        parser.Append(Join(prefix, idr));
        AnnexBH264AccessUnit unit = Assert.Single(parser.Complete());

        AssertIndependentIdr(unit);
        Assert.Equal([9, 7, 8, 5], ReadNalTypes(unit.Bytes));
    }

    [Fact]
    public void LaterIdrWithoutConfigReceivesCachedSpsAndPps()
    {
        byte[] first = BuildIdrAccessUnit(
            fourByteAud: true,
            includeConfig: true,
            0x71);
        byte[] second = BuildIdrAccessUnit(
            fourByteAud: false,
            includeConfig: false,
            0x72);
        var parser = new AnnexBH264AccessUnitParser();

        IReadOnlyList<AnnexBH264AccessUnit> beforeEof =
            parser.Append(Join(first, second));
        AnnexBH264AccessUnit firstUnit = Assert.Single(beforeEof);
        AnnexBH264AccessUnit secondUnit =
            Assert.Single(parser.Complete());

        AssertIndependentIdr(firstUnit);
        AssertIndependentIdr(secondUnit);
        Assert.Equal([9, 7, 8, 5], ReadNalTypes(secondUnit.Bytes));
        Assert.Equal(0x72, GetLastPayloadByte(secondUnit.Bytes));
    }

    [Fact]
    public void UnsafeIdrWithoutKnownParameterSetsIsWithheld()
    {
        var parser = new AnnexBH264AccessUnitParser();
        parser.Append(BuildIdrAccessUnit(
            fourByteAud: true,
            includeConfig: false,
            0x7A));

        Assert.Empty(parser.Complete());
    }

    [Fact]
    public void NonIdrIsRecognizedAsDependent()
    {
        byte[] accessUnit = Join(
            Nal(9, fourByteStartCode: true, 0xF0),
            Nal(1, fourByteStartCode: false, 0x81));
        var parser = new AnnexBH264AccessUnitParser();
        parser.Append(accessUnit);

        AnnexBH264AccessUnit unit = Assert.Single(parser.Complete());

        Assert.False(unit.IsIdr);
        Assert.False(unit.HasSps);
        Assert.False(unit.HasPps);
        Assert.Equal(RemoteFrameFlags.None, unit.Flags);
    }

    [Fact]
    public void OversizedPendingAccessUnitIsRejectedAndCleared()
    {
        var parser = new AnnexBH264AccessUnitParser(
            maxAccessUnitBytes: 32);
        byte[] oversized = Join(
            Nal(9, fourByteStartCode: true, 0xF0),
            Nal(
                5,
                fourByteStartCode: true,
                Enumerable.Repeat((byte)0x55, 40).ToArray()));

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => parser.Append(oversized));

        Assert.Contains("32-byte", exception.Message);
        Assert.Empty(parser.Complete());
    }

    [Fact]
    public void AudWithoutVclIsNotPublishedAtEof()
    {
        var parser = new AnnexBH264AccessUnitParser();
        parser.Append(Nal(9, fourByteStartCode: true, 0xF0));

        Assert.Empty(parser.Complete());
    }

    private static void AssertIndependentIdr(
        AnnexBH264AccessUnit unit)
    {
        Assert.True(unit.IsIdr);
        Assert.True(unit.HasSps);
        Assert.True(unit.HasPps);
        Assert.Equal(
            RemoteFrameFlags.KeyFrame |
                RemoteFrameFlags.CodecConfig,
            unit.Flags);
        Assert.Equal([9, 7, 8, 5], ReadNalTypes(unit.Bytes));
    }

    private static byte[] BuildIdrAccessUnit(
        bool fourByteAud,
        bool includeConfig,
        byte payload)
    {
        var nalUnits = new List<byte[]>
        {
            Nal(9, fourByteAud, 0xF0)
        };
        if (includeConfig)
        {
            nalUnits.Add(Nal(
                7,
                fourByteStartCode: false,
                0x64,
                0x00,
                0x1F));
            nalUnits.Add(Nal(
                8,
                fourByteStartCode: true,
                0xEE,
                0x06));
        }

        nalUnits.Add(Nal(
            5,
            fourByteStartCode: false,
            payload));
        return Join(nalUnits.ToArray());
    }

    private static byte[] Nal(
        int nalType,
        bool fourByteStartCode,
        params byte[] payload)
    {
        byte[] prefix = fourByteStartCode
            ? [0x00, 0x00, 0x00, 0x01]
            : [0x00, 0x00, 0x01];
        byte[] nal = new byte[
            prefix.Length +
            1 +
            payload.Length];
        prefix.CopyTo(nal, 0);
        nal[prefix.Length] = (byte)(
            nalType switch
            {
                5 => 0x60 | nalType,
                7 => 0x60 | nalType,
                8 => 0x60 | nalType,
                _ => nalType
            });
        payload.CopyTo(nal, prefix.Length + 1);
        return nal;
    }

    private static byte[] Join(params byte[][] arrays)
    {
        byte[] joined = new byte[arrays.Sum(array => array.Length)];
        int offset = 0;
        foreach (byte[] array in arrays)
        {
            array.CopyTo(joined, offset);
            offset += array.Length;
        }

        return joined;
    }

    private static int[] ReadNalTypes(
        ReadOnlyMemory<byte> memory)
    {
        ReadOnlySpan<byte> bytes = memory.Span;
        var types = new List<int>();
        for (int index = 0; index <= bytes.Length - 4; index++)
        {
            int startCodeLength = 0;
            if (bytes[index] == 0 &&
                bytes[index + 1] == 0 &&
                bytes[index + 2] == 1)
            {
                startCodeLength = 3;
            }
            else if (index + 4 < bytes.Length &&
                bytes[index] == 0 &&
                bytes[index + 1] == 0 &&
                bytes[index + 2] == 0 &&
                bytes[index + 3] == 1)
            {
                startCodeLength = 4;
            }

            if (startCodeLength == 0)
            {
                continue;
            }

            types.Add(bytes[index + startCodeLength] & 0x1F);
            index += startCodeLength;
        }

        return types.ToArray();
    }

    private static byte GetLastPayloadByte(
        ReadOnlyMemory<byte> bytes)
    {
        return bytes.Span[^1];
    }
}
