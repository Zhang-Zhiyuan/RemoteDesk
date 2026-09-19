using Xunit;

namespace RemoteDesk.Tests;

public sealed class WindowsSecureDesktopUpgradeTests
{
    [Theory]
    [InlineData(false, "old", "new", "20260919000000", "20260920000000", false)]
    [InlineData(true, null, "new", "20260919000000", "20260920000000", false)]
    [InlineData(true, "same", "SAME", "20260919000000", "20260920000000", false)]
    [InlineData(true, "old", "new", "20260919000000", "20260919000000", false)]
    [InlineData(true, "old", "new", "20260920000000", "20260919000000", false)]
    [InlineData(true, "old", "new", null, "20260920000000", false)]
    [InlineData(true, "old", "new", "20260919000000", null, false)]
    [InlineData(true, "old", "new", "bad", "20260920000000", false)]
    [InlineData(true, "old", "new", "20260919000000", "20260920000000", true)]
    public void OnlyElevatedExistingDifferentAndStrictlyNewerBuildsAreEligible(bool elevated, string? oldPath,
        string newPath, string? oldBuild, string? newBuild, bool expected) =>
        Assert.Equal(expected, WindowsSecureDesktopInstallation.ShouldUpgradeExistingHelper(elevated, oldPath, newPath, oldBuild, newBuild));

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void ExistingHelperUpgradeKeepsStartModeAndOnlyReplacesBinaryThenAuthenticates(int startMode)
    {
        var fixture = new Fixture((uint)startMode);
        Assert.True(fixture.Run());
        Assert.Equal(fixture.New, fixture.Current);
        Assert.Equal(["validate", "stop", "change-new", "start-new", "ready-new"], fixture.Actions);
        Assert.Equal(fixture.Old.StartType, fixture.Current!.StartType);
        Assert.Equal(fixture.Old.ErrorControl, fixture.Current.ErrorControl);
        Assert.Empty(fixture.RollbackFailures);
    }

    [Fact]
    public void DisabledHelperAndChangedModeNeverStartOrReconfigure()
    {
        var disabled = new Fixture(4);
        Assert.False(disabled.Run());
        Assert.Empty(disabled.Actions);
        var mismatch = new Fixture();
        mismatch.New = mismatch.New with { StartType = 3 };
        Assert.False(mismatch.Run());
        Assert.Empty(mismatch.Actions);
    }

    [Fact]
    public void MissingOrChangedOwnershipBeforeStopNeverPerformsAMutation()
    {
        var fixture = new Fixture { Current = null };
        Assert.Throws<InvalidOperationException>(() => fixture.Run());
        Assert.Empty(fixture.Actions);
    }

    [Fact]
    public void UpgradeTrustOrAclFailureDoesNotStopTheOldService()
    {
        var fixture = new Fixture { FailAt = "validate" };
        Assert.Throws<InvalidOperationException>(() => fixture.Run());
        Assert.Equal(["validate"], fixture.Actions);
        Assert.Equal(fixture.Old, fixture.Current);
        Assert.True(fixture.Running);
    }

    [Theory]
    [InlineData("change-new")]
    [InlineData("start-new")]
    [InlineData("ready-new")]
    public void FailedUpgradeRestoresThePreviousRegistrationAndRunningHelper(string failAt)
    {
        var fixture = new Fixture { FailAt = failAt };
        var error = Assert.Throws<InvalidOperationException>(() => fixture.Run());
        Assert.Equal(failAt, error.Message);
        Assert.Equal(fixture.Old, fixture.Current);
        Assert.True(fixture.Running);
        Assert.Contains("start-old", fixture.Actions);
        Assert.Contains("ready-old", fixture.Actions);
        Assert.Empty(fixture.RollbackFailures);
    }

    [Fact]
    public void RollbackDoesNotStartAPreviouslyStoppedHelper()
    {
        var fixture = new Fixture { Running = false, FailAt = "ready-new" };
        Assert.Throws<InvalidOperationException>(() => fixture.Run());
        Assert.Equal(fixture.Old, fixture.Current);
        Assert.False(fixture.Running);
        Assert.DoesNotContain("start-old", fixture.Actions);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("change-new")]
    [InlineData("start-new")]
    public void ConcurrentDisableIsNeverOverwrittenOrEnabledByRollback(string mutateAt)
    {
        var fixture = new Fixture();
        fixture.OnAction = action => { if (action == mutateAt) fixture.Current = fixture.Current! with { StartType = 4 }; };
        Assert.Throws<InvalidOperationException>(() => fixture.Run());
        Assert.Equal(4u, fixture.Current!.StartType);
        Assert.DoesNotContain("change-old", fixture.Actions);
        Assert.DoesNotContain("start-old", fixture.Actions);
    }

    [Fact]
    public void ConcurrentDeletionAfterStopDoesNotRecreateService()
    {
        var fixture = new Fixture();
        fixture.OnAction = action => { if (action == "stop") fixture.Current = null; };
        Assert.Throws<InvalidOperationException>(() => fixture.Run());
        Assert.Null(fixture.Current);
        Assert.Equal(["validate", "stop"], fixture.Actions);
    }

    [Fact]
    public void RollbackFailureDoesNotHideOriginalUpgradeFailure()
    {
        var fixture = new Fixture { FailAt = "ready-new" };
        fixture.OnAction = action => { if (action == "change-old") throw new IOException("rollback-failed"); };
        Assert.Equal("ready-new", Assert.Throws<InvalidOperationException>(() => fixture.Run()).Message);
        Assert.IsType<IOException>(Assert.Single(fixture.RollbackFailures));
    }

    private sealed class Fixture
    {
        internal WindowsSecureDesktopInstallation.OwnedServiceConfiguration Old, New;
        internal WindowsSecureDesktopInstallation.OwnedServiceConfiguration? Current;
        internal bool Running = true;
        internal string? FailAt;
        internal Action<string>? OnAction;
        internal List<string> Actions = [];
        internal List<Exception> RollbackFailures = [];

        internal Fixture(uint start = 2)
        {
            Old = new("old.exe", "old-command", start, 1);
            New = Old with { Executable = "new.exe", Command = "new-command" };
            Current = Old;
        }

        private void Observe(string action)
        {
            Actions.Add(action);
            if (FailAt == action) throw new InvalidOperationException(action);
            OnAction?.Invoke(action);
        }

        internal bool Run() => WindowsSecureDesktopInstallation.UpgradeExistingHelperAfterUpdate(Old, New,
            () => Current, () => Observe("validate"), () => Running,
            () => { Running = false; Observe("stop"); },
            command =>
            {
                string action = command == New.Command ? "change-new" : "change-old";
                if (FailAt == action) { Observe(action); return; }
                Current = command == New.Command ? New : Old;
                Observe(action);
            },
            () => { Observe(Current == New ? "start-new" : "start-old"); Running = true; },
            guard => { guard(); Observe(Current == New ? "ready-new" : "ready-old"); guard(); },
            RollbackFailures.Add);
    }
}
