using Xunit;

namespace RemoteDesk.Tests;

public sealed class WindowsSessionShortcutHandlerTests
{
    [Theory]
    [InlineData(0x10, 0xA1, 0x36, false)]
    [InlineData(0x11, 0xA3, 0x1D, true)]
    [InlineData(0x12, 0xA5, 0x38, true)]
    public void PhysicalReleaseOfSyntheticLegacyModifierRestoresUnmodifiedShortcut(int generic, int sided, int scan, bool extended)
    {
        var handler = new WindowsSessionShortcutHandler();
        var win = RemoteInputCommand.KeyDown((int)Keys.LWin);
        handler.Observe(win);
        handler.Observe(RemoteInputCommand.KeyDown(generic));
        handler.Observe(RemoteInputCommand.KeyUp(sided, scan,
            RemoteKeyboardFlags.HasScanCode | (extended ? RemoteKeyboardFlags.Extended : 0)));
        int mockLocks = 0;
        Assert.True(handler.TryHandle(RemoteInputCommand.KeyDown((int)Keys.L),
            pressed => Assert.Equal(win, pressed), () => mockLocks++));
        Assert.Equal(1, mockLocks);
    }

    [Fact]
    public void ExactPhysicalModifierUpLeavesLegacyAndOtherSideGuardedUntilTheirOwnReleases()
    {
        var handler = new WindowsSessionShortcutHandler();
        var generic = RemoteInputCommand.KeyDown((int)Keys.ShiftKey);
        var left = RemoteInputCommand.KeyDown((int)Keys.LShiftKey, 0x2A, RemoteKeyboardFlags.HasScanCode);
        var right = RemoteInputCommand.KeyDown((int)Keys.RShiftKey, 0x36, RemoteKeyboardFlags.HasScanCode);
        handler.Observe(RemoteInputCommand.KeyDown((int)Keys.LWin));
        handler.Observe(generic); handler.Observe(left); handler.Observe(right);
        handler.Observe(right with { Kind = RemoteInputKind.KeyUp });
        handler.Observe(left with { Kind = RemoteInputKind.KeyUp });
        Assert.False(handler.TryHandle(RemoteInputCommand.KeyDown((int)Keys.L), _ => Assert.Fail(), () => Assert.Fail()));
        handler.Observe(generic with { Kind = RemoteInputKind.KeyUp });
        int mockLocks = 0;
        Assert.True(handler.TryHandle(RemoteInputCommand.KeyDown((int)Keys.L), _ => { }, () => mockLocks++));
        Assert.Equal(1, mockLocks);
    }

    [Theory]
    [MemberData(nameof(RemoteKeyboardOwnershipRegressionTests.Modifiers), MemberType = typeof(RemoteKeyboardOwnershipRegressionTests))]
    public void ModifierReleasedThroughAnotherCaptureSourceDoesNotBlockLaterWinL(
        int generic, int sided, int scan, bool extended, bool reverse)
    {
        var flags = RemoteKeyboardFlags.HasScanCode | (extended ? RemoteKeyboardFlags.Extended : 0);
        var down = RemoteInputCommand.KeyDown(reverse ? generic : sided, scan, flags);
        var up = RemoteInputCommand.KeyUp(reverse ? sided : generic, scan, flags);
        var win = RemoteInputCommand.KeyDown((int)Keys.LWin, 0x5B,
            RemoteKeyboardFlags.HasScanCode | RemoteKeyboardFlags.Extended);
        var handler = new WindowsSessionShortcutHandler();
        handler.Observe(win);
        handler.Observe(down);
        Assert.False(handler.TryHandle(RemoteInputCommand.KeyDown((int)Keys.L),
            _ => Assert.Fail("Modifier remains held"), () => Assert.Fail("No OS locking callback")));
        handler.Observe(up);
        var order = new List<string>();
        Assert.True(handler.TryHandle(RemoteInputCommand.KeyDown((int)Keys.L),
            command => { Assert.Equal(win, command); order.Add("release-owned-win"); },
            () => order.Add("mock-lock-only")));
        Assert.Equal(["release-owned-win", "mock-lock-only"], order);
    }

    [Fact]
    public void ReleasingOneShiftSideDoesNotDropTheOtherModifierFromShortcutGuard()
    {
        var handler = new WindowsSessionShortcutHandler();
        var win = RemoteInputCommand.KeyDown((int)Keys.LWin);
        var left = RemoteInputCommand.KeyDown((int)Keys.LShiftKey, 0x2A, RemoteKeyboardFlags.HasScanCode);
        var right = RemoteInputCommand.KeyDown((int)Keys.RShiftKey, 0x36,
            RemoteKeyboardFlags.HasScanCode | RemoteKeyboardFlags.Extended);
        handler.Observe(win);
        handler.Observe(left);
        // An unowned opposite-side up must not clear the held left Shift.
        handler.Observe(RemoteInputCommand.KeyUp((int)Keys.ShiftKey, 0x36, RemoteKeyboardFlags.HasScanCode));
        Assert.False(handler.TryHandle(RemoteInputCommand.KeyDown((int)Keys.L), _ => Assert.Fail(), () => Assert.Fail()));
        handler.Observe(right);
        handler.Observe(RemoteInputCommand.KeyUp((int)Keys.ShiftKey, 0x36, RemoteKeyboardFlags.HasScanCode));
        Assert.False(handler.TryHandle(RemoteInputCommand.KeyDown((int)Keys.L), _ => Assert.Fail(), () => Assert.Fail()));
        handler.Observe(RemoteInputCommand.KeyUp((int)Keys.ShiftKey, 0x2A, RemoteKeyboardFlags.HasScanCode));
        int mockLocks = 0;
        Assert.True(handler.TryHandle(RemoteInputCommand.KeyDown((int)Keys.L),
            command => Assert.Equal(win, command), () => mockLocks++));
        Assert.Equal(1, mockLocks);
    }

