using Xunit;

namespace RemoteDesk.Tests;

public sealed class WindowsSecureDesktopKeyStateTests
{
    [Theory]
    [InlineData(0x10, 0xA1, 0x36, false)]
    [InlineData(0x11, 0xA3, 0x1D, true)]
    [InlineData(0x12, 0xA5, 0x38, true)]
    public void PhysicalReleaseClearsSyntheticLegacyModifierWhenNoExactOwnerExists(int generic, int sided, int scan, bool extended)
    {
        var state = new WindowsSecureDesktopKeyState();
        var pressed = RemoteInputCommand.KeyDown(generic);
        var released = RemoteInputCommand.KeyUp(sided, scan,
            RemoteKeyboardFlags.HasScanCode | (extended ? RemoteKeyboardFlags.Extended : 0));
        state.Press(pressed);
        state.ForgetReleased(released);
        Assert.Equal(0, state.Count);
        state.ReleaseAll(_ => Assert.Fail("Released synthetic modifier must not survive disconnect"));
        state.Press(pressed);
        var nativeReleases = new List<RemoteInputCommand>();
        state.Release(released with { Kind = RemoteInputKind.KeyDown }, nativeReleases.Add);
        Assert.Equal(pressed, Assert.Single(nativeReleases));
        Assert.Equal(0, state.Count);
    }

    [Fact]
    public void ExactPhysicalReleaseDoesNotAlsoClearLegacyOrOtherSideOwner()
    {
        var state = new WindowsSecureDesktopKeyState();
        var generic = RemoteInputCommand.KeyDown((int)Keys.ShiftKey);
        var left = RemoteInputCommand.KeyDown((int)Keys.LShiftKey, 0x2A, RemoteKeyboardFlags.HasScanCode);
        var right = RemoteInputCommand.KeyDown((int)Keys.RShiftKey, 0x36, RemoteKeyboardFlags.HasScanCode);
        state.Press(generic); state.Press(left); state.Press(right);
        state.ForgetReleased(right with { Kind = RemoteInputKind.KeyUp });
        Assert.Equal(2, state.Count);
        var releases = new List<RemoteInputCommand>();
        state.ReleaseAll(releases.Add);
        Assert.Contains(generic, releases);
        Assert.Contains(left, releases);
        Assert.DoesNotContain(right, releases);
    }

    [Fact]
    public void GenericLegacyReleaseKeepsAllMatchingModifierContractEvenWithExactGenericOwner()
    {
        var state = new WindowsSecureDesktopKeyState();
        var generic = RemoteInputCommand.KeyDown((int)Keys.ControlKey);
        state.Press(generic); state.Press(Key(false)); state.Press(Key(true));
        var releases = new List<RemoteInputCommand>();
        state.Release(generic, releases.Add);
        Assert.Equal(3, releases.Count);
        Assert.Equal(0, state.Count);
    }

    [Fact]
    public void ExplicitSideLegacyReleasePrefersExactOwnerOverFallback()
    {
        var state = new WindowsSecureDesktopKeyState();
        var generic = RemoteInputCommand.KeyDown((int)Keys.ShiftKey);
        var right = RemoteInputCommand.KeyDown((int)Keys.RShiftKey);
        state.Press(generic); state.Press(right);
        state.ForgetReleased(right with { Kind = RemoteInputKind.KeyUp });
        var releases = new List<RemoteInputCommand>();
        state.ReleaseAll(releases.Add);
        Assert.Equal(generic, Assert.Single(releases));
    }

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
