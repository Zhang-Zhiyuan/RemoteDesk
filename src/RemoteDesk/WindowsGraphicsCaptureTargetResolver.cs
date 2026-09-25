using System.Runtime.InteropServices;
using Vortice.DXGI;

namespace RemoteDesk;

internal sealed record WindowsGraphicsCaptureTarget(
    int MonitorIndex,
    int AdapterIndex,
    uint AdapterVendorId,
    string AdapterDescription,
    string DeviceName,
    Rectangle Bounds,
    bool IsMonitorOwningAdapter = true);

internal sealed record WindowsGraphicsMonitorIdentity(
    int MonitorIndex,
    nint MonitorHandle,
    string DeviceName,
    Rectangle Bounds);

internal sealed record WindowsGraphicsOutputIdentity(
    int OutputIndex,
    nint MonitorHandle,
    ModeRotation Rotation = ModeRotation.Identity);

internal sealed record WindowsGraphicsAdapterIdentity(
    int AdapterIndex,
    uint VendorId,
    string Description,
    IReadOnlyList<WindowsGraphicsOutputIdentity> Outputs);

internal sealed record WindowsDesktopDuplicationTarget(
    int AdapterIndex,
    int OutputIndex,
    uint AdapterVendorId,
    string AdapterDescription,
    string DeviceName,
    Rectangle Bounds,
    ModeRotation Rotation = ModeRotation.Identity)
{
    // DXGI supplies an unrotated texture. FFmpeg's scale_d3d11 does not
    // apply the desktop rotation, so only identity outputs can use this
    // direct path. WGC and the exact-coordinate GDI fallback are upright.
    public bool CanCaptureWithoutRotation => Rotation == ModeRotation.Identity;
}

internal static class WindowsGraphicsCaptureTargetResolver
{
    public static bool TryResolve(
        string deviceName,
        Rectangle expectedBounds,
        out WindowsGraphicsCaptureTarget? target,
        out string failureDetail)
    {
        target = null;
        if (!TryResolveCandidates(
                deviceName,
                expectedBounds,
                out IReadOnlyList<WindowsGraphicsCaptureTarget> candidates,
                out failureDetail))
        {
            return false;
        }

        target = candidates[0];
        return true;
    }

    public static bool TryResolveCandidates(
        string deviceName,
        Rectangle expectedBounds,
        out IReadOnlyList<WindowsGraphicsCaptureTarget> candidates,
        out string failureDetail)
    {
        return TryResolveCandidates(
            deviceName,
            expectedBounds,
            out candidates,
            out _,
            out failureDetail);
    }

    public static bool TryResolveCandidates(
        string deviceName,
        Rectangle expectedBounds,
        out IReadOnlyList<WindowsGraphicsCaptureTarget> candidates,
        out WindowsDesktopDuplicationTarget?
            desktopDuplicationTarget,
        out string failureDetail)
    {
        candidates = [];
        desktopDuplicationTarget = null;
        failureDetail = string.Empty;
        if (!OperatingSystem.IsWindows())
        {
            failureDetail =
                "Windows Graphics Capture monitor mapping requires Windows.";
            return false;
        }

        try
        {
            IReadOnlyList<WindowsGraphicsMonitorIdentity> monitors =
                EnumerateMonitors();
            IReadOnlyList<WindowsGraphicsAdapterIdentity> adapters =
                EnumerateAdapters();
            return TryResolveCandidatesFromInventory(
                deviceName,
                expectedBounds,
                monitors,
                adapters,
                out candidates,
                out desktopDuplicationTarget,
                out failureDetail);
        }
        catch (Exception ex)
        {
            failureDetail =
                $"Windows Graphics Capture target mapping failed: {ex.Message}";
            return false;
        }
    }

    internal static bool TryResolveFromInventory(
        string deviceName,
        Rectangle expectedBounds,
        IReadOnlyList<WindowsGraphicsMonitorIdentity> monitors,
        IReadOnlyList<WindowsGraphicsAdapterIdentity> adapters,
        out WindowsGraphicsCaptureTarget? target,
        out string failureDetail)
    {
        target = null;
        if (!TryResolveCandidatesFromInventory(
                deviceName,
                expectedBounds,
                monitors,
                adapters,
                out IReadOnlyList<WindowsGraphicsCaptureTarget> candidates,
                out failureDetail))
        {
            return false;
        }

        target = candidates[0];
        return true;
    }

