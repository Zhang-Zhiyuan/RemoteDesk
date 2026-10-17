using System.Runtime.ExceptionServices;
using System.Reflection;
using Xunit;

namespace RemoteDesk.Tests;

[Collection("Relay key preview controls")]
public sealed class SshAliasUiTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellingSshDoesNotCancelRelayOrChangeViewerPauseClipboardAndTransferIntent(bool closeAll) => OnSta(() =>
    {
        using var main = new MainForm(new RemoteDeskSettings(), Path.Combine(AppContext.BaseDirectory, "ssh-ui-preview", Guid.NewGuid().ToString("N")));
        var client = Field<RemoteViewerClient>(main, "_viewerClient"); client.SetLocalInputPaused(true);
        var relayLifetime = Field<CancellationTokenSource>(main, "_relayOperationCancellation");
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(relayLifetime.Token);
        Set(main, "_sshOpening", pending);
        try
        {
        Set(main, "_pastingClipboardFilesFromMain", true); Set(main, "_pullingRemoteFilesFromMain", true);
        var key = Field<TextBox>(main, "_relayViewerPasswordBox"); key.Text = "desktop-key-draft";
        Field<Button>(main, "_sendClipboardButton").Enabled = false;
        Field<Button>(main, "_readClipboardButton").Enabled = true;
        Call(main, "UpdateRelayActionState");
        Assert.True(Field<Button>(main, "_sshTerminalButton").Enabled);
        Assert.False(Field<ToolStripMenuItem>(main, "_manageSshAliasesButton").Enabled);
        Assert.Contains("取消", Field<ToolTip>(main, "_toolTip").GetToolTip(Field<Button>(main, "_sshTerminalButton")));

        if (closeAll) Call(main, "CloseSshTerminals");
        else ((Task)Call(main, "OpenSshTerminalAsync")!).GetAwaiter().GetResult();

        Assert.True(pending.IsCancellationRequested); Assert.False(relayLifetime.IsCancellationRequested);
        Assert.True(client.IsLocalInputPaused); Assert.True(Field<bool>(main, "_pastingClipboardFilesFromMain"));
        Assert.True(Field<bool>(main, "_pullingRemoteFilesFromMain")); Assert.Equal("desktop-key-draft", key.Text);
        Assert.False(Field<Button>(main, "_sendClipboardButton").Enabled); Assert.True(Field<Button>(main, "_readClipboardButton").Enabled);
        Set(main, "_sshOpening", null); Call(main, "UpdateRelayActionState");
        Assert.True(Field<ToolStripMenuItem>(main, "_manageSshAliasesButton").Enabled); // Works offline, no selected device or configured relay.
        Assert.False(Field<ToolStripMenuItem>(main, "_configureSshAliasButton").Enabled);
        Assert.DoesNotContain("请先关闭", Field<ToolTip>(main, "_toolTip").GetToolTip(Field<Button>(main, "_relayLogoutButton")));
        }
        finally { Set(main, "_sshOpening", null); }
    });
    [Fact]
    public void LongAliasesCanBeDistinguishedAndDetailsFollowSelection() => OnSta(() =>
    {
        string prefix = new string('a', 63), user = new string('u', 128);
        SshCommandProfile first = new(Guid.NewGuid(), prefix + "b", "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", new string('B', 64), new string('A', 64), user, null);
        SshCommandProfile second = first with { Id = Guid.NewGuid(), Alias = prefix + "c", DeviceId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb" };
        using var dialog = MainForm.CreateSshAliasManagerDialog([second, first], SystemFonts.MessageBoxFont!, out var aliases);
        var detail = Assert.Single(Assert.IsType<TableLayoutPanel>(aliases.Parent).Controls.OfType<TextBox>());
        Assert.True(detail.ReadOnly); Assert.True(detail.Multiline); Assert.True(detail.WordWrap);
        Assert.Contains(first.Alias, detail.Text); Assert.Contains(first.DeviceId, detail.Text); Assert.Contains(user, detail.Text);
        aliases.SelectedIndex = 1;
        Assert.Contains(second.Alias, detail.Text); Assert.Contains(second.DeviceId, detail.Text);
        Assert.DoesNotContain(first.DeviceId, detail.Text);
    });
    [Fact]
    public void OfflineManagerUsesSavedMetadataAndEmptyManagerCannotRemove() => OnSta(() =>
    {
        SshCommandProfile profile = new(Guid.NewGuid(), "5090", "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", new string('B', 64), new string('A', 64), "fixture", null);
        using var dialog = MainForm.CreateSshAliasManagerDialog([profile], SystemFonts.MessageBoxFont!, out var aliases);
        Assert.Equal(ComboBoxStyle.DropDownList, aliases.DropDownStyle);
        Assert.Contains("5090", aliases.SelectedItem!.ToString());
        Assert.True(Assert.IsType<Button>(dialog.AcceptButton).Enabled);
        using var empty = MainForm.CreateSshAliasManagerDialog([], SystemFonts.MessageBoxFont!, out _);
        Assert.False(Assert.IsType<Button>(empty.AcceptButton).Enabled);
    });
    [Fact]
    public void ConfigurationDialogExplainsFileChangesWithoutCollectingSshPassword() => OnSta(() =>
    {
        using var dialog = MainForm.CreateSshAliasDialog("Owned device", SystemFonts.MessageBoxFont!, "gate-only", null, "5090",
            out var user, out var gate, out var identity, out var alias);
        Assert.Equal("5090", alias.Text); Assert.Equal(64, alias.MaxLength);
        Assert.Equal("gate-only", gate.Text); Assert.True(gate.UseSystemPasswordChar);
        Assert.False(user.UseSystemPasswordChar); Assert.False(identity.UseSystemPasswordChar); Assert.False(alias.UseSystemPasswordChar);
        var panel = Assert.IsType<TableLayoutPanel>(alias.Parent);
        Assert.Contains(panel.Controls.OfType<Label>(), label => label.Text.Contains(".ssh/config", StringComparison.Ordinal));
        Assert.Contains(panel.Controls.OfType<Label>(), label => label.Text.Contains("SSH 密码不保存", StringComparison.Ordinal));
        Assert.Equal("验证并配置别名", Assert.IsType<Button>(dialog.AcceptButton).Text);
    });

    [Fact]
    public void InvalidAliasDoesNotAcceptDialogAndValidAliasUsesExistingInputs() => OnSta(() =>
    {
        using var dialog = MainForm.CreateSshAliasDialog("Owned device", SystemFonts.MessageBoxFont!, "gate-only", null, "50*",
            out var user, out _, out _, out var alias);
        user.Text = "fixture";
        // Controls do not need to be shown or touch a live desktop to dispatch the
        // button handler; reflection avoids PerformClick's visibility filter.
        var click = typeof(Button).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        click.Invoke(dialog.AcceptButton, [EventArgs.Empty]);
        Assert.Equal(DialogResult.None, dialog.DialogResult);
        alias.Text = "5090";
        click.Invoke(dialog.AcceptButton, [EventArgs.Empty]);
        Assert.Equal(DialogResult.OK, dialog.DialogResult);
    });

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
    private static T Field<T>(MainForm form, string field) => (T)typeof(MainForm).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
    private static void Set(MainForm form, string field, object? value) => typeof(MainForm).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, value);
    private static object? Call(MainForm form, string method) => typeof(MainForm).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
}
