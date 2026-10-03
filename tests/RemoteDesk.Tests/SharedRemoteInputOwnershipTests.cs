using Xunit;

namespace RemoteDesk.Tests;

public sealed class SharedRemoteInputOwnershipTests
{
    private static RemoteInputCommand Shift(bool right = true) => RemoteInputCommand.KeyDown(
        right ? (int)Keys.RShiftKey : (int)Keys.LShiftKey, right ? 0x36 : 0x2A, RemoteKeyboardFlags.HasScanCode);

    [Fact]
    public void LastSuccessfulDownOwnsReleaseAndAutoRepeatIsPreserved()
    {
        var state = new SharedRemoteInputOwnership();
        var first = new object(); var second = new object();
        var calls = new List<string>();
        state.Apply(first, Shift(), _ => calls.Add("first-down"));
        state.Apply(first, Shift(), _ => calls.Add("repeat"));
        state.Apply(second, Shift(), _ => calls.Add("second-down"));
        Assert.False(state.Apply(first, Shift() with { Kind = RemoteInputKind.KeyUp }, _ => Assert.Fail("Stale up")));
        Assert.False(state.ReleaseKey(first, Shift(), _ => Assert.Fail("Stale cleanup")));
        Assert.True(state.ReleaseKey(second, Shift(), _ => calls.Add("second-up")));
        Assert.False(state.ReleaseKey(second, Shift(), _ => Assert.Fail("Duplicate up")));
        Assert.Equal(new[] { "first-down", "repeat", "second-down", "second-up" }, calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FailedDownDoesNotTransferOwnershipAndFailedUpCanRetry(bool key)
    {
        var state = new SharedRemoteInputOwnership();
        var first = new object(); var second = new object();
        var down = key ? Shift() : RemoteInputCommand.MouseDown(RemoteMouseButton.Left, 1, 1);
        var up = down with { Kind = key ? RemoteInputKind.KeyUp : RemoteInputKind.MouseUp };
        state.Apply(first, down, _ => { });
        Assert.Throws<InvalidOperationException>(() => state.Apply(second, down, _ => throw new InvalidOperationException()));
        Assert.False(state.Apply(second, up, _ => Assert.Fail("Failed down owns nothing")));
        Assert.Throws<InvalidOperationException>(() => state.Apply(first, up, _ => throw new InvalidOperationException()));
        Assert.True(state.Apply(first, up, _ => { }));
    }

    [Fact]
    public void LeftAndRightModifiersRemainIndependent()
    {
        var state = new SharedRemoteInputOwnership();
        var left = new object(); var right = new object();
        state.Apply(left, Shift(false), _ => { });
        state.Apply(right, Shift(), _ => { });
        Assert.False(state.ReleaseKey(left, Shift(), _ => Assert.Fail("Wrong side")));
        Assert.True(state.ReleaseKey(left, Shift(false), _ => { }));
        Assert.True(state.ReleaseKey(right, Shift(), _ => { }));
    }

    [Fact]
    public void RightShiftAliasAndIncorrectE0FlagShareTheSameOwnership()
    {
        var state = new SharedRemoteInputOwnership();
        var old = new object(); var current = new object();
        state.Apply(old, Shift() with { Data = (int)Keys.ShiftKey, Y = (int)(RemoteKeyboardFlags.HasScanCode | RemoteKeyboardFlags.Extended) }, _ => { });
        state.Apply(current, Shift(), _ => { });
        Assert.False(state.ReleaseKey(old, Shift(), _ => Assert.Fail("Alias bypass")));
        Assert.True(state.ReleaseKey(current, Shift(), _ => { }));
    }

    [Fact]
    public void LegacyUpIsLimitedToItsOwnConnection()
    {
        var state = new SharedRemoteInputOwnership();
        var old = new object(); var current = new object();
        state.Apply(current, Shift(), _ => { });
        var legacy = RemoteInputCommand.KeyUp((int)Keys.ShiftKey);
        Assert.False(state.Apply(old, legacy, _ => Assert.Fail("Foreign legacy up")));
        Assert.True(state.Apply(current, legacy, _ => { }));
    }

    [Fact]
    public void GenericModifierUpReleasesOnlyItsOwnedPhysicalSide()
    {
        var state = new SharedRemoteInputOwnership();
        var left = new object(); var right = new object();
        state.Apply(left, Shift(false), _ => { });
        state.Apply(right, Shift(), _ => { });
        var released = new List<RemoteInputCommand>();
        state.Apply(left, RemoteInputCommand.KeyUp((int)Keys.ShiftKey), released.Add);
        Assert.Equal(Shift(false) with { Kind = RemoteInputKind.KeyUp }, Assert.Single(released));
        Assert.True(state.ReleaseKey(right, Shift(), _ => { }));
    }

    [Fact]
    public void GenericModifierUpReleasesAllOfItsOwnMatchingSidesExactlyOnce()
    {
        var state = new SharedRemoteInputOwnership();
        var owner = new object();
        state.Apply(owner, Shift(false), _ => { });
        state.Apply(owner, Shift(), _ => { });
        var released = new List<RemoteInputCommand>();
        Assert.True(state.Apply(owner, RemoteInputCommand.KeyUp((int)Keys.ShiftKey), released.Add));
        Assert.Equal(new[] { Shift(false) with { Kind = RemoteInputKind.KeyUp }, Shift() with { Kind = RemoteInputKind.KeyUp } }, released);
        Assert.False(state.ReleaseKey(owner, Shift(false), _ => Assert.Fail("Duplicate left up")));
        Assert.False(state.ReleaseKey(owner, Shift(), _ => Assert.Fail("Duplicate right up")));
    }

    [Fact]
    public void EndingOldOwnerCannotEraseNewOwnerOrOtherButtons()
    {
        var state = new SharedRemoteInputOwnership();
        var old = new object(); var current = new object();
        state.Apply(old, Shift(), _ => { });
        state.Apply(current, Shift(), _ => { });
        state.Apply(old, RemoteInputCommand.MouseDown(RemoteMouseButton.Left, 0, 0), _ => { });
        state.Apply(current, RemoteInputCommand.MouseDown(RemoteMouseButton.Left, 0, 0), _ => { });
        state.Apply(old, RemoteInputCommand.MouseDown(RemoteMouseButton.Right, 0, 0), _ => { });
        state.ForgetOwner(old);
        Assert.True(state.ReleaseKey(current, Shift(), _ => { }));
        Assert.True(state.ReleaseMouseButton(current, RemoteMouseButton.Left, () => { }));
        Assert.False(state.ReleaseMouseButton(old, RemoteMouseButton.Right, () => Assert.Fail("Disposed owner")));
    }

    [Fact]
    public void SecurePipeTeardownDoesNotReleaseAnotherPipesKey()
    {
        var shared = new SharedRemoteInputOwnership();
        var old = new WindowsSecureDesktopKeyState();
        var current = new WindowsSecureDesktopKeyState();
        old.Press(Shift()); shared.Apply(old, Shift(), _ => { });
        current.Press(Shift()); shared.Apply(current, Shift(), _ => { });
        old.ReleaseAll(key => shared.ReleaseKey(old, key, _ => Assert.Fail("Old pipe cleanup")));
        Assert.Equal(0, old.Count);
        Assert.Equal(1, current.Count);
        int released = 0;
        current.ReleaseAll(key => shared.ReleaseKey(current, key, _ => released++));
        Assert.Equal(1, released);
    }

    [Fact]
    public void SecurePipeLimitAccommodatesEverySupportedScreenAndReservesListener()
    {
        Assert.True(WindowsSecureDesktopAgent.MaximumActiveConnections >=
            2 * RemoteHostServer.ActiveClientGate<object>.MaximumScreens + 2);
        Assert.Equal(WindowsSecureDesktopAgent.MaximumActiveConnections + 1, WindowsSecureDesktopAgent.MaximumPipeInstances);
        Assert.InRange(WindowsSecureDesktopAgent.MaximumPipeInstances, 1, 16);
    }

    [Fact]
    public void SuppressedWindowReleaseAlsoForgetsItsShortcutModifier()
    {
        using var client = new WindowsSecureDesktopClient();
        var shortcuts = (WindowsSessionShortcutHandler)typeof(WindowsSecureDesktopClient)
            .GetField("_shortcuts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(client)!;
        shortcuts.Observe(RemoteInputCommand.KeyDown((int)Keys.LWin));
        client.ForgetSuppressedRelease(RemoteInputCommand.KeyUp((int)Keys.LWin));
        Assert.False(shortcuts.TryHandle(RemoteInputCommand.KeyDown((int)Keys.L),
            _ => Assert.Fail("Stale modifier"), () => Assert.Fail("False Win+L")));
    }

    [Fact]
    public async Task AnotherWorkersUpCannotInterleaveWithNativeDownAndBookkeeping()
    {
        var state = new SharedRemoteInputOwnership();
        var old = new object(); var current = new object();
        state.Apply(old, Shift(), _ => { });
        using var entered = new ManualResetEventSlim();
        using var complete = new ManualResetEventSlim();
        Task down = Task.Run(() => state.Apply(current, Shift(), _ =>
        {
            entered.Set();
            if (!complete.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
        }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        Task<bool> staleUp = Task.Run(() => state.ReleaseKey(old, Shift(), _ => Assert.Fail("Interleaved stale release")));
        complete.Set();
        await down.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(await staleUp.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(state.ReleaseKey(current, Shift(), _ => { }));
    }
}
