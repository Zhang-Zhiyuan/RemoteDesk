using System.Reflection;
using System.Runtime.ExceptionServices;
using Xunit;

namespace RemoteDesk.Tests;

[Collection("Relay key preview controls")]
public sealed class DirectDeviceKeySelectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectingDeviceWithoutSavedKeyDoesNotReusePreviousDevicesKey(bool saved) => RunSta(() =>
    {
        using var ui = new Preview();
        ui.Apply(ui.A);
        Assert.Equal("synthetic-A-key", ui.Key.Text);
        if (saved) ui.Settings.Viewer.RecentDevices.Add(ui.B);
        ui.Apply(ui.B);
        Assert.Empty(ui.Key.Text);
    });

    [Fact]
    public void RefreshingSameDevicePreservesTypedDraft() => RunSta(() =>
    {
        using var ui = new Preview();
        ui.Apply(ui.A);
        ui.Key.Text = "replacement-key-draft";
        int changes = 0;
        ui.Key.TextChanged += (_, _) => changes++;
        for (int i = 0; i < 20; i++) ui.Apply(ui.A);
        Assert.Equal("replacement-key-draft", ui.Key.Text);
        Assert.Equal(0, changes);
    });

    [Fact]
    public void KnownDeviceChangingAddressPreservesDraft() => RunSta(() =>
    {
        using var ui = new Preview();
        ui.Apply(ui.A);
        ui.Key.Text = "replacement-key-draft";
        ui.A.Address = "192.0.2.91";
        ui.A.Port = 40565;
        ui.Apply(ui.A);
        Assert.Equal("replacement-key-draft", ui.Key.Text);
    });

    [Fact]
    public void SameAddressWithDifferentIdentityDoesNotReuseKey() => RunSta(() =>
    {
        using var ui = new Preview();
        ui.Apply(ui.A);
        ui.B.Address = ui.A.Address;
        ui.Apply(ui.B);
        Assert.Empty(ui.Key.Text);
    });

    [Fact]
    public void SelectingAnotherSavedDeviceLoadsItsOwnKey() => RunSta(() =>
    {
        using var ui = new Preview();
        ui.B.ProtectedPassword = AppSettingsService.ProtectSecret("synthetic-B-key");
        ui.Settings.Viewer.RecentDevices.Add(ui.B);
        ui.Apply(ui.A);
        ui.Apply(ui.B);
        Assert.Equal("synthetic-B-key", ui.Key.Text);
        ui.Apply(ui.A);
        Assert.Equal("synthetic-A-key", ui.Key.Text);
    });

    [Fact]
    public void CorruptSavedKeyDoesNotLeavePreviousDevicesKey() => RunSta(() =>
    {
        using var ui = new Preview();
        ui.B.ProtectedPassword = "not-valid-protected-data";
        ui.Settings.Viewer.RecentDevices.Add(ui.B);
        ui.Apply(ui.A);
        ui.Apply(ui.B);
        Assert.Empty(ui.Key.Text);
    });

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            string previousId = RemoteDeviceIdentity.LocalId;
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally { RemoteDeviceIdentity.LocalId = previousId; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class Preview : IDisposable
    {
        internal readonly SavedRemoteDevice A = new() { Address = "192.0.2.10", Port = 56565,
            DeviceId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", MachineName = "Device A",
            ProtectedPassword = AppSettingsService.ProtectSecret("synthetic-A-key") };
        internal readonly SavedRemoteDevice B = new() { Address = "192.0.2.11", Port = 56565,
            DeviceId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb", MachineName = "Device B" };
        internal readonly RemoteDeskSettings Settings = new();
        private readonly MainForm _form;
        internal readonly TextBox Key;

        internal Preview()
        {
            Settings.Viewer.RecentDevices.Add(A);
            // Preview does not load/save settings, bind service events or start networking.
            _form = new MainForm(Settings, Path.Combine(AppContext.BaseDirectory,
                "direct-key-preview", Guid.NewGuid().ToString("N")));
            Key = (TextBox)typeof(MainForm).GetField("_viewerPasswordBox",
                BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_form)!;
        }

        internal void Apply(SavedRemoteDevice device) => typeof(MainForm)
            .GetMethod("ApplyRemoteDevice", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_form, [MainForm.RemoteDeviceListItem.FromSaved(device)]);

        public void Dispose() => _form.Dispose();
    }
}
