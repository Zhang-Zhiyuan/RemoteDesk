param(
    [Parameter(Mandatory)][int]$TaskProcessId,
    [ValidateSet('Inventory', 'Activate', 'Sample', 'Resize', 'Tabs', 'Permissions', 'RestoreBounds', 'Screenshot', 'AuditLayout')][string]$Action = 'Inventory',
    [ValidateRange(1, 10)][int]$Rounds = 2,
    [ValidateRange(-1, 2)][int]$TabIndex = -1,
    [int[]]$RestoreBounds,
    [ValidateRange(440, 1200)][int]$Height = 850,
    [string]$ScreenshotPath
)
$ErrorActionPreference = 'Stop'
$taskProcess = Get-Process -Id $TaskProcessId
if ($taskProcess.ProcessName -ne 'RemoteDesk') { throw 'Only a specified RemoteDesk process may be measured.' }

# Bounded, process-scoped UI checks. Never read edit-control contents, send
# keyboard input, change settings, or start/stop a host/viewer session.
# Use Windows PowerShell (powershell.exe) for UIAutomation-based actions.
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
public static class RemoteDeskUiMeasure {
    public struct Rect { public int Left, Top, Right, Bottom; }
    public sealed class Window {
        public long Handle; public string Title; public string ClassName;
        public bool Visible; public uint Dpi; public Rect Bounds;
    }
    public sealed class Sample { public double Milliseconds; public bool Completed; }
    private delegate bool EnumCallback(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, IntPtr state);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect bounds);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", SetLastError=true)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr hdc, uint flags);
    public static bool Render(long handle, IntPtr hdc) {
        IntPtr previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try { return PrintWindow(new IntPtr(handle), hdc, 2); }
        finally { SetThreadDpiAwarenessContext(previous); }
    }
    public static Window[] Find(int pid) {
        IntPtr previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try {
        var windows = new List<Window>();
        EnumWindows((window, state) => {
            uint owner; GetWindowThreadProcessId(window, out owner);
            if (owner != pid) return true;
            var title = new StringBuilder(256); var cls = new StringBuilder(256);
            GetWindowText(window, title, title.Capacity); GetClassName(window, cls, cls.Capacity);
            if (title.ToString() != "RemoteDesk") return true;
            Rect bounds; GetWindowRect(window, out bounds);
            if (bounds.Right - bounds.Left < 200) return true;
            windows.Add(new Window { Handle=window.ToInt64(), Title=title.ToString(), ClassName=cls.ToString(),
                Visible=IsWindowVisible(window), Dpi=GetDpiForWindow(window), Bounds=bounds });
            return true;
        }, IntPtr.Zero);
        return windows.ToArray();
        } finally { SetThreadDpiAwarenessContext(previous); }
    }
    public static void Activate(long handle) {
        if (!PostMessage(new IntPtr(handle), RegisterWindowMessage("RemoteDesk.ShowMainWindow.v1"), IntPtr.Zero, IntPtr.Zero))
            throw new InvalidOperationException("Activation failed.");
    }
    public static Sample Ping(long handle) {
        IntPtr result; var watch = Stopwatch.StartNew();
        bool completed = SendMessageTimeout(new IntPtr(handle), 0, IntPtr.Zero, IntPtr.Zero, 3, 1500, out result) != IntPtr.Zero;
        return new Sample { Milliseconds=watch.Elapsed.TotalMilliseconds, Completed=completed };
    }
    public static double Resize(long handle, Rect bounds, int width, int height) {
        IntPtr previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try {
            var watch = Stopwatch.StartNew();
            if (!SetWindowPos(new IntPtr(handle), IntPtr.Zero, bounds.Left, bounds.Top, width, height, 0x14))
                throw new InvalidOperationException("Resize failed.");
            return watch.Elapsed.TotalMilliseconds;
        } finally { SetThreadDpiAwarenessContext(previous); }
    }
}
'@

