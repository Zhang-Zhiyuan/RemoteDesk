using System.Windows.Forms;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class SettingsSaveNoticeTests
{
    [Fact]
    public void NoticeStaysPendingAfterFailedRetryAndClearsOnlyOnSuccess()
    {
        using var notice = new SettingsSaveNotice();
        Assert.False(notice.HasPendingChanges);
        Assert.False(notice.Visible);

        notice.ApplyResult(new SettingsSaveResult(false, "access denied"));
        Assert.True(notice.HasPendingChanges);
        Assert.Contains("设置未保存", notice.MessageText);
        Assert.Contains("access denied", notice.MessageText);
        Assert.True(notice.Visible);

        int retries = 0;
        notice.RetryRequested += (_, _) =>
        {
            retries++;
            notice.ApplyResult(new SettingsSaveResult(retries >= 2, retries < 2 ? "disk full" : null));
        };
        notice.RetryButton.PerformClick();
        Assert.Equal(1, retries);
        Assert.True(notice.HasPendingChanges);
        Assert.Contains("disk full", notice.MessageText);
        notice.RetryButton.PerformClick();
        Assert.Equal(2, retries);
        Assert.False(notice.HasPendingChanges);
        Assert.False(notice.Visible);
        Assert.Empty(notice.MessageText);
    }

    [Theory]
    [InlineData(360)]
    [InlineData(640)]
    public void NarrowNoticeKeepsRetryButtonWithinItsBounds(int width)
    {
        using var notice = new SettingsSaveNotice { Width = width };
        notice.ApplyResult(new SettingsSaveResult(false, new string('x', 200)));
        notice.Height = notice.GetPreferredSize(new Size(width, 0)).Height;
        notice.PerformLayout();

        Assert.True(notice.Height > 0);
        Assert.InRange(notice.RetryButton.Left, 0, width - 1);
        Assert.InRange(notice.RetryButton.Right, 1, width);
        Assert.InRange(notice.RetryButton.Bottom, 1, notice.Height);
    }
}
