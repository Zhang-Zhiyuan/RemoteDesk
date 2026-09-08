using Xunit;

namespace RemoteDesk.Tests;

public sealed class ViewerReconnectQualificationTests
{
    private static ViewerConnectionSnapshot CreateConnection() =>
        new(
            "host",
            56565,
            "password",
            ViewerVideoMode.StableJpeg);

    [Fact]
    public void DeviceInfoSynchronouslyQualifiesBeforeDelayedDisconnectUi()
    {
        var qualification =
            new ViewerReconnectQualification();
        qualification.BeginManualConnection(
            CreateConnection());

        ViewerReconnectQualificationResult result =
            qualification.ObserveDeviceInfo(
                connectionGeneration: 31,
                reconnectAttemptOrdinal: 0);

        Assert.True(result.Accepted);
        Assert.True(result.IntentCreated);
        Assert.True(result.GenerationQualified);
        Assert.NotNull(result.Connection);
        Assert.True(qualification.IsCurrent(
            result.IntentId,
            connectionGeneration: 31));
        Assert.True(ViewerReconnectPolicy.ShouldStartReconnect(
            connectedEvent: false,
            currentConnected: false,
            hasSuccessfulIntent: result.Accepted));
    }

    [Fact]
    public void OlderDeviceInfoCannotReplaceNewerQualifiedGeneration()
    {
        var qualification =
            new ViewerReconnectQualification();
        qualification.BeginManualConnection(
            CreateConnection());
        ViewerReconnectQualificationResult current =
            qualification.ObserveDeviceInfo(
                connectionGeneration: 44,
                reconnectAttemptOrdinal: 0);

        ViewerReconnectQualificationResult stale =
            qualification.ObserveDeviceInfo(
                connectionGeneration: 43,
                reconnectAttemptOrdinal: 0);

        Assert.False(stale.Accepted);
        Assert.True(qualification.IsCurrent(
            current.IntentId,
            connectionGeneration: 44));
        Assert.False(qualification.IsCurrent(
            current.IntentId,
            connectionGeneration: 43));
    }

    [Fact]
    public void DetachedOldIntentCannotQualifyUnownedReplacementGeneration()
    {
        var qualification =
            new ViewerReconnectQualification();
        qualification.BeginManualConnection(
            CreateConnection());
        ViewerReconnectQualificationResult current =
            qualification.ObserveDeviceInfo(
                connectionGeneration: 44,
                reconnectAttemptOrdinal: 0);

        ViewerReconnectQualificationResult unowned =
            qualification.ObserveDeviceInfo(
                connectionGeneration: 45,
                reconnectAttemptOrdinal: 0);

        Assert.False(unowned.Accepted);
        Assert.True(qualification.IsCurrent(
            current.IntentId,
            connectionGeneration: 44));
    }

    [Fact]
    public void ManualDisconnectFenceRejectsDelayedDeviceInfo()
    {
        var qualification =
            new ViewerReconnectQualification();
        qualification.BeginManualConnection(
            CreateConnection());
        qualification.CancelForManualDisconnect();

        ViewerReconnectQualificationResult delayed =
            qualification.ObserveDeviceInfo(
                connectionGeneration: 55,
                reconnectAttemptOrdinal: 0);

        Assert.False(delayed.Accepted);
        Assert.False(qualification.IsCurrent(
            intentId: 1,
            connectionGeneration: 55));
    }

    [Fact]
    public void ReconnectIntentIdAndAttemptOrdinalRemainFenced()
    {
        var qualification =
            new ViewerReconnectQualification();
        var connection = CreateConnection();
        qualification.SetActiveIntent(
            intentId: 9,
            connection);

        ViewerReconnectQualificationResult result =
            qualification.ObserveDeviceInfo(
                connectionGeneration: 61,
                reconnectAttemptOrdinal: 4);

        Assert.True(result.Accepted);
        Assert.False(result.IntentCreated);
        Assert.Equal(9, result.IntentId);
        Assert.Equal(4, result.ReconnectAttemptOrdinal);
        qualification.ClearActiveIntent(intentId: 9);
        Assert.False(qualification.IsCurrent(
            intentId: 9,
            connectionGeneration: 61));
    }
}
