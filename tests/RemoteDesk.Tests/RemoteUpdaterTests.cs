using System.Diagnostics;
using System.Security.Cryptography;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class RemoteUpdaterTests
{
    [Theory]
    [InlineData("RemoteDesk.exe")]
    [InlineData(@"C:\tools\RemoteDesk.exe")]
    public void ValidateRemoteUpdatePackageNameAcceptsRemoteDeskExe(string fileName)
    {
        RemoteUpdater.ValidateRemoteUpdatePackageName(fileName);
    }

    [Theory]
    [InlineData("RemoteDesk.zip")]
    [InlineData("Other.exe")]
    [InlineData("")]
    public void ValidateRemoteUpdatePackageNameRejectsUnexpectedNames(string fileName)
    {
        Assert.Throws<InvalidDataException>(() => RemoteUpdater.ValidateRemoteUpdatePackageName(fileName));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1024)]
    public void ValidateRemoteUpdatePackageLengthAcceptsPositiveLengths(long fileLength)
    {
        RemoteUpdater.ValidateRemoteUpdatePackageLength(fileLength);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ValidateRemoteUpdatePackageLengthRejectsEmptyOrNegativeLengths(long fileLength)
    {
        Assert.Throws<InvalidDataException>(() => RemoteUpdater.ValidateRemoteUpdatePackageLength(fileLength));
    }

    [Fact]
    public void ValidateRemoteUpdatePackageLengthRejectsOversizedPackages()
    {
        Assert.Throws<InvalidDataException>(() =>
            RemoteUpdater.ValidateRemoteUpdatePackageLength(RemoteMessageCodec.MaxFileTransferBytes + 1));
    }

    [Theory]
    [InlineData("RemoteDesk.exe")]
    [InlineData("RemoteDesk (1).exe")]
    public void ValidateReceivedRemoteUpdatePackagePathAcceptsUniqueSavedExeNames(string fileName)
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), $"RemoteDesk.RemoteUpdaterTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string packagePath = Path.Combine(tempDirectory, fileName);
            File.WriteAllBytes(packagePath, [1, 2, 3]);

            RemoteUpdater.ValidateReceivedRemoteUpdatePackagePath(packagePath);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void ValidateReceivedRemoteUpdatePackagePathRejectsNonExeSavedFile()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), $"RemoteDesk.RemoteUpdaterTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string packagePath = Path.Combine(tempDirectory, "RemoteDesk.zip");
            File.WriteAllBytes(packagePath, [1, 2, 3]);

            Assert.Throws<InvalidOperationException>(() =>
                RemoteUpdater.ValidateReceivedRemoteUpdatePackagePath(packagePath));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void ScheduleApplyAndRestartRejectsPackageThatIsCurrentExecutable()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), $"RemoteDesk.RemoteUpdaterTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string executable = Path.Combine(tempDirectory, "RemoteDesk.exe");
            File.WriteAllBytes(executable, [1, 2, 3]);

            Assert.Throws<InvalidOperationException>(() => RemoteUpdater.ScheduleApplyAndRestart(
                executable,
                executable,
                processId: 12345,
                _ => { },
                _ => throw new InvalidOperationException("exit should not be called")));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void ScheduleApplyAndRestartRejectsInvalidHealthPort()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RemoteUpdater.ScheduleApplyAndRestart(
                "package.exe",
                "RemoteDesk.exe",
                processId: 12345,
                healthPort: 0,
                _ => { },
                _ => throw new InvalidOperationException("exit should not be called")));
    }

    [Fact]
    public void CreateUpdaterScriptVerifiesUpdatedProcessPathPortOwnerAndHandshake()
    {
        string script = RemoteUpdater.CreateUpdaterScript();

        Assert.Contains("[int]$HealthPort", script, StringComparison.Ordinal);
        Assert.Contains("Get-CimInstance", script, StringComparison.Ordinal);
        Assert.Contains("Test-RemoteDeskTargetProcess", script, StringComparison.Ordinal);
        Assert.Contains("Get-NetTCPConnection", script, StringComparison.Ordinal);
        Assert.Contains("OwningProcess", script, StringComparison.Ordinal);
        Assert.Contains("Test-RemoteDeskHandshake", script, StringComparison.Ordinal);
        Assert.Contains("\"RDK1\"", script, StringComparison.Ordinal);
        Assert.Contains("$updateHealthy = Wait-RemoteDeskHealthy", script, StringComparison.Ordinal);
        Assert.Contains("updated RemoteDesk is healthy", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Start-Sleep -Seconds 3", script, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateUpdaterScriptRevalidatesHashAndSelectedSignatureModeBeforeInstall()
    {
        string script = RemoteUpdater.CreateUpdaterScript();

        Assert.Contains("$ExpectedPackageSha256", script, StringComparison.Ordinal);
        Assert.Contains("$ExpectedSignerCertificateSha256", script, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash", script, StringComparison.Ordinal);
        Assert.Contains("Get-AuthenticodeSignature", script, StringComparison.Ordinal);
        Assert.Contains("Test-RemoteDeskTrustedPackage", script, StringComparison.Ordinal);
        Assert.Contains("$AllowUnsignedPersonalUpdate", script, StringComparison.Ordinal);
        Assert.Contains("'NotSigned'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Unblock-File", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateUpdaterScriptResumesHostForUpdatedAndRollbackProcesses()
    {
        string script = RemoteUpdater.CreateUpdaterScript();

        Assert.Contains("[switch]$ResumeHostAfterUpdate", script, StringComparison.Ordinal);
        Assert.Contains(
            RemoteUpdater.ResumeHostAfterUpdateArgument,
            script,
            StringComparison.Ordinal);
        Assert.Equal(
            2,
            CountOccurrences(
                script,
                "-ArgumentList $effectiveRestartArgument"));
    }

    [Fact]
    public void UnsignedRemoteDeskAssemblyHasPersonalUpdateIdentity()
    {
        string assemblyPath = typeof(RemoteUpdater).Assembly.Location;

        Assert.True(
            RemoteUpdateTrust.TryReadUnsignedBuildStamp(
                assemblyPath,
                out string buildStamp,
                out string error),
            error);
        Assert.NotNull(
            RemoteDeskBuildInfo.NormalizeBuildStamp(buildStamp));
        Assert.True(
            RemoteUpdateTrust.CanUseAsCurrentPackage(
                assemblyPath));
        System.Security.SecurityException sameVersion =
            Assert.Throws<System.Security.SecurityException>(() =>
                RemoteUpdateTrust.ValidateUpgrade(
                    assemblyPath,
                    assemblyPath));
        Assert.Contains(
            "同版本或降级",
            sameVersion.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BuildStampUpgradeRequiresStrictlyNewerValidBuild()
    {
        RemoteUpdateTrust.ValidateBuildStampUpgrade(
            "20260901010101",
            "20260901010102");

        Assert.Throws<System.Security.SecurityException>(() =>
            RemoteUpdateTrust.ValidateBuildStampUpgrade(
                "20260901010101",
                "20260901010101"));
        Assert.Throws<System.Security.SecurityException>(() =>
            RemoteUpdateTrust.ValidateBuildStampUpgrade(
                "20260901010101",
                "20260831235959"));
        Assert.Throws<System.Security.SecurityException>(() =>
            RemoteUpdateTrust.ValidateBuildStampUpgrade(
                "unknown",
                "20260901010102"));
    }

    [Fact]
    public void CreateUpdaterScriptRetainsBackupUntilHealthAndRollsBackUnhealthyUpdate()
    {
        string script = RemoteUpdater.CreateUpdaterScript();

        int installIndex = script.IndexOf(
            "-Source $PackagePath",
            StringComparison.Ordinal);
        int healthIndex = script.IndexOf(
            "$updateHealthy = Wait-RemoteDeskHealthy",
            StringComparison.Ordinal);
        int backupCleanupIndex = script.IndexOf(
            "-Description \"old executable backup\"",
            StringComparison.Ordinal);

        Assert.True(installIndex >= 0, "The update package must be installed.");
        Assert.True(
            healthIndex > installIndex,
            "Health verification must happen after installation.");
        Assert.True(
            backupCleanupIndex > healthIndex,
            "The old executable must be retained until the update is healthy.");
        Assert.Contains(
            "-Source $BackupPath",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "-Destination $failedUpdatePath",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$rollbackHealthy = Wait-RemoteDeskHealthy",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "recovery files retained when present",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void UpdaterUsesOnlyProtectedWindowsPowerShellModulesWithoutChangingParentEnvironment()
    {
        string? before = Environment.GetEnvironmentVariable("PSModulePath");
        ProcessStartInfo info = RemoteUpdater.CreateWindowsPowerShellStartInfo(AppContext.BaseDirectory);
        string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "Modules");
        Assert.Equal(expected, info.Environment["PSModulePath"]);
        Assert.Equal(before, Environment.GetEnvironmentVariable("PSModulePath"));
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
        Assert.True(Path.IsPathFullyQualified(info.FileName));
    }

    [Fact]
    public async Task WindowsPowerShellActuallyLoadsHashSignatureAndNetworkHealthCmdlets()
    {
        // Execute a read-only check through exactly the child-process environment
        // used by the updater. Calling powershell via pwsh's native invocation
        // would hide the bug because pwsh silently repairs PSModulePath itself.
        ProcessStartInfo info = RemoteUpdater.CreateWindowsPowerShellStartInfo(AppContext.BaseDirectory);
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        string fixture = typeof(RemoteUpdater).Assembly.Location;
        info.Environment["REMOTEDESK_UPDATER_FIXTURE_FILE"] = fixture;
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add("""
            $ErrorActionPreference = 'Stop'
            if ($PSVersionTable.PSVersion.Major -ne 5) { throw 'Expected Windows PowerShell 5.1' }
            Write-Output ('HASH=' + (Get-FileHash -LiteralPath $env:REMOTEDESK_UPDATER_FIXTURE_FILE -Algorithm SHA256).Hash)
            Write-Output ('SIGNATURE=' + (Get-AuthenticodeSignature -LiteralPath $env:REMOTEDESK_UPDATER_FIXTURE_FILE).Status)
            if ((Get-Command Get-NetTCPConnection -ErrorAction Stop).Name -ne 'Get-NetTCPConnection') { throw 'Missing health cmdlet' }
            Write-Output 'HEALTH_CMDLET_READY'
            """);
        using Process process = Process.Start(info)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await process.WaitForExitAsync(timeout.Token);
            string actual = await output;
            Assert.True(process.ExitCode == 0, await error);
            using var input = File.OpenRead(fixture);
            Assert.Contains("HASH=" + Convert.ToHexString(SHA256.HashData(input)), actual, StringComparison.Ordinal);
            Assert.Contains("SIGNATURE=NotSigned", actual, StringComparison.Ordinal);
            Assert.Contains("HEALTH_CMDLET_READY", actual, StringComparison.Ordinal);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
        }
    }

    [Fact]
    public void EarlyPackageFailureStillHasTheRollbackWorkingDirectory()
    {
        string script = RemoteUpdater.CreateUpdaterScript();
        int initialize = script.IndexOf("$targetDirectory = Split-Path -Parent $TargetPath", StringComparison.Ordinal);
        int waitForExit = script.IndexOf("waiting for process $ProcessId to exit", StringComparison.Ordinal);
        Assert.True(initialize >= 0 && initialize < waitForExit);
        Assert.Equal(1, CountOccurrences(script, "$targetDirectory = Split-Path -Parent $TargetPath"));
        Assert.Contains("package verification could not run:", script, StringComparison.Ordinal);
    }

    private static int CountOccurrences(
        string value,
        string search)
    {
        int count = 0;
        int index = 0;
        while ((index = value.IndexOf(
            search,
            index,
            StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }

        return count;
    }
}
