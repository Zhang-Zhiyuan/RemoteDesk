using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Xunit;

namespace RemoteDesk.Tests;

[Collection("Relay key preview controls")]
public sealed class MainConnectionActionTests
{
    [Theory]
    [InlineData("ToggleViewerAsync")]
    [InlineData("ToggleRelayViewerAsync")]
    public void EitherPageCanCancelAnAttemptWithoutCancellingPublicationOrAcceptingAnotherAttempt(string entry) => OnSta(() =>
    {
        using var ui = new Preview();
        var attempt = (CancellationTokenSource)Call(ui.Form, "BeginViewerConnectionAction", false)!;
        var token = attempt.Token;
        AssertButtons(ui.Form, "取消连接", true);
        Assert.False(Get<TextBox>(ui.Form, "_viewerHostBox").Enabled);
        Pump((Task)Call(ui.Form, entry)!);
        Assert.True(token.IsCancellationRequested);
        AssertButtons(ui.Form, "取消中…", false);
        Assert.False(Get<CancellationTokenSource>(ui.Form, "_relayOperationCancellation").IsCancellationRequested);
        Pump((Task)Call(ui.Form, entry)!);
        Assert.Same(attempt, Get<CancellationTokenSource>(ui.Form, "_viewerConnectionCancellation"));
        Pump((Task)Call(ui.Form, "CompleteCancelledViewerConnectionAsync")!);
        Call(ui.Form, "EndViewerConnectionAction", attempt);
        Assert.True(Get<TextBox>(ui.Form, "_viewerHostBox").Enabled);
        Assert.Equal("连接", Get<Button>(ui.Form, "_viewerToggleButton").Text);
        Assert.Null(Field("_viewerWindow").GetValue(ui.Form));
        Assert.Null(Field("_pendingViewerConnection").GetValue(ui.Form));
        Assert.False((bool)Call(ui.Form, "IsViewerReconnecting")!);
        var next = (CancellationTokenSource)Call(ui.Form, "BeginViewerConnectionAction", false)!;
        Assert.False(next.IsCancellationRequested);
        AssertButtons(ui.Form, "取消连接", true);
        Call(ui.Form, "EndViewerConnectionAction", next);
    });

    [Fact]
    public void ClosingPreviewCancelsAnAttemptAndRepeatedDisposeIsSafe() => OnSta(() =>
    {
        using var ui = new Preview();
        var attempt = (CancellationTokenSource)Call(ui.Form, "BeginViewerConnectionAction", false)!;
        ui.Form.Dispose();
        Assert.True(attempt.IsCancellationRequested);
        Call(ui.Form, "EndViewerConnectionAction", attempt);
        ui.Form.Dispose();
    });

    [Fact]
    public void LateCleanupCannotEnableInputsOrDisposeANewerAttempt() => OnSta(() =>
    {
        using var ui = new Preview();
        using var old = new CancellationTokenSource();
        var current = (CancellationTokenSource)Call(ui.Form, "BeginViewerConnectionAction", false)!;
        Call(ui.Form, "EndViewerConnectionAction", old);
        Assert.Same(current, Get<CancellationTokenSource>(ui.Form, "_viewerConnectionCancellation"));
        Assert.False(Get<TextBox>(ui.Form, "_viewerHostBox").Enabled);
        AssertButtons(ui.Form, "取消连接", true);
        Assert.False(current.IsCancellationRequested);
        Call(ui.Form, "EndViewerConnectionAction", current);
    });