    internal static bool TryResolveCandidatesFromInventory(
        string deviceName,
        Rectangle expectedBounds,
        IReadOnlyList<WindowsGraphicsMonitorIdentity> monitors,
        IReadOnlyList<WindowsGraphicsAdapterIdentity> adapters,
        out IReadOnlyList<WindowsGraphicsCaptureTarget> candidates,
        out string failureDetail)
    {
        return TryResolveCandidatesFromInventory(
            deviceName,
            expectedBounds,
            monitors,
            adapters,
            out candidates,
            out _,
            out failureDetail);
    }

    internal static bool TryResolveCandidatesFromInventory(
        string deviceName,
        Rectangle expectedBounds,
        IReadOnlyList<WindowsGraphicsMonitorIdentity> monitors,
        IReadOnlyList<WindowsGraphicsAdapterIdentity> adapters,
        out IReadOnlyList<WindowsGraphicsCaptureTarget> candidates,
        out WindowsDesktopDuplicationTarget?
            desktopDuplicationTarget,
        out string failureDetail)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        ArgumentNullException.ThrowIfNull(adapters);
        candidates = [];
        desktopDuplicationTarget = null;
        failureDetail = string.Empty;

        if (string.IsNullOrWhiteSpace(deviceName))
        {
            failureDetail =
                "Windows Graphics Capture requires a physical display device name.";
            return false;
        }

