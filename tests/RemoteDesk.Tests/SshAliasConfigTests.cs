using System.Diagnostics;
using System.Text;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class SshAliasConfigTests
{
    [Theory]
    [InlineData("5090", true)]
    [InlineData("Lab-PC_2", true)]
    [InlineData("*", false)]
    [InlineData("-oProxyCommand", false)]
    [InlineData("lab pc", false)]
    [InlineData("电脑", false)]
    [InlineData("a\nHost *", false)]
    [InlineData("a%h", false)]
    public void AliasUsesConservativeLiteralGrammar(string value, bool expected) => Assert.Equal(expected, SshAliasConfig.IsValidAlias(value));

    [Fact]
    public void InstallPreservesOriginalBytesAndBackupThenRemoveRestoresThem()
    {
        using var fixture = new Fixture();
        byte[] original = [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes("# 用户原配置\r\nHost other\r\n  User original\r\nHost *\r\n  Compression yes")];
        File.WriteAllBytes(fixture.Config, original);
        var result = fixture.Install();
        Assert.NotNull(result.BackupPath);
        Assert.Equal(original, File.ReadAllBytes(result.BackupPath));
        byte[] installed = File.ReadAllBytes(fixture.Config);
        Assert.True(installed.AsSpan().StartsWith(original.AsSpan(0, 3)));
        Assert.True(installed.AsSpan().EndsWith(original.AsSpan(3)));
        Assert.Null(fixture.Install().BackupPath); // No duplicate include or avoidable backup.
        fixture.Remove();
        Assert.Equal(original, File.ReadAllBytes(fixture.Config));
    }

    [Theory]
    [InlineData("Host 5090\n User other\n")]
    [InlineData("hOsT=50*\n User other\n")]
    [InlineData("Host unrelated 50?0\n")]
    [InlineData("Host * !elsewhere\n")]
    [InlineData("Host 50\\90\n")]
    [InlineData("Include other\n")]
    [InlineData("Host unrelated\n Match exec \"do-not-execute\"\n")]
    public void AmbiguousOrOverlappingUserConfigIsUnchanged(string body)
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Config, body, new UTF8Encoding(false));
        byte[] before = File.ReadAllBytes(fixture.Config);
        Assert.Throws<InvalidOperationException>(() => fixture.Install());
        Assert.Equal(before, File.ReadAllBytes(fixture.Config));
        Assert.False(File.Exists(fixture.Managed));
        Assert.Empty(Directory.GetFiles(fixture.Directory, "*backup*"));
    }

    [Theory]
    [InlineData("Host *\n User fallback\n")]
    [InlineData("Host 50* !5090\n User other\n")]
    [InlineData("# Include is only a comment\nHost another\n")]
    public void NonOverlappingAndPlainGlobalHostAreAllowed(string body)
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Config, body, new UTF8Encoding(false));
        fixture.Install();
        Assert.EndsWith(body, File.ReadAllText(fixture.Config));
    }

    [Fact]
    public void ManagedAliasesAreIndependentAndCannotSilentlyTakeEachOthersNames()
    {
        using var fixture = new Fixture();
        fixture.Install();
        var other = fixture.Profile with { Id = Guid.NewGuid(), Alias = "3080m" };
        fixture.Install(other);
        Assert.Throws<InvalidOperationException>(() => fixture.Install(other with { Alias = "5090" }));
        fixture.Remove();
        Assert.DoesNotContain("Host 5090\n", File.ReadAllText(fixture.Managed));
        Assert.Contains("Host 3080m\n", File.ReadAllText(fixture.Managed));
        Assert.Contains("Include ", File.ReadAllText(fixture.Config));
        fixture.Remove(other);
        Assert.Equal("", File.ReadAllText(fixture.Config));
    }

    [Fact]
    public void ModifiedOwnedIncludeIsNotSilentlyOverwritten()
    {
        using var fixture = new Fixture(); fixture.Install();
        string modified = File.ReadAllText(fixture.Config).Replace("Host *", "Host *\n  User user-added");
        File.WriteAllText(fixture.Config, modified);
        Assert.Throws<IOException>(() => fixture.Install());
        Assert.Equal(modified, File.ReadAllText(fixture.Config));
    }

    [Fact]
    public void FailedUserWriteRollsBackManagedEntries()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Config, "# untouched user configuration\n");
        byte[] user = File.ReadAllBytes(fixture.Config);
        // Deny replacement/deletion, but permit the initial consistency read.
        using (var locked = new FileStream(fixture.Config, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<IOException>(() => fixture.Install());
        Assert.Equal(user, File.ReadAllBytes(fixture.Config));
        Assert.False(File.Exists(fixture.Managed));
    }

    [Fact]
    public void OfflineRemovalNeedsNoHelperAndLeavesOtherAliasesPinsAndIdentityUntouched()
    {
        using var fixture = new Fixture(); fixture.Install();
        var other = fixture.Profile with { Id = Guid.NewGuid(), Alias = "other-owned-alias" }; fixture.Install(other);
        byte[] pins = Encoding.UTF8.GetBytes("owned fixture host pins, no real credential");
        File.WriteAllBytes(fixture.Hosts, pins);
        SshAliasConfig.Remove(fixture.Profile, Path.Combine(fixture.Directory, "helper-that-is-not-installed.exe"), fixture.Config, fixture.Managed, fixture.Hosts);
        Assert.DoesNotContain("Host 5090\n", File.ReadAllText(fixture.Managed));
        Assert.Contains("Host other-owned-alias\n", File.ReadAllText(fixture.Managed));
        Assert.Equal(pins, File.ReadAllBytes(fixture.Hosts));
        Assert.Contains("owned nonexecutable test fixture", File.ReadAllText(fixture.Helper));
    }

    [Fact]
    public void RemovingLastAliasRollsBackManagedFileWhenUserConfigCannotBeReplaced()
    {
        using var fixture = new Fixture(); fixture.Install();
        byte[] user = File.ReadAllBytes(fixture.Config), managed = File.ReadAllBytes(fixture.Managed);
        using (var locked = new FileStream(fixture.Config, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<IOException>(() => fixture.Remove());
        Assert.Equal(user, File.ReadAllBytes(fixture.Config));
        Assert.Equal(managed, File.ReadAllBytes(fixture.Managed));
    }

    [Fact]
    public void NearLimitUserConfigRejectsIncludeGrowthBeforeEitherFileChanges()
    {
        using var fixture = new Fixture(); fixture.Install();
        byte[] managedBefore = File.ReadAllBytes(fixture.Managed);
        byte[] original = Encoding.UTF8.GetBytes("#" + new string('x', 1024 * 1024 - 34) + "\n");
        File.WriteAllBytes(fixture.Config, original);
        var other = fixture.Profile with { Id = Guid.NewGuid(), Alias = "3080m" };

        IOException error = Assert.Throws<IOException>(() => fixture.Install(other));

        Assert.Contains("1 MiB", error.Message);
        Assert.Contains("均未修改", error.Message);
        Assert.Equal(original, File.ReadAllBytes(fixture.Config));
        Assert.Equal(managedBefore, File.ReadAllBytes(fixture.Managed));
        Assert.Empty(Directory.GetFiles(fixture.Directory, "*backup*"));
        Assert.Empty(Directory.GetFiles(fixture.Directory, "*.tmp"));
    }

    [Fact]
    public void GeneratedConfigContainsOnlyOpaqueProfileIdAndPinnedHostIdentity()
    {
        using var fixture = new Fixture(); fixture.Install();
        string config = File.ReadAllText(fixture.Managed);
        Assert.Contains(" --stdio " + fixture.Profile.Id.ToString("D"), config);
        string unescaped = config.Replace("%%", "");
        Assert.DoesNotContain("%h", unescaped); Assert.DoesNotContain("%r", unescaped); Assert.DoesNotContain("%n", unescaped);
        Assert.Contains("HostKeyAlias " + SshTerminalLauncher.HostKeyAlias(fixture.Profile.RelayPin, fixture.Profile.DeviceId), config);
        Assert.Contains("StrictHostKeyChecking ask", config);
        Assert.DoesNotContain("Password", config); Assert.DoesNotContain("AccessToken", config);
    }

    [Fact]
    public void PercentInIncludePathIsRejectedAcrossOpenSshVersionDifferences()
    {
        using var fixture = new Fixture("literal %h %d %% folder");
        Assert.Contains("百分号", Assert.Throws<ArgumentException>(() => fixture.Install()).Message);
        Assert.False(File.Exists(fixture.Config)); Assert.False(File.Exists(fixture.Managed));
    }

    [Fact]
    public async Task RealSystemSshParsesOnlyOwnedConfigAndPreservesLiteralPaths()
    {
        using var fixture = new Fixture("ordinary folder");
        File.WriteAllText(fixture.Config, "Host *\n User fallback\n Compression yes\n");
        fixture.Install(fixture.Profile with { Username = @"DOMAIN\fixture", IdentityFile = Path.Combine(fixture.Directory, "identity %h with spaces") });
        var start = new ProcessStartInfo(SshTerminalLauncher.ExecutablePath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-G"); start.ArgumentList.Add("-F"); start.ArgumentList.Add(fixture.Config); start.ArgumentList.Add("5090");
        using var process = Process.Start(start)!;
        string output = await process.StandardOutput.ReadToEndAsync(), error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(process.ExitCode == 0, error);
        Assert.Contains("user DOMAIN\\fixture", output);
        Assert.Contains("hostname " + fixture.Profile.Id.ToString("D") + ".remotedesk.invalid", output);
        Assert.Contains("compression yes", output);
        Assert.Contains("proxycommand \"" + fixture.Helper.Replace('\\', '/').Replace("%", "%%"), output);
        Assert.Contains("userknownhostsfile " + fixture.Hosts.Replace('\\', '/'), output);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "RemoteDesk-alias-tests", Guid.NewGuid().ToString("N"));
        internal string Config => Path.Combine(Directory, "config");
        internal string Managed => Path.Combine(Directory, "managed config");
        internal string Hosts => Path.Combine(Directory, "known_hosts %h %%");
        internal string Helper => Path.Combine(Directory, "owned-helper %h %d %%.exe");
        internal SshCommandProfile Profile { get; } = new(Guid.NewGuid(), "5090", "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", new string('B', 64), new string('A', 64), "fixture", null);
        internal Fixture(string? folder = null)
        {
            if (folder is not null) Directory = Path.Combine(Directory, folder);
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(Helper, "owned nonexecutable test fixture; ssh -G never launches it");
        }
        internal SshAliasConfigResult Install(SshCommandProfile? profile = null) => SshAliasConfig.Install(profile ?? Profile, Helper, Config, Managed, Hosts);
        internal void Remove(SshCommandProfile? profile = null) => SshAliasConfig.Remove(profile ?? Profile, Helper, Config, Managed, Hosts);
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