$taskWindows = @([RemoteDeskUiMeasure]::Find($TaskProcessId))
if ($Action -eq 'Inventory') { $taskWindows | ConvertTo-Json -Depth 5; return }
if ($taskWindows.Count -ne 1) { throw "Expected exactly one main window; found $($taskWindows.Count)." }
$taskWindow = $taskWindows[0]
if ($Action -eq 'RestoreBounds') {
    if ($RestoreBounds.Count -ne 4 -or $RestoreBounds[2] -lt 300 -or $RestoreBounds[3] -lt 200) { throw 'Supply explicit physical x, y, width, height.' }
    $bounds = $taskWindow.Bounds
    $bounds.Left = $RestoreBounds[0]; $bounds.Top = $RestoreBounds[1]
    [void][RemoteDeskUiMeasure]::Resize($taskWindow.Handle, $bounds, $RestoreBounds[2], $RestoreBounds[3])
    [RemoteDeskUiMeasure]::Find($TaskProcessId) | ConvertTo-Json -Depth 5
    return
}
if ($Action -eq 'Activate') {
    [RemoteDeskUiMeasure]::Activate($taskWindow.Handle)
    Start-Sleep -Milliseconds 500
    [RemoteDeskUiMeasure]::Find($TaskProcessId) | ConvertTo-Json -Depth 5
    return
}
if (!$taskWindow.Visible) { throw 'Show the main window with Activate before measuring it.' }
if ($Action -eq 'AuditLayout') {
    if (!$ScreenshotPath -or (Test-Path -LiteralPath $ScreenshotPath)) { throw 'Supply a new evidence directory.' }
    New-Item -ItemType Directory -Path $ScreenshotPath | Out-Null
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$taskWindow.Handle)
    $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::TabItem)
    $tabs = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($tabs.Count -ne 3) { throw 'Expected the three main-window tabs.' }
    $originalTab = @($tabs | Where-Object { $_.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected })[0]
    $bounds = $taskWindow.Bounds
    try {
        foreach ($logicalWidth in @(1120, 800, 640)) {
            [void][RemoteDeskUiMeasure]::Resize($taskWindow.Handle, $bounds, [int]($logicalWidth * $taskWindow.Dpi / 96), [int](640 * $taskWindow.Dpi / 96))
            for ($index = 0; $index -lt 3; $index++) {
                $tabs[$index].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
                Start-Sleep -Milliseconds 200
                & $PSCommandPath -TaskProcessId $TaskProcessId -Action Screenshot -ScreenshotPath (Join-Path $ScreenshotPath "main-$index-$logicalWidth.png")
            }
        }
    } finally {
        [void][RemoteDeskUiMeasure]::Resize($taskWindow.Handle, $bounds, $bounds.Right - $bounds.Left, $bounds.Bottom - $bounds.Top)
        $originalTab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    }
    return
}
if ($Action -eq 'Screenshot') {
    if (!$ScreenshotPath -or (Test-Path -LiteralPath $ScreenshotPath)) { throw 'Supply a new screenshot file path.' }
    Add-Type -AssemblyName System.Drawing
    $bitmap = [Drawing.Bitmap]::new($taskWindow.Bounds.Right - $taskWindow.Bounds.Left, $taskWindow.Bounds.Bottom - $taskWindow.Bounds.Top)
    try {
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $hdc = $graphics.GetHdc()
            try { if (![RemoteDeskUiMeasure]::Render($taskWindow.Handle, $hdc)) { throw 'PrintWindow failed.' } }
            finally { $graphics.ReleaseHdc($hdc) }
        } finally { $graphics.Dispose() }
        $bitmap.Save([IO.Path]::GetFullPath($ScreenshotPath), [Drawing.Imaging.ImageFormat]::Png)
    } finally { $bitmap.Dispose() }
    return
}

function Measure-UiSamples([int]$Count = 80) {
    $taskProcess.Refresh()
    $initialCpu = $taskProcess.TotalProcessorTime.TotalMilliseconds
    $sampleClock = [Diagnostics.Stopwatch]::StartNew()
    $samples = @(for ($i = 0; $i -lt $Count; $i++) {
        [RemoteDeskUiMeasure]::Ping($taskWindow.Handle)
        Start-Sleep -Milliseconds 40
    })
    $taskProcess.Refresh()
    $latencies = @($samples.Milliseconds | Sort-Object)
    [pscustomobject]@{
        count = $Count; timeouts = @($samples | Where-Object { !$_.Completed }).Count
        medianMs = $latencies[[int][Math]::Floor($Count / 2)]
        p95Ms = $latencies[[Math]::Min($Count - 1, [int][Math]::Ceiling($Count * .95) - 1)]
        maxMs = $latencies[-1]; cpuMs = $taskProcess.TotalProcessorTime.TotalMilliseconds - $initialCpu
        elapsedMs = $sampleClock.Elapsed.TotalMilliseconds; privateMB = $taskProcess.PrivateMemorySize64 / 1MB
    }
}

