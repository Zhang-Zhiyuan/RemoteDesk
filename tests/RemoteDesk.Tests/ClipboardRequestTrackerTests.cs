using Xunit;

namespace RemoteDesk.Tests;

public sealed class ClipboardRequestTrackerTests
{
    [Fact]
    public void SlowRelayReplyUsesItsOwnDeadlineAndStillRejectsChangedClipboard()
    {
        long now = 0;
        var tracker = new ClipboardRequestTracker(() => now);
        var request = tracker.Begin(1, true, 10, ClipboardRequestTracker.RelayTimeoutMilliseconds)!;
        now = 12000;
        Assert.Equal(18000, request.RemainingTimeoutMilliseconds);
        Assert.False(request.Expired);
        Assert.True(tracker.CanApply(request, 10));
        Assert.False(tracker.CanApply(request, 11));
        now = 30000;
        Assert.Equal(0, request.RemainingTimeoutMilliseconds);
        Assert.True(request.Expired);
        Assert.False(tracker.CanApply(request, 10));
        Assert.Null(tracker.Begin(1, false));
        Assert.Same(request, tracker.Take(1, true, true));
        Assert.NotNull(tracker.Begin(1, false));
    }

    [Fact]
    public void ReadReplyIsFencedByLocalClipboardSequenceAndConnection()
    {
        var tracker = new ClipboardRequestTracker();
        var request = tracker.Begin(4, read: true, localSequence: 10)!;
        Assert.Null(tracker.Begin(4, read: false));
        Assert.Null(tracker.Take(3, textReply: true, success: true));
        Assert.Null(tracker.Take(4, textReply: false, success: true));
        Assert.Same(request, tracker.Take(4, textReply: true, success: true));
        Assert.True(tracker.CanApply(request, 10));
        Assert.False(tracker.CanApply(request, 11));
        Assert.NotNull(tracker.Begin(5, read: true, localSequence: 10));
        Assert.False(tracker.CanApply(request, 10));
        Assert.Null(tracker.Begin(4, read: false));
    }

    [Fact]
    public void LaterUserRequestInvalidatesAnAlreadyQueuedClipboardWrite()
    {
        var tracker = new ClipboardRequestTracker();
        var request = tracker.Begin(1, read: true, localSequence: 10)!;
        tracker.Take(1, textReply: true, success: true);
        tracker.Begin(1, read: false);
        Assert.False(tracker.CanApply(request, 10));
    }

    [Fact]
    public void TimeoutRetainsReplySlotUntilDrained()
    {
        long now = 0;
        var tracker = new ClipboardRequestTracker(() => now);
        var request = tracker.Begin(1, read: true, localSequence: 10)!;
        now = 8000;
        Assert.True(request.Expired);
        Assert.False(tracker.CanApply(request, 10));
        Assert.Null(tracker.Begin(1, read: true));
        Assert.Same(request, tracker.Take(1, textReply: true, success: true));
        Assert.NotNull(tracker.Begin(1, read: true));
    }

    [Fact]
    public async Task WritesWaitForStatusAndReconnectCompletesOldWaiters()
    {
        var tracker = new ClipboardRequestTracker();
        var request = tracker.Begin(1, read: false)!;
        Assert.False(request.Completion.Task.IsCompleted);
        Assert.Null(tracker.Take(1, textReply: true, success: true));
        Assert.NotNull(tracker.Begin(2, read: true));
        Assert.True(request.Completion.Task.IsCompletedSuccessfully);
        Assert.False(await request.Completion.Task);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FailureDrainsEitherKindOfRequest(bool read)
    {
        var tracker = new ClipboardRequestTracker();
        var request = tracker.Begin(1, read)!;
        Assert.Same(request, tracker.Take(1, textReply: false, success: false));
        Assert.NotNull(tracker.Begin(1, read));
    }

    [Fact]
    public async Task CancelledStaWriteDoesNotTouchSystemClipboard()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            ClipboardTextService.SetTextAsync("must not be written", () => false));
    }
}
