using System.Runtime.InteropServices;

namespace RemoteDesk;

internal static class WindowsSecureDesktopDisplays
{
    internal static IReadOnlyList<ScreenCaptureTarget> GetTargets()
    {
        var targets = new List<ScreenCaptureTarget>();
        MonitorCallback callback = (nint monitor, nint dc, ref NativeRect rectangle, nint data) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
            if (GetMonitorInfo(monitor, ref info))
            {
                Rectangle bounds = Rectangle.FromLTRB(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom);
                if (bounds.Width > 0 && bounds.Height > 0)
                    targets.Add(new(info.Device, info.Device, bounds, (info.Flags & 1) != 0));
            }
            return true;
        };
        WindowsSecureDesktopNative.Check(EnumDisplayMonitors(0, 0, callback, 0));
        if (targets.Count == 0) throw new InvalidOperationException("登录界面暂未提供屏幕。");
        Rectangle all = targets.Select(target => target.Bounds).Aggregate(Rectangle.Union);
        targets.Add(new(ScreenCaptureTarget.AllScreensId, "所有屏幕", all));
        return targets;
    }

    internal static Rectangle ResolveCaptureBounds(SecureDesktopRequest request, IReadOnlyList<ScreenCaptureTarget> targets)
    {
        if (request.CaptureTargetId is { } id)
            return targets.FirstOrDefault(target => string.Equals(target.Id, id, StringComparison.OrdinalIgnoreCase))?.Bounds
                ?? throw new ScreenCaptureTargetUnavailableException(id);
        Rectangle requested = new(request.Left, request.Top, request.Width, request.Height);
        if (!Contains(targets, requested)) throw new InvalidOperationException("登录界面屏幕范围已变化。");
        return requested;
    }

    internal static bool Contains(IReadOnlyList<ScreenCaptureTarget> targets, Rectangle bounds) =>
        targets.Any(target => target.Bounds.Contains(bounds));

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size; public NativeRect Monitor, Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    private delegate bool MonitorCallback(nint monitor, nint dc, ref NativeRect rectangle, nint data);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
