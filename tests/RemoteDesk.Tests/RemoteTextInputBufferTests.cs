using Xunit;

namespace RemoteDesk.Tests;

public sealed class RemoteTextInputBufferTests
{
    [Theory]
    [InlineData('a', "a")]
    [InlineData('中', "中")]
    [InlineData(' ', " ")]
    [InlineData('\r', "")]
    [InlineData('\b', "")]
    public void OrdinaryCharactersAreSentAsText(char value, string expected)
    {
        Assert.Equal(expected, new RemoteTextInputBuffer().Append(value, 1));
    }

    [Fact]
    public void EmojiIsSentAsOneCompleteCodePoint()
    {
        var buffer = new RemoteTextInputBuffer();
        Assert.Empty(buffer.Append('\ud83d', 1));
        Assert.Equal("😀", buffer.Append('\ude00', 1));
    }

    [Fact]
    public void OrphanSurrogateDoesNotCorruptFollowingChinese()
    {
        var buffer = new RemoteTextInputBuffer();
        Assert.Empty(buffer.Append('\ude00', 1));
        Assert.Empty(buffer.Append('\ud83d', 1));
        Assert.Equal("文", buffer.Append('文', 1));
        Assert.Empty(buffer.Append('\ude00', 1));
    }

    [Fact]
    public void SurrogateCannotCrossConnectionsOrFocusReset()
    {
        var buffer = new RemoteTextInputBuffer();
        Assert.Empty(buffer.Append('\ud83d', 1));
        Assert.Empty(buffer.Append('\ude00', 2));
        Assert.Empty(buffer.Append('\ud83d', 2));
        buffer.Reset();
        Assert.Empty(buffer.Append('\ude00', 2));
    }
}
