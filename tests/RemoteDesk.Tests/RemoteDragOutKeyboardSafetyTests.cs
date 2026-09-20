using Xunit;

namespace RemoteDesk.Tests;

public sealed class RemoteDragOutKeyboardSafetyTests
{
    [Theory]
    [InlineData(0x11)]
    [InlineData(0xa2)]
    [InlineData(0xa3)]
    [InlineData(0x10)]
    [InlineData(0xa1)]
    [InlineData(0x12)]
    [InlineData(0xa5)]
    [InlineData(0x5b)]
    [InlineData(0x5c)]
    [InlineData(0x43)]
    public void HeldKeysNeverBecomeModifiedEscapeOrGetReleasedBySyntheticCopy(int virtualKey)
    {
        var ownership = new RemoteInputOwnershipTracker();
        var sent = new List<RemoteInputCommand>();
        Assert.True(ownership.TryQueueKey(RemoteInputCommand.KeyDown(virtualKey), 7,
            command => { sent.Add(command); return new(true, 7); },
            (_, _) => true));
        bool keyboardAllowed = RemoteViewerWindow.CanSendRemoteDragOutKeyboard(
            ownership.AnyPressedKey(7, _ => true), ownership.HasPendingReleases(7));

        Assert.False(keyboardAllowed);
        Assert.Equal(new[] { RemoteInputCommand.MouseUp(RemoteMouseButton.Left, 10, 20) },
            RemoteViewerWindow.CreateRemoteDragOutCancelCommands(new(10, 20), keyboardAllowed));
        Assert.Empty(RemoteViewerWindow.CreateRemoteDragOutCopyCommands(keyboardAllowed));
        Assert.True(ownership.AnyPressedKey(7, key => key.VirtualKey == virtualKey));
        Assert.Equal(new[] { RemoteInputCommand.KeyDown(virtualKey) }, sent);
    }

    [Fact]
    public void UnmodifiedDragRetainsBalancedEscapeAndCopySequences()
    {
        Assert.True(RemoteViewerWindow.CanSendRemoteDragOutKeyboard(false, false));
        Assert.Equal(new[]
        {
            RemoteInputCommand.KeyDown((int)Keys.Escape),
            RemoteInputCommand.KeyUp((int)Keys.Escape),
            RemoteInputCommand.MouseUp(RemoteMouseButton.Left, 10, 20)
        }, RemoteViewerWindow.CreateRemoteDragOutCancelCommands(new(10, 20)));
        Assert.Equal(new[]
        {
            RemoteInputCommand.KeyDown((int)Keys.ControlKey),
            RemoteInputCommand.KeyDown((int)Keys.C),
            RemoteInputCommand.KeyUp((int)Keys.C),
            RemoteInputCommand.KeyUp((int)Keys.ControlKey)
        }, RemoteViewerWindow.CreateRemoteDragOutCopyCommands());
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PendingReleaseOrAnyHeldKeyDisablesAllSyntheticDragKeyboard(bool held, bool pending)
    {
        Assert.False(RemoteViewerWindow.CanSendRemoteDragOutKeyboard(held, pending));
    }

    [Fact]
    public void OnlyCurrentGenerationHeldKeysBlockCurrentShortcutPolicy()
    {
        var ownership = new RemoteInputOwnershipTracker();
        ownership.TryQueueKey(RemoteInputCommand.KeyDown((int)Keys.ControlKey), 7,
            _ => new(true, 7), (_, _) => true);
        Assert.False(RemoteViewerWindow.CanSendRemoteDragOutKeyboard(
            ownership.AnyPressedKey(7, _ => true), ownership.HasPendingReleases(7)));
        Assert.True(RemoteViewerWindow.CanSendRemoteDragOutKeyboard(
            ownership.AnyPressedKey(8, _ => true), ownership.HasPendingReleases(8)));
    }
}
