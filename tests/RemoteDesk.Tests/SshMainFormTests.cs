using System.Reflection;
using System.Runtime.ExceptionServices;
using Xunit;

namespace RemoteDesk.Tests;

[Collection("Relay key preview controls")]
public sealed class SshMainFormTests
{
    [Fact]
    public void DirectPickerTracksUnseenRoutedPeersWithoutLosingSelectionOnHeartbeatOrDhcp() => OnSta(() =>
    {
        using var main = CreatePreview();
        var a = Device with { MachineName = "Lab A", Platform = "Linux", DirectAddresses = ["192.0.2.12"], DirectPort = 56565 };
        var b = a with { DeviceId = Guid.NewGuid().ToString(), MachineName = "Lab B", DirectAddresses = ["192.0.2.13"] };
        Assert.True(main.ApplyRelayDeviceList([a,b], 0));
        var combo = Field<ComboBox>(main, "_discoveredHostsBox");
        var list = Field<ListBox>(main, "_discoveredHostsList");
        var host = Field<TextBox>(main, "_viewerHostBox");
        var key = Field<TextBox>(main, "_viewerPasswordBox");
        var target = list.Items.Cast<MainForm.RemoteDeviceListItem>().Single(i => i.DeviceId == a.DeviceId);
        Assert.Equal(2, list.Items.Count); Assert.Contains(target, combo.Items.Cast<object>());
        combo.SelectedIndex = -1; combo.SelectedItem = target;
        Assert.Equal("192.0.2.12", host.Text); Assert.Equal("owned-device-key", key.Text);
        key.Text = "unsaved-edit";
        Assert.True(main.ApplyRelayDeviceList([a with { LastSeenSeconds = 3 },b], 0));
        Assert.Same(target, combo.SelectedItem); Assert.Same(target, list.SelectedItem);
        a = a with { DirectAddresses = ["192.0.2.33"] };
        Assert.True(main.ApplyRelayDeviceList([a,b], 0));
        Assert.Equal("192.0.2.33", host.Text); Assert.Equal("unsaved-edit", key.Text);
        Assert.Contains("Linux", Field<Button>(main, "_directSshButton").Text);
        Assert.False(main.ApplyRelayDeviceList([], -1));
        Assert.Equal(2, list.Items.Count);
        Assert.True(main.ApplyRelayDeviceList([b], 0));
        Assert.Single(list.Items.Cast<object>()); Assert.Null(combo.SelectedItem); Assert.Empty(key.Text);
        Assert.Empty(Field<RemoteDeskSettings>(main, "_settings").Viewer.RecentDevices);
    });
    [Fact]
    public void DirectAndRelayShowSameMachineAndKeepWindowsAndWslRoutesDistinct() => OnSta(() =>
    {
        using var main = CreatePreview();
        var settings = Field<RemoteDeskSettings>(main, "_settings");
        const string address = "192.0.2.44";
        string staleId = Guid.NewGuid().ToString();
        settings.Viewer.RecentDevices = [new() { Address = address, Port = 40565, DeviceId = staleId, MachineName = "DESKTOP-LAB" }];
        var live = new DiscoveredHost("DESKTOP-LAB", address, 40565, "", true, false, "Windows",
            RemoteDeviceCapabilities.DeviceIdentity, DeviceId: Device.DeviceId) { WindowsSshPort = 2223 };
        var guest = new SshDestination(Guid.NewGuid().ToString(), "WSL · Ubuntu");
        var online = Device with { CanRename = true, SharedName = "Lab", MachineName = "Lab", SupportsSsh = true, WslDestinations = [guest] };
        Call(main, "UpdateDiscoveredHosts", new[] { live }, false);
        Assert.True(main.ApplyRelayDeviceList([online], 0));
        var list = Field<ListBox>(main, "_discoveredHostsList");
        var current = Assert.IsType<MainForm.RemoteDeviceListItem>(list.Items[0]);
        Assert.Equal("Lab", current.ListTitle);
        Assert.Contains("已选择 Lab", Field<Label>(main, "_viewerStatusLabel").Text);
        Assert.Equal("DESKTOP-LAB", current.MachineName);
        Assert.Contains("旧记录", Assert.IsType<MainForm.RemoteDeviceListItem>(list.Items[1]).ListTitle);
        Assert.Contains("Windows 原生", Field<Button>(main, "_directSshButton").Text);
        using var dialog = (Form)Call(main, "CreateDirectSshDialog")!;
        Assert.Contains("Lab", dialog.Text);
        var controls = dialog.Controls.OfType<TableLayoutPanel>().Single().Controls;
        var system = controls.OfType<ComboBox>().Single();
        var port = controls.OfType<TextBox>().Single(box => box.AccessibleName == "SSH 端口");
        Assert.Equal("Windows 原生", system.SelectedItem);
        Assert.Equal("2223", port.Text);
        Assert.Contains("192.0.2.44:2223", controls["sshEndpointPreview"]!.Text);
        Assert.True(((Button)dialog.AcceptButton!).Enabled);
        system.SelectedIndex = 1;
        Assert.Empty(port.Text);
        Assert.Contains("映射端口", controls["sshEndpointPreview"]!.Text);
        Assert.False(((Button)dialog.AcceptButton!).Enabled);
        port.Text = "2223";
        Assert.False(((Button)dialog.AcceptButton!).Enabled);
        port.Text = "2202";
        Assert.Contains("WSL", controls["sshEndpointPreview"]!.Text);
        Assert.True(((Button)dialog.AcceptButton!).Enabled);
        Assert.Contains("Ubuntu", controls["sshRelayDestination"]!.Text);
        Assert.True((bool)Call(main, "SelectRelaySshTarget", Device.DeviceId, guest.Id)!);
        Assert.Equal(guest, Field<ComboBox>(main, "_sshSystem").SelectedItem);
        Assert.False((bool)Call(main, "SelectRelaySshTarget", Device.DeviceId, Guid.NewGuid().ToString())!);
    });

