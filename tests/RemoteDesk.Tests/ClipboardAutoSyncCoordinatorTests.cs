using Xunit;

namespace RemoteDesk.Tests;

public sealed class ClipboardAutoSyncCoordinatorTests
{
    [Fact]
    public async Task InitialBaselineDoesNotReadOrOverwriteEitherExistingClipboard()
    {
        var fixture = new Fixture { LocalText = "local old", RemoteText = "remote old" };
        Assert.True(await fixture.Sync());
        Assert.Equal("local old", fixture.LocalText);
        Assert.Equal("remote old", fixture.RemoteText);
        Assert.Equal(0, fixture.LocalReads);
        Assert.Empty(fixture.Sent);
        Assert.Empty(fixture.Applied);
        Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task MouseCopyOnLocalPushesTextAfterBaseline()
    {
        var fixture = new Fixture();
        Assert.True(await fixture.Sync());
        fixture.CopyLocal("mouse copied 中文😀");
        Assert.True(await fixture.Sync());
        Assert.Equal("mouse copied 中文😀", Assert.Single(fixture.Sent));
        Assert.Equal(fixture.LocalText, fixture.RemoteText);
        Assert.Empty(fixture.Applied);
    }

    [Fact]
    public async Task MouseCopyOnRemotePullsTextAfterBaselineAndDoesNotEchoItBack()
    {
        var fixture = new Fixture();
        Assert.True(await fixture.Sync());
        fixture.RemoteText = "remote copied 中文😀";
        Assert.True(await fixture.Sync());
        Assert.Equal(fixture.RemoteText, Assert.Single(fixture.Applied));
        Assert.True(await fixture.Sync());
        Assert.True(await fixture.Sync());
        Assert.Single(fixture.Applied);
        Assert.Empty(fixture.Sent);
    }

    [Fact]
    public async Task LocalCopyWhileRemoteReplyIsPendingWinsAndIsPushedOnNextPoll()
    {
        var fixture = new Fixture();
        Assert.True(await fixture.Sync());
        var started = Ready();
        var reply = new TaskCompletionSource<RemoteControlMessage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.GetOverride = _ => { started.SetResult(); return reply.Task; };
        Task<bool> pending = fixture.Sync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.CopyLocal("new local copy");
        reply.SetResult(Snapshot("remote copied earlier", ""));
        Assert.False(await pending);
        Assert.Empty(fixture.Applied);
        Assert.Equal("new local copy", fixture.LocalText);
        fixture.GetOverride = null;
        Assert.True(await fixture.Sync());
        Assert.Equal("new local copy", Assert.Single(fixture.Sent));
    }

    [Fact]
    public async Task LocalMutationWhileReadingDoesNotSendTheStaleRead()
    {
        var fixture = new Fixture();
        Assert.True(await fixture.Sync());
        fixture.CopyLocal("copy one");
        fixture.ReadOverride = () =>
        {
            string stale = fixture.LocalText;
            fixture.CopyLocal("copy two");
            return Task.FromResult(stale);
        };
        Assert.False(await fixture.Sync());
        Assert.Empty(fixture.Sent);
        fixture.ReadOverride = null;
        Assert.True(await fixture.Sync());
        Assert.Equal("copy two", Assert.Single(fixture.Sent));
    }

    [Fact]
    public async Task FailedPushRetriesWithoutMarkingLocalSequenceAsSynchronized()
    {
        var fixture = new Fixture();
        Assert.True(await fixture.Sync());
        fixture.CopyLocal("needs retry");
        fixture.SendSucceeds = false;
        Assert.False(await fixture.Sync());
        fixture.SendSucceeds = true;
        Assert.True(await fixture.Sync());
        Assert.Equal(new[] { "needs retry", "needs retry" }, fixture.Sent);
        Assert.Equal("needs retry", fixture.RemoteText);
    }

    [Fact]
    public async Task FailedSnapshotDoesNotAdvanceRevisionAndCanBeRetried()
    {
        var fixture = new Fixture();
        Assert.True(await fixture.Sync());
        string baseline = ClipboardAutoSyncCoordinator.Revision(fixture.RemoteText);
        fixture.RemoteText = "fresh after temporary failure";
        fixture.GetOverride = _ => Task.FromResult<RemoteControlMessage?>(new(
            RemoteControlKind.ClipboardSnapshot, [], null, null,
            Success: false, StatusMessage: "retry", TransferId: "r", ClipboardRevision: ""));
        Assert.False(await fixture.Sync());
        Assert.Empty(fixture.Applied);
        fixture.GetOverride = null;
        Assert.True(await fixture.Sync());
        Assert.Equal(fixture.RemoteText, Assert.Single(fixture.Applied));
        Assert.Equal(baseline, fixture.Requests[^1]);
        Assert.Equal(baseline, fixture.Requests[^2]);
    }

    [Fact]
    public async Task EmptyLocalOrNonTextCopyRebaselinesWithoutRestoringOldRemoteText()
    {
        var fixture = new Fixture();
        Assert.True(await fixture.Sync());
        fixture.CopyLocal("");
        Assert.True(await fixture.Sync());
        Assert.True(await fixture.Sync());
        Assert.Empty(fixture.Sent);
        Assert.Empty(fixture.Applied);
        Assert.Empty(fixture.LocalText);
        fixture.RemoteText = "subsequent remote text copy";
        Assert.True(await fixture.Sync());
        Assert.Equal(fixture.RemoteText, Assert.Single(fixture.Applied));
    }

    [Fact]
    public async Task RemoteEmptyOrNonTextClipboardIsPreservedWhenOpeningAnotherContextMenu()
    {
        var fixture = new Fixture();
        Assert.True(await fixture.Sync());
        fixture.CopyLocal("preserve text");
        Assert.True(await fixture.Sync());
        fixture.RemoteText = "";
        Assert.True(await fixture.Sync());
        Assert.Equal("preserve text", fixture.LocalText);
        Assert.Empty(fixture.Applied);
        Assert.True(await fixture.Sync(ensureLocalBeforeInput: true));
        Assert.Equal("", fixture.RemoteText);
        Assert.Single(fixture.Sent);
    }

    [Fact]
    public async Task ManualAppliedNotificationPreventsEchoAndInvalidatesLateAutomaticReply()
    {
        var fixture = new Fixture();
        Assert.True(await fixture.Sync());
        var started = Ready();
        var reply = new TaskCompletionSource<RemoteControlMessage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.GetOverride = _ => { started.SetResult(); return reply.Task; };
        Task<bool> pending = fixture.Sync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.CopyLocal("manual read reply");
        fixture.RemoteText = fixture.LocalText;
        fixture.Coordinator.ObserveAppliedText(fixture.Sequence, fixture.LocalText);
        reply.SetResult(Snapshot("late automatic reply", ""));
        Assert.False(await pending);
        fixture.GetOverride = null;
        Assert.True(await fixture.Sync());
        Assert.Empty(fixture.Applied);
        Assert.Empty(fixture.Sent);
        Assert.Equal("manual read reply", fixture.LocalText);
    }

    [Fact]
    public async Task InactiveConnectionNeverReadsSendsOrAppliesClipboard()
    {
        var fixture = new Fixture { IsCurrent = false };
        fixture.CopyLocal("not for old connection");
        Assert.False(await fixture.Sync());
        Assert.Equal(0, fixture.LocalReads);
        Assert.Empty(fixture.Requests);
        Assert.Empty(fixture.Sent);
        Assert.Empty(fixture.Applied);
    }

    [Fact]
    public async Task GenerationOrFocusChangeDropsLateRemoteSnapshot()
    {
        var fixture = new Fixture();
        Assert.True(await fixture.Sync());
        var started = Ready();
        var reply = new TaskCompletionSource<RemoteControlMessage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.GetOverride = _ => { started.SetResult(); return reply.Task; };
        Task<bool> pending = fixture.Sync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.IsCurrent = false;
        reply.SetResult(Snapshot("old session reply", ""));
        Assert.False(await pending);
        Assert.Empty(fixture.Applied);
    }

    [Fact]
    public async Task BackgroundGracePeriodCanPullRemoteCopyButCannotPublishFreshLocalText()
    {
        var fixture = new Fixture();
        Assert.True(await fixture.Sync());
        fixture.RemoteText = "remote copy before leaving viewer";
        Assert.True(await fixture.Sync(allowLocalPush: false));
        Assert.Equal(fixture.RemoteText, Assert.Single(fixture.Applied));
        fixture.CopyLocal("different app secret");
        int requestsBefore = fixture.Requests.Count;
        Assert.False(await fixture.Sync(allowLocalPush: false));
        Assert.Empty(fixture.Sent);
        Assert.Equal(requestsBefore, fixture.Requests.Count);
    }

    [Fact]
    public async Task FirstContextMenuWaitsForLocalTextAcknowledgementBeforeProceeding()
    {
        var fixture = new Fixture { LocalText = "copy from another program", RemoteText = "stale remote" };
        var started = Ready();
        var acknowledge = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.SendOverride = _ => { started.SetResult(); return acknowledge.Task; };
        Task<bool> beforeClick = fixture.Sync(ensureLocalBeforeInput: true);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(beforeClick.IsCompleted);
        Assert.Equal("copy from another program", Assert.Single(fixture.Sent));
        Assert.Empty(fixture.Requests);
        acknowledge.SetResult(true);
        Assert.True(await beforeClick);
        Assert.True(await fixture.Sync(ensureLocalBeforeInput: true));
        Assert.Single(fixture.Sent);
    }

    [Fact]
    public async Task FailedContextMenuPushDoesNotSignalReadyForPaste()
    {
        var fixture = new Fixture { SendSucceeds = false };
        Assert.False(await fixture.Sync(ensureLocalBeforeInput: true));
        fixture.SendSucceeds = true;
        Assert.True(await fixture.Sync(ensureLocalBeforeInput: true));
        Assert.Equal(2, fixture.Sent.Count);
    }

    [Fact]
    public async Task LocalCopyDuringAcknowledgementIsRetriedBeforeAnotherContextMenu()
    {
        var fixture = new Fixture();
        var started = Ready();
        var acknowledge = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.SendOverride = _ => { started.SetResult(); return acknowledge.Task; };
        Task<bool> pending = fixture.Sync(ensureLocalBeforeInput: true);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.CopyLocal("second copy");
        acknowledge.SetResult(true);
        Assert.False(await pending);
        fixture.SendOverride = null;
        Assert.True(await fixture.Sync(ensureLocalBeforeInput: true));
        Assert.Equal("second copy", fixture.Sent[^1]);
    }

    [Fact]
    public async Task ClipboardChangeImmediatelyBeforeApplyRejectsTheDelayedWrite()
    {
        var fixture = new Fixture();
        Assert.True(await fixture.Sync());
        fixture.RemoteText = "remote text to apply";
        fixture.BeforeApply = () => fixture.CopyLocal("fresh local priority");
        Assert.False(await fixture.Sync());
        Assert.Empty(fixture.Applied);
        Assert.Equal("fresh local priority", fixture.LocalText);
        fixture.BeforeApply = null;
        Assert.True(await fixture.Sync());
        Assert.Equal("fresh local priority", Assert.Single(fixture.Sent));
    }

    [Fact]
    public async Task SameContentWithNewLocalSequenceOverridesAnUnobservedRemoteCopy()
    {
        var fixture = new Fixture { LocalText = "A", RemoteText = "A" };
        fixture.Coordinator.ObserveAppliedText(fixture.Sequence, "A");
        fixture.RemoteText = "B";
        fixture.CopyLocal("A");
        Assert.True(await fixture.Sync());
        Assert.Equal("A", Assert.Single(fixture.Sent));
        Assert.Equal("A", fixture.RemoteText);
        Assert.True(await fixture.Sync());
        Assert.True(await fixture.Sync(ensureLocalBeforeInput: true));
        Assert.Single(fixture.Sent);
        Assert.Empty(fixture.Applied);
    }

    [Fact]
    public async Task OverlongLocalCopyIsNotTruncatedOrMarkedAsSynced()
    {
        var fixture = new Fixture();
        Assert.True(await fixture.Sync());
        fixture.CopyLocal(new string('中', 256_001));
        Assert.False(await fixture.Sync());
        Assert.Empty(fixture.Sent);
        fixture.CopyLocal("smaller retry");
        Assert.True(await fixture.Sync());
        Assert.Equal("smaller retry", Assert.Single(fixture.Sent));
    }

    [Fact]
    public async Task UnknownWindowsClipboardSequenceNeverReadsOrOverwritesClipboard()
    {
        var fixture = new Fixture { Sequence = 0 };
        Assert.False(await fixture.Sync(ensureLocalBeforeInput: true));
        Assert.Equal(0, fixture.LocalReads);
        Assert.Empty(fixture.Sent);
        Assert.Empty(fixture.Requests);
        Assert.Empty(fixture.Applied);
    }

    [Fact]
    public async Task ConcurrentPollsAreSerializedAroundNetworkRequests()
    {
        var fixture = new Fixture();
        var started = Ready();
        var reply = new TaskCompletionSource<RemoteControlMessage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.GetOverride = _ => { started.TrySetResult(); return reply.Task; };
        Task<bool> first = fixture.Sync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<bool> second = fixture.Sync();
        Assert.Single(fixture.Requests);
        Assert.False(second.IsCompleted);
        fixture.GetOverride = null;
        reply.SetResult(Snapshot(fixture.RemoteText, ""));
        Assert.All(await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2)), result => Assert.True(result));
        Assert.Equal(2, fixture.Requests.Count);
        Assert.Empty(fixture.Applied);
    }

    private static TaskCompletionSource Ready() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static RemoteControlMessage Snapshot(string text, string known)
    {
        string revision = ClipboardAutoSyncCoordinator.Revision(text);
        bool changed = revision != known;
        return new(RemoteControlKind.ClipboardSnapshot, [], null, null,
            Text: changed ? text : "", Success: true, TransferId: "snapshot",
            ClipboardRevision: revision, ClipboardHasText: text.Length != 0, ClipboardChanged: changed);
    }

    private sealed class Fixture
    {
        public uint Sequence = 10;
        public string LocalText = "local baseline";
        public string RemoteText = "remote baseline";
        public bool IsCurrent = true;
        public bool SendSucceeds = true;
        public int LocalReads;
        public readonly List<string> Sent = [];
        public readonly List<string> Applied = [];
        public readonly List<string> Requests = [];
        public Func<Task<string>>? ReadOverride;
        public Func<string, Task<bool>>? SendOverride;
        public Func<string, Task<RemoteControlMessage?>>? GetOverride;
        public Action? BeforeApply;
        private ClipboardAutoSyncCoordinator? _coordinator;

        public ClipboardAutoSyncCoordinator Coordinator => _coordinator ??= new(
            () => Sequence,
            () => { LocalReads++; return ReadOverride?.Invoke() ?? Task.FromResult(LocalText); },
            async text =>
            {
                Sent.Add(text);
                bool success = SendOverride is null ? SendSucceeds : await SendOverride(text);
                if (success) RemoteText = text;
                return success;
            },
            known =>
            {
                Requests.Add(known);
                return GetOverride?.Invoke(known) ?? Task.FromResult<RemoteControlMessage?>(Snapshot(RemoteText, known));
            },
            (text, current) =>
            {
                BeforeApply?.Invoke();
                if (!current()) throw new OperationCanceledException();
                Applied.Add(text);
                CopyLocal(text);
                return Task.FromResult(Sequence);
            },
            () => IsCurrent);

        public Task<bool> Sync(bool ensureLocalBeforeInput = false, bool allowLocalPush = true) =>
            Coordinator.SyncAsync(ensureLocalBeforeInput, allowLocalPush);

        public void CopyLocal(string text) { LocalText = text; Sequence++; }
    }
}