        WindowsGraphicsMonitorIdentity[] deviceMatches = monitors
            .Where(
                monitor => string.Equals(
                    monitor.DeviceName,
                    deviceName,
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (deviceMatches.Length != 1)
        {
            failureDetail = deviceMatches.Length == 0
                ? $"Windows monitor {deviceName} was not found."
                : $"Windows monitor {deviceName} is ambiguous.";
            return false;
        }

        WindowsGraphicsMonitorIdentity monitor = deviceMatches[0];
        if (monitor.Bounds != expectedBounds)
        {
            failureDetail =
                $"Windows monitor {deviceName} changed bounds from " +
                $"{expectedBounds.Width}x{expectedBounds.Height} at " +
                $"({expectedBounds.Left},{expectedBounds.Top}) to " +
                $"{monitor.Bounds.Width}x{monitor.Bounds.Height} at " +
                $"({monitor.Bounds.Left},{monitor.Bounds.Top}).";
            return false;
        }

        var outputMatches = adapters
            .SelectMany(
                adapter => adapter.Outputs.Select(
                    output => new
                    {
                        Adapter = adapter,
                        Output = output
                    }))
            .Where(
                match => match.Output.MonitorHandle ==
                    monitor.MonitorHandle)
            .ToArray();
        if (outputMatches.Length != 1)
        {
            failureDetail = outputMatches.Length == 0
                ? $"No DXGI adapter owns Windows monitor {deviceName}."
                : $"Multiple DXGI outputs claim Windows monitor {deviceName}.";
            return false;
        }

        WindowsGraphicsAdapterIdentity owningAdapter =
            outputMatches[0].Adapter;
        WindowsGraphicsOutputIdentity owningOutput =
            outputMatches[0].Output;
        if (owningAdapter.AdapterIndex < 0 ||
            owningOutput.OutputIndex < 0)
        {
            failureDetail =
                $"DXGI mapping for Windows monitor {deviceName} has a " +
                "negative adapter or output index.";
            return false;
        }

        desktopDuplicationTarget =
            new WindowsDesktopDuplicationTarget(
                owningAdapter.AdapterIndex,
                owningOutput.OutputIndex,
                owningAdapter.VendorId,
                owningAdapter.Description,
                monitor.DeviceName,
                monitor.Bounds,
                owningOutput.Rotation);

        // gfxcapture can create its D3D11 frame pool on a render adapter
        // that does not own the selected monitor. The measured hybrid
        // AMD-display/NVIDIA-render machine produces native BGRA WGC surfaces
        // directly into h264_nvenc on the NVIDIA adapter, while the owning
        // AMD adapter cannot negotiate that surface with AMF or MF. Prefer
        // every NVIDIA encode adapter deterministically, then retain the
        // monitor owner as the native-vendor fallback. The host validates
        // every candidate by waiting for a complete first access unit.
        WindowsGraphicsAdapterIdentity[] orderedAdapters = adapters
            .Where(
                adapter =>
                    adapter.VendorId ==
                    FfmpegDesktopH264Capture.NvidiaVendorId)
            .OrderBy(
                adapter =>
                    adapter.AdapterIndex ==
                    owningAdapter.AdapterIndex
                        ? 0
                        : 1)
            .ThenBy(adapter => adapter.AdapterIndex)
            .Concat([owningAdapter])
            .GroupBy(adapter => adapter.AdapterIndex)
            .Select(group => group.First())
            .ToArray();
        candidates = orderedAdapters
            .Select(
                adapter =>
                    new WindowsGraphicsCaptureTarget(
                        monitor.MonitorIndex,
                        adapter.AdapterIndex,
                        adapter.VendorId,
                        adapter.Description,
                        monitor.DeviceName,
                        monitor.Bounds,
                        IsMonitorOwningAdapter:
                            adapter.AdapterIndex ==
                            owningAdapter.AdapterIndex))
            .ToArray();
        return true;
    }

    private static IReadOnlyList<WindowsGraphicsMonitorIdentity>
        EnumerateMonitors()
    {
        var monitors = new List<WindowsGraphicsMonitorIdentity>();
        int monitorIndex = 0;
        bool enumerated = EnumDisplayMonitors(
            nint.Zero,
            nint.Zero,
            (monitor, _, _, _) =>
            {
                int currentMonitorIndex = monitorIndex++;
                var info = new MonitorInfoEx
                {
                    Size = Marshal.SizeOf<MonitorInfoEx>()
                };
                if (!GetMonitorInfoW(monitor, ref info))
                {
                    return true;
                }

                monitors.Add(
                    new WindowsGraphicsMonitorIdentity(
                        currentMonitorIndex,
                        monitor,
                        info.DeviceName,
                        Rectangle.FromLTRB(
                            info.Monitor.Left,
                            info.Monitor.Top,
                            info.Monitor.Right,
                            info.Monitor.Bottom)));
                return true;
            },
            nint.Zero);
        if (!enumerated)
        {
            throw new InvalidOperationException(
                $"EnumDisplayMonitors failed with Win32 error " +
                $"{Marshal.GetLastWin32Error()}.");
        }

        return monitors;
    }

    private static IReadOnlyList<WindowsGraphicsAdapterIdentity>
        EnumerateAdapters()
    {
        var adapters = new List<WindowsGraphicsAdapterIdentity>();
        using IDXGIFactory1 factory =
            DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint adapterIndex = 0; ; adapterIndex++)
        {
            var adapterResult = factory.EnumAdapters1(
                adapterIndex,
                out IDXGIAdapter1 adapter);
            if (adapterResult.Failure)
            {
                break;
            }

            using (adapter)
            {
                var outputs =
                    new List<WindowsGraphicsOutputIdentity>();
                for (uint outputIndex = 0; ; outputIndex++)
                {
                    var outputResult = adapter.EnumOutputs(
                        outputIndex,
                        out IDXGIOutput output);
                    if (outputResult.Failure)
                    {
                        break;
                    }

                    using (output)
                    {
                        OutputDescription outputDescription = output.Description;
                        outputs.Add(
                            new WindowsGraphicsOutputIdentity(
                                checked((int)outputIndex),
                                outputDescription.Monitor,
                                outputDescription.Rotation));
                    }
                }

                AdapterDescription1 description =
                    adapter.Description1;
                adapters.Add(
                    new WindowsGraphicsAdapterIdentity(
                        checked((int)adapterIndex),
                        description.VendorId,
                        description.Description,
                        outputs));
            }
        }

        return adapters;
    }

    private delegate bool MonitorEnumProc(
        nint monitor,
        nint deviceContext,
        nint monitorRectangle,
        nint data);

    [DllImport(
        "user32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(
        nint deviceContext,
        nint clipRectangle,
        MonitorEnumProc callback,
        nint data);

    [DllImport(
        "user32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(
        nint monitor,
        ref MonitorInfoEx info);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(
        LayoutKind.Sequential,
        CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public NativeRectangle Monitor;
        public NativeRectangle Work;
        public uint Flags;

        [MarshalAs(
            UnmanagedType.ByValTStr,
            SizeConst = 32)]
        public string DeviceName;
    }
}