    [Theory]
    [InlineData((int)Keys.LWin, 0, 0)]
    [InlineData((int)Keys.RWin, 0, 0)]
    [InlineData((int)Keys.LWin, 0x5B, 3)]
    public void WinLReleasesOwnWindowsKeyBeforeLockingAndConsumesLateUps(int vk, int scan, int flagValue)
    {
        var flags = (RemoteKeyboardFlags)flagValue;
        var handler = new WindowsSessionShortcutHandler();
        var win = RemoteInputCommand.KeyDown(vk, scan, flags);
        var letter = RemoteInputCommand.KeyDown((int)Keys.L);
        var order = new List<string>();
        handler.Observe(win);
        Assert.True(handler.TryHandle(letter, key => { Assert.Equal(win, key); order.Add("release"); }, () => order.Add("lock")));
        Assert.Equal(new[] { "release", "lock" }, order);
        Assert.True(handler.TryHandle(letter, _ => Assert.Fail(), () => Assert.Fail())); // Repeat does not lock again.
        Assert.True(handler.TryHandle(RemoteInputCommand.KeyUp((int)Keys.L), _ => Assert.Fail(), () => Assert.Fail()));
        Assert.True(handler.TryHandle(RemoteInputCommand.KeyUp(vk, scan, flags), _ => Assert.Fail(), () => Assert.Fail()));
        Assert.False(handler.TryHandle(letter, _ => Assert.Fail(), () => Assert.Fail())); // Plain password letter L.
    }

    [Theory]
    [InlineData((int)Keys.ControlKey)]
    [InlineData((int)Keys.RControlKey)]
    [InlineData((int)Keys.ShiftKey)]
    [InlineData((int)Keys.Menu)]
    public void ModifiedWinLIsNotMistakenForLock(int modifier)
    {
        var handler = new WindowsSessionShortcutHandler();
        handler.Observe(RemoteInputCommand.KeyDown((int)Keys.LWin));
        handler.Observe(RemoteInputCommand.KeyDown(modifier));
        Assert.False(handler.TryHandle(RemoteInputCommand.KeyDown((int)Keys.L), _ => Assert.Fail(), () => Assert.Fail()));
        handler.Observe(RemoteInputCommand.KeyUp(modifier));
        Assert.True(handler.TryHandle(RemoteInputCommand.KeyDown((int)Keys.L), _ => { }, () => { }));
    }

    [Fact]
    public void AnotherConnectionsModifierCannotTriggerLock()
    {
        var first = new WindowsSessionShortcutHandler();
        var second = new WindowsSessionShortcutHandler();
        first.Observe(RemoteInputCommand.KeyDown((int)Keys.LWin));
        Assert.False(second.TryHandle(RemoteInputCommand.KeyDown((int)Keys.L), _ => Assert.Fail(), () => Assert.Fail()));
    }

    [Fact]
    public void LockFailureDoesNotReplayLetterOrReReleaseWindowsKey()
    {
        var handler = new WindowsSessionShortcutHandler();
        var win = RemoteInputCommand.KeyDown((int)Keys.LWin);
        handler.Observe(win);
        var letter = RemoteInputCommand.KeyDown((int)Keys.L);
        Assert.Throws<InvalidOperationException>(() => handler.TryHandle(letter, _ => { }, () => throw new InvalidOperationException()));
        Assert.True(handler.TryConsumeRelease(win));
        Assert.True(handler.TryHandle(RemoteInputCommand.KeyUp((int)Keys.L), _ => Assert.Fail(), () => Assert.Fail()));
        Assert.False(handler.TryHandle(letter, _ => Assert.Fail(), () => Assert.Fail()));
    }

    [Fact]
    public void FailedModifierReleaseRetainsOwnershipAndDoesNotLock()
    {
        var handler = new WindowsSessionShortcutHandler();
        var win = RemoteInputCommand.KeyDown((int)Keys.LWin);
        handler.Observe(win);
        Assert.Throws<InvalidOperationException>(() => handler.TryHandle(RemoteInputCommand.KeyDown((int)Keys.L),
            _ => throw new InvalidOperationException(), () => Assert.Fail()));
        Assert.False(handler.TryConsumeRelease(win)); // Caller must still perform native release.
    }
}