    [Fact]
    public void AnAddressReusedByAnotherIdentityDoesNotInheritSshPreferences()
    {
        var old = new DirectSshPreference("192.0.2.44", true, new("wsl.example", 2202, "guest")) { DeviceId = Guid.NewGuid().ToString() };
        var known = old with { DeviceId = Device.DeviceId };
        var legacy = old with { DeviceId = null };
        Assert.Equal(known, Assert.Single(DirectSshPreference.ForDevice([old, legacy, known], "192.0.2.99", Device.DeviceId, true)));
        Assert.Empty(DirectSshPreference.ForDevice([old, legacy], "192.0.2.44", Device.DeviceId, true));
        Assert.Equal(legacy, Assert.Single(DirectSshPreference.ForDevice([old, legacy], "192.0.2.44", null, false)));
        Assert.Throws<ArgumentException>(() => MainForm.ValidateDirectSshSystem(false, new("192.0.2.44", null, ""), "192.0.2.44", 0));
        Assert.Throws<ArgumentException>(() => MainForm.ValidateDirectSshSystem(false, new("192.0.2.44", null, ""), "192.0.2.44", null, hasWsl: true));
        MainForm.ValidateDirectSshSystem(false, new("windows-alias", null, ""), "192.0.2.44", null, hasWsl: true);
    }
    [Fact]
    public void DirectSshKeepsSeparateDraftsUntilDialogCloses() => OnSta(() =>
    {
        using var main = CreatePreview();
        Field<TextBox>(main, "_viewerHostBox").Text = "192.0.2.1";
        var settings = Field<RemoteDeskSettings>(main, "_settings");
        settings.Viewer.DirectSshTargets = [new("192.0.2.1", false, new("saved.example", 22, "saved"))];
        using var dialog = (Form)Call(main, "CreateDirectSshDialog")!;
        var controls = dialog.Controls.OfType<TableLayoutPanel>().Single().Controls;
        var system = controls.OfType<ComboBox>().Single();
        var fields = controls.OfType<TextBox>().ToDictionary(box => box.AccessibleName!);
        var host = fields["SSH 主机 / IP"];
        var port = fields["SSH 端口"];
        var user = fields["系统账号"];
        Assert.Equal(("saved.example", "22", "saved"), (host.Text, port.Text, user.Text));
        (host.Text, port.Text, user.Text) = ("native.example", "", "native");
        system.SelectedIndex = 1;
        Assert.Equal(("192.0.2.1", "", ""), (host.Text, port.Text, user.Text));
        (host.Text, port.Text, user.Text) = ("guest.example", "222", "guest");
        system.SelectedIndex = 0;
        Assert.Equal(("native.example", "", "native"), (host.Text, port.Text, user.Text));
        system.SelectedIndex = 1;
        Assert.Equal(("guest.example", "222", "guest"), (host.Text, port.Text, user.Text));
        Assert.Equal("saved.example", Assert.Single(settings.Viewer.DirectSshTargets).Target.Host);
    });

