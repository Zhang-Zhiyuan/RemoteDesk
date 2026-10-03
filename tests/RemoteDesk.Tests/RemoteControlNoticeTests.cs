using System.Reflection;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class RemoteControlNoticeTests
{
    [Fact]
    public void OnlyAuthenticatedLeasesMarkActivityAndOldCleanupCannotHideReplacement()
    {
        var activity = new RemoteControlActivity();
        Assert.False(activity.IsActive);
        var first = activity.Begin(() => Task.CompletedTask);
        using var second = activity.Begin(() => Task.CompletedTask);
        first.Dispose();
        first.Dispose();
        Assert.True(activity.IsActive);
        second.Dispose();
        Assert.False(activity.IsActive);
    }

    [Fact]
    public void DelayedUiCallbacksReadCurrentStateInsteadOfReplayingStaleTransitions()
    {
        var activity = new RemoteControlActivity();
        var queued = new List<Func<bool>>();
        activity.Changed += () => queued.Add(() => activity.IsActive);
        activity.Begin(() => Task.CompletedTask).Dispose();
        using var live = activity.Begin(() => Task.CompletedTask);
        Assert.All(queued, read => Assert.True(read()));
    }

    [Fact]
    public async Task LocalDisconnectRunsOutsideStateLockAndWaitsForBothOwners()
    {
        var activity = new RemoteControlActivity();
        IDisposable? first = null, second = null;
        first = activity.Begin(() => { first!.Dispose(); return Task.CompletedTask; });
        second = activity.Begin(() => { second!.Dispose(); return Task.CompletedTask; });
        await activity.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(activity.IsActive);
        await activity.DisconnectAsync();
    }

    [Fact]
    public async Task ConcurrentLeasesLeaveNoStaleIndicator()
    {
        var activity = new RemoteControlActivity();
        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() =>
        {
            using var lease = activity.Begin(() => Task.CompletedTask);
            Assert.True(activity.IsActive);
        })));
        Assert.False(activity.IsActive);
    }

    [Theory]
    [InlineData(320, 9f)]
    [InlineData(320, 18f)]
    [InlineData(640, 24f)]
    [InlineData(1920, 12f)]
    public void NoticeWrapsTextWithoutCoveringItsDisconnectButton(int width, float fontSize)
    {
        using var notice = new RemoteControlNotice();
        using var font = new Font("Microsoft YaHei UI", fontSize);
        notice.Font = font;
        var area = new Rectangle(-width, 30, width, 600);
        notice.ArrangeOnScreen(area);
        var label = notice.Controls.OfType<Label>().Single();
        var button = notice.Controls.OfType<Button>().Single();
        Assert.True(area.Contains(notice.Bounds));
        Assert.True(notice.ClientRectangle.Contains(label.Bounds));
        Assert.True(notice.ClientRectangle.Contains(button.Bounds));
        Assert.False(label.Bounds.IntersectsWith(button.Bounds));
        Assert.True(label.Height >= TextRenderer.MeasureText(label.Text, font,
            new Size(label.Width, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height);
    }

    [Fact]
    public void NoticeDoesNotActivateAndStaysSeparateFromMainWindow()
    {
        using var notice = new RemoteControlNotice();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var cp = (CreateParams)typeof(RemoteControlNotice).GetProperty("CreateParams", flags)!.GetValue(notice)!;
        Assert.NotEqual(0, cp.ExStyle & 0x08000000);
        Assert.NotEqual(0, cp.ExStyle & 0x80);
        Assert.True((bool)typeof(RemoteControlNotice).GetProperty("ShowWithoutActivation", flags)!.GetValue(notice)!);
        Assert.Null(notice.Owner);
        Assert.False(notice.ShowInTaskbar);
        Assert.NotEqual(0, cp.ExStyle & 0x8);
    }

    [Fact]
    public void ScreenRemovalClampsOffscreenNoticeBackIntoVisibleWorkArea()
    {
        var area = new Rectangle(0, 40, 1280, 680);
        Rectangle result = RemoteControlNotice.PlaceWithin(area, new Size(400, 50), new Point(-1800, 1000));
        Assert.True(area.Contains(result));
        Assert.Equal(new Point(0, 670), result.Location);
    }
}
