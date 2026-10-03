using System.Reflection;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class IndependentScreenSessionTests
{
    [Fact]
    public void AdditionalScreenJoinsWithoutReplacingTheController()
    {
        var gate = new RemoteHostServer.ActiveClientGate<object>();
        var primary = new object(); var secondary = new object();
        Assert.Empty(gate.Activate(primary));
        string token = Assert.IsType<string>(gate.GetAttachmentToken(primary));
        Assert.Equal(64, token.Length);
        Assert.True(gate.TryAttach(secondary, token));
        Assert.Same(primary, gate.Current);
        Assert.Equal(token, gate.GetAttachmentToken(secondary));
        Assert.False(gate.Release(secondary));
        Assert.Same(primary, gate.Current);
        Assert.True(gate.Release(primary));
        Assert.Null(gate.GetAttachmentToken(primary));
        Assert.False(gate.TryAttach(new object(), token));
    }

    [Fact]
    public void NewControllerReplacesEveryScreenAndInvalidatesOldTickets()
    {
        var gate = new RemoteHostServer.ActiveClientGate<object>();
        var first = new object(); var second = new object(); var replacement = new object();
        gate.Activate(first);
        string token = gate.GetAttachmentToken(first)!;
        Assert.True(gate.TryAttach(second, token));
        Assert.Equal(new[] { first, second }, gate.Activate(replacement));
        Assert.NotEqual(token, gate.GetAttachmentToken(replacement));
        Assert.False(gate.TryAttach(new object(), token));
        Assert.False(gate.Release(first));
        Assert.False(gate.Release(second));
        Assert.Same(replacement, gate.Current);
        Assert.Null(gate.GetAttachmentToken(first));
    }

    [Fact]
    public void JoiningScreensHasABoundAndNeverEvictsAnotherWindow()
    {
        var gate = new RemoteHostServer.ActiveClientGate<object>();
        var first = new object();
        gate.Activate(first);
        string token = gate.GetAttachmentToken(first)!;
        for (int index = 1; index < RemoteHostServer.ActiveClientGate<object>.MaximumScreens; index++)
            Assert.True(gate.TryAttach(new object(), token));
        Assert.False(gate.TryAttach(new object(), token));
        Assert.Same(first, gate.Current);
        Assert.False(gate.TryAttach(first, token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF")]
    public void InvalidAttachmentNeverTakesOver(string? token)
    {
        var gate = new RemoteHostServer.ActiveClientGate<object>();
        var owner = new object();
        gate.Activate(owner);
        Assert.False(gate.TryAttach(new object(), token));
        Assert.Same(owner, gate.Current);
    }

    [Fact]
    public void LastScreenOwnsGroupLifetimeEvenIfFirstTransportEnds()
    {
        var gate = new RemoteHostServer.ActiveClientGate<object>();
        var first = new object(); var second = new object();
        gate.Activate(first);
        string token = gate.GetAttachmentToken(first)!;
        Assert.True(gate.TryAttach(second, token));
        Assert.False(gate.Release(first));
        Assert.Same(second, gate.Current);
        Assert.Equal(token, gate.GetAttachmentToken(second));
        Assert.True(gate.Release(second));
        Assert.Null(gate.Current);
    }

    [Fact]
    public void ScreenAttachmentControlsRoundTrip()
    {
        string token = new('A', 64);
        var offer = RemoteMessageCodec.DecodeControl(RemoteMessageCodec.EncodeScreenAttachmentOffer(token));
        Assert.Equal(RemoteControlKind.ScreenAttachmentOffer, offer.Kind);
        Assert.Equal(token, offer.ScreenSessionToken);
        var join = RemoteMessageCodec.DecodeControl(RemoteMessageCodec.EncodeScreenAttachmentJoin(new(token, @"\\.\DISPLAY2")));
        Assert.Equal(RemoteControlKind.ScreenAttachmentJoin, join.Kind);
        Assert.Equal(token, join.ScreenSessionToken);
        Assert.Equal(@"\\.\DISPLAY2", join.TargetId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG")]
    public void AttachmentCodecRejectsMalformedSecrets(string? token) =>
        Assert.Throws<InvalidDataException>(() => RemoteScreenAttachment.ValidateToken(token));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(ScreenCaptureTarget.AllScreensId)]
    public void EachAttachedWindowRequiresAPhysicalScreen(string target) =>
        Assert.Throws<InvalidDataException>(() => RemoteMessageCodec.EncodeScreenAttachmentJoin(new(new('A', 64), target)));

    [Fact]
    public void PauseAndResumeWakeCaptureWithoutChangingQualityOrCodec()
    {
        var state = new RemoteHostServer.ViewerSessionState();
        state.SetSupportedVideoCodecs(RemoteVideoCodecs.H264AnnexB);
        var before = state.GetVideoSelection();
        CancellationToken changed = state.GetVideoSelectionChangeToken(before.Version);
        Assert.True(state.SetScreenStreamPaused(true));
        Assert.True(state.ScreenStreamPaused);
        Assert.True(changed.IsCancellationRequested);
        Assert.False(state.SetScreenStreamPaused(true));
        Assert.Equal(before.Version + 1, state.VideoCodecVersion);
        Assert.True(state.SetScreenStreamPaused(false));
        Assert.False(state.ScreenStreamPaused);
        Assert.Equal(before.SupportedCodecs, state.SupportedVideoCodecs);
        Assert.Equal(before.Version + 2, state.VideoCodecVersion);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PauseControlRoundTrips(bool paused)
    {
        var control = RemoteMessageCodec.DecodeControl(RemoteMessageCodec.EncodeScreenStreamPause(paused));
        Assert.Equal(RemoteControlKind.ScreenStreamPause, control.Kind);
        Assert.Equal(paused, control.Success);
    }

    [Fact]
    public void DisconnectedClientCannotCreateAnUnqualifiedScreenConnection()
    {
        using var client = new RemoteViewerClient();
        Assert.False(client.CanOpenAdditionalScreen);
        Assert.Throws<InvalidOperationException>(() => client.CreateAdditionalScreenClient("DISPLAY2"));
    }

    [Fact]
    public void MissingAttachedScreenNeverSilentlyChangesToThePrimaryScreen()
    {
        var primary = ScreenCaptureService.GetAvailableTargets().First(target => !target.IsAllScreens);
        var secondary = new ScreenCaptureTarget("synthetic-unplugged-screen", "Secondary", new Rectangle(-1080, 0, 1080, 1920));
        Type type = typeof(RemoteHostServer).GetNestedType("CaptureSessionState", BindingFlags.NonPublic)!;
        using var state = (IDisposable)Activator.CreateInstance(type, secondary, 100, null, false)!;
        CaptureTargetStateSnapshot Refresh(params ScreenCaptureTarget[] targets) =>
            (CaptureTargetStateSnapshot)type.GetMethod("RefreshCaptureTopologyWithAutomaticFallback")!.Invoke(state, [targets])!;
        var removed = Refresh(primary);
        Assert.False(removed.IsAvailable);
        Assert.Equal(secondary.Id, removed.Target.Id);
        var restored = Refresh(primary, secondary);
        Assert.True(restored.IsAvailable);
        Assert.Equal(secondary.Id, restored.Target.Id);
        Assert.Equal(secondary.Bounds, type.GetProperty("LastCaptureBounds")!.GetValue(state));
    }
}
