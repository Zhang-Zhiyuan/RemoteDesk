using Xunit;

namespace RemoteDesk.Tests;

public sealed class ClipboardSnapshotHostTests
{
    private const RemoteDeviceCapabilities Required = RemoteDeviceCapabilities.ClipboardText |
        RemoteDeviceCapabilities.ClipboardSnapshotV1;

    [Fact]
    public void WindowsAdvertisesSnapshotCapabilityAndSnapshotRequestsDoNotBlockInput()
    {
        Assert.Equal(1 << 28, (int)RemoteDeviceCapabilities.ClipboardSnapshotV1);
        Assert.Equal(Required, RemoteDeviceCapabilityInfo.LocalWindows(false) & Required);
        Assert.True(RemoteHostServer.CanInputOvertakeControl(RemoteControlKind.ClipboardSnapshotRequest));
        Assert.False(RemoteHostServer.CanInputOvertakeControl(RemoteControlKind.ClipboardSetText));
    }

    [Theory]
    [InlineData((int)RemoteDeviceCapabilities.None)]
    [InlineData((int)RemoteDeviceCapabilities.ClipboardText)]
    [InlineData((int)RemoteDeviceCapabilities.ClipboardSnapshotV1)]
    public async Task UnnegotiatedOrIncompleteCapabilitiesNeverReadClipboard(int capabilities)
    {
        int sequenceReads = 0;
        int textReads = 0;
        var state = new RemoteHostServer.ViewerSessionState
        {
            Capabilities = (RemoteDeviceCapabilities)capabilities,
            ClipboardSnapshots = new ClipboardSnapshotService(() => { sequenceReads++; return 1; },
                () => { textReads++; return Task.FromResult("private"); })
        };
        Assert.Null(await RemoteHostServer.BuildClipboardSnapshotResponseAsync(Request(), state, CancellationToken.None));
        Assert.Equal(0, sequenceReads);
        Assert.Equal(0, textReads);
    }

    [Fact]
    public async Task SnapshotReplyPreservesRequestIdAndChangedOnlyText()
    {
        var state = new RemoteHostServer.ViewerSessionState
        {
            Capabilities = Required,
            ClipboardSnapshots = new ClipboardSnapshotService(() => 5, () => Task.FromResult("mouse copied 中文"))
        };
        byte[]? firstPayload = await RemoteHostServer.BuildClipboardSnapshotResponseAsync(Request("first"), state, CancellationToken.None);
        Assert.NotNull(firstPayload);
        RemoteControlMessage first = RemoteMessageCodec.DecodeControl(firstPayload);
        Assert.Equal("first", first.TransferId);
        Assert.True(first.Success);
        Assert.True(first.ClipboardChanged);
        Assert.True(first.ClipboardHasText);
        Assert.Equal("mouse copied 中文", first.Text);
        byte[]? unchangedPayload = await RemoteHostServer.BuildClipboardSnapshotResponseAsync(
            Request("second", first.ClipboardRevision!), state, CancellationToken.None);
        Assert.NotNull(unchangedPayload);
        RemoteControlMessage next = RemoteMessageCodec.DecodeControl(unchangedPayload);
        Assert.Equal("second", next.TransferId);
        Assert.Equal(first.ClipboardRevision, next.ClipboardRevision);
        Assert.False(next.ClipboardChanged);
        Assert.True(next.ClipboardHasText);
        Assert.Equal("", next.Text);
    }

    [Fact]
    public async Task ClipboardReadFailureReturnsCorrelatedGenericFailureWithoutPrivateText()
    {
        var state = new RemoteHostServer.ViewerSessionState
        {
            Capabilities = Required,
            ClipboardSnapshots = new ClipboardSnapshotService(() => 5,
                () => throw new IOException("private token should never be returned"))
        };
        byte[]? payload = await RemoteHostServer.BuildClipboardSnapshotResponseAsync(Request(), state, CancellationToken.None);
        Assert.NotNull(payload);
        RemoteControlMessage error = RemoteMessageCodec.DecodeControl(payload);
        Assert.Equal("request", error.TransferId);
        Assert.False(error.Success);
        Assert.False(error.ClipboardChanged);
        Assert.False(error.ClipboardHasText);
        Assert.Equal("", error.ClipboardRevision);
        Assert.Equal("", error.Text);
        Assert.DoesNotContain("private", error.StatusMessage!);
    }

