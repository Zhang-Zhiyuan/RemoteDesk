namespace RemoteDesk;

// Older Windows GPU capture paths converted RGB with driver defaults and
// emitted an untagged SPS. MFTs can synthesize range/matrix attributes for
// this stream, so use its VUI to distinguish it from tagged capture.
internal sealed class WindowsH264VideoSignal
{
    private const int MaxSpsBytes = 1024;
    private byte[]? _previousSps;
    private bool _untagged;

    internal bool UsesDriverDefaults(ReadOnlySpan<byte> accessUnit)
    {
        // Inspect only the header; never walk the compressed image.
        ReadOnlySpan<byte> header = accessUnit[..Math.Min(accessUnit.Length, 8192)];
        ReadOnlySpan<byte> prefix = [0, 0, 1];
        int start = header.IndexOf(prefix);
        while (start >= 0 && start + prefix.Length < header.Length)
        {
            header = header[(start + prefix.Length)..];
            int type = header[0] & 31;
            if (type is >= 1 and <= 4) return _untagged;
            if (type == 5) break;
            start = header.IndexOf(prefix);
            if (type != 7) continue;
            if (start < 0 || start > MaxSpsBytes + 1) break;
            var sps = header[..start].TrimEnd((byte)0);
            if (sps.Length < 5 || (sps[0] & 0x80) != 0) break;
            if (!sps.SequenceEqual(_previousSps))
            {
                _untagged = IsVideoSignalAbsent(sps[1..]);
                _previousSps = sps.ToArray();
            }
            return _untagged;
        }
        _previousSps = null;
        _untagged = false;
        return false;
    }

    // ITU-T H.264 sections 7.3.2.1.1 and E.1.1. Unknown syntax leaves
    // normal decoder-based color conversion in effect.
    private static bool IsVideoSignalAbsent(ReadOnlySpan<byte> escaped)
    {
        Span<byte> rbsp = stackalloc byte[MaxSpsBytes];
        int length = 0;
        for (int i = 0; i < escaped.Length; i++)
        {
            if (i >= 2 && escaped[i] == 3 && escaped[i - 1] == 0 && escaped[i - 2] == 0) continue;
            rbsp[length++] = escaped[i];
        }
        var bits = new SpsBits(rbsp[..length]);
        try
        {
            int profile = bits.Read(8);
            bits.Read(16); // constraints and level
            bits.Unsigned(); // SPS id
            if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135 or 144)
            {
                int chroma = bits.Unsigned();
                if (chroma > 3) return false;
                if (chroma == 3) bits.Read(1);
                bits.Unsigned(); // luma and chroma bit depths
                bits.Unsigned();
                bits.Read(1);
                if (bits.Flag())
                {
                    int lists = chroma == 3 ? 12 : 8;
                    for (int i = 0; i < lists; i++)
                        if (bits.Flag()) bits.SkipScalingList(i < 6 ? 16 : 64);
                }
            }
            else if (profile is not (66 or 77 or 88)) return false;

            bits.Unsigned(); // log2_max_frame_num_minus4
            int order = bits.Unsigned();
            if (order == 0) bits.Unsigned();
            else if (order == 1)
            {
                bits.Read(1);
                bits.Unsigned(); // signed offsets use the same code length
                bits.Unsigned();
                int cycle = bits.Unsigned();
                if (cycle > 255) return false;
                for (int i = 0; i < cycle; i++) bits.Unsigned();
            }
            else if (order != 2) return false;
            bits.Unsigned(); // max reference frames
            bits.Read(1);
            bits.Unsigned(); // dimensions in macroblocks
            bits.Unsigned();
            if (!bits.Flag()) bits.Read(1);
            bits.Read(1); // direct_8x8_inference_flag
            if (bits.Flag())
                for (int i = 0; i < 4; i++) bits.Unsigned();
            if (!bits.Flag()) return true; // no VUI
            if (bits.Flag() && bits.Read(8) == 255) { bits.Read(16); bits.Read(16); }
            if (bits.Flag()) bits.Read(1); // overscan
            return !bits.Flag(); // video_signal_type_present_flag
        }
        catch (InvalidDataException) { return false; }
    }

    private ref struct SpsBits(ReadOnlySpan<byte> bytes)
    {
        private readonly ReadOnlySpan<byte> _bytes = bytes;
        private int _offset;

        internal int Read(int count)
        {
            if (count > _bytes.Length * 8 - _offset) throw new InvalidDataException();
            int value = 0;
            for (int end = _offset + count; _offset < end; _offset++)
                value = (value << 1) | ((_bytes[_offset / 8] >> (7 - _offset % 8)) & 1);
            return value;
        }

        internal bool Flag() => Read(1) != 0;

        internal int Unsigned()
        {
            int zeros = 0;
            while (!Flag())
                if (++zeros > 30) throw new InvalidDataException();
            return (1 << zeros) - 1 + Read(zeros);
        }

        internal void SkipScalingList(int count)
        {
            int scale = 8;
            for (int i = 0; i < count && scale != 0; i++)
            {
                int code = Unsigned();
                int delta = (code & 1) == 0 ? -(code / 2) : code / 2 + 1;
                scale = (scale + delta) & 255;
            }
        }
    }
}
