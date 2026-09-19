using Xunit;

namespace RemoteDesk.Tests;

public sealed class ClipboardSnapshotServiceTests
{
    [Fact]
    public async Task StableSequenceCachesTextWithoutOpeningClipboardAgain()
    {
        int reads = 0;
        var service = new ClipboardSnapshotService(() => 12, () =>
        {
            reads++;
            return Task.FromResult("中文😀");
        });
        ClipboardSnapshotResult first = await service.ReadAsync("");
        ClipboardSnapshotResult next = await service.ReadAsync(first.Revision);
        ClipboardSnapshotResult differentViewerBaseline = await service.ReadAsync("");
        Assert.Equal(1, reads);
        Assert.True(first.Success);
        Assert.True(first.HasText);
        Assert.True(first.Changed);
        Assert.Equal("中文😀", first.Text);
        Assert.Equal("e973a1c1b41c5c9f4fbac31fcc311536dfffd003bfb914580455170a599953fa", first.Revision);
        Assert.True(next.Success);
        Assert.True(next.HasText);
        Assert.False(next.Changed);
        Assert.Empty(next.Text);
        Assert.Equal(first, differentViewerBaseline);
    }

    [Fact]
    public async Task NewSequenceWithSameTextDoesNotCauseClipboardEcho()
    {
        uint sequence = 2;
        int reads = 0;
        var service = new ClipboardSnapshotService(() => sequence, () =>
        {
            reads++;
            return Task.FromResult("same text");
        });
        ClipboardSnapshotResult first = await service.ReadAsync("");
        sequence++;
        ClipboardSnapshotResult next = await service.ReadAsync(first.Revision);
        Assert.Equal(2, reads);
        Assert.True(next.Success);
        Assert.False(next.Changed);
        Assert.Empty(next.Text);
        Assert.Equal(first.Revision, next.Revision);
    }

    [Fact]
    public async Task NewSequenceWithNewTextProducesAChangedSnapshot()
    {
        uint sequence = 2;
        string text = "old";
        var service = new ClipboardSnapshotService(() => sequence, () => Task.FromResult(text));
        ClipboardSnapshotResult first = await service.ReadAsync("");
        sequence++;
        text = "fresh";
        ClipboardSnapshotResult next = await service.ReadAsync(first.Revision);
        Assert.True(next.Changed);
        Assert.Equal("fresh", next.Text);
        Assert.NotEqual(first.Revision, next.Revision);
    }

    [Fact]
    public async Task EmptyOrNonTextClipboardHasAStableEmptyDigest()
    {
        var service = new ClipboardSnapshotService(() => 3, () => Task.FromResult(""));
        ClipboardSnapshotResult first = await service.ReadAsync("");
        ClipboardSnapshotResult next = await service.ReadAsync(first.Revision);
        Assert.True(first.Success);
        Assert.True(first.Changed);
        Assert.False(first.HasText);
        Assert.Empty(first.Text);
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", first.Revision);
        Assert.False(next.Changed);
    }

