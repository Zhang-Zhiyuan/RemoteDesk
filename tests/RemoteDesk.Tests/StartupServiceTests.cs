using Xunit;

namespace RemoteDesk.Tests;

public sealed class StartupServiceTests
{
    private const string CurrentExecutable = @"C:\Program Files\RemoteDesk\RemoteDesk.exe";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyRegistrationIsDisabled(string? command)
    {
        StartupRegistrationStatus status = StartupService.EvaluateRegistration(
            command,
            CurrentExecutable);

        Assert.False(status.IsRegistered);
        Assert.False(status.TargetsCurrentExecutable);
        Assert.Null(status.TargetExecutablePath);
    }

    [Fact]
    public void QuotedCurrentExecutableIsRecognizedExactly()
    {
        StartupRegistrationStatus status = StartupService.EvaluateRegistration(
            "\"c:\\PROGRAM FILES\\RemoteDesk\\RemoteDesk.exe\" --tray",
            CurrentExecutable);

        Assert.True(status.IsRegistered);
        Assert.True(status.TargetsCurrentExecutable);
        Assert.Equal(
            @"c:\PROGRAM FILES\RemoteDesk\RemoteDesk.exe",
            status.TargetExecutablePath);
    }

    [Fact]
    public void ExecutablePathPrefixDoesNotCreateFalsePositive()
    {
        StartupRegistrationStatus status = StartupService.EvaluateRegistration(
            "\"C:\\Program Files\\RemoteDesk\\RemoteDesk.exe.old\" --tray",
            CurrentExecutable);

        Assert.True(status.IsRegistered);
        Assert.False(status.TargetsCurrentExecutable);
    }

    [Fact]
    public void RegistrationForAnotherCopyStillReportsEnabled()
    {
        StartupRegistrationStatus status = StartupService.EvaluateRegistration(
            "\"D:\\Portable\\RemoteDesk.exe\" --tray",
            CurrentExecutable);

        Assert.True(status.IsRegistered);
        Assert.False(status.TargetsCurrentExecutable);
        Assert.Equal(@"D:\Portable\RemoteDesk.exe", status.TargetExecutablePath);
    }

    [Fact]
    public void MalformedQuotedRegistrationCanStillBeRemovedByTheUi()
    {
        StartupRegistrationStatus status = StartupService.EvaluateRegistration(
            "\"C:\\Broken Path\\RemoteDesk.exe --tray",
            CurrentExecutable);

        Assert.True(status.IsRegistered);
        Assert.False(status.TargetsCurrentExecutable);
        Assert.Null(status.TargetExecutablePath);
    }

    [Fact]
    public void RegistrationCommandQuotesExecutableAndStartsInTray()
    {
        Assert.Equal(
            "\"C:\\Program Files\\RemoteDesk\\RemoteDesk.exe\" --tray",
            StartupService.BuildRegistrationCommand(CurrentExecutable));
    }

    [Fact]
    public void DisabledRegistrationIsNotOrphaned()
    {
        Assert.False(StartupService.IsOrphanedRegistration(
            StartupRegistrationStatus.Disabled,
            _ => false));
    }

    [Fact]
    public void CurrentExecutableRegistrationIsNotOrphaned()
    {
        StartupRegistrationStatus status = StartupService.EvaluateRegistration(
            $"\"{CurrentExecutable}\" --tray",
            CurrentExecutable);

        Assert.False(StartupService.IsOrphanedRegistration(status, _ => false));
    }

    [Fact]
    public void ExistingOtherCopyRegistrationIsPreserved()
    {
        StartupRegistrationStatus status = StartupService.EvaluateRegistration(
            "\"D:\\Portable\\RemoteDesk.exe\" --tray",
            CurrentExecutable);

        Assert.False(StartupService.IsOrphanedRegistration(status, _ => true));
    }

    [Fact]
    public void MissingOtherCopyRegistrationIsOrphaned()
    {
        StartupRegistrationStatus status = StartupService.EvaluateRegistration(
            "\"D:\\Removed\\RemoteDesk.exe\" --tray",
            CurrentExecutable);

        Assert.True(StartupService.IsOrphanedRegistration(status, _ => false));
    }

    [Fact]
    public void MalformedRegistrationIsOrphaned()
    {
        StartupRegistrationStatus status = StartupService.EvaluateRegistration(
            "\"C:\\Broken Path\\RemoteDesk.exe --tray",
            CurrentExecutable);

        Assert.True(StartupService.IsOrphanedRegistration(status, _ => true));
    }

    [Theory]
    [InlineData(true, false, "--install-persistent-startup")]
    [InlineData(true, true, "--enable-persistent-startup")]
    [InlineData(false, true, "--disable-persistent-startup")]
    [InlineData(false, false, null)]
    public async Task StartupChangesUseApprovedAdministratorTaskInsteadOfCreatingRunEntry(
        bool enabled, bool installed, string? expected)
    {
        var operations = new List<string>();
        int removed = 0;
        await StartupService.ApplyChangeAsync(enabled, installed,
            operation => { operations.Add(operation); return Task.CompletedTask; }, () => removed++);
        Assert.Equal(expected is null ? Array.Empty<string>() : [expected], operations);
        Assert.Equal(!enabled && !installed ? 1 : 0, removed);
    }

    [Fact]
    public async Task FailedMigrationLeavesLegacyRegistrationIntact()
    {
        bool removed = false;
        await Assert.ThrowsAsync<IOException>(() => StartupService.ApplyChangeAsync(true, false,
            _ => Task.FromException(new IOException("fixture install failure")), () => removed = true));
        Assert.False(removed);
    }

    [Fact]
    public void MigrationRequiresExistingOptInAndPreservesOtherValidCopies()
    {
        Assert.False(StartupService.ShouldMigrateLegacyRegistration(StartupRegistrationStatus.Disabled));
        var current = StartupService.EvaluateRegistration(StartupService.BuildRegistrationCommand(CurrentExecutable), CurrentExecutable);
        Assert.True(StartupService.ShouldMigrateLegacyRegistration(current));
        Assert.False(StartupService.ShouldMigrateLegacyRegistration(current with { IsPersistent = true }));
        var other = StartupService.EvaluateRegistration("\"D:\\Portable\\RemoteDesk.exe\" --tray", CurrentExecutable);
        Assert.False(StartupService.ShouldMigrateLegacyRegistration(other, _ => true));
        Assert.True(StartupService.ShouldMigrateLegacyRegistration(other, _ => false));
    }
}
