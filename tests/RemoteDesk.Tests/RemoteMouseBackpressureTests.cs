using System.Drawing;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class RemoteMouseBackpressureTests
{
    [Fact]
    public void FailedMouseUpRetainsActualReleasePointAcrossLaterMotion()
    {
        var tracker = new RemoteInputOwnershipTracker();
        Assert.True(tracker.TryQueueMouseDown(RemoteMouseButton.Left, new(1, 2), _ => new(true, 7)));
        Assert.False(tracker.TryQueueMouseUp(RemoteMouseButton.Left, new Point(30, 40), 7, (_, _) => false));
        tracker.UpdatePressedMousePosition(new(90, 100), 7);
        RemoteInputCommand? actual = null;
        Assert.True(tracker.TryQueueMouseUp(RemoteMouseButton.Left, null, 7,
            (command, _) => { actual = command; return true; }));
        Assert.Equal(RemoteInputCommand.MouseUp(RemoteMouseButton.Left, 30, 40), actual);
    }

    [Fact]
    public void LegacyMouseDownCannotOverwriteAnUnadmittedRelease()
    {
        var tracker = new RemoteInputOwnershipTracker();
        Assert.True(tracker.TryQueueMouseDown(RemoteMouseButton.Left, new(1, 2), _ => new(true, 7)));
        Assert.False(tracker.TryQueueMouseUp(RemoteMouseButton.Left, new Point(30, 40), 7, (_, _) => false));
        int attempts = 0;
        Assert.False(tracker.TryQueueMouseDown(RemoteMouseButton.Left, new(80, 90),
            _ => { attempts++; return new(true, 7); }));
        Assert.Equal(0, attempts);
    }

    [Fact]
    public void MouseRepressAdmitsOldUpBeforeNewDownAndLateRetryDoesNotReleaseIt()
    {
        var tracker = PendingMouse();
        var admitted = new List<RemoteInputCommand>();
        Assert.True(tracker.TryQueueMouseDown(RemoteMouseButton.Left, new(80, 90), 7,
            command => { admitted.Add(command); return new(true, 7); },
            (command, generation) => { Assert.Equal(7, generation); admitted.Add(command); return true; }));
        Assert.Equal(new[] { RemoteInputCommand.MouseUp(RemoteMouseButton.Left, 30, 40),
            RemoteInputCommand.MouseDown(RemoteMouseButton.Left, 80, 90) }, admitted);
        Assert.False(tracker.HasPendingReleases(7));
        Assert.Equal(0, tracker.RetryPendingReleases(7, (_, _) => throw new InvalidOperationException()));
        Assert.Equal(1, tracker.PressedMouseButtonCount);
        Assert.True(tracker.TryGetPressedMouseButton(RemoteMouseButton.Left, out var point, out var generation));
        Assert.Equal(new(80, 90), point);
        Assert.Equal(7, generation);
    }

    [Fact]
    public void PendingMouseReleasePreventsNewTypingOrClickUntilAdmitted()
    {
        var tracker = PendingMouse();
        Assert.False(tracker.TryQueueMouseDown(RemoteMouseButton.Right, new(10, 20), 7,
            _ => throw new InvalidOperationException(), (_, _) => false));
        Assert.False(tracker.TryQueueKey(RemoteInputCommand.KeyDown(0x41), 7,
            _ => throw new InvalidOperationException(), (_, _) => false));
        Assert.True(tracker.HasPendingReleases(7));
        Assert.False(tracker.HasPendingKeyReleases(7));
        Assert.Equal(1, tracker.PressedMouseButtonCount);
        Assert.Equal(0, tracker.PressedKeyCount);
    }

    [Fact]
    public void PendingModifierReleasePreventsMouseClickFromBecomingCtrlClick()
    {
        var tracker = new RemoteInputOwnershipTracker();
        Assert.True(tracker.TryQueueKey(RemoteInputCommand.KeyDown(0x11), 7, _ => new(true, 7), (_, _) => false));
        Assert.False(tracker.TryQueueKey(RemoteInputCommand.KeyUp(0x11), 7,
            _ => throw new InvalidOperationException(), (_, _) => false));
        Assert.False(tracker.TryQueueMouseDown(RemoteMouseButton.Left, new(10, 20), 7,
            _ => throw new InvalidOperationException(), (_, _) => false));
        var admitted = new List<RemoteInputCommand>();
        Assert.True(tracker.TryQueueMouseDown(RemoteMouseButton.Left, new(10, 20), 7,
            command => { admitted.Add(command); return new(true, 7); },
            (command, _) => { admitted.Add(command); return true; }));
        Assert.Equal(new[] { RemoteInputCommand.KeyUp(0x11),
            RemoteInputCommand.MouseDown(RemoteMouseButton.Left, 10, 20) }, admitted);
    }

    [Fact]
    public void NewGenerationRetiresOldMouseAndOldRetryCannotDiscardNewPendingMouse()
    {
        var tracker = PendingMouse();
        Assert.True(tracker.TryQueueMouseDown(RemoteMouseButton.Left, new(80, 90), 8,
            _ => new(true, 8), (_, _) => throw new InvalidOperationException("Never replay to the new connection")));
        Assert.False(tracker.TryQueueMouseUp(RemoteMouseButton.Left, new Point(100, 110), 8, (_, _) => false));
        Assert.False(tracker.HasPendingReleases(7));
        Assert.Equal(0, tracker.RetryPendingReleases(7, (_, _) => throw new InvalidOperationException()));
        Assert.True(tracker.HasPendingReleases(8));
        Assert.Equal(1, tracker.RetryPendingReleases(8, (command, generation) =>
        {
            Assert.Equal(RemoteInputCommand.MouseUp(RemoteMouseButton.Left, 100, 110), command);
            Assert.Equal(8, generation);
            return true;
        }));
        Assert.Equal(0, tracker.PressedMouseButtonCount);
    }

    [Fact]
    public void ReleaseAllAndDuplicateMouseUpPreserveFirstUnadmittedUpPosition()
    {
        var tracker = PendingMouse();
        tracker.UpdatePressedMousePosition(new(500, 600), 7);
        Assert.False(tracker.TryQueueMouseUp(RemoteMouseButton.Left, new Point(800, 900), 7, (command, _) =>
        {
            Assert.Equal(RemoteInputCommand.MouseUp(RemoteMouseButton.Left, 30, 40), command);
            return false;
        }));
        Assert.Equal(0, tracker.ReleaseAll(7, (command, _) =>
        {
            Assert.Equal(RemoteInputCommand.MouseUp(RemoteMouseButton.Left, 30, 40), command);
            return false;
        }));
        Assert.Equal(1, tracker.RetryPendingReleases(7, (command, _) =>
        {
            Assert.Equal(RemoteInputCommand.MouseUp(RemoteMouseButton.Left, 30, 40), command);
            return true;
        }));
    }

    [Fact]
    public void FailedReleaseAllSnapshotsLastMousePointOnce()
    {
        var tracker = new RemoteInputOwnershipTracker();
        Assert.True(tracker.TryQueueMouseDown(RemoteMouseButton.Right, new(1, 2), _ => new(true, 7)));
        tracker.UpdatePressedMousePosition(new(30, 40), 7);
        Assert.Equal(0, tracker.ReleaseAll(7, (_, _) => false));
        tracker.UpdatePressedMousePosition(new(500, 600), 7);
        Assert.True(tracker.HasPendingReleases(7));
        Assert.Equal(1, tracker.RetryPendingReleases(7, (command, _) =>
        {
            Assert.Equal(RemoteInputCommand.MouseUp(RemoteMouseButton.Right, 30, 40), command);
            return true;
        }));
    }

    [Fact]
    public void UnifiedRetryDoesNotReleaseStillHeldButtonsOrKeys()
    {
        var tracker = new RemoteInputOwnershipTracker();
        Assert.True(tracker.TryQueueMouseDown(RemoteMouseButton.Left, new(1, 2), _ => new(true, 7)));
        Assert.True(tracker.TryQueueMouseDown(RemoteMouseButton.Right, new(3, 4), _ => new(true, 7)));
        Assert.True(tracker.TryQueueKey(RemoteInputCommand.KeyDown(0x41), 7, _ => new(true, 7), (_, _) => false));
        Assert.False(tracker.TryQueueMouseUp(RemoteMouseButton.Right, new Point(30, 40), 7, (_, _) => false));
        Assert.Equal(1, tracker.RetryPendingReleases(7, (command, _) =>
        {
            Assert.Equal(RemoteInputCommand.MouseUp(RemoteMouseButton.Right, 30, 40), command);
            return true;
        }));
        Assert.False(tracker.HasPendingReleases(7));
        Assert.Equal(1, tracker.PressedMouseButtonCount);
        Assert.Equal(1, tracker.PressedKeyCount);
    }

    [Fact]
    public void PartialUnifiedRetryKeepsOnlyFailedMouseAndNeverRepeatsAcceptedKeyUp()
    {
        var tracker = new RemoteInputOwnershipTracker();
        Assert.True(tracker.TryQueueMouseDown(RemoteMouseButton.Left, new(1, 2), _ => new(true, 7)));
        Assert.True(tracker.TryQueueKey(RemoteInputCommand.KeyDown(0x11), 7, _ => new(true, 7), (_, _) => false));
        Assert.Equal(0, tracker.ReleaseAll(7, (_, _) => false));
        var admitted = new List<RemoteInputCommand>();
        Assert.Equal(1, tracker.RetryPendingReleases(7, (command, _) =>
        {
            if (command.Kind == RemoteInputKind.MouseUp) return false;
            admitted.Add(command);
            return true;
        }));
        Assert.True(tracker.HasPendingReleases(7));
        Assert.False(tracker.HasPendingKeyReleases(7));
        Assert.Equal(1, tracker.RetryPendingReleases(7,
            (command, _) => { admitted.Add(command); return true; }));
        Assert.Equal(new[] { RemoteInputCommand.KeyUp(0x11),
            RemoteInputCommand.MouseUp(RemoteMouseButton.Left, 1, 2) }, admitted);
        Assert.False(tracker.HasPendingReleases(7));
        Assert.Equal(0, tracker.RetryPendingReleases(7, (_, _) => throw new InvalidOperationException()));
    }

    [Fact]
    public void ProductionReleaseReserveDrainRetriesMouseUpAtItsOriginalReleasePoint()
    {
        const int capacity = 256;
        var tracker = new RemoteInputOwnershipTracker();
        var queue = new RemoteInputQueue();
        RemoteInputQueueAdmission QueueCurrent(RemoteInputCommand command) => new(queue.Enqueue(command, capacity), 7);
        bool QueueOwned(RemoteInputCommand command, long generation) => generation == 7 && queue.Enqueue(command, capacity);
        Assert.True(tracker.TryQueueMouseDown(RemoteMouseButton.Left, new(1, 2), 7, QueueCurrent, QueueOwned));
        for (int index = 1; index < capacity; index++)
            Assert.True(queue.Enqueue(RemoteInputCommand.KeyDown(0x41), capacity));
        for (int index = 0; index < RemoteInputQueue.ReleaseReserveCapacity; index++)
            Assert.True(queue.Enqueue(RemoteInputCommand.KeyUp(0x41), capacity));
        Assert.False(tracker.TryQueueMouseUp(RemoteMouseButton.Left, new Point(30, 40), 7, QueueOwned));
        tracker.UpdatePressedMousePosition(new(100, 110), 7);
        Assert.Equal(320, queue.Count);
        while (queue.TryDequeue(out _)) { }
        Assert.Equal(1, tracker.RetryPendingReleases(7, QueueOwned));
        Assert.True(queue.TryDequeue(out var release));
        Assert.Equal(RemoteInputCommand.MouseUp(RemoteMouseButton.Left, 30, 40), release);
        Assert.False(queue.TryDequeue(out _));
        Assert.False(tracker.HasPendingReleases(7));
    }

    [Fact]
    public void RetryRejectsMissingAdmissionCallback()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new RemoteInputOwnershipTracker().RetryPendingReleases(7, null!));
    }

    private static RemoteInputOwnershipTracker PendingMouse()
    {
        var tracker = new RemoteInputOwnershipTracker();
        Assert.True(tracker.TryQueueMouseDown(RemoteMouseButton.Left, new(1, 2), _ => new(true, 7)));
        Assert.False(tracker.TryQueueMouseUp(RemoteMouseButton.Left, new Point(30, 40), 7, (_, _) => false));
        return tracker;
    }
}