$results = [Collections.Generic.List[object]]::new()
if ($Action -eq 'Sample') { $results.Add((Measure-UiSamples)) }
if ($Action -eq 'Resize') {
    $originalResizeTab = $null
    if ($TabIndex -ge 0) {
        Add-Type -AssemblyName UIAutomationClient
        Add-Type -AssemblyName UIAutomationTypes
        $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$taskWindow.Handle)
        $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::TabItem)
        $resizeTabs = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($resizeTabs.Count -ne 3) { throw 'Expected the three main-window tabs.' }
        foreach ($tab in $resizeTabs) {
            $pattern = $tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
            if ($pattern.Current.IsSelected) { $originalResizeTab = $pattern }
        }
        $resizeTabs[$TabIndex].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    }
    $originalBounds = $taskWindow.Bounds
    $originalWidth = $originalBounds.Right - $originalBounds.Left
    $originalHeight = $originalBounds.Bottom - $originalBounds.Top
    try {
        for ($round = 0; $round -lt $Rounds; $round++) {
            foreach ($logicalWidth in @(1320, 1240, 1160, 1080, 1000, 920, 840, 760, 840, 920, 1000, 1080, 1160, 1240, 1320)) {
                $width = [int][Math]::Round($logicalWidth * $taskWindow.Dpi / 96)
                $resizeMs = [RemoteDeskUiMeasure]::Resize($taskWindow.Handle, $originalBounds, $width, $Height)
                $results.Add([pscustomobject]@{ width=$width; height=$Height; round=$round; resizeMs=$resizeMs; response=[RemoteDeskUiMeasure]::Ping($taskWindow.Handle) })
                Start-Sleep -Milliseconds 120
            }
        }
    } finally {
        [void][RemoteDeskUiMeasure]::Resize($taskWindow.Handle, $originalBounds, $originalWidth, $originalHeight)
        if ($null -ne $originalResizeTab) { $originalResizeTab.Select() }
    }
}
if ($Action -in @('Tabs', 'Permissions')) {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$taskWindow.Handle)
    $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::TabItem)
    $tabs = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($tabs.Count -ne 3) { throw 'Expected the three main-window tabs.' }
    $originalTab = $null
    foreach ($tab in $tabs) {
        $pattern = $tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        if ($pattern.Current.IsSelected) { $originalTab = $pattern }
    }
    try {
        if ($Action -eq 'Permissions') {
            $tabs[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            $expandName = ConvertFrom-Json '"\u88ab\u63a7\u6743\u9650\u8bbe\u7f6e\u2026"'
            $collapseName = ConvertFrom-Json '"\u6536\u8d77\u6743\u9650\u8bbe\u7f6e"'
            $buttonCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
            $buttons = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition)
            $button = @($buttons | Where-Object { $_.Current.Name -in @($expandName, $collapseName) })
            if ($button.Count -ne 1) { throw 'Permission panel toggle not uniquely identified.' }
            $button = $button[0]
            $originalCaption = $button.Current.Name
            try {
                for ($round = 0; $round -lt 4; $round++) {
                    $watch = [Diagnostics.Stopwatch]::StartNew()
                    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                    $results.Add([pscustomobject]@{ toggle=$round; invokeMs=$watch.Elapsed.TotalMilliseconds; samples=(Measure-UiSamples 20) })
                }
            } finally {
                if ($button.Current.Name -ne $originalCaption) {
                    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                }
            }
        } else {
        foreach ($tab in $tabs) {
            $pattern = $tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
            $watch = [Diagnostics.Stopwatch]::StartNew()
            $pattern.Select()
            $results.Add([pscustomobject]@{ tab=$tab.Current.Name; selectMs=$watch.Elapsed.TotalMilliseconds; samples=(Measure-UiSamples 100) })
        }
        }
    } finally { if ($null -ne $originalTab) { $originalTab.Select() } }
}
[pscustomobject]@{ pid=$TaskProcessId; action=$Action; window=$taskWindow; results=$results } | ConvertTo-Json -Depth 8