    [Fact]
    public void StoppedWslSelectionDoesNotSilentlyChangeToWindows() => OnSta(() =>
    {
        using var main = CreatePreview();
        var distro = new WslSshTarget("bc42af8e-521a-4741-a7e3-71da214aff17", "Ubuntu");
        var guest = new SshDestination(distro.RouteId(Device.DeviceId), "WSL · Ubuntu");
        var online = Device with { SupportsSsh = true, WslDestinations = [guest] };
        Assert.Equal(Device.DeviceId, ((SshDestination)Call(main, "SelectedSshSystem", online)!).Id);
        var picker = Field<ComboBox>(main, "_sshSystem");
        picker.SelectedIndex = 1;
        Assert.Equal(guest, Call(main, "SelectedSshSystem", online));
        var stopped = Assert.IsType<SshDestination>(Call(main, "SelectedSshSystem", Device));
        Assert.Equal(guest.Id, stopped.Id);
        Assert.False(stopped.Available);
        Assert.Equal(guest, Call(main, "SelectedSshSystem", online));
        Assert.NotEqual(distro.RouteId(Guid.NewGuid().ToString()), guest.Id);
    });

    [Fact]
    public void SshOnlyBusyDeviceAllowsIndependentSshButNotDesktop() => OnSta(() =>
    {
        using var main = CreatePreview();
        var list = Field<ListView>(main, "_relayDevicesList");
        _ = list.Handle;
        var device = Device with { SupportsSsh = true, SupportsDesktop = false, Busy = true };
        main.ApplyRelayDeviceList([device], 0);
        list.Items[0].Selected = true;
        Call(main, "UpdateRelayActionState");
        Assert.True(Field<Button>(main, "_sshTerminalButton").Enabled);
        Assert.False(Field<Button>(main, "_relayConnectButton").Enabled);
        Assert.Null(MainForm.SshConnectionBlockReason(device));
        Assert.NotNull(MainForm.SshConnectionBlockReason(device with { SupportsSsh = false }));
    });

