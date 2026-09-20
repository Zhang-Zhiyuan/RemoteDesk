using Xunit;

namespace RemoteDesk.Tests;

public sealed class RemoteKeyboardOwnershipRegressionTests
{
    public static IEnumerable<object[]> Modifiers()
    {
        foreach (var key in new[] { (0x10, 0xA0, 0x2A, false), (0x10, 0xA1, 0x36, false),
            (0x11, 0xA2, 0x1D, false), (0x11, 0xA3, 0x1D, true),
            (0x12, 0xA4, 0x38, false), (0x12, 0xA5, 0x38, true) })
        foreach (bool reverse in new[] { false, true })
            yield return [key.Item1, key.Item2, key.Item3, key.Item4, reverse];
    }

    [Theory]
    [MemberData(nameof(Modifiers))]
    public void HookAndRawModifierReleaseSharePhysicalIdentityInAllThreeTrackers(
        int generic, int sided, int scan, bool extended, bool reverse)
    {
        RemoteKeyboardFlags flags = Flags(extended);
        var pressed = RemoteInputCommand.KeyDown(reverse ? generic : sided, scan, flags);
        var released = RemoteInputCommand.KeyUp(reverse ? sided : generic, scan, flags);
        Assert.Equal(RemoteKeyboardInput.PhysicalKey(pressed), RemoteKeyboardInput.PhysicalKey(released));

        var viewer = new RemoteInputOwnershipTracker();
        Assert.True(Press(viewer, pressed));
        RemoteInputCommand? emitted = null;
        Assert.True(viewer.TryQueueKey(released, 7, _ => throw new InvalidOperationException(),
            (command, generation) => { Assert.Equal(7, generation); emitted = command; return true; }));
        Assert.Equal(pressed with { Kind = RemoteInputKind.KeyUp }, emitted);
        Assert.Equal(0, viewer.PressedKeyCount);

        var host = new RemoteHostServer.RemoteInputStateTracker();
        host.Observe(pressed); host.Observe(released);
        Assert.Equal(0, host.PressedKeyCount);
        Assert.Equal(0, host.ReleaseAll(_ => Assert.Fail("Already released"), _ => Assert.Fail()).AttemptedCount);

        var secure = new WindowsSecureDesktopKeyState();
        secure.Press(pressed); secure.ForgetReleased(released);
        Assert.Equal(0, secure.Count);
        secure.ReleaseAll(_ => Assert.Fail("Already released"));
    }

    [Theory]
    [InlineData(0x10, 0x2A, false, 0x36, false)]
    [InlineData(0x11, 0x1D, false, 0x1D, true)]
    [InlineData(0x12, 0x38, false, 0x38, true)]
    [InlineData(0x0D, 0x1C, false, 0x1C, true)]
    public void UnownedOtherSideKeyUpCannotReleaseHeldPhysicalKey(
        int vk, int leftScan, bool leftExtended, int rightScan, bool rightExtended)
    {
        var left = RemoteInputCommand.KeyDown(vk, leftScan, Flags(leftExtended));
        var right = RemoteInputCommand.KeyUp(vk, rightScan, Flags(rightExtended));
        var viewer = new RemoteInputOwnershipTracker();
        Press(viewer, left);
        Assert.False(viewer.TryQueueKey(right, 7, _ => throw new InvalidOperationException(), (_, _) =>
            throw new InvalidOperationException("Wrong side must not be emitted")));
        Assert.Equal(1, viewer.PressedKeyCount);
        var host = new RemoteHostServer.RemoteInputStateTracker();
        host.Observe(left); host.Observe(right);
        Assert.Equal(1, host.PressedKeyCount);
        var secure = new WindowsSecureDesktopKeyState();
        secure.Press(left); secure.ForgetReleased(right);
        Assert.Equal(1, secure.Count);
    }

    [Fact]
    public void RepeatedRightShiftAcrossSourcesRetainsOriginalReleaseMetadataAndOneOwner()
    {
        var original = RemoteInputCommand.KeyDown(0xA1, 0x36, Flags(true));
        var repeat = RemoteInputCommand.KeyDown(0x10, 0x36, Flags(false));
        var viewer = new RemoteInputOwnershipTracker();
        Press(viewer, original); Press(viewer, repeat);
        Assert.Equal(1, viewer.PressedKeyCount);
        var viewerReleases = new List<RemoteInputCommand>();
        Assert.Equal(1, viewer.ReleaseAll(7, (command, _) => { viewerReleases.Add(command); return true; }));
        Assert.Equal(original with { Kind = RemoteInputKind.KeyUp }, Assert.Single(viewerReleases));
        var host = new RemoteHostServer.RemoteInputStateTracker();
        host.Observe(original); host.Observe(repeat);
        var hostReleases = new List<RemoteInputCommand>();
        host.ReleaseAll(hostReleases.Add, _ => Assert.Fail());
        Assert.Equal(original, Assert.Single(hostReleases));
        var secure = new WindowsSecureDesktopKeyState();
        secure.Press(original); secure.Press(repeat);
        var secureReleases = new List<RemoteInputCommand>();
        secure.ReleaseAll(secureReleases.Add);
        Assert.Equal(original, Assert.Single(secureReleases));
    }

