using Xunit;

namespace RemoteDesk.Tests;

public sealed class RemoteViewerClientInputQueueTests
{
    [Fact]
    public void InputQueueCoalescesConsecutiveMouseMoves()
    {
        var queue = new RemoteInputQueue();

        Assert.True(queue.Enqueue(RemoteInputCommand.MouseMove(10, 10), maxQueuedInputs: 8));
        Assert.True(queue.Enqueue(RemoteInputCommand.MouseMove(20, 30), maxQueuedInputs: 8));

        Assert.Equal(1, queue.Count);
        Assert.True(queue.TryDequeue(out RemoteInputCommand command));
        Assert.Equal(RemoteInputKind.MouseMove, command.Kind);
        Assert.Equal(20, command.X);
        Assert.Equal(30, command.Y);
        Assert.False(queue.TryDequeue(out _));
    }

    [Fact]
    public void InputQueueSkipsDuplicateMouseMoves()
    {
        var queue = new RemoteInputQueue();

        Assert.True(queue.Enqueue(RemoteInputCommand.MouseMove(10, 10), maxQueuedInputs: 8));
        Assert.False(queue.Enqueue(RemoteInputCommand.MouseMove(10, 10), maxQueuedInputs: 8));

        Assert.Equal(1, queue.Count);
        Assert.True(queue.TryDequeue(out RemoteInputCommand command));
        Assert.Equal(RemoteInputKind.MouseMove, command.Kind);
        Assert.Equal(10, command.X);
        Assert.Equal(10, command.Y);
        Assert.False(queue.TryDequeue(out _));
    }

    [Fact]
    public void InputQueueDropsOldestMouseMoveWhenFull()
    {
        var queue = new RemoteInputQueue();

        queue.Enqueue(RemoteInputCommand.KeyDown(65), maxQueuedInputs: 4);
        queue.Enqueue(RemoteInputCommand.MouseMove(10, 10), maxQueuedInputs: 4);
        queue.Enqueue(RemoteInputCommand.MouseDown(RemoteMouseButton.Left, 10, 10), maxQueuedInputs: 4);
        queue.Enqueue(RemoteInputCommand.KeyUp(65), maxQueuedInputs: 4);
        queue.Enqueue(RemoteInputCommand.MouseUp(RemoteMouseButton.Left, 10, 10), maxQueuedInputs: 4);

        RemoteInputCommand[] commands = Drain(queue);
        Assert.Equal(4, commands.Length);
        Assert.DoesNotContain(commands, command => command.Kind == RemoteInputKind.MouseMove);
        Assert.Equal(RemoteInputKind.KeyDown, commands[0].Kind);
        Assert.Equal(RemoteInputKind.MouseDown, commands[1].Kind);
        Assert.Equal(RemoteInputKind.KeyUp, commands[2].Kind);
        Assert.Equal(RemoteInputKind.MouseUp, commands[3].Kind);
    }

    [Fact]
    public void InputQueueRejectsReliableCommandsInsteadOfDroppingQueuedStateWhenFull()
    {
        var queue = new RemoteInputQueue();
        RemoteInputCommand[] queued =
        [
            RemoteInputCommand.KeyDown(65),
            RemoteInputCommand.MouseDown(RemoteMouseButton.Left, 10, 10),
            RemoteInputCommand.KeyDown(66),
            RemoteInputCommand.MouseWheel(120, 10, 10)
        ];
        foreach (RemoteInputCommand command in queued)
        {
            Assert.True(queue.Enqueue(command, maxQueuedInputs: queued.Length));
        }

        Assert.False(queue.Enqueue(RemoteInputCommand.KeyDown(67), maxQueuedInputs: queued.Length));
        Assert.False(queue.Enqueue(RemoteInputCommand.TextInput('x'), maxQueuedInputs: queued.Length));

        Assert.Equal(queued, Drain(queue));
    }

    [Fact]
    public void InputQueueUsesBoundedReserveForKeyAndMouseReleaseWithoutDroppingReliableState()
    {
        var queue = new RemoteInputQueue();
        RemoteInputCommand[] queued =
        [
            RemoteInputCommand.KeyDown(65),
            RemoteInputCommand.MouseDown(RemoteMouseButton.Left, 10, 10)
        ];
        foreach (RemoteInputCommand command in queued)
        {
            Assert.True(queue.Enqueue(command, maxQueuedInputs: queued.Length));
        }

        RemoteInputCommand keyUp = RemoteInputCommand.KeyUp(65);
        RemoteInputCommand mouseUp = RemoteInputCommand.MouseUp(RemoteMouseButton.Left, 10, 10);
        Assert.True(queue.Enqueue(keyUp, maxQueuedInputs: queued.Length));
        Assert.True(queue.Enqueue(mouseUp, maxQueuedInputs: queued.Length));

        Assert.Equal([.. queued, keyUp, mouseUp], Drain(queue));
    }

