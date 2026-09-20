using Xunit;

namespace RemoteDesk.Tests;

public sealed class RemoteKeyboardSpecialKeyTests
{
    public static IEnumerable<object[]> KeypadAliases()
    {
        foreach (var key in new[] { (0x60, 0x2D, 0x52), (0x61, 0x23, 0x4F),
            (0x62, 0x28, 0x50), (0x63, 0x22, 0x51), (0x64, 0x25, 0x4B),
            (0x65, 0x0C, 0x4C), (0x66, 0x27, 0x4D), (0x67, 0x24, 0x47),
            (0x68, 0x26, 0x48), (0x69, 0x21, 0x49), (0x6E, 0x2E, 0x53) })
        foreach (bool reverse in new[] { false, true })
            yield return [key.Item1, key.Item2, key.Item3, reverse];
    }

    [Theory]
    [MemberData(nameof(KeypadAliases))]
    public void KeypadDigitNavigationAliasChangeReleasesSamePhysicalKey(int digit, int navigation, int scan, bool reverse)
    {
        var down = RemoteInputCommand.KeyDown(reverse ? navigation : digit, scan, RemoteKeyboardFlags.HasScanCode);
        var up = RemoteInputCommand.KeyUp(reverse ? digit : navigation, scan, RemoteKeyboardFlags.HasScanCode);
        Assert.Equal(RemoteKeyboardInput.PhysicalKey(down), RemoteKeyboardInput.PhysicalKey(up));
        AssertReleaseAcrossAllTrackers(down, up);
    }

    [Theory]
    [MemberData(nameof(KeypadAliases))]
    public void DedicatedExtendedNavigationCannotReleaseKeypadOwner(int digit, int navigation, int scan, bool reverse)
    {
        var down = RemoteInputCommand.KeyDown(reverse ? navigation : digit, scan, RemoteKeyboardFlags.HasScanCode);
        var unrelatedUp = RemoteInputCommand.KeyUp(navigation, scan,
            RemoteKeyboardFlags.HasScanCode | RemoteKeyboardFlags.Extended);
        var viewer = new RemoteInputOwnershipTracker();
        Press(viewer, down);
        Assert.False(viewer.TryQueueKey(unrelatedUp, 7, _ => throw new InvalidOperationException(), (_, _) =>
            throw new InvalidOperationException("Dedicated navigation is not the held keypad key")));
        Assert.Equal(1, viewer.PressedKeyCount);
        var host = new RemoteHostServer.RemoteInputStateTracker();
        host.Observe(down); host.Observe(unrelatedUp);
        Assert.Equal(1, host.PressedKeyCount);
        var secure = new WindowsSecureDesktopKeyState();
        secure.Press(down); secure.ForgetReleased(unrelatedUp);
        Assert.Equal(1, secure.Count);
    }

    [Theory]
    [InlineData(0x1D, false)]
    [InlineData(0x45, false)]
    [InlineData(0x1D, true)]
    [InlineData(0x45, true)]
    public void PauseWithIncompleteE1ScanUsesVirtualKeyRatherThanControlOrNumLock(int scan, bool extended)
    {
        var flags = RemoteKeyboardFlags.HasScanCode | (extended ? RemoteKeyboardFlags.Extended : 0);
        var down = RemoteInputCommand.KeyDown((int)Keys.Pause, scan, flags);
        var up = down with { Kind = RemoteInputKind.KeyUp };
        Assert.Equal(new NativeKeyboardInput((ushort)Keys.Pause, 0, 0), InputInjector.CreateNativeKeyboardInput(down));
        Assert.Equal(new NativeKeyboardInput((ushort)Keys.Pause, 0, 2), InputInjector.CreateNativeKeyboardInput(up));
    }

    [Fact]
    public void PauseScanVariantsAndLegacyReleaseHaveOneOwnershipIdentity()
    {
        var down = RemoteInputCommand.KeyDown((int)Keys.Pause, 0x1D, RemoteKeyboardFlags.HasScanCode);
        var up = RemoteInputCommand.KeyUp((int)Keys.Pause, 0x45, RemoteKeyboardFlags.HasScanCode);
        Assert.Equal(RemoteKeyboardInput.PhysicalKey(down), RemoteKeyboardInput.PhysicalKey(up));
        Assert.Equal(RemoteKeyboardInput.PhysicalKey(down), RemoteKeyboardInput.PhysicalKey(RemoteInputCommand.KeyUp((int)Keys.Pause)));
        AssertReleaseAcrossAllTrackers(down, up);
        var ctrl = RemoteInputCommand.KeyDown((int)Keys.LControlKey, 0x1D, RemoteKeyboardFlags.HasScanCode);
        var numLock = RemoteInputCommand.KeyDown((int)Keys.NumLock, 0x45, RemoteKeyboardFlags.HasScanCode);
        Assert.NotEqual(RemoteKeyboardInput.PhysicalKey(down), RemoteKeyboardInput.PhysicalKey(ctrl));
        Assert.NotEqual(RemoteKeyboardInput.PhysicalKey(down), RemoteKeyboardInput.PhysicalKey(numLock));
    }

