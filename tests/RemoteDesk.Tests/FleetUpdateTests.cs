using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class FleetUpdateTests
{
    [Fact]
    public void OnlyTheSignedWindowsPackageCanBeScheduled()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Fleet test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var publisher = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        string hash = new('a', 64);
        var signed = Encoding.ASCII.GetBytes($"RemoteDesk/fleet/v1\n1.0.56\n20261120000000\nwin-x64\n42\n{hash}");
        var signature = Convert.ToBase64String(key.SignData(signed, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        JsonElement Ticket(string platform, int size) => JsonSerializer.SerializeToElement(new
            { version = "1.0.56", build = "20261120000000", platform, size, sha256 = hash, signature });
        var ticket = Ticket("win-x64", 42);
        Assert.Equal(hash, FleetUpdate.Verify(ticket, publisher).Sha256);
        Assert.Throws<CryptographicException>(() => FleetUpdate.Verify(Ticket("win-x64", 43), publisher));
        Assert.Throws<InvalidDataException>(() => FleetUpdate.Verify(Ticket("android", 42), publisher));
        using var productionPublisher = FleetUpdate.LoadPublisher();
        Assert.Throws<CryptographicException>(() => FleetUpdate.Verify(ticket, productionPublisher));
    }
}