    [Fact]
    public void InputQueueRejectsReleaseAfterReserveFillsWithoutDeletingReliableState()
    {
        const int capacity = 2;
        var queue = new RemoteInputQueue();
        var expected = new List<RemoteInputCommand>
        {
            RemoteInputCommand.KeyDown(65),
            RemoteInputCommand.MouseDown(RemoteMouseButton.Left, 10, 10)
        };
        foreach (RemoteInputCommand command in expected)
        {
            Assert.True(queue.Enqueue(command, capacity));
        }

        for (int index = 0; index < RemoteInputQueue.ReleaseReserveCapacity; index++)
        {
            RemoteInputCommand release = index % 2 == 0
                ? RemoteInputCommand.KeyUp(65 + index)
                : RemoteInputCommand.MouseUp(RemoteMouseButton.Left, index, index);
            Assert.True(queue.Enqueue(release, capacity));
            expected.Add(release);
        }

        Assert.False(queue.Enqueue(RemoteInputCommand.KeyUp(999), capacity));
        Assert.Equal(capacity + RemoteInputQueue.ReleaseReserveCapacity, queue.Count);
        Assert.Equal(expected, Drain(queue));
    }

    [Fact]
    public void InputQueueRemovesStaleMouseMovesBeforeAppendingLatestMove()
    {
        var queue = new RemoteInputQueue();

        queue.Enqueue(RemoteInputCommand.MouseMove(10, 10), maxQueuedInputs: 8);
        queue.Enqueue(RemoteInputCommand.KeyDown(65), maxQueuedInputs: 8);
        queue.Enqueue(RemoteInputCommand.MouseMove(20, 20), maxQueuedInputs: 8);
        queue.Enqueue(RemoteInputCommand.MouseDown(RemoteMouseButton.Left, 20, 20), maxQueuedInputs: 8);
        queue.Enqueue(RemoteInputCommand.MouseMove(30, 40), maxQueuedInputs: 8);

        RemoteInputCommand[] commands = Drain(queue);

        Assert.Equal(3, commands.Length);
        Assert.Equal(RemoteInputKind.KeyDown, commands[0].Kind);
        Assert.Equal(RemoteInputKind.MouseDown, commands[1].Kind);
        Assert.Equal(RemoteInputKind.MouseMove, commands[2].Kind);
        Assert.Equal(30, commands[2].X);
        Assert.Equal(40, commands[2].Y);
    }

    [Fact]
    public void RemovePendingMouseMovesPreservesReliableInputOrder()
    {
        var queue = new RemoteInputQueue();
        RemoteInputCommand keyDown = RemoteInputCommand.KeyDown(65);
        RemoteInputCommand mouseDown =
            RemoteInputCommand.MouseDown(
                RemoteMouseButton.Left,
                20,
                30);
        RemoteInputCommand keyUp = RemoteInputCommand.KeyUp(65);

        Assert.True(queue.Enqueue(keyDown, maxQueuedInputs: 8));
        Assert.True(queue.Enqueue(
            RemoteInputCommand.MouseMove(20, 30),
            maxQueuedInputs: 8));
        Assert.True(queue.Enqueue(mouseDown, maxQueuedInputs: 8));
        Assert.True(queue.Enqueue(keyUp, maxQueuedInputs: 8));

        Assert.Equal(1, queue.RemovePendingMouseMoves());
        Assert.Equal(0, queue.RemovePendingMouseMoves());
        Assert.Equal(
            [keyDown, mouseDown, keyUp],
            Drain(queue));
    }

    [Fact]
    public void InputQueueReusesStorageAfterDrain()
    {
        var queue = new RemoteInputQueue();

        for (int index = 0; index < 300; index++)
        {
            queue.Enqueue(RemoteInputCommand.KeyDown(index), maxQueuedInputs: 512);
        }

        Assert.Equal(300, Drain(queue).Length);

        queue.Enqueue(RemoteInputCommand.KeyUp(42), maxQueuedInputs: 512);

        Assert.Equal(1, queue.Count);
        Assert.True(queue.TryDequeue(out RemoteInputCommand command));
        Assert.Equal(RemoteInputKind.KeyUp, command.Kind);
        Assert.Equal(42, command.Data);
    }

