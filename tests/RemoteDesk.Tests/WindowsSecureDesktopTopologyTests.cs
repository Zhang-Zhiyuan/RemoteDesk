using System.Text.Json;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class WindowsSecureDesktopTopologyTests
{
    private static readonly Rectangle Landscape = new(0, 0, 1920, 1080);
    private static readonly Rectangle Portrait = new(0, 0, 1080, 1920);
    private static readonly Rectangle SecondBounds = new(1920, 0, 1920, 1080);
    private static ScreenCaptureTarget Target(string id, Rectangle bounds) => new(id, id, bounds);
    private static SecureDesktopRequest Request(string? id = "original") =>
        new("capture", Width: 1920, Height: 1080, CaptureTargetId: id);

    [Fact]
    public void ExistingMonitorKeepsItsIdentityAndUsesCurrentOrientation()
    {
        var selection = WindowsSecureDesktopDisplays.ResolveCaptureSelection(Request(),
            [Target("original", Portrait), Target("other", SecondBounds)]);
        Assert.Equal(Portrait, selection.Bounds);
        Assert.False(selection.IsFallback);
    }

    [Fact]
    public void MissingIdUsesOnlyTheUniquePhysicalLoginScreenWithoutChangingOriginalRequest()
    {
        var request = Request();
        var selection = WindowsSecureDesktopDisplays.ResolveCaptureSelection(request,
            [Target("winlogon-display", Portrait), Target(ScreenCaptureTarget.AllScreensId, Portrait)]);
        Assert.True(selection.IsFallback);
        Assert.Equal(Portrait, selection.Bounds);
        Assert.Equal("original", request.CaptureTargetId);
        var restored = WindowsSecureDesktopDisplays.ResolveCaptureSelection(request,
            [Target("original", Landscape), Target("other", SecondBounds)]);
        Assert.Equal(Landscape, restored.Bounds);
        Assert.False(restored.IsFallback);
    }

    [Fact]
    public void MissingMonitorNeverFallsBackToPrimaryOrAggregateAmongSeveralScreens()
    {
        ScreenCaptureTarget[] targets = [Target("first", Landscape) with { IsPrimary = true },
            Target("second", SecondBounds), Target(ScreenCaptureTarget.AllScreensId, Rectangle.Union(Landscape, SecondBounds))];
        var error = Assert.Throws<SecureDesktopTargetException>(() =>
            WindowsSecureDesktopDisplays.ResolveCaptureSelection(Request(), targets));
        Assert.False(error.TopologyChanged);
    }

    [Fact]
    public void AggregateAloneDoesNotClaimAUniquePhysicalMonitor()
    {
        Assert.Throws<SecureDesktopTargetException>(() => WindowsSecureDesktopDisplays.ResolveCaptureSelection(
            Request(), [Target(ScreenCaptureTarget.AllScreensId, Landscape)]));
        Assert.Throws<SecureDesktopTargetException>(() => WindowsSecureDesktopDisplays.ResolveCaptureSelection(Request(), []));
    }

    [Fact]
    public void ExplicitAllScreensKeepsTheActualAggregateAndDoesNotBecomeSingleScreen()
    {
        Rectangle aggregate = Rectangle.Union(Landscape, SecondBounds);
        ScreenCaptureTarget[] targets = [Target("first", Landscape), Target("second", SecondBounds),
            Target(ScreenCaptureTarget.AllScreensId, aggregate)];
        var selection = WindowsSecureDesktopDisplays.ResolveCaptureSelection(Request(ScreenCaptureTarget.AllScreensId), targets);
        Assert.Equal(aggregate, selection.Bounds);
        Assert.False(selection.IsFallback);
        Assert.True(WindowsSecureDesktopDisplays.MatchesCurrentBounds(targets, aggregate));
    }

    [Fact]
    public void PointerBoundsMustMatchACurrentMonitorOrExactExplicitAggregate()
    {
        Rectangle aggregate = Rectangle.Union(Landscape, SecondBounds);
        ScreenCaptureTarget[] targets = [Target("first", Landscape), Target("second", SecondBounds),
            Target(ScreenCaptureTarget.AllScreensId, aggregate)];
        Assert.True(WindowsSecureDesktopDisplays.MatchesCurrentBounds(targets, Landscape));
        Assert.True(WindowsSecureDesktopDisplays.MatchesCurrentBounds(targets, SecondBounds));
        Assert.True(WindowsSecureDesktopDisplays.MatchesCurrentBounds(targets, aggregate));
        Assert.False(WindowsSecureDesktopDisplays.MatchesCurrentBounds(targets, new Rectangle(960, 0, 1920, 1080)));
        Assert.False(WindowsSecureDesktopDisplays.MatchesCurrentBounds(targets, Portrait));
    }

    [Fact]
    public void LegacyCaptureCannotGuessNewGeometryFromAnOldContainedRectangle()
    {
        var error = Assert.Throws<SecureDesktopTargetException>(() =>
            WindowsSecureDesktopDisplays.ResolveCaptureSelection(Request(null), [Target("larger", new Rectangle(0, 0, 3840, 2160))]));
        Assert.True(error.TopologyChanged);
        var valid = WindowsSecureDesktopDisplays.ResolveCaptureSelection(Request(null), [Target("same", Landscape)]);
        Assert.False(valid.IsFallback);
    }

    [Theory]
    [InlineData(false, "target-unavailable")]
    [InlineData(true, "topology-changed")]
    public void HelperTopologyClassificationSurvivesFramedRoundtripWithoutExceptionText(bool changed, string expectedCode)
    {
        SecureDesktopReply reply = WindowsSecureDesktopProtocol.FailureReply(new SecureDesktopTargetException(changed));
        using var stream = new MemoryStream();
        WindowsSecureDesktopProtocol.Write(stream, reply, default);
        stream.Position = 0;
        var restored = WindowsSecureDesktopProtocol.Read<SecureDesktopReply>(stream, default);
        Assert.Equal("error", restored.Status);
        Assert.Equal(expectedCode, restored.ErrorCode);
        var error = Assert.IsType<SecureDesktopTargetException>(WindowsSecureDesktopProtocol.HelperFailure(restored));
        Assert.Equal(changed, error.TopologyChanged);
    }

    [Fact]
    public void NativeFailureAndUnknownOldCodesCannotBecomeRecoveryAuthorityOrLeakDetails()
    {
        const string secret = "private user input and native path";
        var reply = WindowsSecureDesktopProtocol.FailureReply(new InvalidOperationException(secret));
        Assert.Equal("request-failed", reply.ErrorCode);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(reply));
        foreach (string? code in new string?[] { null, "unknown", "target-unavailable " })
        {
            var error = WindowsSecureDesktopProtocol.HelperFailure(reply with { ErrorCode = code, Error = secret });
            Assert.IsType<InvalidOperationException>(error);
            Assert.DoesNotContain(secret, error.Message);
        }
    }

    [Fact]
    public void OldRepliesWithoutNewFieldsStillDeserializeAndNewFieldsAreOptional()
    {
        var legacy = JsonSerializer.Deserialize<SecureDesktopReply>("{\"Status\":\"error\",\"Error\":\"legacy\"}")!;
        Assert.Null(legacy.ErrorCode);
        Assert.False(legacy.CaptureTargetFallback);
        Assert.IsType<InvalidOperationException>(WindowsSecureDesktopProtocol.HelperFailure(legacy));
        var extended = new SecureDesktopReply("active", CaptureTargetFallback: true);
        using var stream = new MemoryStream();
        WindowsSecureDesktopProtocol.Write(stream, extended, default);
        stream.Position = 0;
        Assert.True(WindowsSecureDesktopProtocol.Read<SecureDesktopReply>(stream, default).CaptureTargetFallback);
    }

    [Fact]
    public void SecureDesktopCaptureIsNotVetoedByNormalDesktopTopologyAndUnlockRestoresOriginalTarget()
    {
        bool secure = false;
        using var capture = new ScreenCaptureService(Target("original", Landscape), () => secure);
        ScreenCaptureTarget[] unrelated = [Target("unrelated", SecondBounds)];
        Assert.False(capture.GetTargetAvailability(unrelated).IsAvailable);
        secure = true;
        var login = capture.GetTargetAvailability(unrelated);
        Assert.True(login.IsAvailable); // Provisional request geometry; not permission to inject a pointer.
        Assert.Equal(Landscape, login.Bounds);
        secure = false;
        Assert.False(capture.GetTargetAvailability(unrelated).IsAvailable);
        var restored = capture.GetTargetAvailability([Target("original", Portrait), .. unrelated]);
        Assert.True(restored.IsAvailable);
        Assert.Equal(Portrait, restored.Bounds);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, true)]
    [InlineData(true, false, false, true)]
    public void PointerInputWaitsForFreshPixelsAfterFailureLockAndUnlock(bool ready, bool capturedSecure, bool currentSecure, bool expected) =>
        Assert.Equal(expected, RemoteHostServer.CanUseCapturedPointerGeometry(ready, capturedSecure, currentSecure));

    [Theory]
    [InlineData((int)RemoteInputKind.MouseMove)]
    [InlineData((int)RemoteInputKind.MouseDown)]
    [InlineData((int)RemoteInputKind.MouseUp)]
    [InlineData((int)RemoteInputKind.MouseWheel)]
    public void InactiveHelperReplyNeverReplaysOldPointerCoordinates(int kind)
    {
        var command = new RemoteInputCommand((RemoteInputKind)kind, RemoteMouseButton.Left, 1, 1, 0);
        Assert.Throws<SecureDesktopTargetException>(() => WindowsSecureDesktopClient.ValidateInactiveFallback(
            new SecureDesktopRequest("input", command)));
    }

    [Fact]
    public void InactiveHelperKeepsTargetIndependentReleasesAndKeysCompatible()
    {
        WindowsSecureDesktopClient.ValidateInactiveFallback(new("release-key", RemoteInputCommand.KeyDown(0x10)));
        WindowsSecureDesktopClient.ValidateInactiveFallback(new("release-mouse"));
        WindowsSecureDesktopClient.ValidateInactiveFallback(new("input", RemoteInputCommand.KeyUp(0x10)));
    }
}
