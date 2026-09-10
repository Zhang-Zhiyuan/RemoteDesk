using Xunit;

namespace RemoteDesk.Tests;

public sealed class WindowsSecureDesktopKeyStateTests
{
    private static RemoteInputCommand Key(bool extended) => RemoteInputCommand.KeyDown((int)Keys.ControlKey,
        0x1D, RemoteKeyboardFlags.HasScanCode | (extended ? RemoteKeyboardFlags.Extended : 0));

    [Fact]
    public void LeftAndRightModifierOwnershipIsIndependent()
    {
        var state = new WindowsSecureDesktopKeyState();
        state.Press(Key(false)); state.Press(Key(true));
        Assert.Equal(2, state.Count);
        state.ForgetReleased(Key(true) with { Kind = RemoteInputKind.KeyUp });
        var released = new List<RemoteInputCommand>();
        state.ReleaseAll(released.Add);
        Assert.Equal(new[] { Key(false) }, released);
        Assert.Equal(0, state.Count);
    }

    [Fact]
    public void KeyRepeatDoesNotDuplicateOwnership()
    {
        var state = new WindowsSecureDesktopKeyState();
        state.Press(Key(false)); state.Press(Key(false));
        Assert.Equal(1, state.Count);
        var released = new List<RemoteInputCommand>();
        state.Release(Key(false), released.Add);
        Assert.Single(released);
        state.Release(Key(false), _ => Assert.Fail()); // Not another client's key.
    }

    [Fact]
    public void GenericMobileReleaseClearsAllMatchingPhysicalKeys()
    {
        var state = new WindowsSecureDesktopKeyState();
        state.Press(Key(false)); state.Press(Key(true));
        var released = new List<RemoteInputCommand>();
        state.Release(RemoteInputCommand.KeyDown((int)Keys.ControlKey), released.Add);
        Assert.Equal(2, released.Count);
        Assert.Equal(0, state.Count);
    }

    [Fact]
    public void NativeFailureRetainsOwnershipForDisconnectCleanup()
    {
        var state = new WindowsSecureDesktopKeyState();
        state.Press(Key(false)); state.Press(Key(true));
        Assert.Throws<InvalidOperationException>(() => state.Release(Key(false), _ => throw new InvalidOperationException()));
        Assert.Equal(2, state.Count);
        state.ReleaseAll(command => { if (command == Key(false)) throw new InvalidOperationException(); });
        Assert.Equal(1, state.Count);
        state.ReleaseAll(command => Assert.Equal(Key(false), command));
        Assert.Equal(0, state.Count);
    }
}
