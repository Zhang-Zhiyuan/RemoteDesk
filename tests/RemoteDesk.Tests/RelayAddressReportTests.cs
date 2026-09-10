using System.Text.Json;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class RelayAddressReportTests
{
    [Theory]
    [InlineData("10.1.2.3")] [InlineData("192.168.1.2")] [InlineData("100.64.0.1")] [InlineData("198.51.100.42")]
    public void LiteralUnicastAddressesAreAccepted(string value) => Assert.True(RelayAddressReport.IsUsableIPv4(value));

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("localhost")] [InlineData("127.0.0.1")]
    [InlineData("0.1.2.3")] [InlineData("169.254.1.2")] [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")] [InlineData("::1")] [InlineData("::ffff:10.1.2.3")]
    [InlineData("010.1.2.3")] [InlineData("10.1.2.3:1234")] [InlineData("10.1.2.3\n")]
    [InlineData("127.1")] [InlineData("0x7f000001")]
    public void UnusableOrAmbiguousHintsCannotBecomeConnectionTargets(string? value) =>
        Assert.False(RelayAddressReport.IsUsableIPv4(value));

    [Fact]
    public void ReportIsBoundedAndDeduplicated()
    {
        string[] values = ["10.1.2.3", "10.1.2.3", "invalid", ..Enumerable.Range(1, 40).Select(i => $"192.0.2.{i}")];
        var result = RelayAddressReport.Normalize(values);
        Assert.Equal(8, result.Count);
        Assert.Equal("10.1.2.3", result[0]);
        Assert.Equal(8, result.Distinct().Count());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"directPort\":true,\"directAddresses\":[\"10.1.2.3\"]}")]
    [InlineData("{\"directPort\":\"56565\",\"directAddresses\":[\"10.1.2.3\"]}")]
    [InlineData("{\"directPort\":65536,\"directAddresses\":[\"10.1.2.3\"]}")]
    [InlineData("{\"directPort\":56565,\"directAddresses\":\"10.1.2.3\"}")]
    [InlineData("{\"directPort\":56565,\"directAddresses\":[null,{},42,\"localhost\"]}")]
    public void OptionalMalformedMetadataDoesNotBreakLegacyDirectory(string json)
    {
        using var document = JsonDocument.Parse(json);
        var report = RelayAddressReport.Parse(document.RootElement);
        Assert.Empty(report.Addresses);
        Assert.Equal(0, report.Port);
    }

    [Fact]
    public void ActualCustomHostPortIsPreserved()
    {
        using var document = JsonDocument.Parse("{\"directPort\":40565,\"directAddresses\":[\"192.0.2.4\",\"192.0.2.4\",null]}");
        var report = RelayAddressReport.Parse(document.RootElement);
        Assert.Equal("192.0.2.4", Assert.Single(report.Addresses));
        Assert.Equal(40565, report.Port);
        var device = new RelayOnlineDevice(Guid.NewGuid().ToString(), "host", "Windows", null, false, 0)
            { DirectAddresses = report.Addresses, DirectPort = report.Port };
        Assert.Equal("192.0.2.4:40565", device.AddressDisplay);
    }
}