    [Fact]
    public async Task UnavailableSequenceDoesNotOpenClipboardOrReturnPreviouslyCachedText()
    {
        uint sequence = 7;
        int reads = 0;
        var service = new ClipboardSnapshotService(() => sequence, () =>
        {
            reads++;
            return Task.FromResult("cached secret");
        });
        Assert.True((await service.ReadAsync("")).Success);
        sequence = 0;
        AssertSafeFailure(await service.ReadAsync(""));
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task ClipboardMutationDuringReadRetriesBeforePublishing()
    {
        uint sequence = 1;
        int reads = 0;
        var service = new ClipboardSnapshotService(() => sequence, () =>
        {
            if (++reads == 1)
            {
                sequence = 2;
                return Task.FromResult("obsolete");
            }
            return Task.FromResult("fresh");
        });
        ClipboardSnapshotResult snapshot = await service.ReadAsync("");
        Assert.Equal(2, reads);
        Assert.True(snapshot.Success);
        Assert.Equal("fresh", snapshot.Text);
        Assert.Equal(RemoteMessageCodec.ComputeClipboardRevision("fresh"), snapshot.Revision);
        Assert.Equal(snapshot, await service.ReadAsync(""));
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task ContinuouslyChangingClipboardFailsWithoutReturningUnstableText()
    {
        uint sequence = 1;
        int reads = 0;
        var service = new ClipboardSnapshotService(() => sequence, () =>
        {
            reads++;
            sequence++;
            return Task.FromResult("unstable secret");
        });
        AssertSafeFailure(await service.ReadAsync(""));
        Assert.Equal(3, reads);
    }

    [Fact]
    public async Task ReadFailureDoesNotReturnExceptionDetailsAndDoesNotPoisonCache()
    {
        int reads = 0;
        var service = new ClipboardSnapshotService(() => 5, () =>
        {
            if (++reads == 1) throw new InvalidOperationException("secret account password");
            return Task.FromResult("retry succeeded");
        });
        ClipboardSnapshotResult failed = await service.ReadAsync("");
        AssertSafeFailure(failed);
        Assert.DoesNotContain("secret", failed.StatusMessage);
        Assert.Equal("retry succeeded", (await service.ReadAsync("")).Text);
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task SequenceReadFailureDoesNotOpenClipboardOrExposeExceptionDetails()
    {
        int reads = 0;
        var service = new ClipboardSnapshotService(() => throw new InvalidOperationException("secret desktop"), () =>
        {
            reads++;
            return Task.FromResult("must not read");
        });
        ClipboardSnapshotResult failed = await service.ReadAsync("");
        AssertSafeFailure(failed);
        Assert.DoesNotContain("secret", failed.StatusMessage);
        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task OversizeClipboardIsRejectedWithoutTruncationAndWithoutCaching()
    {
        string text = new('中', 256_001);
        var service = new ClipboardSnapshotService(() => 9, () => Task.FromResult(text));
        AssertSafeFailure(await service.ReadAsync(""));
        text = text[..256_000];
        ClipboardSnapshotResult maxSize = await service.ReadAsync("");
        Assert.True(maxSize.Success);
        Assert.Equal(text, maxSize.Text);
    }

    [Fact]
    public async Task InvalidUtf16NeverProducesAnUnverifiableDigest()
    {
        var service = new ClipboardSnapshotService(() => 9, () => Task.FromResult("\ud800"));
        AssertSafeFailure(await service.ReadAsync(""));
    }

    [Fact]
    public async Task TimeoutReleasesGateAndLateTextCannotFillTheCache()
    {
        var neverCompletesInTime = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        var service = new ClipboardSnapshotService(() => 10, () =>
            ++reads == 1 ? neverCompletesInTime.Task : Task.FromResult("current"), TimeSpan.FromMilliseconds(50));
        AssertSafeFailure(await service.ReadAsync("").WaitAsync(TimeSpan.FromSeconds(3)));
        neverCompletesInTime.SetResult("late secret");
        ClipboardSnapshotResult fresh = await service.ReadAsync("");
        Assert.True(fresh.Success);
        Assert.Equal("current", fresh.Text);
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task SessionCancellationPropagatesAndLateReadCannotContaminateANewRequest()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelledSession = new CancellationTokenSource();
        int reads = 0;
        var service = new ClipboardSnapshotService(() => 10, () =>
        {
            if (++reads == 1)
            {
                started.SetResult();
                return read.Task;
            }
            return Task.FromResult("new session");
        });
        Task<ClipboardSnapshotResult> pending = service.ReadAsync("", cancelledSession.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancelledSession.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        read.SetResult("old session secret");
        Assert.Equal("new session", (await service.ReadAsync("")).Text);
    }

    [Fact]
    public async Task ConcurrentRequestsShareOneStableRead()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        var service = new ClipboardSnapshotService(() => 4, () =>
        {
            reads++;
            started.TrySetResult();
            return read.Task;
        });
        Task<ClipboardSnapshotResult> first = service.ReadAsync("");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<ClipboardSnapshotResult> second = service.ReadAsync("");
        Assert.False(second.IsCompleted);
        read.SetResult("shared");
        ClipboardSnapshotResult[] results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(results[0], results[1]);
        Assert.Equal(1, reads);
    }

    internal static void AssertSafeFailure(ClipboardSnapshotResult result)
    {
        Assert.False(result.Success);
        Assert.False(result.HasText);
        Assert.False(result.Changed);
        Assert.Empty(result.Text);
        Assert.Empty(result.Revision);
        Assert.False(string.IsNullOrWhiteSpace(result.StatusMessage));
    }
}