    [Fact]
    public void BothShiftKeysRemainIndependentThroughCrossSourceRelease()
    {
        var left = RemoteInputCommand.KeyDown(0xA0, 0x2A, Flags(false));
        var right = RemoteInputCommand.KeyDown(0xA1, 0x36, Flags(true));
        var rightUp = RemoteInputCommand.KeyUp(0x10, 0x36, Flags(false));
        var viewer = new RemoteInputOwnershipTracker();
        Press(viewer, left); Press(viewer, right);
        Assert.True(viewer.TryQueueKey(rightUp, 7, _ => throw new InvalidOperationException(),
            (command, _) => { Assert.Equal(right with { Kind = RemoteInputKind.KeyUp }, command); return true; }));
        Assert.Equal(1, viewer.PressedKeyCount);
        Assert.True(viewer.AnyPressedKey(7, key => key.VirtualKey == 0xA0));
        var host = new RemoteHostServer.RemoteInputStateTracker();
        host.Observe(left); host.Observe(right); host.Observe(rightUp);
        var hostReleases = new List<RemoteInputCommand>();
        host.ReleaseAll(hostReleases.Add, _ => Assert.Fail());
        Assert.Equal(left, Assert.Single(hostReleases));
        var secure = new WindowsSecureDesktopKeyState();
        secure.Press(left); secure.Press(right); secure.ForgetReleased(rightUp);
        var secureReleases = new List<RemoteInputCommand>();
        secure.ReleaseAll(secureReleases.Add);
        Assert.Equal(left, Assert.Single(secureReleases));
    }

    [Fact]
    public void CrossSourceReleaseFailureRetainsOwnershipUntilSuccessfulAdmission()
    {
        var viewer = new RemoteInputOwnershipTracker();
        var down = RemoteInputCommand.KeyDown(0xA1, 0x36, Flags(true));
        var up = RemoteInputCommand.KeyUp(0x10, 0x36, Flags(false));
        Press(viewer, down);
        Assert.False(viewer.TryQueueKey(up, 7, _ => throw new InvalidOperationException(), (_, _) => false));
        Assert.Equal(1, viewer.PressedKeyCount);
        Assert.True(viewer.TryQueueKey(up, 7, _ => throw new InvalidOperationException(), (_, _) => true));
        Assert.Equal(0, viewer.PressedKeyCount);
        Assert.False(viewer.TryQueueKey(up, 7, _ => throw new InvalidOperationException(), (_, _) => throw new InvalidOperationException()));
    }

    [Fact]
    public void LegacyGenericReleaseRemainsCompatibleButExplicitSideCannotReleaseOppositeSide()
    {
        var left = RemoteInputCommand.KeyDown(0xA0, 0x2A, Flags(false));
        var right = RemoteInputCommand.KeyDown(0xA1, 0x36, Flags(false));
        Assert.True(RemoteKeyboardInput.MatchesLegacyRelease(right, RemoteInputCommand.KeyUp(0x10)));
        Assert.False(RemoteKeyboardInput.MatchesLegacyRelease(left, RemoteInputCommand.KeyUp(0xA1)));
        Assert.False(RemoteKeyboardInput.MatchesLegacyRelease(RemoteInputCommand.KeyDown(0xA0), right));
        var secure = new WindowsSecureDesktopKeyState();
        secure.Press(left); secure.Press(right);
        var releases = new List<RemoteInputCommand>();
        secure.Release(RemoteInputCommand.KeyDown(0x10), releases.Add);
        Assert.Equal(2, releases.Count);
        Assert.Equal(0, secure.Count);
    }

    [Theory]
    [InlineData(0x41, 0x36)]
    [InlineData(0xA0, 0x36)]
    [InlineData(0xA1, 0x2A)]
    [InlineData(0xA3, 0x1D)]
    [InlineData(0xA5, 0x38)]
    public void UnrelatedOrUnknownKeyMetadataIsNotSilentlyNormalized(int vk, int scan)
    {
        Assert.Equal(Flags(true), RemoteKeyboardInput.NormalizeFlags(vk, scan, Flags(true)));
        Assert.Equal(RemoteKeyboardFlags.Extended, RemoteKeyboardInput.NormalizeFlags(vk, scan, RemoteKeyboardFlags.Extended));
    }

    private static bool Press(RemoteInputOwnershipTracker viewer, RemoteInputCommand command) =>
        viewer.TryQueueKey(command, 7, _ => new(true, 7), (_, _) => false);
    private static RemoteKeyboardFlags Flags(bool extended) => RemoteKeyboardFlags.HasScanCode |
        (extended ? RemoteKeyboardFlags.Extended : RemoteKeyboardFlags.None);
}
