using System.Reflection;
using System.Runtime.ExceptionServices;
using Xunit;

namespace RemoteDesk.Tests;

[Collection("Relay key preview controls")]
public sealed class MainViewerDpiLayoutTests
{
    [Fact]
    public void ParkedViewerCommandsRemainInOwningFormsDpiNotificationTree() => OnSta(() =>
    {
        using var client = new RemoteViewerClient();
        using var viewer = new RemoteViewerWindow(client, "Synthetic DPI controls", true, true, true, true, true, false);
        var overflow = Get<ViewerActionOverflow>(viewer, "_statusActionOverflow");
        var parked = Get<Panel>(overflow, "_overflowOwner");
        Assert.Same(viewer, parked.FindForm());
        Assert.Equal(Size.Empty, parked.Size);
        Assert.False(parked.TabStop);
        Assert.NotEqual(DockStyle.Fill, parked.Dock);
    });

    [Fact]
    public void SavedDeviceActionsWrapInsteadOfClippingAtLargeDpi() => OnSta(() =>
    {
        string? identity = RemoteDeviceIdentity.LocalId;
        try
        {
            using var main = new MainForm(new RemoteDeskSettings(), Path.Combine(Path.GetTempPath(), "RemoteDeskDpiPreview"));
            var edit = Get<Button>(main, "_editSavedDeviceRemarkButton");
            var actions = Assert.IsType<FlowLayoutPanel>(edit.Parent);
            Assert.True(actions.WrapContents);
            Assert.False(actions.AutoSize);
            // Detach only these owned preview controls so layout can run without
            // showing a window or changing any real settings/input/session.
            actions.Parent!.Controls.Remove(actions);
            using (actions)
            {
                foreach (Button button in actions.Controls) button.MinimumSize = new Size(200, 50);
                actions.Width = 300;
                MainForm.ConstrainWrappedActionRow(actions, actions.Width);
                actions.PerformLayout();
                Assert.All(actions.Controls.Cast<Control>(), control => Assert.True(actions.ClientRectangle.Contains(control.Bounds)));
                Assert.True(actions.Height >= 100);
            }
        }
        finally { RemoteDeviceIdentity.LocalId = identity; }
    });

    private static T Get<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