    [Fact]
    public void SshTargetCannotBorrowDifferentDesktopDevicesKeyDraft() => OnSta(() =>
    {
        using var main = CreatePreview();
        var route = new RelayConnectionOptions("owned.invalid", 56567, new string('t', 32), new string('A', 64), Device.DeviceId);
        Field<TextBox>(main, "_relayViewerPasswordBox").Text = "other-device-draft";
        typeof(MainForm).GetField("_relayKeySelection", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main,
            RelayDeviceKeys.Scope(route) + ":bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
        Assert.Equal("owned-device-key", Call(main, "GetSshDeviceKey", Device, route));
        typeof(MainForm).GetField("_relayKeySelection", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main,
            RelayDeviceKeys.Scope(route) + ":" + Device.DeviceId);
        Assert.Equal("other-device-draft", Call(main, "GetSshDeviceKey", Device, route));
    });

    [Fact]
    public void HostProviderSnapshotDoesNotReadControlsAndDefaultsEnabled() => OnSta(() =>
    {
        using var main = CreatePreview();
        Field<TextBox>(main, "_hostPasswordBox").Text = "owned-host-key";
        Call(main, "ApplySshSettingsToUi"); // Preview deliberately skips the normal settings-load path.
        RelaySshHostOptions snapshot = Field<RelaySshHostOptions>(main, "_sshHostOptions");
        Assert.True(snapshot.IsAvailable);
        Assert.Equal(22, snapshot.Port);
        Assert.Equal("owned-host-key", snapshot.DeviceKey);
        Field<TextBox>(main, "_hostPasswordBox").Text = "unpublished-change";
        Assert.Same(snapshot, Field<RelaySshHostOptions>(main, "_sshHostOptions"));
        Assert.Equal("owned-host-key", snapshot.DeviceKey);
    });

    [Fact]
    public void SshLoginDialogContainsNoSshPasswordFieldAndWrapsActions() => OnSta(() =>
    {
        using var dialog = MainForm.CreateSshConnectionDialog("Owned device", SystemFonts.MessageBoxFont!, "device-key", out var user, out var key);
        Assert.False(user.UseSystemPasswordChar);
        Assert.True(key.UseSystemPasswordChar);
        Assert.Equal("device-key", key.Text);
        var panel = Assert.IsType<TableLayoutPanel>(user.Parent);
        Assert.Single(panel.Controls.OfType<TextBox>()); // Only the required username is exposed.
        Assert.Equal("sshAdvancedSettings", key.Parent!.Name);
        Assert.False(Assert.Single(panel.Controls.OfType<CheckBox>()).Checked);
        var actions = Assert.Single(dialog.Controls.OfType<FlowLayoutPanel>());
        Assert.True(actions.WrapContents);
        Assert.Equal(DockStyle.Bottom, actions.Dock);
    });

    [Fact]
    public void SshDialogPrefillsOnlyConnectionPreferencesAndDeviceGateKey() => OnSta(() =>
    {
        var profile = new SshTerminalProfile("owned-alias", "fixture-user", Path.Combine(Path.GetTempPath(), "owned-key"));
        using var dialog = MainForm.CreateSshConnectionDialog("Owned device", SystemFonts.MessageBoxFont!, "device-key", profile,
            out var user, out var gate, out var identity);
        Assert.Equal(profile.Username, user.Text);
        Assert.Equal(profile.IdentityFile, identity.Text);
        Assert.Equal("device-key", gate.Text);
        Assert.False(identity.UseSystemPasswordChar);
    });

    [Fact]
    public void MissingDeviceKeyRemainsInRequiredFieldsAndMoreSettingsAreOptional() => OnSta(() =>
    {
        using var dialog = MainForm.CreateSshConnectionDialog("Owned device", SystemFonts.MessageBoxFont!, "", out var user, out var key);
        Assert.Same(user.Parent, key.Parent);
        var panel = Assert.IsType<TableLayoutPanel>(user.Parent);
        Assert.False(Assert.Single(panel.Controls.OfType<CheckBox>()).Checked);
        Assert.Equal("打开终端", Assert.IsType<Button>(dialog.AcceptButton).Text);
    });

    [Fact]
    public void TerminalOptionsDoNotCrowdPrimaryActions() => OnSta(() =>
    {
        using var main = CreatePreview();
        var primary = Field<Button>(main, "_sshTerminalButton");
        Assert.Equal("打开终端", primary.Text);
        Assert.Same(primary.Parent, Field<Button>(main, "_sshMoreButton").Parent);
        Assert.Contains(Field<ToolStripMenuItem>(main, "_configureSshAliasButton"), primary.ContextMenuStrip!.Items.Cast<ToolStripItem>());
        Assert.Contains(Field<ToolStripMenuItem>(main, "_sshLoginSettingsButton"), primary.ContextMenuStrip.Items.Cast<ToolStripItem>());
        Assert.Contains(Field<ToolStripMenuItem>(main, "_sshPairKeyButton"), primary.ContextMenuStrip.Items.Cast<ToolStripItem>());
    });

    [Fact]
    public void SshHostPortFillsItsLayoutCellAcrossDpiChanges() => OnSta(() =>
    {
        using var main = CreatePreview();
        var port = Field<NumericUpDown>(main, "_sshHostPortBox");
        Assert.Equal(DockStyle.Top, port.Dock);
        Assert.Equal(1, port.Minimum);
        Assert.Equal(65535, port.Maximum);
    });

    [Fact]
    public void DelayedRegistrationCannotReviveWithdrawnPublication() => OnSta(() =>
    {
        using var main = CreatePreview();
        RelayConnectionOptions route = PrepareSshRegistration(main);
        Assert.True(main.TryGetCurrentRelayHostRegistration(route, 0, out int port));
        Assert.Equal(0, port); // Pure SSH never advertises an old desktop port.
        Field<CheckBox>(main, "_relayRegisterHostBox").Checked = false;
        Assert.False(main.TryGetCurrentRelayHostRegistration(route, 0, out _));
    });

    [Fact]
    public void DelayedRegistrationCannotUseOldIdentityOrUnavailableServices() => OnSta(() =>
    {
        using var main = CreatePreview();
        RelayConnectionOptions route = PrepareSshRegistration(main);
        Assert.False(main.TryGetCurrentRelayHostRegistration(route with { AccessToken = new string('x', 32) }, 0, out _));
        Assert.False(main.TryGetCurrentRelayHostRegistration(route, -1, out _));
        typeof(MainForm).GetField("_sshHostOptions", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, RelaySshHostOptions.Disabled);
        Assert.False(main.TryGetCurrentRelayHostRegistration(route, 0, out _));
    });

    private static RelayConnectionOptions PrepareSshRegistration(MainForm main)
    {
        Field<CheckBox>(main, "_relayRegisterHostBox").Checked = true;
        typeof(MainForm).GetField("_sshHostOptions", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main,
            new RelaySshHostOptions(true, 22, "owned-host-key"));
        var settings = Field<RemoteDeskSettings>(main, "_settings");
        return (RelayConnectionOptions)Call(main, "CreateRelayOptions", settings.Relay.DeviceId)!;
    }

    private static readonly RelayOnlineDevice Device = new("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", "Owned target", "Windows", null, false, 0);
    private static MainForm CreatePreview()
    {
        var route = new RelayConnectionOptions("owned.invalid", 56567, new string('t', 32), new string('A', 64), Device.DeviceId);
        return new MainForm(new RemoteDeskSettings { Host = new HostSettings { ProtectedPassword = AppSettingsService.ProtectSecret("owned-host-key") },
            Relay = new RelaySettings { ServerAddress = route.ServerAddress, RelayPort = route.Port, TlsCertificateSha256 = route.TlsCertificateSha256,
                ProtectedAccessToken = AppSettingsService.ProtectSecret(route.AccessToken), DeviceId = "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee",
                DeviceKeys = RelayDeviceKeys.Remember([], route, AppSettingsService.ProtectSecret("owned-device-key")!) } },
            Path.Combine(AppContext.BaseDirectory, "ssh-preview", Guid.NewGuid().ToString("N")));
    }
    private static T Field<T>(object target, string name) => (T)typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static object? Call(object target, string name, params object[] args) => typeof(MainForm).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            string previous = RemoteDeviceIdentity.LocalId;
            try { action(); } catch (Exception failure) { error = failure; }
            finally { RemoteDeviceIdentity.LocalId = previous; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
