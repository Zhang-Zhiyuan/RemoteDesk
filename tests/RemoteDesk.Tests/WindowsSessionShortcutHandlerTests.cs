using Xunit;

namespace RemoteDesk.Tests;

public sealed class WindowsSessionShortcutHandlerTests
{
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
