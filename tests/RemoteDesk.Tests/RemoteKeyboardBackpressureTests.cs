using Xunit;

namespace RemoteDesk.Tests;

public sealed class RemoteKeyboardBackpressureTests
{
    private static readonly RemoteInputCommand ControlDown = RemoteInputCommand.KeyDown(
        0xA3, 0x1D, RemoteKeyboardFlags.HasScanCode | RemoteKeyboardFlags.Extended);
    private static readonly RemoteInputCommand ControlUp = ControlDown with { Kind = RemoteInputKind.KeyUp };

    [Fact]
    public void NewKeyDownCannotPassAnUnadmittedModifierRelease()
    {
        var tracker = new RemoteInputOwnershipTracker();
        Press(tracker, ControlDown);
        Assert.False(tracker.TryQueueKey(ControlUp, 7, _ => throw new InvalidOperationException(), (_, _) => false));

        int newDownAttempts = 0;
        Assert.False(tracker.TryQueueKey(RemoteInputCommand.KeyDown(0x41), 7,
            _ => { newDownAttempts++; return new(true, 7); }, (_, _) => false));
        Assert.Equal(0, newDownAttempts);
        Assert.Equal(1, tracker.PressedKeyCount);
    }

    [Fact]
    public void RepressAdmitsTheOldReleaseBeforeTheNewDown()
    {
        var tracker = new RemoteInputOwnershipTracker();
        Press(tracker, ControlDown);
        Assert.False(tracker.TryQueueKey(ControlUp, 7, _ => throw new InvalidOperationException(), (_, _) => false));
        var admitted = new List<RemoteInputCommand>();

        Assert.True(tracker.TryQueueKey(ControlDown, 7,
            command => { admitted.Add(command); return new(true, 7); },
            (command, generation) => { Assert.Equal(7, generation); admitted.Add(command); return true; }));
        Assert.Equal(new[] { ControlUp, ControlDown }, admitted);
        Assert.Equal(1, tracker.PressedKeyCount);
    }

    [Fact]
    public void FailedReleaseAllKeyUpMustBeAdmittedBeforeLaterTyping()
    {
        var tracker = new RemoteInputOwnershipTracker();
        Press(tracker, ControlDown);
        Assert.Equal(0, tracker.ReleaseAll(7, (_, _) => false));
        var admitted = new List<RemoteInputCommand>();
        var letter = RemoteInputCommand.KeyDown(0x41);

        Assert.True(tracker.TryQueueKey(letter, 7,
            command => { admitted.Add(command); return new(true, 7); },
            (command, _) => { admitted.Add(command); return true; }));
        Assert.Equal(new[] { ControlUp, letter }, admitted);
    }

    [Fact]
    public void RetryOnlyReleasesPendingKeysUsingOriginalPressMetadata()
    {
        var tracker = new RemoteInputOwnershipTracker();
        Press(tracker, ControlDown);
        Press(tracker, RemoteInputCommand.KeyDown(0x41));
        Assert.True(tracker.TryQueueMouseDown(RemoteMouseButton.Left, new(3, 4), _ => new(true, 7)));
        var genericUp = RemoteInputCommand.KeyUp(0x11, 0x1D,
            RemoteKeyboardFlags.HasScanCode | RemoteKeyboardFlags.Extended);
        Assert.False(tracker.TryQueueKey(genericUp, 7, _ => throw new InvalidOperationException(), (_, _) => false));
        Assert.False(tracker.TryQueueMouseUp(RemoteMouseButton.Left, null, 7, (_, _) => false));
        Assert.True(tracker.HasPendingKeyReleases(7));
        var admitted = new List<RemoteInputCommand>();

        Assert.Equal(1, tracker.RetryPendingKeyReleases(7,
            (command, generation) => { Assert.Equal(7, generation); admitted.Add(command); return true; }));
        Assert.Equal(ControlUp, Assert.Single(admitted));
        Assert.Equal(1, tracker.PressedKeyCount);
        Assert.Equal(1, tracker.PressedMouseButtonCount);
        Assert.False(tracker.HasPendingKeyReleases(7));
        Assert.Equal(0, tracker.RetryPendingKeyReleases(7, (_, _) => throw new InvalidOperationException()));
    }