    [Fact]
    public async Task WithdrawalOfCapabilitiesWhileReadingSuppressesResponse()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new RemoteHostServer.ViewerSessionState
        {
            Capabilities = Required,
            ClipboardSnapshots = new ClipboardSnapshotService(() => 6, () =>
            {
                started.SetResult();
                return read.Task;
            })
        };
        Task<byte[]?> pending = RemoteHostServer.BuildClipboardSnapshotResponseAsync(Request(), state, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        state.Capabilities = RemoteDeviceCapabilities.ClipboardText;
        read.SetResult("not sent after opt-out");
        Assert.Null(await pending);
    }

    [Fact]
    public async Task SessionCancellationDoesNotReturnLateClipboardData()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = new CancellationTokenSource();
        var state = new RemoteHostServer.ViewerSessionState
        {
            Capabilities = Required,
            ClipboardSnapshots = new ClipboardSnapshotService(() => 6, () =>
            {
                started.SetResult();
                return read.Task;
            })
        };
        Task<byte[]?> pending = RemoteHostServer.BuildClipboardSnapshotResponseAsync(Request(), state, session.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        session.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        read.SetResult("old connection");
    }

    [Fact]
    public async Task SnapshotCacheIsSessionOwnedEvenWhenWindowsSequenceNumbersMatch()
    {
        var firstState = new RemoteHostServer.ViewerSessionState
        {
            Capabilities = Required,
            ClipboardSnapshots = new ClipboardSnapshotService(() => 9, () => Task.FromResult("first session"))
        };
        var secondState = new RemoteHostServer.ViewerSessionState
        {
            Capabilities = Required,
            ClipboardSnapshots = new ClipboardSnapshotService(() => 9, () => Task.FromResult("second session"))
        };
        byte[]? first = await RemoteHostServer.BuildClipboardSnapshotResponseAsync(Request(), firstState, CancellationToken.None);
        byte[]? second = await RemoteHostServer.BuildClipboardSnapshotResponseAsync(Request(), secondState, CancellationToken.None);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal("first session", RemoteMessageCodec.DecodeControl(first).Text);
        Assert.Equal("second session", RemoteMessageCodec.DecodeControl(second).Text);
    }

    [Fact]
    public async Task OtherControlKindsCannotAccidentallyInvokeSnapshotRead()
    {
        var state = new RemoteHostServer.ViewerSessionState
        {
            Capabilities = Required,
            ClipboardSnapshots = new ClipboardSnapshotService(() => throw new InvalidOperationException("must not read"))
        };
        RemoteControlMessage other = RemoteMessageCodec.DecodeControl(RemoteMessageCodec.EncodeClipboardGetText());
        Assert.Null(await RemoteHostServer.BuildClipboardSnapshotResponseAsync(other, state, CancellationToken.None));
    }

    [Fact]
    public async Task SnapshotReadsDoNotConsumeTheLegacyShortcutSequenceMarker()
    {
        var state = new RemoteHostServer.ViewerSessionState
        {
            Capabilities = Required | RemoteDeviceCapabilities.ClipboardSequenceTracking,
            ClipboardSnapshots = new ClipboardSnapshotService(() => 14, () => Task.FromResult("read-only snapshot"))
        };
        state.RecordClipboardInputSequence(13, nowMilliseconds: 1000);
        Assert.NotNull(await RemoteHostServer.BuildClipboardSnapshotResponseAsync(Request(), state, CancellationToken.None));
        Assert.Equal((uint)13, state.TakePendingClipboardInputSequence(1100, 2000, out bool expired));
        Assert.False(expired);
    }

    private static RemoteControlMessage Request(string id = "request", string revision = "") =>
        RemoteMessageCodec.DecodeControl(RemoteMessageCodec.EncodeClipboardSnapshotRequest(id, revision));
}
