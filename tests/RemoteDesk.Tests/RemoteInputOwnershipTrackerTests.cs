using System.Drawing;
using System.Windows.Forms;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class RemoteInputOwnershipTrackerTests
{
    [Fact]
    public void MouseUpWithoutCurrentMappingUsesLastPointAndClearsOnce()
    {
        var tracker = new RemoteInputOwnershipTracker();
        Assert.True(tracker.TryQueueMouseDown(
            RemoteMouseButton.Left,
            new Point(12, 18),
            command =>
            {
                Assert.Equal(
                    RemoteInputCommand.MouseDown(
                        RemoteMouseButton.Left,
                        12,
                        18),
                    command);
                return new RemoteInputQueueAdmission(
                    Accepted: true,
                    ConnectionGeneration: 41);
            }));

        tracker.UpdatePressedMousePosition(
            new Point(30, 44),
            connectionGeneration: 41);
        var releases = new List<RemoteInputCommand>();
        Assert.True(tracker.TryQueueMouseUp(
            RemoteMouseButton.Left,
            mappedRemotePoint: null,
            currentConnectionGeneration: 41,
            (command, generation) =>
            {
                Assert.Equal(41, generation);
                releases.Add(command);
                return true;
            }));

        Assert.Equal(
            [RemoteInputCommand.MouseUp(
                RemoteMouseButton.Left,
                30,
                44)],
            releases);
        Assert.Equal(0, tracker.PressedMouseButtonCount);
        Assert.False(tracker.TryQueueMouseUp(
            RemoteMouseButton.Left,
            mappedRemotePoint: null,
            currentConnectionGeneration: 41,
            (_, _) =>
            {
                throw new InvalidOperationException(
                    "已释放的按钮不应重复入队。");
            }));
    }

    [Fact]
    public void FailedMouseUpAdmissionRetainsOwnershipForRetry()
    {
        var tracker = new RemoteInputOwnershipTracker();
        Assert.True(tracker.TryQueueMouseDown(
            RemoteMouseButton.Right,
            new Point(7, 9),
            _ => new RemoteInputQueueAdmission(
                Accepted: true,
                ConnectionGeneration: 12)));

        int attempts = 0;
        Assert.False(tracker.TryQueueMouseUp(
            RemoteMouseButton.Right,
            mappedRemotePoint: null,
            currentConnectionGeneration: 12,
            (command, generation) =>
            {
                attempts++;
                Assert.Equal(12, generation);
                Assert.Equal(7, command.X);
                Assert.Equal(9, command.Y);
                return false;
            }));
        Assert.Equal(1, tracker.PressedMouseButtonCount);

        Assert.True(tracker.TryQueueMouseUp(
            RemoteMouseButton.Right,
            mappedRemotePoint: null,
            currentConnectionGeneration: 12,
            (_, generation) =>
            {
                attempts++;
                return generation == 12;
            }));
        Assert.Equal(2, attempts);
        Assert.Equal(0, tracker.PressedMouseButtonCount);
    }

    [Fact]
    public void ReleaseAllIsBestEffortAndSuccessfulAdmissionsAreExactlyOnce()
    {
        var tracker = new RemoteInputOwnershipTracker();
        Assert.True(tracker.TryQueueMouseDown(
            RemoteMouseButton.Left,
            new Point(4, 5),
            _ => new RemoteInputQueueAdmission(true, 3)));
        Assert.True(tracker.TryQueueMouseDown(
            RemoteMouseButton.Middle,
            new Point(8, 9),
            _ => new RemoteInputQueueAdmission(true, 3)));
        Assert.True(tracker.TryQueueKey(
            RemoteInputCommand.KeyDown((int)Keys.A),
            currentConnectionGeneration: 3,
            _ => new RemoteInputQueueAdmission(true, 3),
            (_, _) => false));

        var admitted = new List<RemoteInputCommand>();
        int released = tracker.ReleaseAll(
            currentConnectionGeneration: 3,
            (command, generation) =>
            {
                Assert.Equal(3, generation);
                if (command.Kind == RemoteInputKind.MouseUp &&
                    command.Button == RemoteMouseButton.Middle)
                {
                    return false;
                }

                admitted.Add(command);
                return true;
            });

        Assert.Equal(2, released);
        Assert.Equal(0, tracker.PressedKeyCount);
        Assert.Equal(1, tracker.PressedMouseButtonCount);
        Assert.Contains(
            admitted,
            command =>
                command.Kind == RemoteInputKind.MouseUp &&
                command.Button == RemoteMouseButton.Left);
        Assert.Contains(
            admitted,
            command =>
                command.Kind == RemoteInputKind.KeyUp &&
                command.Data == (int)Keys.A);

        Assert.Equal(
            1,
            tracker.ReleaseAll(
                currentConnectionGeneration: 3,
                (command, generation) =>
                {
                    Assert.Equal(
                        RemoteMouseButton.Middle,
                        command.Button);
                    Assert.Equal(3, generation);
                    admitted.Add(command);
                    return true;
                }));
        Assert.Equal(0, tracker.PressedMouseButtonCount);
        Assert.Equal(0, tracker.ReleaseAll(
            currentConnectionGeneration: 3,
            (_, _) =>
            {
                throw new InvalidOperationException(
                    "成功入队的释放不应重发。");
            }));
    }

    [Fact]
    public void KeyUpUsesOwningConnectionGenerationAndRetriesFailure()
    {
        var tracker = new RemoteInputOwnershipTracker();
        RemoteInputCommand keyDown =
            RemoteInputCommand.KeyDown(
                (int)Keys.ControlKey,
                scanCode: 29,
                RemoteKeyboardFlags.HasScanCode);
        Assert.True(tracker.TryQueueKey(
            keyDown,
            currentConnectionGeneration: 77,
            _ => new RemoteInputQueueAdmission(true, 77),
            (_, _) => false));

        RemoteInputCommand keyUp =
            RemoteInputCommand.KeyUp(
                (int)Keys.ControlKey,
                scanCode: 29,
                RemoteKeyboardFlags.HasScanCode);
        Assert.False(tracker.TryQueueKey(
            keyUp,
            currentConnectionGeneration: 77,
            _ => default,
            (_, generation) => generation == 78));
        Assert.Equal(1, tracker.PressedKeyCount);

        Assert.True(tracker.TryQueueKey(
            keyUp,
            currentConnectionGeneration: 77,
            _ => default,
            (command, generation) =>
            {
                Assert.Equal(77, generation);
                Assert.Equal(RemoteInputKind.KeyUp, command.Kind);
                Assert.Equal(29, command.X);
                return true;
            }));
        Assert.Equal(0, tracker.PressedKeyCount);
    }

    [Fact]
    public void NewConnectionRetiresOwnershipThatCannotBeRetried()
    {
        var tracker = new RemoteInputOwnershipTracker();
        Assert.True(tracker.TryQueueMouseDown(
            RemoteMouseButton.Left,
            new Point(3, 4),
            _ => new RemoteInputQueueAdmission(true, 10)));
        Assert.True(tracker.TryQueueKey(
            RemoteInputCommand.KeyDown((int)Keys.B),
            currentConnectionGeneration: 10,
            _ => new RemoteInputQueueAdmission(true, 10),
            (_, _) => false));

        Assert.Equal(
            0,
            tracker.ReleaseAll(
                currentConnectionGeneration: 11,
                (_, _) =>
                {
                    throw new InvalidOperationException(
                        "旧连接所有权不能写入新连接。");
                }));
        Assert.Equal(0, tracker.PressedMouseButtonCount);
        Assert.Equal(0, tracker.PressedKeyCount);
    }

    [Fact]
    public void AcceptedKeyDownOnNewGenerationOwnsMatchingKeyUp()
    {
        var tracker = new RemoteInputOwnershipTracker();
        RemoteInputCommand keyDown =
            RemoteInputCommand.KeyDown((int)Keys.C);
        Assert.True(tracker.TryQueueKey(
            keyDown,
            currentConnectionGeneration: 20,
            _ => new RemoteInputQueueAdmission(true, 20),
            (_, _) => false));
        Assert.True(tracker.TryQueueKey(
            keyDown,
            currentConnectionGeneration: 21,
            _ => new RemoteInputQueueAdmission(true, 21),
            (_, _) => false));

        Assert.True(tracker.TryQueueKey(
            RemoteInputCommand.KeyUp((int)Keys.C),
            currentConnectionGeneration: 21,
            _ => default,
            (_, generation) => generation == 21));
        Assert.Equal(0, tracker.PressedKeyCount);
    }
}