    [Fact]
    public void RepeatedFailureIsBoundedAndDoesNotReplayKeyDown()
    {
        var tracker = new RemoteInputOwnershipTracker();
        Press(tracker, ControlDown);
        Assert.False(tracker.TryQueueKey(ControlUp, 7, _ => throw new InvalidOperationException(), (_, _) => false));
        int attempts = 0;
        for (int retry = 0; retry < 3; retry++)
        {
            Assert.Equal(0, tracker.RetryPendingKeyReleases(7, (command, generation) =>
            {
                Assert.Equal(ControlUp, command);
                Assert.Equal(7, generation);
                attempts++;
                return false;
            }));
            Assert.True(tracker.HasPendingKeyReleases(7));
        }
        Assert.Equal(3, attempts);
        Assert.Equal(1, tracker.PressedKeyCount);
    }

    [Fact]
    public void PartialRetryRemovesOnlyAcceptedReleasesAndStillBlocksNewDown()
    {
        var tracker = new RemoteInputOwnershipTracker();
        var letter = RemoteInputCommand.KeyDown(0x41);
        Press(tracker, ControlDown);
        Press(tracker, letter);
        Assert.Equal(0, tracker.ReleaseAll(7, (_, _) => false));
        var admitted = new List<RemoteInputCommand>();
        Assert.Equal(1, tracker.RetryPendingKeyReleases(7, (command, _) =>
        {
            if (command.Data != 0x41) return false;
            admitted.Add(command);
            return true;
        }));
        Assert.True(tracker.HasPendingKeyReleases(7));
        Assert.Equal(1, tracker.PressedKeyCount);
        Assert.False(tracker.TryQueueKey(RemoteInputCommand.KeyDown(0x42), 7,
            _ => throw new InvalidOperationException("Must not enqueue a new down"), (_, _) => false));
        Assert.Equal(1, tracker.RetryPendingKeyReleases(7,
            (command, _) => { admitted.Add(command); return true; }));
        Assert.Equal(new[] { letter with { Kind = RemoteInputKind.KeyUp }, ControlUp }, admitted);
        Assert.False(tracker.HasPendingKeyReleases(7));
        Assert.Equal(0, tracker.PressedKeyCount);
    }

    [Fact]
    public void DelayedRetryDoesNotReleaseNewSameGenerationPress()
    {
        var tracker = new RemoteInputOwnershipTracker();
        Press(tracker, ControlDown);
        Assert.False(tracker.TryQueueKey(ControlUp, 7, _ => throw new InvalidOperationException(), (_, _) => false));
        Assert.True(tracker.TryQueueKey(ControlDown, 7, _ => new(true, 7), (_, _) => true));
        Assert.False(tracker.HasPendingKeyReleases(7));
        Assert.Equal(0, tracker.RetryPendingKeyReleases(7, (_, _) => throw new InvalidOperationException()));
        Assert.True(tracker.AnyPressedKey(7, key => key.VirtualKey == 0xA3));
        Assert.True(tracker.TryQueueKey(ControlUp, 7, _ => throw new InvalidOperationException(), (_, _) => true));
    }

    [Fact]
    public void DelayedOldGenerationRetryCannotReleaseOrDiscardNewPendingPress()
    {
        var tracker = new RemoteInputOwnershipTracker();
        Press(tracker, ControlDown);
        Assert.False(tracker.TryQueueKey(ControlUp, 7, _ => throw new InvalidOperationException(), (_, _) => false));
        Assert.True(tracker.TryQueueKey(ControlDown, 8, _ => new(true, 8),
            (_, _) => throw new InvalidOperationException("Old ownership must be retired, not replayed")));
        Assert.False(tracker.TryQueueKey(ControlUp, 8, _ => throw new InvalidOperationException(), (_, _) => false));
        Assert.False(tracker.HasPendingKeyReleases(7));
        Assert.Equal(0, tracker.RetryPendingKeyReleases(7, (_, _) => throw new InvalidOperationException()));
        Assert.True(tracker.HasPendingKeyReleases(8));
        Assert.Equal(1, tracker.PressedKeyCount);
        Assert.Equal(1, tracker.RetryPendingKeyReleases(8, (command, generation) =>
        {
            Assert.Equal(ControlUp, command);
            Assert.Equal(8, generation);
            return true;
        }));
    }

    [Fact]
    public void RetryForCurrentConnectionNeverReplaysPriorGeneration()
    {
        var tracker = new RemoteInputOwnershipTracker();
        Press(tracker, ControlDown);
        Assert.False(tracker.TryQueueKey(ControlUp, 7, _ => throw new InvalidOperationException(), (_, _) => false));
        Assert.False(tracker.HasPendingKeyReleases(8));
        Assert.Equal(0, tracker.RetryPendingKeyReleases(8, (_, _) => throw new InvalidOperationException()));
        Assert.Equal(0, tracker.ReleaseAll(8, (_, _) => throw new InvalidOperationException()));
        Assert.Equal(0, tracker.PressedKeyCount);
    }

