namespace RemoteDesk;

// WM_CHAR/WM_IME_CHAR carry UTF-16 code units, not Unicode code points.
internal sealed class RemoteTextInputBuffer
{
    private char _highSurrogate;
    private long _generation;

    public string Append(char value, long generation)
    {
        if (_generation != generation)
        {
            Reset();
            _generation = generation;
        }

        if (char.IsHighSurrogate(value))
        {
            _highSurrogate = value;
            return string.Empty;
        }

        char high = _highSurrogate;
        _highSurrogate = '\0';
        if (char.IsLowSurrogate(value))
        {
            return high == '\0' ? string.Empty : new string([high, value]);
        }

        return char.IsControl(value) ? string.Empty : value.ToString();
    }

    public void Reset()
    {
        _highSurrogate = '\0';
        _generation = 0;
    }
}
