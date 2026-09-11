using System.Text.Json;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class RelayAdminLoginTests
{
    private const string Server = "relay.test";
    private const string Secret = "owned-test-internal-access-token-only";

    private static string Response() => JsonSerializer.Serialize(new
    {
        version = 1, port = 56567, accessToken = Secret, tlsCertificateSha256 = new string('A', 64)
    });

    [Fact]
    public void RootLoginReadsInternalCredentialsWithoutChangingDeviceAuthentication()
    {
        RelayProvisionResult result = RelayAdminLogin.ParseResponse(Response(), Server, "SHA256:owned");
        Assert.Equal(Server, result.ServerAddress);
        Assert.Equal(56567, result.RelayPort);
        Assert.Equal(Secret, result.AccessToken);
        Assert.False(result.Installed);
        Assert.DoesNotContain(Secret, result.ToString());
    }

    [Fact]
    public void LoginCommandsContainOnlyBundledReaderAndNoPassword()
    {
        string command = RelayAdminLogin.ReadCommand(root: true);
        Assert.StartsWith("python3 -c ", command);
        Assert.StartsWith("sudo -k -S -p '' -- python3 -c ", RelayAdminLogin.ReadCommand(root: false));
        Assert.DoesNotContain("systemctl", command);
        Assert.DoesNotContain("password", command);
        Assert.Contains(RelayAdminLogin.ResourceName, typeof(RelayAdminLogin).Assembly.GetManifestResourceNames());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"version\":true}")]
    [InlineData("{\"errorCode\":[]}")]
    [InlineData("{\"version\":1,\"port\":99999999999999999999999999}")]
    [InlineData("{\"version\":2,\"accessToken\":\"never-echo-this\"}")]
    [InlineData("{\"version\":1,\"port\":0,\"accessToken\":\"never-echo-this\",\"tlsCertificateSha256\":\"bad\"}")]
    public void InvalidReturnedConfigurationCannotBeSavedOrLeakSecrets(string response)
    {
        Exception error = Assert.Throws<InvalidOperationException>(() => RelayAdminLogin.ParseResponse(response, Server, "SHA256:owned"));
        Assert.DoesNotContain("never-echo-this", error.Message);
    }

    [Theory]
    [InlineData("not_configured", "部署 / 更新服务器")]
    [InlineData("permission_denied", "root")]
    [InlineData("untrusted-error", "配置无效")]
    public void ReaderFailuresHaveApplicationOwnedMessages(string code, string expected)
    {
        string response = JsonSerializer.Serialize(new {errorCode = code, password = "never-echo-this"});
        Exception error = Assert.Throws<InvalidOperationException>(() => RelayAdminLogin.ParseResponse(response, Server, "SHA256:owned"));
        Assert.Contains(expected, error.Message);
        Assert.DoesNotContain("never-echo-this", error.Message);
    }

    [Fact]
    public void OversizedServerOutputIsRejected()
    {
        Assert.Throws<InvalidOperationException>(() => RelayAdminLogin.ParseResponse(new string('x', 65537), Server, "SHA256:owned"));
    }

    [Fact]
    public void AdministratorPasswordAndInternalTokensAreRedactedFromDebugStrings()
    {
        var request = new RelayProvisionRequest(Server, 22, "root", "root-secret-never-log", 56567);
        Assert.DoesNotContain(request.AdminPassword, request.ToString());
        Assert.DoesNotContain(Secret, new RelayConnectionOptions(Server, 56567, Secret, new string('A', 64), Guid.NewGuid().ToString()).ToString());
        Assert.DoesNotContain(typeof(RelaySettings).GetProperties(), property =>
            property.Name.Contains("AdminPassword", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("RootPassword", StringComparison.OrdinalIgnoreCase));
    }
}
