using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class ViewerFileOperationLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClosedViewerCannotConfirmFilesEvenWhenSharedConnectionRemainsAlive(bool disposed) => OnSta(() =>
    {
        using var connection = new ConnectedClient();
        using var viewer = new RemoteViewerWindow(connection.Client, "lifetime", false, false, true, false, false, false);
        CheckTarget(viewer, connection.Client.InputConnectionGeneration);
        if (disposed) viewer.Dispose();
        else typeof(RemoteViewerWindow).GetField("_isClosing", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewer, true);

        Assert.True(connection.Client.IsConnected);
        Assert.Throws<OperationCanceledException>(() => CheckTarget(viewer, connection.Client.InputConnectionGeneration));
    });

    [Fact]
    public void LiveViewerStillRejectsAChangedConnectionGeneration() => OnSta(() =>
    {
        using var connection = new ConnectedClient();
        using var viewer = new RemoteViewerWindow(connection.Client, "lifetime", false, false, true, false, false, false);
        CheckTarget(viewer, connection.Client.InputConnectionGeneration);
        Assert.Throws<IOException>(() => CheckTarget(viewer, connection.Client.InputConnectionGeneration + 1));
    });

    private static void CheckTarget(RemoteViewerWindow viewer, long generation)
    {
        try { typeof(RemoteViewerWindow).GetMethod("EnsureFileTargetCurrent", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(viewer, [generation]); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Synthetic viewer lifetime check timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class ConnectedClient : IDisposable
    {
        private readonly TcpClient _local = new();
        private readonly TcpClient _peer;
        internal RemoteViewerClient Client { get; } = new();

        internal ConnectedClient()
        {
            // A raw local socket pair only: no RemoteDesk handshake, session,
            // capture, input, clipboard or file operations are started.
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            _local.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            _peer = listener.AcceptTcpClient();
            Set("_tcpClient", _local);
            Set("_stream", _local.GetStream());
        }

        private void Set(string name, object? value) =>
            typeof(RemoteViewerClient).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Client, value);

        public void Dispose()
        {
            Set("_tcpClient", null);
            Set("_stream", null);
            Client.Dispose();
            _peer.Dispose();
            _local.Dispose();
        }
    }
}