    [Fact]
    public void InputQueueRejectsWhenMaxQueuedInputsIsZero()
    {
        var queue = new RemoteInputQueue();

        Assert.False(queue.Enqueue(RemoteInputCommand.KeyDown(65), maxQueuedInputs: 0));

        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void FindInputDropIndexPrefersOldestMouseMove()
    {
        RemoteInputCommand[] queue =
        [
            RemoteInputCommand.KeyDown(65),
            RemoteInputCommand.MouseMove(10, 10),
            RemoteInputCommand.MouseMove(20, 20),
            RemoteInputCommand.KeyUp(65)
        ];

        int dropIndex = RemoteViewerClient.FindInputDropIndex(queue);

        Assert.Equal(1, dropIndex);
    }

    [Fact]
    public void FindInputDropIndexReturnsNegativeWhenNoLossyCommandExists()
    {
        RemoteInputCommand[] queue =
        [
            RemoteInputCommand.KeyDown(65),
            RemoteInputCommand.MouseDown(RemoteMouseButton.Left, 10, 10),
            RemoteInputCommand.MouseUp(RemoteMouseButton.Left, 10, 10)
        ];

        int dropIndex = RemoteViewerClient.FindInputDropIndex(queue);

        Assert.Equal(-1, dropIndex);
    }

    [Fact]
    public void ViewerInputQueueUsesLowLatencyBound()
    {
        Assert.Equal(256, RemoteViewerClient.MaxQueuedInputs);
    }

    [Fact]
    public void WindowsFrameSocketBuffersUseLowLatencyBound()
    {
        Assert.Equal(128 * 1024, RemoteViewerClient.FrameReceiveBufferBytes);
        Assert.Equal(128 * 1024, RemoteHostServer.FrameSendBufferBytes);
    }

    [Fact]
    public void TextInputReportsTruncatedWhenReliableQueueAppliesBackpressure()
    {
        var queue = new RemoteInputQueue();
        for (int index = 0; index < RemoteViewerClient.MaxQueuedInputs; index++)
        {
            Assert.True(queue.Enqueue(
                RemoteInputCommand.KeyDown(index),
                RemoteViewerClient.MaxQueuedInputs));
        }

        RemoteTextInputResult result = RemoteViewerClient.QueueTextInput(queue, "blocked");

        Assert.Equal(0, result.SentCodePoints);
        Assert.True(result.Truncated);
        Assert.Equal(RemoteViewerClient.MaxQueuedInputs, queue.Count);
    }

    [Fact]
    public void InputBatchSizeKeepsWriteLockShortForInteractiveLatency()
    {
        Assert.InRange(RemoteViewerClient.MaxInputBatchSize, 1, 16);
    }

    [Fact]
    public void HeartbeatTimeoutIsSuppressedWhileLongRunningControlOperationIsPending()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset staleMessage = now - TimeSpan.FromMinutes(1);

        Assert.True(RemoteViewerClient.ShouldDisconnectForHeartbeat(
            now,
            staleMessage,
            longRunningControlOperationPending: false));
        Assert.False(RemoteViewerClient.ShouldDisconnectForHeartbeat(
            now,
            staleMessage,
            longRunningControlOperationPending: true));
        Assert.True(RemoteViewerClient.ShouldDisconnectForHeartbeat(
            RemoteViewerClient.SuppressedHeartbeatTimeout +
                TimeSpan.FromMilliseconds(1),
            longRunningControlOperationPending: true));
        Assert.False(RemoteViewerClient.ShouldDisconnectForHeartbeat(
            TimeSpan.FromSeconds(17),
            longRunningControlOperationPending: false));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void HeartbeatTimeoutSuppressionIncludesReturnedFilePreparation(
        bool confirmationPending,
        bool returnedFileRequestPending,
        bool expected)
    {
        Assert.Equal(
            expected,
            RemoteViewerClient.IsHeartbeatTimeoutSuppressed(
                confirmationPending,
                returnedFileRequestPending));
    }

    private static RemoteInputCommand[] Drain(RemoteInputQueue queue)
    {
        var commands = new List<RemoteInputCommand>();
        while (queue.TryDequeue(out RemoteInputCommand command))
        {
            commands.Add(command);
        }

        return commands.ToArray();
    }
}
