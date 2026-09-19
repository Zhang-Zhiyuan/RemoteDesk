using Xunit;

namespace RemoteDesk.Tests;

public sealed class OutgoingFileHeartbeatTests
{
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, true)]
    public void GraceIncludesOnlyPendingOperations(bool confirmation, bool returnedFiles, bool outgoingFiles, bool expected) =>
        Assert.Equal(expected, RemoteViewerClient.IsHeartbeatTimeoutSuppressed(confirmation, returnedFiles, outgoingFiles));

    [Fact]
    public void UploadGraceIsBoundedAndOrdinaryTimeoutUnchanged()
    {
        Assert.False(RemoteViewerClient.ShouldDisconnectForHeartbeat(TimeSpan.FromSeconds(18), false));
        Assert.True(RemoteViewerClient.ShouldDisconnectForHeartbeat(TimeSpan.FromSeconds(19), false));
        Assert.False(RemoteViewerClient.ShouldDisconnectForHeartbeat(TimeSpan.FromSeconds(149), true));
        Assert.True(RemoteViewerClient.ShouldDisconnectForHeartbeat(TimeSpan.FromSeconds(151), true));
    }

    [Fact]
    public void OldUploadCompletionCannotClearNewConnectionGrace()
    {
        using var viewer = new RemoteViewerClient(allowLocalConnectionsForTesting: true);
        using var oldConnection = new CancellationTokenSource();
        using var newConnection = new CancellationTokenSource();
        viewer.BeginOutgoingFileTransfer(oldConnection);
        Assert.True(viewer.IsOutgoingFileTransferPending(oldConnection));
        Assert.False(viewer.IsOutgoingFileTransferPending(newConnection));
        viewer.BeginOutgoingFileTransfer(newConnection);
        viewer.EndOutgoingFileTransfer(oldConnection);
        Assert.False(viewer.IsOutgoingFileTransferPending(oldConnection));
        Assert.True(viewer.IsOutgoingFileTransferPending(newConnection));
        viewer.EndOutgoingFileTransfer(newConnection);
        Assert.False(viewer.IsOutgoingFileTransferPending(newConnection));
    }

    [Fact]
    public void CancelledConnectionGetsNoUploadGrace()
    {
        using var viewer = new RemoteViewerClient(allowLocalConnectionsForTesting: true);
        using var connection = new CancellationTokenSource();
        viewer.BeginOutgoingFileTransfer(connection);
        connection.Cancel();
        Assert.False(viewer.IsOutgoingFileTransferPending(connection));
        viewer.EndOutgoingFileTransfer(connection);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(149999, true)]
    [InlineData(150000, false)]
    [InlineData(150001, false)]
    public void UnconfirmedDeliveryDrainHasIndependentFiniteDeadline(int milliseconds, bool expected) =>
        Assert.Equal(expected, RemoteViewerClient.IsOutgoingFileDeliveryDrainPending(TimeSpan.FromMilliseconds(milliseconds)));

    [Fact]
    public void SuccessfulUnconfirmedUploadKeepsOnlyItsOwnersDeliveryGrace()
    {
        using var viewer = new RemoteViewerClient(allowLocalConnectionsForTesting: true);
        using var connection = new CancellationTokenSource();
        using var other = new CancellationTokenSource();
        viewer.BeginOutgoingFileTransfer(connection);
        viewer.EndOutgoingFileTransfer(connection, pendingDelivery: true);
        Assert.True(viewer.IsOutgoingFileTransferPending(connection));
        Assert.False(viewer.IsOutgoingFileTransferPending(other));
        Assert.False(RemoteViewerClient.ShouldDisconnectForHeartbeat(TimeSpan.FromSeconds(19),
            viewer.IsOutgoingFileTransferPending(connection)));
        Assert.True(RemoteViewerClient.ShouldDisconnectForHeartbeat(TimeSpan.FromSeconds(151),
            viewer.IsOutgoingFileTransferPending(connection)));
        connection.Cancel();
        Assert.False(viewer.IsOutgoingFileTransferPending(connection));
    }

    [Fact]
    public void OldUnconfirmedCompletionCannotReplaceNewConnectionsActiveUpload()
    {
        using var viewer = new RemoteViewerClient(allowLocalConnectionsForTesting: true);
        using var oldConnection = new CancellationTokenSource();
        using var newConnection = new CancellationTokenSource();
        viewer.BeginOutgoingFileTransfer(oldConnection);
        viewer.BeginOutgoingFileTransfer(newConnection);
        viewer.EndOutgoingFileTransfer(oldConnection, pendingDelivery: true);
        Assert.True(viewer.IsOutgoingFileTransferPending(newConnection));
        Assert.False(viewer.IsOutgoingFileTransferPending(oldConnection));
        viewer.EndOutgoingFileTransfer(newConnection);
        Assert.False(viewer.IsOutgoingFileTransferPending(newConnection));
    }

    [Fact]
    public void NewUploadReplacesPriorDeliveryDrainAndAcknowledgedCompletionClearsIt()
    {
        using var viewer = new RemoteViewerClient(allowLocalConnectionsForTesting: true);
        using var connection = new CancellationTokenSource();
        viewer.BeginOutgoingFileTransfer(connection);
        viewer.EndOutgoingFileTransfer(connection, pendingDelivery: true);
        viewer.BeginOutgoingFileTransfer(connection);
        Assert.True(viewer.IsOutgoingFileTransferPending(connection));
        viewer.EndOutgoingFileTransfer(connection);
        Assert.False(viewer.IsOutgoingFileTransferPending(connection));
    }
}