    [Theory]
    [InlineData((int)Keys.Capital, 0x3A, false)]
    [InlineData((int)Keys.NumLock, 0x45, true)]
    [InlineData((int)Keys.PrintScreen, 0x37, true)]
    [InlineData((int)Keys.PrintScreen, 0x54, false)]
    [InlineData((int)Keys.Cancel, 0x46, true)]
    [InlineData((int)Keys.RMenu, 0x38, true)]
    public void OtherSpecialKeysKeepTheirOriginalNativeScanMetadata(int vk, int scan, bool extended)
    {
        var flags = RemoteKeyboardFlags.HasScanCode | (extended ? RemoteKeyboardFlags.Extended : 0);
        var down = RemoteInputCommand.KeyDown(vk, scan, flags);
        Assert.Equal(new NativeKeyboardInput(0, (ushort)scan, (uint)(8 | (extended ? 1 : 0))), InputInjector.CreateNativeKeyboardInput(down));
        Assert.Equal(new NativeKeyboardInput(0, (ushort)scan, (uint)(10 | (extended ? 1 : 0))),
            InputInjector.CreateNativeKeyboardInput(down with { Kind = RemoteInputKind.KeyUp }));
    }

    [Fact]
    public void AltGrCompanionControlAndRightAltKeepIndependentOwnershipThroughGenericUps()
    {
        var control = RemoteInputCommand.KeyDown((int)Keys.LControlKey, 0x1D, RemoteKeyboardFlags.HasScanCode);
        var alt = RemoteInputCommand.KeyDown((int)Keys.RMenu, 0x38,
            RemoteKeyboardFlags.HasScanCode | RemoteKeyboardFlags.Extended);
        var tracker = new RemoteInputOwnershipTracker();
        Press(tracker, control); Press(tracker, alt);
        var releases = new List<RemoteInputCommand>();
        Assert.True(tracker.TryQueueKey(RemoteInputCommand.KeyUp((int)Keys.ControlKey, 0x1D, RemoteKeyboardFlags.HasScanCode),
            7, _ => throw new InvalidOperationException(), (command, _) => { releases.Add(command); return true; }));
        Assert.Equal(control with { Kind = RemoteInputKind.KeyUp }, Assert.Single(releases));
        Assert.True(tracker.AnyPressedKey(7, key => key.VirtualKey == (int)Keys.RMenu));
        Assert.Equal(1, tracker.ReleaseAll(7, (command, _) => { releases.Add(command); return true; }));
        Assert.Equal(alt with { Kind = RemoteInputKind.KeyUp }, releases[1]);
    }

    [Fact]
    public void UnrelatedVkCannotUseKeypadAliasesToReleaseAnotherKey()
    {
        var keypad = RemoteInputCommand.KeyDown((int)Keys.NumPad1, 0x4F, RemoteKeyboardFlags.HasScanCode);
        var unrelated = RemoteInputCommand.KeyUp((int)Keys.A, 0x4F, RemoteKeyboardFlags.HasScanCode);
        Assert.NotEqual(RemoteKeyboardInput.PhysicalKey(keypad), RemoteKeyboardInput.PhysicalKey(unrelated));
    }

    [Fact]
    public void LegacyNavigationUpKeepsOriginalSameVirtualKeyFallbackAfterKeypadCanonicalization()
    {
        var down = RemoteInputCommand.KeyDown((int)Keys.End, 0x4F, RemoteKeyboardFlags.HasScanCode);
        var up = RemoteInputCommand.KeyUp((int)Keys.End);
        Assert.True(RemoteKeyboardInput.MatchesLegacyRelease(down, up));
        AssertReleaseAcrossAllTrackers(down, up);
        Assert.False(RemoteKeyboardInput.MatchesLegacyRelease(down,
            RemoteInputCommand.KeyUp((int)Keys.End, 0x4F,
                RemoteKeyboardFlags.HasScanCode | RemoteKeyboardFlags.Extended)));
    }

    private static void AssertReleaseAcrossAllTrackers(RemoteInputCommand down, RemoteInputCommand up)
    {
        var viewer = new RemoteInputOwnershipTracker();
        Press(viewer, down);
        RemoteInputCommand? actual = null;
        Assert.True(viewer.TryQueueKey(up, 7, _ => throw new InvalidOperationException(),
            (command, _) => { actual = command; return true; }));
        Assert.Equal(down with { Kind = RemoteInputKind.KeyUp }, actual);
        Assert.Equal(0, viewer.PressedKeyCount);
        var host = new RemoteHostServer.RemoteInputStateTracker();
        host.Observe(down); host.Observe(up);
        Assert.Equal(0, host.PressedKeyCount);
        var secure = new WindowsSecureDesktopKeyState();
        secure.Press(down); secure.ForgetReleased(up);
        Assert.Equal(0, secure.Count);
    }

    private static void Press(RemoteInputOwnershipTracker tracker, RemoteInputCommand command) =>
        Assert.True(tracker.TryQueueKey(command, 7, _ => new(true, 7), (_, _) => false));
}
