using System.Runtime.InteropServices;

// Managed PMv2 notification simulation, NOT a physical monitor-DPI change.
// .NET 8 explicitly supports the target DPI in BEFOREPARENT's wParam for tests:
// https://github.com/dotnet/winforms/blob/v8.0.0/src/System.Windows.Forms/src/System/Windows/Forms/Control.cs
// Native GetDpiForWindow is intentionally recorded separately and stays at the
// real monitor DPI. Native OS chrome/rasterization is not claimed as simulated.
internal static class SyntheticDpi
{
    internal static object Apply(Form form, int dpi)
    {
        if (dpi is < 48 or > 768) throw new ArgumentOutOfRangeException(nameof(dpi));
        Control[] descendants = Descendants(form).ToArray();
        foreach (Control control in descendants) _ = control.Handle;
        var previousHandles = descendants.ToDictionary(control => control, control => control.Handle);
        int oldDpi = form.DeviceDpi;
        Rectangle previous = form.Bounds;
        var suggested = new NativeRect(previous.Left, previous.Top,
            previous.Left + (int)Math.Round(previous.Width * (double)dpi / oldDpi),
            previous.Top + (int)Math.Round(previous.Height * (double)dpi / oldDpi));
        foreach (Control control in descendants.Reverse()) SendMessage(control.Handle, 0x02E2, dpi, 0);
        nint memory = Marshal.AllocHGlobal(Marshal.SizeOf<NativeRect>());
        try
        {
            Marshal.StructureToPtr(suggested, memory, false);
            SendMessage(form.Handle, 0x02E0, dpi | (dpi << 16), memory);
        }
        finally { Marshal.FreeHGlobal(memory); }
        foreach (Control control in descendants.Where(control => !control.IsDisposed))
            SendMessage(control.Handle, 0x02E3, dpi, 0);
        Pump();
        // A native handle recreated during scaling queries the unchanged real
        // monitor (e.g. 144), unlike an actual move where its new HWND would
        // already belong to the target DPI. Replay notifications only for such
        // proven recreated HWNDs; never conceal an ordinary missing child route.
        var recreated = descendants.Where(control => !control.IsDisposed &&
            control.Handle != previousHandles[control] && control.DeviceDpi != dpi &&
            control.DeviceDpi == NativeDpi(control)).ToArray();
        var recreationTrace = recreated.Select(control => new { type = control.GetType().Name,
            beforeReplayDpi = control.DeviceDpi, nativeDpi = NativeDpi(control), targetDpi = dpi,
            reason = "Recreated HWND queried unchanged physical monitor DPI; replay synthetic target notifications" }).ToArray();
        foreach (Control control in recreated.Reverse()) SendMessage(control.Handle, 0x02E2, dpi, 0);
        foreach (Control control in recreated) SendMessage(control.Handle, 0x02E3, dpi, 0);
        if (recreated.Length > 0) Pump();
        return new { requestedDpi = dpi, oldDpi, managedDpi = form.DeviceDpi, nativeDpi = NativeDpi(form),
            managedChildDpis = Descendants(form).Select(control => control.DeviceDpi).Distinct().Order().ToArray(),
            fontPoints = form.Font.SizeInPoints, form.Bounds, recreationTrace,
            simulation = "BEFOREPARENT bottom-up (explicit .NET test wParam), WM_DPICHANGED, AFTERPARENT top-down; physical monitor DPI unchanged" };
    }

    internal static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control descendant in Descendants(child)) yield return descendant;
        }
    }

    internal static uint NativeDpi(Control control) => GetDpiForWindow(control.Handle);
    internal static void Pump() { for (int i = 0; i < 6; i++) Application.DoEvents(); }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativeRect(int Left, int Top, int Right, int Bottom);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
}
