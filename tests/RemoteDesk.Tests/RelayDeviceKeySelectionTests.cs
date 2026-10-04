using System.Reflection;
using System.Runtime.ExceptionServices;
using Xunit;

namespace RemoteDesk.Tests;

[CollectionDefinition("Relay key preview controls", DisableParallelization = true)]
public sealed class RelayKeyPreviewCollection;

[Collection("Relay key preview controls")]
public sealed class RelayDeviceKeySelectionTests
{
    [Fact]
    public void UserSelectingAnotherDeviceWhileDirectoryRequestIsPendingUsesItsKey() => RunSta(() =>
    {
        using var ui = new Preview();
        ui.Select(0);
        Assert.Equal("synthetic-key-A", ui.Key.Text);
        ui.SetRefreshPending(true);
        ui.Select(1);

        Assert.Equal("synthetic-key-B", ui.Key.Text);
        Assert.True(ui.Connect.Enabled);
        Assert.Equal("synthetic-key-B", ui.RequestSelectedKey());
        Assert.Contains("Device B", ui.Key.PlaceholderText);
    });

    [Fact]
    public void UnchangedDirectoryRefreshKeepsTheTypedKeyDraft() => RunSta(() =>
    {
        using var ui = new Preview();
        ui.Select(1);
        ui.Key.Text = "typed-key-draft";
        ui.SetRefreshPending(true);
        Assert.True(ui.Form.ApplyRelayDeviceList([ui.A with { LastSeenSeconds = 5 }, ui.B], 0));
        ui.FinishRefresh();

        Assert.Equal("typed-key-draft", ui.Key.Text);
        Assert.Equal("typed-key-draft", ui.RequestSelectedKey());
    });

    [Fact]
    public void TopologyReconciliationDoesNotClearOrReloadTheSelectedDevicesDraft() => RunSta(() =>
    {
        using var ui = new Preview();
        ui.Select(1);
        ui.Key.Text = "typed-key-draft";
        int selectionEvents = 0, keyChanges = 0;
        ui.List.SelectedIndexChanged += (_, _) => selectionEvents++;
        ui.Key.TextChanged += (_, _) => keyChanges++;
        ui.SetRefreshPending(true);
        Assert.True(ui.Form.ApplyRelayDeviceList([ui.B, ui.A, Preview.Device("C")], 0));
        ui.FinishRefresh();

        Assert.True(selectionEvents > 0, "Exercise real native selection events while rows are removed and restored.");
        Assert.Equal(0, keyChanges);
        Assert.Equal("typed-key-draft", ui.Key.Text);
        Assert.Same(ui.B, Assert.Single(ui.List.SelectedItems.Cast<ListViewItem>()).Tag);
    });

    [Fact]
    public void SelectedDeviceGoingOfflineClearsItsKeyAndDisablesConnect() => RunSta(() =>
    {
        using var ui = new Preview();
        ui.Select(1);
        ui.SetRefreshPending(true);
        Assert.True(ui.Form.ApplyRelayDeviceList([ui.A], 0));
        ui.FinishRefresh();

        Assert.Empty(ui.List.SelectedItems);
        Assert.Empty(ui.Key.Text);
        Assert.False(ui.Connect.Enabled);
    });

    [Fact]
    public void OldServerDirectoryCompletionCannotReplaceCurrentRowsOrKey() => RunSta(() =>
    {
        using var ui = new Preview();
        ui.Settings.Relay.ServerAddress = "new-relay.invalid";
        ui.SetConfigurationGeneration(1);
        Assert.True(ui.Form.ApplyRelayDeviceList([ui.B], 1));
        ui.Select(0);
        ui.Key.Text = "new-server-key-draft";
        ListViewItem current = ui.List.Items[0];

        Assert.False(ui.Form.ApplyRelayDeviceList([ui.A], 0));
        Assert.Same(current, Assert.Single(ui.List.Items.Cast<ListViewItem>()));
        Assert.Same(ui.B, current.Tag);
        Assert.Equal("new-server-key-draft", ui.Key.Text);
    });

    private static void RunSta(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            string previousLocalId = RemoteDeviceIdentity.LocalId;
            try { test(); }
            catch (Exception error) { failure = error; }
            finally { RemoteDeviceIdentity.LocalId = previousLocalId; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Synthetic relay selection controls timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class Preview : IDisposable
    {
        internal readonly RemoteDeskSettings Settings;
        internal readonly MainForm Form;
        internal readonly ListView List;
        internal readonly TextBox Key;
        internal readonly Button Connect;
        internal readonly RelayOnlineDevice A = Device("A");
        internal readonly RelayOnlineDevice B = Device("B");

        internal Preview()
        {
            var route = new RelayConnectionOptions("synthetic-relay.invalid", 56567,
                new string('T', 32), new string('A', 64), A.DeviceId);
            Settings = new RemoteDeskSettings { Relay = new RelaySettings {
                ServerAddress = route.ServerAddress, RelayPort = route.Port,
                DeviceId = "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee",
                ProtectedAccessToken = AppSettingsService.ProtectSecret(route.AccessToken),
                TlsCertificateSha256 = route.TlsCertificateSha256,
                DeviceKeys = RelayDeviceKeys.Remember(
                    RelayDeviceKeys.Remember([], route, AppSettingsService.ProtectSecret("synthetic-key-A")!),
                    route with { DeviceId = B.DeviceId }, AppSettingsService.ProtectSecret("synthetic-key-B")!)
            } };
            // Preview bypasses settings load/save, service startup and WireEvents.
            // Bind only the production selection callback to an unshown native list.
            Form = new MainForm(Settings, Path.Combine(AppContext.BaseDirectory, "relay-key-preview", Guid.NewGuid().ToString("N")));
            List = Field<ListView>("_relayDevicesList");
            Key = Field<TextBox>("_relayViewerPasswordBox");
            Connect = Field<Button>("_relayConnectButton");
            _ = List.Handle;
            var handler = (EventHandler)Method("RelayDeviceSelectionChanged").CreateDelegate(typeof(EventHandler), Form);
            List.SelectedIndexChanged += handler;
            Assert.True(Form.ApplyRelayDeviceList([A, B], 0));
        }

        internal static RelayOnlineDevice Device(string letter) => new(
            letter == "A" ? "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa" :
            letter == "B" ? "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb" : "cccccccc-cccc-4ccc-8ccc-cccccccccccc",
            "Device " + letter, "Windows", null, false, 0);

        internal void Select(int index) => List.Items[index].Selected = true;
        internal void SetRefreshPending(bool value) => SetField("_relayRefreshInProgress", value);
        internal void SetConfigurationGeneration(long value) => SetField("_relayConfigurationGeneration", value);
        internal void FinishRefresh()
        {
            SetRefreshPending(false);
            Method("SelectRelayDeviceKey").Invoke(Form, null);
            Method("UpdateRelayActionState").Invoke(Form, null);
        }
        internal string? RequestSelectedKey() => (string?)Method("RequestRelayDeviceKey").Invoke(Form,
            [Assert.Single(List.SelectedItems.Cast<ListViewItem>()).Tag]);
        private T Field<T>(string name) => (T)typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Form)!;
        private void SetField(string name, object value) => typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Form, value);
        private static MethodInfo Method(string name) => typeof(MainForm).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        public void Dispose() => Form.Dispose();
    }
}