    [Fact]
    public void SuccessfulPhysicalKeyUpClearsPendingWithoutBackgroundDuplicate()
    {
        var tracker = new RemoteInputOwnershipTracker();
        Press(tracker, ControlDown);
        Assert.False(tracker.TryQueueKey(ControlUp, 7, _ => throw new InvalidOperationException(), (_, _) => false));
        Assert.True(tracker.TryQueueKey(ControlUp, 7, _ => throw new InvalidOperationException(), (_, _) => true));
        Assert.False(tracker.HasPendingKeyReleases(7));
        Assert.Equal(0, tracker.RetryPendingKeyReleases(7, (_, _) => throw new InvalidOperationException()));
    }

    [Fact]
    public void FailedOrUnownedInputDoesNotInventPendingRelease()
    {
        var tracker = new RemoteInputOwnershipTracker();
        Assert.False(tracker.TryQueueKey(ControlDown, 7, _ => new(false, 7), (_, _) => false));
        Assert.False(tracker.TryQueueKey(ControlUp, 7, _ => throw new InvalidOperationException(),
            (_, _) => throw new InvalidOperationException()));
        Assert.False(tracker.HasPendingKeyReleases(7));
        Assert.Equal(0, tracker.RetryPendingKeyReleases(7, (_, _) => throw new InvalidOperationException()));
        Assert.Equal(0, tracker.PressedKeyCount);
    }

    [Fact]
    public void ReleaseAcceptedButNewDownRejectedDoesNotRetainTheOldOwner()
    {
        var tracker = new RemoteInputOwnershipTracker();
        Press(tracker, ControlDown);
        Assert.False(tracker.TryQueueKey(ControlUp, 7, _ => throw new InvalidOperationException(), (_, _) => false));
        Assert.False(tracker.TryQueueKey(ControlDown, 7, _ => new(false, 7), (_, _) => true));
        Assert.False(tracker.HasPendingKeyReleases(7));
        Assert.Equal(0, tracker.PressedKeyCount);
    }

    [Fact]
    public void FullProductionReleaseReserveDrainsThenAdmitsOwnedKeyUpWithoutFurtherTyping()
    {
        const int capacity = 256;
        var tracker = new RemoteInputOwnershipTracker();
        var queue = new RemoteInputQueue();
        RemoteInputQueueAdmission QueueCurrent(RemoteInputCommand command) => new(queue.Enqueue(command, capacity), 7);
        bool QueueOwned(RemoteInputCommand command, long generation) => generation == 7 && queue.Enqueue(command, capacity);
        Assert.True(tracker.TryQueueKey(ControlDown, 7, QueueCurrent, QueueOwned));
        for (int index = 1; index < capacity; index++)
            Assert.True(queue.Enqueue(RemoteInputCommand.KeyDown(0x41), capacity));
        for (int index = 0; index < RemoteInputQueue.ReleaseReserveCapacity; index++)
            Assert.True(queue.Enqueue(RemoteInputCommand.KeyUp(0x41), capacity));
        Assert.Equal(320, queue.Count);
        Assert.False(tracker.TryQueueKey(ControlUp, 7, QueueCurrent, QueueOwned));
        Assert.True(tracker.HasPendingKeyReleases(7));

        var drained = new List<RemoteInputCommand>();
        while (queue.TryDequeue(out var command)) drained.Add(command);
        Assert.Equal(320, drained.Count);
        Assert.DoesNotContain(ControlUp, drained);
        Assert.Equal(1, tracker.RetryPendingKeyReleases(7, QueueOwned));
        Assert.True(queue.TryDequeue(out var release));
        Assert.Equal(ControlUp, release);
        Assert.False(queue.TryDequeue(out _));
        Assert.False(tracker.HasPendingKeyReleases(7));
        Assert.Equal(0, tracker.PressedKeyCount);
    }

    [Fact]
    public void RetryRejectsMissingAdmissionCallback()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new RemoteInputOwnershipTracker().RetryPendingKeyReleases(7, null!));
    }

    private static void Press(RemoteInputOwnershipTracker tracker, RemoteInputCommand command, long generation = 7) =>
        Assert.True(tracker.TryQueueKey(command, generation, _ => new(true, generation), (_, _) => false));
}
