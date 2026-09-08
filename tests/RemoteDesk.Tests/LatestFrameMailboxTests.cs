using Xunit;

namespace RemoteDesk.Tests;

public sealed class LatestFrameMailboxTests
{
    [Fact]
    public void OfferKeepsOnlyLatestFrameWhileUiDispatchIsPending()
    {
        var mailbox = new LatestFrameMailbox<string>();

        LatestFrameOffer<string> first = mailbox.Offer("frame-1");
        LatestFrameOffer<string> second = mailbox.Offer("frame-2");

        Assert.True(first.Accepted);
        Assert.True(first.ShouldSchedule);
        Assert.True(mailbox.HasPendingDispatch);
        Assert.Null(first.Replaced);
        Assert.True(second.Accepted);
        Assert.False(second.ShouldSchedule);
        Assert.Equal("frame-1", second.Replaced);
        Assert.Equal("frame-2", mailbox.TakeLatest());
        Assert.False(mailbox.HasPendingDispatch);
        Assert.False(mailbox.CompleteDispatch());
    }

    [Fact]
    public void FrameOfferedDuringDispatchRequestsOneFollowUpDispatch()
    {
        var mailbox = new LatestFrameMailbox<string>();

        Assert.True(mailbox.Offer("frame-1").ShouldSchedule);
        Assert.Equal("frame-1", mailbox.TakeLatest());
        Assert.False(mailbox.Offer("frame-2").ShouldSchedule);
        Assert.True(mailbox.CompleteDispatch());
        Assert.Equal("frame-2", mailbox.TakeLatest());
        Assert.False(mailbox.CompleteDispatch());
    }

    [Fact]
    public void CloseReturnsPendingFrameAndRejectsFutureFrames()
    {
        var mailbox = new LatestFrameMailbox<string>();
        mailbox.Offer("pending");

        Assert.Equal("pending", mailbox.Close());
        Assert.False(mailbox.HasPendingDispatch);

        LatestFrameOffer<string> rejected = mailbox.Offer("late");
        Assert.False(rejected.Accepted);
        Assert.False(rejected.ShouldSchedule);
        Assert.Null(rejected.Replaced);
        Assert.Null(mailbox.TakeLatest());
    }
}
