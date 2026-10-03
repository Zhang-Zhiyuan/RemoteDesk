using System.Reflection;
using System.Runtime.InteropServices;
using RemoteDesk;

// Actual launcher/viewer on AdaptiveLayoutProbe's private desktop. No saved
// configuration, user desktop capture, network sessions or injected input.
internal static class ViewerWindowLifetimeProbe
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Verify(string output)
    {
        var checks = new List<object>();
        void Check(string name, bool passed)
        {
            checks.Add(new { name, passed });
            Program.Save(Path.Combine(output, "viewer-lifetime.json"), new { checks });
            if (!passed) throw new InvalidOperationException(name);
        }

        using var main = new MainForm(new RemoteDeskSettings(), Path.Combine(output, "logs"));
        using var icon = new NotifyIcon();
        typeof(MainForm).GetField("_notifyIcon", PrivateInstance)!.SetValue(main, icon);
        var trayOption = (CheckBox)typeof(MainForm).GetField("_minimizeToTrayBox", PrivateInstance)!.GetValue(main)!;
        trayOption.Checked = false;
        void OpenViewer() => typeof(MainForm).GetMethod("ShowViewerWindow", PrivateInstance)!.Invoke(main, null);
        main.Show(); Pump(); OpenViewer(); Pump();
        var viewer = (RemoteViewerWindow)typeof(MainForm).GetField("_viewerWindow", PrivateInstance)!.GetValue(main)!;
        Check("viewer has no managed or native owner", viewer.Owner is null && GetWindow(viewer.Handle, 4) == 0);
        Check("viewer has its own taskbar entry", viewer.ShowInTaskbar);
        using var ownedDialog = new Form { Text = "Owned-window negative control" };
        ownedDialog.Owner = main;
        ownedDialog.Show(); Pump();
        Program.Save(Path.Combine(output, "initial-state.json"), new {
            viewer.Visible, viewer.WindowState, nativeVisible = IsWindowVisible(viewer.Handle), iconic = IsIconic(viewer.Handle),
            style = GetWindowLongPtr(viewer.Handle, -16).ToInt64(), owner = GetWindow(viewer.Handle, 4).ToInt64(), mainStyle = GetWindowLongPtr(main.Handle, -16).ToInt64() });
        main.WindowState = FormWindowState.Minimized; Pump();
        Program.Save(Path.Combine(output, "minimize-state.json"), new {
            main.Visible, main.WindowState, viewerVisible = viewer.Visible, viewerState = viewer.WindowState,
            nativeVisible = IsWindowVisible(viewer.Handle), iconic = IsIconic(viewer.Handle),
            owner = GetWindow(viewer.Handle, 4).ToInt64(), viewer.IsDisposed });
        Check("owned-window negative control hides with launcher", !HasVisibleStyle(ownedDialog.Handle));
        Check("launcher minimize keeps viewer visible", HasVisibleStyle(viewer.Handle) && !IsIconic(viewer.Handle));
        ownedDialog.Close();
        main.WindowState = FormWindowState.Normal; Pump();
        trayOption.Checked = true;
        main.WindowState = FormWindowState.Minimized; Pump();
        Check("launcher tray hide keeps viewer visible", !main.Visible && HasVisibleStyle(viewer.Handle) && !IsIconic(viewer.Handle));

        viewer.WindowState = FormWindowState.Maximized; Pump();
        viewer.WindowState = FormWindowState.Minimized; Pump();
        OpenViewer(); Pump();
        Check("reopening existing viewer restores maximized placement", viewer.WindowState == FormWindowState.Maximized);
        Check("reopening viewer does not open launcher", !main.Visible);
        viewer.WindowState = FormWindowState.Normal; Pump();
        viewer.ToggleFullScreen(); Pump();
        typeof(MainForm).GetMethod("ShowFromTray", PrivateInstance)!.Invoke(main, null); Pump();
        main.WindowState = FormWindowState.Minimized; Pump();
        Check("fullscreen viewer survives launcher hide", viewer.FormBorderStyle == FormBorderStyle.None && HasVisibleStyle(viewer.Handle));
        viewer.ToggleFullScreen(); Pump();
        viewer.Close(); Pump();
        Check("closing viewer leaves launcher alive", !main.IsDisposed && typeof(MainForm).GetField("_viewerWindow", PrivateInstance)!.GetValue(main) is null);
        OpenViewer(); Pump();
        var secondViewer = (RemoteViewerWindow)typeof(MainForm).GetField("_viewerWindow", PrivateInstance)!.GetValue(main)!;
        main.Dispose(); Pump();
        Check("application disposal closes independent viewer", secondViewer.IsDisposed);
    }

    private static void Pump() { Application.DoEvents(); Thread.Sleep(20); Application.DoEvents(); }
    // IsWindowVisible also consults the deliberately invisible private station's
    // desktop. Test each real HWND's visibility style, including a negative control.
    private static bool HasVisibleStyle(nint window) => (GetWindowLongPtr(window, -16).ToInt64() & 0x10000000) != 0;
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
}
