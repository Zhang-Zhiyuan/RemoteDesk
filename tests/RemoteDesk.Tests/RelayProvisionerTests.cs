using System.Reflection;
using System.Text.Json;
using Renci.SshNet.Sftp;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class RelayProvisionerTests
{
    [Theory]
    [InlineData(false, "1.0.8", true)]
    [InlineData(true, "invalid", true)]
    [InlineData(true, "1.0", true)]
    [InlineData(true, "1.0.8.1", true)]
    [InlineData(true, "1.0.8", false)]
    public void InstallerSuccessRequiresVerifiedRuntimeIdentity(bool verified, string version, bool validHash)
    {
        string json = JsonSerializer.Serialize(new
        {
            port = 56567, accessToken = new string('a', 64), tlsCertificateSha256 = new string('b', 64),
            healthVerified = verified, serverVersion = version, serverSourceSha256 = validHash ? new string('c', 64) : "bad"
        });
        Assert.Throws<InvalidOperationException>(() => RelayProvisioner.ParseResult("REMOTEDESK_RELAY_RESULT=" + json));
    }

    [Fact]
    public void VerifiedRuntimeIdentityIsRetainedInInstallerResult()
    {
        string json = JsonSerializer.Serialize(new
        {
            port = 56567, accessToken = new string('a', 64), tlsCertificateSha256 = new string('b', 64),
            healthVerified = true, serverVersion = "1.0.8", serverSourceSha256 = new string('c', 64)
        });
        var result = RelayProvisioner.ParseResult("REMOTEDESK_RELAY_RESULT=" + json);
        Assert.True(result.HealthVerified);
        Assert.Equal("1.0.8", result.ServerVersion);
        Assert.Equal(new string('c', 64), result.ServerSourceSha256);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"port\":56567,\"accessToken\":null}")]
    public void InvalidInstallerResultIsAnActionableError(string json)
    {
        Assert.Throws<InvalidOperationException>(() =>
            RelayProvisioner.ParseResult("REMOTEDESK_RELAY_RESULT=" + json));
    }

    [Theory]
    [InlineData("/tmp/not-remotedesk")]
    [InlineData("/tmp/remotedesk-relay-; echo injected")]
    public void InstallerCommandRejectsUnexpectedRemotePaths(string path)
    {
        Assert.Throws<ArgumentException>(() => RelayProvisioner.BuildInstallCommand(path, false));
    }

    [Fact]
    public void LinuxInstallerAndServerAreEmbeddedInWindowsAssembly()
    {
        string[] resources = typeof(RelayProvisioner)
            .Assembly.GetManifestResourceNames();

        Assert.Contains(
            "RemoteDesk.Relay.install_remotedesk_relay.sh",
            resources);
        Assert.Contains(
            "RemoteDesk.Relay.remotedesk_relay_server.py",
            resources);
    }

    [Theory]
    [InlineData("SHA256:abc=", "SHA256:abc")]
    [InlineData("sha256:abc==", "SHA256:abc")]
    [InlineData("  SHA256:value  ", "SHA256:value")]
    [InlineData("abc", "SHA256:abc")]
    [InlineData("  AbC+/value=  ", "SHA256:AbC+/value")]
    [InlineData("", "")]
    [InlineData("  ", "")]
    [InlineData(null, "")]
    public void SshFingerprintNormalizationIsStable(
        string? value,
        string expected)
    {
        Assert.Equal(
            expected,
            RelayProvisioner.NormalizeSshFingerprint(value));
    }

    [Fact]
    public void SshNetAndOpenSshFingerprintsIdentifyTheSameKey()
    {
        string sshNetFingerprint = Convert.ToBase64String(
            Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
        string openSshFingerprint = "SHA256:" + sshNetFingerprint.TrimEnd('=');

        string normalized = RelayProvisioner.NormalizeSshFingerprint(sshNetFingerprint);

        Assert.Equal(openSshFingerprint, normalized);
        Assert.Equal(normalized, RelayProvisioner.NormalizeSshFingerprint(normalized));
    }

    [Fact]
    public void SshFingerprintNormalizationPreservesCaseSensitiveKeyData()
    {
        Assert.NotEqual(
            RelayProvisioner.NormalizeSshFingerprint("SHA256:AbC+/value"),
            RelayProvisioner.NormalizeSshFingerprint("sha256:abc+/value="));
    }

    [Theory]
    [InlineData(RelayProvisioner.TemporaryDirectoryMode, true)]
    [InlineData(RelayProvisioner.TemporaryRequestMode, false)]
    public void TemporaryPermissionsAreAcceptedBySshNetAndOwnerOnly(short mode, bool executable)
    {
        // SSH.NET exposes no public constructor. Exercise the actual dependency's
        // permission parser offline instead of duplicating its octal conversion.
        var attributes = (SftpFileAttributes)Activator.CreateInstance(
            typeof(SftpFileAttributes), BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null, args: [DateTime.UnixEpoch, DateTime.UnixEpoch, 0L, 0, 0, 0u, null],
            culture: null)!;

        attributes.SetPermissions(mode);

        Assert.True(attributes.OwnerCanRead);
        Assert.True(attributes.OwnerCanWrite);
        Assert.Equal(executable, attributes.OwnerCanExecute);
        Assert.False(attributes.GroupCanRead || attributes.GroupCanWrite || attributes.GroupCanExecute);
        Assert.False(attributes.OthersCanRead || attributes.OthersCanWrite || attributes.OthersCanExecute);
        Assert.False(attributes.IsUIDBitSet || attributes.IsGroupIDBitSet || attributes.IsStickyBitSet);
    }

    [Fact]
    public void PersistedRelaySettingsHaveNoAdministratorPasswordField()
    {
        Assert.Null(typeof(RelaySettings).GetProperty(
            "AdminPassword",
            BindingFlags.Public |
            BindingFlags.Instance |
            BindingFlags.IgnoreCase));
        Assert.NotNull(typeof(RelaySettings).GetProperty(
            nameof(RelaySettings.ProtectedAccessToken)));
    }
}