    [Fact]
    public void DisconnectIsNotOfferedAsACancellableConnection() => OnSta(() =>
    {
        using var ui = new Preview();
        Assert.Null(Call(ui.Form, "BeginViewerConnectionAction", true));
        AssertButtons(ui.Form, "断开中…", false);
        Assert.True((bool)Call(ui.Form, "TryCancelViewerConnectionAction")!);
        Assert.Null(Field("_viewerConnectionCancellation").GetValue(ui.Form));
        Call(ui.Form, "EndViewerConnectionAction", new object?[] { null });
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManualCancelStopsAnActualUnresponsiveHandshakeAndClosesItsSocket(bool authenticated) => OnSta(() =>
    {
        using var ui = new Preview();
        using var client = new RemoteViewerClient(allowLocalConnectionsForTesting: true);
        Get<RemoteViewerClient>(ui.Form, "_viewerClient").Dispose();
        Field("_viewerClient").SetValue(ui.Form, client);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Get<TextBox>(ui.Form, "_viewerHostBox").Text = "127.0.0.1";
        Get<NumericUpDown>(ui.Form, "_viewerPortBox").Value = ((IPEndPoint)listener.LocalEndpoint).Port;
        Get<TextBox>(ui.Form, "_viewerPasswordBox").Text = "owned-fixture";
        var attempt = (CancellationTokenSource)Call(ui.Form, "BeginViewerConnectionAction", false)!;
        try
        {
            Task<TcpClient> accepted = listener.AcceptTcpClientAsync();
            Task connecting = (Task)Call(ui.Form, "ConnectViewerAsync", attempt.Token)!;
            Pump(accepted);
            using TcpClient peer = accepted.Result;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            SecureSession? session = null;
            if (authenticated)
            {
                // Authenticate but deliberately withhold DeviceInfo. This is
                // later than TCP/TLS setup, with a live receive loop to drain.
                Task<ServerAuthenticationResult> authentication = Protocol.AuthenticateServerDetailedAsync(
                    peer.GetStream(), "owned-fixture", timeout.Token);
                Pump(authentication);
                Assert.True(authentication.Result.IsAuthenticated);
                session = authentication.Result.Session!;
                var connected = Task.Run(async () =>
                {
                    while (!client.IsConnected) await Task.Delay(5, timeout.Token);
                });
                Pump(connected);
            }
            using var authenticatedSession = session;
            Assert.False(connecting.IsCompleted);
            Pump((Task)Call(ui.Form, "ToggleViewerAsync")!);
            Assert.ThrowsAny<OperationCanceledException>(() => Pump(connecting));
            Pump((Task)Call(ui.Form, "CompleteCancelledViewerConnectionAsync")!);
            Assert.False(client.IsConnected);
            // Drain any client capability packets and observe a real EOF; no
            // screen capture or system input is involved in either fixture.
            Pump(Task.Run(async () =>
            {
                var buffer = new byte[4096];
                while (await peer.GetStream().ReadAsync(buffer, timeout.Token) != 0) { }
            }));
            Assert.Null(Field("_viewerWindow").GetValue(ui.Form));
        }
        finally { Call(ui.Form, "EndViewerConnectionAction", attempt); }
    });

    [Fact]
    public void CancelledEndpointResolutionDoesNotProbeOrChangeTheTypedTarget() => OnSta(() =>
    {
        using var ui = new Preview();
        var host = Get<TextBox>(ui.Form, "_viewerHostBox");
        host.Text = "owned-fixture.invalid";
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            Pump((Task)Call(ui.Form, "ResolveViewerEndpointAsync", cancellation.Token)!));
        Assert.Equal("owned-fixture.invalid", host.Text);
    });

    private static void AssertButtons(MainForm form, string text, bool enabled)
    {
        foreach (string name in new[] { "_viewerToggleButton", "_relayConnectButton" })
        {
            Assert.Equal(text, Get<Button>(form, name).Text);
            Assert.Equal(enabled, Get<Button>(form, name).Enabled);
        }
    }
    private static FieldInfo Field(string name) => typeof(MainForm).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static T Get<T>(MainForm form, string name) => (T)Field(name).GetValue(form)!;
    private static object? Call(MainForm form, string name, params object?[] args) =>
        typeof(MainForm).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(form, args);
    private static void Pump(Task task)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(10))
        { Application.DoEvents(); Thread.Sleep(5); }
        Assert.True(task.IsCompleted, "Owned UI action did not finish within ten seconds.");
        task.GetAwaiter().GetResult();
    }
    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(25)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private sealed class Preview : IDisposable
    {
        private readonly string _identity = RemoteDeviceIdentity.LocalId;
        public MainForm Form { get; } = new(new RemoteDeskSettings(),
            Path.Combine(Path.GetTempPath(), "RemoteDeskConnectionUiTests", Guid.NewGuid().ToString("N")));
        public void Dispose() { Form.Dispose(); RemoteDeviceIdentity.LocalId = _identity; }
    }
}
