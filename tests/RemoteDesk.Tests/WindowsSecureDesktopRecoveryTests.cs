using System.ComponentModel;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class WindowsSecureDesktopRecoveryTests
{
    private const string OwnerSid = "S-1-5-21-1-2-3-1001";

    public static TheoryData<string[], string, bool> InvalidRecoveryCommands => new()
    {
        { [], OwnerSid, true },
        { [WindowsSecureDesktopInstallation.RecoverAfterUpdateArgument], OwnerSid, true },
        { [WindowsSecureDesktopInstallation.RecoverAfterUpdateArgument, OwnerSid, "--quiet"], OwnerSid, true },
        { [WindowsSecureDesktopInstallation.RecoverAfterUpdateArgument, OwnerSid, "--install-secure-desktop"], OwnerSid, true },
        { [WindowsSecureDesktopInstallation.InstallArgument, OwnerSid], OwnerSid, true },
        { [WindowsSecureDesktopInstallation.RecoverAfterUpdateArgument, OwnerSid], OwnerSid, false },
        { [WindowsSecureDesktopInstallation.RecoverAfterUpdateArgument, "S-1-5-21-1-2-3-1002"], OwnerSid, true },
        { [WindowsSecureDesktopInstallation.RecoverAfterUpdateArgument, "S-1-5-18"], "S-1-5-18", true },
        { [WindowsSecureDesktopInstallation.RecoverAfterUpdateArgument, "not-a-sid"], "not-a-sid", true },
        { [WindowsSecureDesktopInstallation.RecoverAfterUpdateArgument, ""], "", true }
    };

    [Theory]
    [MemberData(nameof(InvalidRecoveryCommands))]
    public void RecoveryChildRejectsInvalidIdentityPermissionOrExtraArguments(string[] args, string currentSid, bool elevated)
    {
        int calls = 0;
        int exit = WindowsSecureDesktopInstallation.ExecuteRecoveryCommand(args, currentSid, elevated,
            () => { calls++; return true; });
        Assert.Equal(1, exit);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 2)]
    public void RecoveryChildRunsOnlyRequestedRecoveryAndReturnsFixedResult(bool recovered, int expectedExit)
    {
        int calls = 0;
        int exit = WindowsSecureDesktopInstallation.ExecuteRecoveryCommand(
            [WindowsSecureDesktopInstallation.RecoverAfterUpdateArgument, OwnerSid], OwnerSid, true,
            () => { calls++; return recovered; });
        Assert.Equal(expectedExit, exit);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void RecoveryChildConvertsFailureToFixedExitCode()
    {
        int exit = WindowsSecureDesktopInstallation.ExecuteRecoveryCommand(
            [WindowsSecureDesktopInstallation.RecoverAfterUpdateArgument, OwnerSid], OwnerSid, true,
            () => throw new IOException("private path or identity"));
        Assert.Equal(1, exit);
    }

    [Fact]
    public void RecoveryChildLaunchIsHiddenWithoutShellElevationOrFreeFormArguments()
    {
        const string executable = @"C:\Program Files\RemoteDesk\Managed\RemoteDesk.exe";
        var start = WindowsSecureDesktopInstallation.CreateRecoveryStartInfo(executable, OwnerSid);
        Assert.Equal(executable, start.FileName);
        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
        Assert.Equal(System.Diagnostics.ProcessWindowStyle.Hidden, start.WindowStyle);
        Assert.Equal(string.Empty, start.Verb);
        Assert.Equal(string.Empty, start.Arguments);
        Assert.Equal([WindowsSecureDesktopInstallation.RecoverAfterUpdateArgument, OwnerSid], start.ArgumentList);
    }

    [Fact]
    public void RecoveryChildLaunchRejectsRelativeExecutablePath()
    {
        Assert.Throws<ArgumentException>(() => WindowsSecureDesktopInstallation.CreateRecoveryStartInfo("RemoteDesk.exe", OwnerSid));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(2, true)]
    public void RecoveryChildResultHasNoAmbiguousSuccessCode(int exit, bool recovered) =>
        Assert.Equal(recovered, WindowsSecureDesktopInstallation.InterpretRecoveryExitCode(exit));

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(5)]
    public void RecoveryChildUnexpectedFailureCodeDoesNotExposeUntrustedData(int exit)
    {
        var error = Assert.Throws<InvalidOperationException>(() => WindowsSecureDesktopInstallation.InterpretRecoveryExitCode(exit));
        Assert.Equal("更新后的锁屏辅助恢复未完成；被控端仍保持运行，请检查锁屏控制服务。", error.Message);
    }

    [Fact]
    public void UnelevatedProcessDoesNotEvenReadInstallation()
    {
        var fixture = new Fixture { Elevated = false };
        Assert.False(fixture.Recover());
        Assert.Equal(0, fixture.Reads);
        fixture.AssertUnchanged();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("not-owned")]
    public void NoEnabledOwnedInstallationIsNotInstalledOrEnabled(string reason)
    {
        // InstalledExecutable(requireEnabled: true) returns null in all three cases.
        Assert.NotEmpty(reason);
        var fixture = new Fixture { Installed = null };
        Assert.False(fixture.Recover());
        Assert.Equal(0, fixture.Validations);
        fixture.AssertUnchanged();
    }

    [Theory]
    [InlineData(@"C:\Other\RemoteDesk.exe")]
    [InlineData(@"C:\Protected\RemoteDesk.exe.old")]
    [InlineData(@"C:\Protected\Sub\..\RemoteDesk.exe")]
    public void DifferentRegisteredExecutableIsNeverRestarted(string installed)
    {
        var fixture = new Fixture { Installed = installed };
        Assert.False(fixture.Recover());
        fixture.AssertUnchanged();
    }

    [Fact]
    public void HealthyAuthenticatedHelperIsNotRestarted()
    {
        var fixture = new Fixture { QueryFailure = null };
        Assert.False(fixture.Recover());
        Assert.Equal(1, fixture.Queries);
        Assert.Equal(0, fixture.Restarts);
    }

    [Theory]
    [InlineData((int)SecureDesktopServerIdentityMismatch.SystemAccount)]
    [InlineData((int)SecureDesktopServerIdentityMismatch.Session)]
    public void OtherIdentityMismatchNeverTriggersRestart(int mismatch)
    {
        var fixture = new Fixture { QueryFailure = WrappedMismatch((SecureDesktopServerIdentityMismatch)mismatch) };
        Assert.Same(fixture.QueryFailure, Assert.Throws<InvalidOperationException>(() => fixture.Recover()));
        Assert.Equal(0, fixture.Restarts);
    }

    [Theory]
    [InlineData((int)SecureDesktopIdentityQuery.OpenProcess)]
    [InlineData((int)SecureDesktopIdentityQuery.OpenProcessToken)]
    [InlineData((int)SecureDesktopIdentityQuery.QueryFullProcessImageName)]
    [InlineData((int)SecureDesktopIdentityQuery.ProcessIdToSessionId)]
    [InlineData((int)SecureDesktopIdentityQuery.GetTokenElevation)]
    [InlineData((int)SecureDesktopIdentityQuery.GetNamedPipeServerProcessId)]
    public void NativeIdentityQueryFailureIsReportedWithoutRestart(int operation)
    {
        var fixture = new Fixture { QueryFailure = new InvalidOperationException("fixed diagnostic",
            new SecureDesktopIdentityQueryException((SecureDesktopIdentityQuery)operation, 5)) };
        Assert.Same(fixture.QueryFailure, Assert.Throws<InvalidOperationException>(() => fixture.Recover()));
        Assert.Equal(0, fixture.Restarts);
    }

    [Fact]
    public void GenericErrorTextIsNotUsedAsRecoveryAuthority()
    {
        var fixture = new Fixture { QueryFailure = new InvalidOperationException("Executable mismatch") };
        Assert.Same(fixture.QueryFailure, Assert.Throws<InvalidOperationException>(() => fixture.Recover()));
        Assert.Equal(0, fixture.Restarts);
    }

    [Fact]
    public void NestedMismatchOutsideDirectQueryFailureIsNotRecovered()
    {
        var fixture = new Fixture { QueryFailure = new InvalidOperationException("outer", WrappedMismatch()) };
        Assert.Same(fixture.QueryFailure, Assert.Throws<InvalidOperationException>(() => fixture.Recover()));
        Assert.Equal(0, fixture.Restarts);
    }

    [Fact]
    public void ProtectedFileFailurePropagatesBeforeQueryOrRestart()
    {
        var expected = new UnauthorizedAccessException("protected file rejected");
        var fixture = new Fixture { OnValidate = () => throw expected };
        Assert.Same(expected, Assert.Throws<UnauthorizedAccessException>(() => fixture.Recover()));
        fixture.AssertUnchanged();
    }

    [Fact]
    public void RegistrationChangeDuringStatusQueryPreventsAnyRestart()
    {
        var fixture = new Fixture();
        fixture.OnQuery = () => fixture.Installed = @"C:\Other\RemoteDesk.exe";
        Assert.Throws<InvalidOperationException>(() => fixture.Recover());
        Assert.Equal(0, fixture.Restarts);
    }

    [Fact]
    public void DisableDuringProtectedFileRevalidationPreventsAnyRestart()
    {
        var fixture = new Fixture();
        fixture.OnValidate = () => { if (fixture.Validations == 2) fixture.Installed = null; };
        Assert.Throws<InvalidOperationException>(() => fixture.Recover());
        Assert.Equal(0, fixture.Restarts);
    }

    [Fact]
    public void DisableAfterStopIsNotUndoneByStart()
    {
        var fixture = new Fixture();
        fixture.AfterStop = () => fixture.Installed = null;
        Assert.Throws<InvalidOperationException>(() => fixture.Recover());
        Assert.Equal(1, fixture.Stops);
        Assert.Equal(0, fixture.Starts);
        Assert.Equal(0, fixture.ReadyChecks);
    }

    [Fact]
    public void OwnershipChangeDuringRestartPreventsReadinessOrSuccess()
    {
        var fixture = new Fixture();
        fixture.AfterStart = () => fixture.Installed = @"C:\Other\RemoteDesk.exe";
        Assert.Throws<InvalidOperationException>(() => fixture.Recover());
        Assert.Equal(1, fixture.Restarts);
        Assert.Equal(0, fixture.ReadyChecks);
    }

    [Fact]
    public void OwnershipChangeDuringReadinessIsNotReportedAsSuccess()
    {
        var fixture = new Fixture();
        fixture.OnReady = () => fixture.Installed = null;
        Assert.Throws<InvalidOperationException>(() => fixture.Recover());
        Assert.Equal(1, fixture.Restarts);
        Assert.Equal(1, fixture.ReadyChecks);
    }

    [Fact]
    public void SuccessfulSharedExecutableRecoveryRestartsExactlyOnceAndAuthenticates()
    {
        var fixture = new Fixture();
        Assert.True(fixture.Recover());
        Assert.Equal(1, fixture.Queries);
        Assert.Equal(1, fixture.Restarts);
        Assert.Equal(1, fixture.Stops);
        Assert.Equal(1, fixture.Starts);
        Assert.Equal(1, fixture.ReadyChecks);
        Assert.True(fixture.Validations >= 7);
    }

    [Fact]
    public void ExactPathComparisonRetainsWindowsCaseInsensitivity()
    {
        var fixture = new Fixture { Installed = @"c:\protected\REMOTEDESK.EXE" };
        Assert.True(fixture.Recover());
        Assert.Equal(1, fixture.Restarts);
    }

    [Fact]
    public void FailedAuthenticationAfterRestartDoesNotRetryTheRestart()
    {
        var expected = new InvalidOperationException("not ready");
        var fixture = new Fixture { OnReady = () => throw expected };
        Assert.Same(expected, Assert.Throws<InvalidOperationException>(() => fixture.Recover()));
        Assert.Equal(1, fixture.Restarts);
        Assert.Equal(1, fixture.ReadyChecks);
    }

    [Fact]
    public void NativeRestartFailurePropagatesWithoutRetryOrReadiness()
    {
        var expected = new Win32Exception(5);
        var fixture = new Fixture { AfterStop = () => throw expected };
        Assert.Same(expected, Assert.Throws<Win32Exception>(() => fixture.Recover()));
        Assert.Equal(1, fixture.Restarts);
        Assert.Equal(0, fixture.ReadyChecks);
    }

    private static InvalidOperationException WrappedMismatch(
        SecureDesktopServerIdentityMismatch mismatch = SecureDesktopServerIdentityMismatch.Executable) =>
        new("fixed diagnostic", new SecureDesktopServerIdentityException(mismatch));

    private sealed class Fixture
    {
        internal bool Elevated = true;
        internal string? Installed = @"C:\Protected\RemoteDesk.exe";
        internal Exception? QueryFailure = WrappedMismatch();
        internal Action? OnValidate, OnQuery, AfterStop, AfterStart, OnReady;
        internal int Reads, Validations, Queries, Restarts, Stops, Starts, ReadyChecks;

        internal bool Recover() => WindowsSecureDesktopInstallation.RecoverSharedExecutableAfterUpdate(
            Elevated, @"C:\Protected\RemoteDesk.exe",
            () => { Reads++; return Installed; },
            path => { Assert.Equal(@"C:\Protected\RemoteDesk.exe", path, ignoreCase: true); Validations++; OnValidate?.Invoke(); },
            () => { Queries++; OnQuery?.Invoke(); if (QueryFailure is not null) throw QueryFailure; },
            guard =>
            {
                Restarts++;
                guard();
                Stops++;
                AfterStop?.Invoke();
                guard();
                Starts++;
                AfterStart?.Invoke();
                guard();
            },
            guard => { ReadyChecks++; guard(); OnReady?.Invoke(); guard(); });

        internal void AssertUnchanged()
        {
            Assert.Equal(0, Queries);
            Assert.Equal(0, Restarts);
            Assert.Equal(0, ReadyChecks);
        }
    }
}
