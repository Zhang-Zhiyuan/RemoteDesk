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

    [Fact]
    public void UpdaterAcknowledgesStartupBeforeWaitingForTheRunningApplication()
    {
        string script = RemoteUpdater.CreateUpdaterScript();
        int acknowledgement = script.IndexOf("Write-RemoteDeskUpdateLog \"updater ready: PID=$PID\"", StringComparison.Ordinal);
        int wait = script.IndexOf("Write-RemoteDeskUpdateLog \"waiting for process", StringComparison.Ordinal);
        Assert.True(acknowledgement >= 0 && acknowledgement < wait);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealUpdaterArgumentsBindInWindowsPowerShellWithoutExecutingTheInstaller(bool signed)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"RemoteDesk.UpdateBinding.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            // Run the exact production parameter declaration and launch arguments,
            // but no installer body: these checks cannot stop or replace any app.
            string production = RemoteUpdater.CreateUpdaterScript();
            string header = production[..production.IndexOf("$ErrorActionPreference", StringComparison.Ordinal)];
            string script = Path.Combine(directory, "binding $ ' fixture.ps1");
            File.WriteAllText(script, header + """
                if ($ExpectedPackageSha256 -ne ('a' * 64)) { throw 'Wrong package hash' }
                if ($ExpectedSignerCertificateSha256 -ne ('0' * 64)) { throw 'Wrong signer hash' }
                if ($RestartArgument.Count -ne 1 -or $RestartArgument[0] -cne '--tray') { throw 'Wrong restart argument' }
                if (-not $ResumeHostAfterUpdate -or $HealthPort -ne 56565) { throw 'Wrong health/resume parameters' }
                Write-Output ('BINDING_OK;unsigned=' + $AllowUnsignedPersonalUpdate.IsPresent)
                """);
            ProcessStartInfo info = RemoteUpdater.CreateUpdaterProcessStartInfo(
                script, Environment.ProcessId, Path.Combine(directory, "RemoteDesk (1).exe"),
                Path.Combine(directory, "RemoteDesk.exe"), Path.Combine(directory, "RemoteDesk.old"),
                Path.Combine(directory, "update.log"), directory, 56565,
                new string('a', 64), new string('0', 64), signed);
            Assert.Contains("-NonInteractive", info.ArgumentList);
            Assert.Contains("-PackageSha256", info.ArgumentList);
            Assert.Contains("-SignerSha256", info.ArgumentList);
            Assert.DoesNotContain("-ExpectedPackageSha256", info.ArgumentList);
            Assert.DoesNotContain("-ExpectedSignerCertificateSha256", info.ArgumentList);
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            using Process child = Process.Start(info)!;
            try
            {
                Task<string> output = child.StandardOutput.ReadToEndAsync();
                Task<string> error = child.StandardError.ReadToEndAsync();
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Assert.True(child.ExitCode == 0, await error);
                Assert.Contains("BINDING_OK;unsigned=" + (!signed), await output, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); }
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("ready")]
    [InlineData("absent")]
    [InlineData("wrong-pid")]
    [InlineData("exited")]
    public async Task UpdaterStartupRequiresALiveChildAndItsExactAcknowledgement(string state)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"RemoteDesk.UpdateReady.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string log = Path.Combine(directory, "owned-startup.log");
            // A console host can retain its working-directory handle briefly
            // after the owned PowerShell child exits; don't use the fixture as cwd.
            ProcessStartInfo info = RemoteUpdater.CreateWindowsPowerShellStartInfo(AppContext.BaseDirectory);
            info.Environment["REMOTEDESK_OWNED_STARTUP_LOG"] = log;
            info.Environment["REMOTEDESK_OWNED_STARTUP_STATE"] = state;
            info.ArgumentList.Add("-NoProfile");
            info.ArgumentList.Add("-NonInteractive");
            info.ArgumentList.Add("-Command");
            info.ArgumentList.Add("""
                if ($env:REMOTEDESK_OWNED_STARTUP_STATE -eq 'exited') { exit 42 }
                if ($env:REMOTEDESK_OWNED_STARTUP_STATE -eq 'ready') {
                    [IO.File]::WriteAllText($env:REMOTEDESK_OWNED_STARTUP_LOG, "[test] updater ready: PID=$PID")
                }
                if ($env:REMOTEDESK_OWNED_STARTUP_STATE -eq 'wrong-pid') {
                    [IO.File]::WriteAllText($env:REMOTEDESK_OWNED_STARTUP_LOG, "[test] updater ready: PID=${PID}0")
                }
                Start-Sleep -Seconds 8
                """);
            using Process child = Process.Start(info)!;
            try
            {
                if (state == "ready") RemoteUpdater.WaitForUpdaterReady(child, log, TimeSpan.FromSeconds(5));
                else if (state == "exited")
                {
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
                        RemoteUpdater.WaitForUpdaterReady(child, log, TimeSpan.FromSeconds(1)));
                    Assert.Contains("42", error.Message, StringComparison.Ordinal);
                }
                else Assert.Throws<TimeoutException>(() =>
                    RemoteUpdater.WaitForUpdaterReady(child, log, TimeSpan.FromSeconds(1)));
            }
            finally
            {
                if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); }
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
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
