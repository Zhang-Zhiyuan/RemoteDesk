using Xunit;

namespace RemoteDesk.Tests;

public sealed class PrivacyScreenTests
{
    [Theory]
    [InlineData(1, 1, 2, 2, true)]
    [InlineData(1, 1, 1, 2, false)]
    [InlineData(1, 2, 2, 2, false)]
    public void ViewerRejectsOldQueryEnableAndReconnectReplies(long requestedGeneration, long generation, long requestedRevision, long revision, bool expected)
        => Assert.Equal(expected, RemoteViewerWindow.AcceptPrivacyReply(requestedGeneration, generation, requestedRevision, revision));

    [Fact]
    public void EmergencyChordDoesNotTakeOtherApplicationsShortcuts()
    {
        Assert.True(WindowsPrivacyScreen.IsRestoreShortcut(Keys.Control | Keys.Alt | Keys.Shift | Keys.P));
        Assert.False(WindowsPrivacyScreen.IsRestoreShortcut(Keys.Control | Keys.Shift | Keys.P));
        Assert.False(WindowsPrivacyScreen.IsRestoreShortcut(Keys.Control | Keys.Alt | Keys.Shift | Keys.Q));
        Assert.False(WindowsPrivacyScreen.RequestLocalRestore()); // No curtain created by unit tests.
    }
    [Theory]
    [InlineData(0, "2E02696400")]
    [InlineData(1, "2E02696401")]
    [InlineData(2, "2E02696402")]
    public void RequestMatchesCrossPlatformWire(int value, string hex)
    {
        var action = (PrivacyScreenAction)value;
        byte[] bytes = RemoteMessageCodec.EncodePrivacyScreenRequest("id", action);
        Assert.Equal(hex, Convert.ToHexString(bytes));
        RemoteControlMessage decoded = RemoteMessageCodec.DecodeControl(bytes);
        Assert.Equal(action, decoded.PrivacyAction);
        Assert.Equal("id", decoded.TransferId);
        Assert.Throws<InvalidDataException>(() => RemoteMessageCodec.DecodeControl(bytes.Concat(new byte[] { 0 }).ToArray()));
    }
    [Fact]
    public void StatusMatchesCrossPlatformAndRejectsMalformedFlags()
    {
        var status = new PrivacyScreenStatus(true, true, true, "中");
        byte[] payload = RemoteMessageCodec.EncodePrivacyScreenStatus("id", status);
        Assert.Equal("2F02696401010103E4B8AD", Convert.ToHexString(payload));
        Assert.Equal(status, RemoteMessageCodec.DecodeControl(payload).PrivacyStatus);
        foreach (int index in new[] { 4, 5, 6 })
        {
            byte[] invalid = [.. payload]; invalid[index] = 2;
            Assert.Throws<InvalidDataException>(() => RemoteMessageCodec.DecodeControl(invalid));
        }
        payload[6] = 0;
        Assert.Throws<InvalidDataException>(() => RemoteMessageCodec.DecodeControl(payload));
        Assert.Throws<InvalidDataException>(() => RemoteMessageCodec.DecodeControl(Convert.FromHexString("2E02696403")));
    }
    [Fact]
    public async Task PermissionDefaultsOffAndRevocationRestoresImmediately()
    {
        var backend = new Backend(); using var session = new PrivacyScreenSession(backend);
        var owner = new object();
        Assert.False((await session.RequestAsync(PrivacyScreenAction.Enable, owner, default)).Success);
        Assert.Equal(0, backend.Starts);
        Assert.False(new HostSettings().AllowPrivacyScreen);
        await session.SetAllowedAsync(true);
        Assert.True((await session.RequestAsync(PrivacyScreenAction.Enable, owner, default)).Enabled);
        await session.SetAllowedAsync(false);
        Assert.False(backend.IsActive);
        Assert.False((await session.RequestAsync(PrivacyScreenAction.Query, owner, default)).Allowed);
    }
    [Fact]
    public async Task QueryOrOtherScreenDoesNotOwnCurtainAndStaleDisconnectCannotRestoreNewOwner()
    {
        var backend = new Backend(); using var session = new PrivacyScreenSession(backend);
        await session.SetAllowedAsync(true);
        using var first = new CancellationTokenSource(); using var second = new CancellationTokenSource();
        var a = new object(); var b = new object();
        await session.RequestAsync(PrivacyScreenAction.Enable, a, first.Token);
        await session.RequestAsync(PrivacyScreenAction.Enable, b, second.Token);
        Assert.Equal(1, backend.Starts);
        second.Cancel();
        Assert.True((await session.RequestAsync(PrivacyScreenAction.Query, a, default)).Enabled);
        await session.RequestAsync(PrivacyScreenAction.Disable, b, default);
        using var third = new CancellationTokenSource();
        await session.RequestAsync(PrivacyScreenAction.Enable, b, third.Token);
        first.Cancel();
        Assert.True((await session.RequestAsync(PrivacyScreenAction.Query, b, default)).Enabled);
        third.Cancel();
        Assert.False((await session.RequestAsync(PrivacyScreenAction.Query, b, default)).Enabled);
    }
    [Fact]
    public async Task StartupFailureOrRevocationDuringStartNeverLeavesPartialCurtain()
    {
        var backend = new Backend { Fail = true }; using var session = new PrivacyScreenSession(backend);
        await session.SetAllowedAsync(true);
        Assert.False((await session.RequestAsync(PrivacyScreenAction.Enable, new object(), default)).Success);
        Assert.False(backend.IsActive);
        backend.Fail = false;
        backend.StartBarrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var enabled = session.RequestAsync(PrivacyScreenAction.Enable, new object(), default);
        var revoked = session.SetAllowedAsync(false);
        backend.StartBarrier.SetResult();
        Assert.False((await enabled).Enabled);
        await revoked;
        Assert.False(backend.IsActive);
    }
    [Fact]
    public async Task DisconnectDuringStartupRestoresPartialCurtainAndAllowsRetry()
    {
        var backend = new Backend { StartBarrier = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var session = new PrivacyScreenSession(backend);
        await session.SetAllowedAsync(true);
        using var connection = new CancellationTokenSource();
        var started = session.RequestAsync(PrivacyScreenAction.Enable, new object(), connection.Token);
        Assert.True(backend.IsActive); // Model a partially created multi-monitor curtain.
        connection.Cancel();
        var status = await started;
        Assert.False(status.Success);
        Assert.False(status.Enabled);
        Assert.False(backend.IsActive);
        backend.StartBarrier = null;
        Assert.True((await session.RequestAsync(PrivacyScreenAction.Enable, new object(), default)).Enabled);
    }
    [Fact]
    public async Task CompletedStartupWithoutAnActiveCurtainIsNeverAcknowledgedAsSuccess()
    {
        var backend = new Backend { ActivateOnStart = false };
        using var session = new PrivacyScreenSession(backend);
        await session.SetAllowedAsync(true);
        var status = await session.RequestAsync(PrivacyScreenAction.Enable, new object(), default);
        Assert.False(status.Success);
        Assert.False(status.Enabled);
    }
    [Fact]
    public async Task LocalRestoreIsReflectedWithoutAViewerCommandAndDisposeRestores()
    {
        var backend = new Backend(); var session = new PrivacyScreenSession(backend);
        await session.SetAllowedAsync(true);
        await session.RequestAsync(PrivacyScreenAction.Enable, new object(), default);
        backend.Stop();
        Assert.False((await session.RequestAsync(PrivacyScreenAction.Query, new object(), default)).Enabled);
        await session.RequestAsync(PrivacyScreenAction.Enable, new object(), default);
        session.Dispose(); session.Dispose();
        Assert.False(backend.IsActive);
        await session.SetAllowedAsync(true);
        Assert.False(session.Allowed);
        Assert.False((await session.RequestAsync(PrivacyScreenAction.Enable, new object(), default)).Success);
        Assert.True(RemoteHostServer.CanInputOvertakeControl(RemoteControlKind.PrivacyScreenRequest));
    }
    private sealed class Backend : IPrivacyScreenBackend
    {
        public bool IsActive { get; private set; }
        public string InactiveReason => "Restored";
        public int Starts;
        public bool Fail;
        public bool ActivateOnStart = true;
        public TaskCompletionSource? StartBarrier;
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            Starts++; IsActive = ActivateOnStart;
            if (StartBarrier is { } barrier) await barrier.Task.WaitAsync(cancellationToken);
            if (Fail) throw new IOException("partial startup failure");
        }
        public void Stop() => IsActive = false;
        public void Dispose() => Stop();
    }
}
