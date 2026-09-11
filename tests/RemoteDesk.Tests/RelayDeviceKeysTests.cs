using Xunit;

namespace RemoteDesk.Tests;

public class RelayDeviceKeysTests
{
    private static readonly RelayConnectionOptions A = new("relay.test", 56567, new string('T', 32), new string('A', 64), "11111111-1111-4111-8111-111111111111");
    private static readonly RelayConnectionOptions B = A with { DeviceId = "22222222-2222-4222-8222-222222222222" };

    [Fact]
    public void DeviceKeysAreScopedToTargetAndVerifiedServer()
    {
        var keys = RelayDeviceKeys.Remember([], A, "protected-key-a");
        keys = RelayDeviceKeys.Remember(keys, B, "protected-key-b");
        Assert.Equal("protected-key-a", RelayDeviceKeys.Find(keys, A));
        Assert.Equal("protected-key-b", RelayDeviceKeys.Find(keys, B));
        Assert.Null(RelayDeviceKeys.Find(keys, A with { ServerAddress = "different.test" }));
        Assert.Null(RelayDeviceKeys.Find(keys, A with { Port = 443 }));
        Assert.Null(RelayDeviceKeys.Find(keys, A with { TlsCertificateSha256 = new string('B', 64) }));
    }

    [Fact]
    public void UpdatingOneKeyPreservesTheOtherAndRedactsDebugOutput()
    {
        var keys = RelayDeviceKeys.Remember(RelayDeviceKeys.Remember([], A, "old-a"), B, "key-b");
        keys = RelayDeviceKeys.Remember(keys, A, "new-a");
        Assert.Equal(2, keys.Count);
        Assert.Equal("new-a", RelayDeviceKeys.Find(keys, A));
        Assert.Equal("key-b", RelayDeviceKeys.Find(keys, B));
        Assert.DoesNotContain("new-a", keys[0].ToString());
    }

    [Fact]
    public void InvalidOrOversizedSavedRecordsAreDropped()
    {
        Assert.Empty(RelayDeviceKeys.Normalize([null, new(), new() { Scope = new string('A', 64), DeviceId = A.DeviceId, ProtectedPassword = new string('x', 16385) }]));
        List<RelayDeviceKey> keys = [];
        for (int i = 0; i < 60; i++) keys = RelayDeviceKeys.Remember(keys, A with { DeviceId = Guid.NewGuid().ToString() }, "owned-protected-key");
        Assert.Equal(50, keys.Count);
    }

    [Fact]
    public void PublicationIsAppliedOnlyAfterPersistence()
    {
        var settings = new RelaySettings { RegisterThisDevice = true };
        Assert.False(RelayDeviceKeys.SetPublication(settings, false, () => false));
        Assert.True(settings.RegisterThisDevice);
        Assert.Throws<IOException>(() => RelayDeviceKeys.SetPublication(settings, false, () => throw new IOException()));
        Assert.True(settings.RegisterThisDevice);
        Assert.True(RelayDeviceKeys.SetPublication(settings, false, () => !settings.RegisterThisDevice));
        Assert.False(settings.RegisterThisDevice);
    }

    [Fact]
    public void LogoutDropsServerCredentialsAndIdentityButPreservesDeviceKeys()
    {
        var previous = new RelaySettings { ServerAddress = A.ServerAddress, DeviceId = B.DeviceId,
            ProtectedAccessToken = "protected-server-token", TlsCertificateSha256 = A.TlsCertificateSha256,
            SshHostKeySha256 = "owned-server-ssh-identity", RegisterThisDevice = false,
            DeviceKeys = RelayDeviceKeys.Remember([], A, "protected-device-key") };
        RelaySettings loggedOut = RelayDeviceKeys.WithoutServerLogin(previous);
        Assert.Null(loggedOut.ServerAddress);
        Assert.Null(loggedOut.ProtectedAccessToken);
        Assert.Null(loggedOut.TlsCertificateSha256);
        Assert.Null(loggedOut.SshHostKeySha256);
        Assert.Equal(B.DeviceId, loggedOut.DeviceId);
        Assert.Equal("protected-device-key", RelayDeviceKeys.Find(loggedOut.DeviceKeys, A));
        Assert.False(loggedOut.RegisterThisDevice);
        Assert.Equal("protected-server-token", previous.ProtectedAccessToken);
    }
}
