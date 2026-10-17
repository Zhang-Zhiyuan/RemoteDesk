using System.Reflection;
using System.Runtime.ExceptionServices;
using Xunit;

namespace RemoteDesk.Tests;

[Collection("Relay key preview controls")]
public sealed class SshMainFormTests
{
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
    public void HostProviderSnapshotDoesNotReadControlsAndDefaultsDisabled() => OnSta(() =>
    {
        using var main = CreatePreview();
        Field<TextBox>(main, "_hostPasswordBox").Text = "owned-host-key";
        Call(main, "ApplySshSettingsToUi"); // Preview deliberately skips the normal settings-load path.
        RelaySshHostOptions snapshot = Field<RelaySshHostOptions>(main, "_sshHostOptions");
        Assert.False(snapshot.Enabled);
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
