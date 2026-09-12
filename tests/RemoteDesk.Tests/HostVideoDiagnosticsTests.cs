using Xunit;

namespace RemoteDesk.Tests;

public sealed class HostVideoDiagnosticsTests
{
    [Fact]
    public void SessionHistoryIsBoundedAndMetricsDoNotEvictStartupFailure()
    {
        var status = new HostVideoDiagnostics();
        status.Record("H.264 unavailable: ffmpeg is unavailable");
        for (int i = 0; i < 100; i++) status.Record("画面统计：" + i);
        string text = Assert.IsType<string>(status.TryRead(10));
        Assert.Contains("ffmpeg is unavailable", text);
        Assert.Contains("画面统计：99", text);
        for (int i = 0; i < 100; i++) status.Record("H.264 统计：" + i);
        text = Assert.IsType<string>(status.TryRead(1010));
        Assert.Contains("ffmpeg is unavailable", text);
        Assert.Contains("H.264 统计：99", text);
        for (int i = 0; i < 100; i++) status.Record(new string('中', 10000));
        Assert.True(status.TryRead(3000)!.Length <= HostVideoDiagnostics.MaximumCharacters);
    }

    [Fact]
    public void RequestsAreRateLimitedAndSessionHistoriesAreSeparate()
    {
        var first = new HostVideoDiagnostics();
        first.Record("first session");
        Assert.Contains("first session", first.TryRead(0));
        Assert.Null(first.TryRead(999));
        Assert.NotNull(first.TryRead(1000));
        Assert.DoesNotContain("first session", new HostVideoDiagnostics().TryRead(0));
    }

    [Fact]
    public void BoundedUnicodeDiagnosticAndRequestRoundTrip()
    {
        var request = RemoteMessageCodec.DecodeControl(RemoteMessageCodec.EncodeHostVideoDiagnosticsRequest());
        Assert.Equal(RemoteControlKind.HostVideoDiagnosticsRequest, request.Kind);
        const string text = "硬件编码不可用\nJPEG fallback";
        var reply = RemoteMessageCodec.DecodeControl(RemoteMessageCodec.EncodeHostVideoDiagnostics(text));
        Assert.Equal(RemoteControlKind.HostVideoDiagnostics, reply.Kind);
        Assert.Equal(text, reply.Text);
        Assert.Throws<ArgumentException>(() => RemoteMessageCodec.EncodeHostVideoDiagnostics(new string('x', 4097)));
    }

    [Fact]
    public async Task OldPeersAreNeverSentAnUnrecognizedDiagnosticRequest()
    {
        using var client = new RemoteViewerClient();
        Assert.False(await client.RequestHostVideoDiagnosticsAsync());
    }

    [Fact]
    public void DiagnosticReplyCannotBlockInputReadsBehindVideoWrites() =>
        Assert.True(RemoteHostServer.CanInputOvertakeControl(RemoteControlKind.HostVideoDiagnosticsRequest));
}
