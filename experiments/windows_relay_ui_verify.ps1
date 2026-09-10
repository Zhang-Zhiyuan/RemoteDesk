param([Parameter(Mandatory=$true)][int]$TargetProcessId,
      [Parameter(Mandatory=$true)][string]$OutputDirectory,
      [switch]$ReturnToTray)
$ErrorActionPreference = 'Stop'
$product = Get-CimInstance Win32_Process -Filter "ProcessId=$TargetProcessId"
if (!$product -or $product.Name -ne 'RemoteDesk.exe' -or $product.CommandLine -like '*--secure-desktop-*') {
    throw 'Only an explicitly selected RemoteDesk GUI can be inspected.'
}
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
$null = [IO.Directory]::CreateDirectory($outputPath)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class RelayUiNative {
    public delegate bool EnumCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern uint RegisterWindowMessage(string text);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect bounds);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr window, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    public static IntPtr Find(int pid) {
        IntPtr found=IntPtr.Zero;
        EnumWindows((window, parameter) => {
            uint owner; GetWindowThreadProcessId(window, out owner);
            var text=new StringBuilder(512); GetWindowText(window, text, text.Capacity);
            if(owner==(uint)pid && text.ToString()=="RemoteDesk") { found=window; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@
$window = [RelayUiNative]::Find($TargetProcessId)
if ($window -eq [IntPtr]::Zero) { throw 'RemoteDesk main window not found.' }
$wasVisible = [RelayUiNative]::IsWindowVisible($window)
$previousForeground = [RelayUiNative]::GetForegroundWindow()
$previousTab = $null
try {
    $null = [RelayUiNative]::PostMessage($window, [RelayUiNative]::RegisterWindowMessage('RemoteDesk.ShowMainWindow.v1'), [IntPtr]::Zero, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 700
    # Showing a tray-hidden WinForms form recreates its HWND when ShowInTaskbar changes.
    $window = [RelayUiNative]::Find($TargetProcessId)
    if ($window -eq [IntPtr]::Zero) { throw 'Main window disappeared while activating.' }
    $root = [Windows.Automation.AutomationElement]::FromHandle($window)
    $tabs = $root.FindAll([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::TabItem))
    $relayTab = $null
    foreach ($tab in $tabs) {
        $selection = $tab.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern)
        if ($selection.Current.IsSelected) { $previousTab = $tab }
        if ($tab.Current.Name -eq '公网中继') { $relayTab = $tab }
    }
    if (!$relayTab) { throw ('Relay tab not found; observed: ' + (($tabs | ForEach-Object { $_.Current.Name }) -join ', ')) }
    $relayTab.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 1200
    $controls = $root.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
    $status = @()
    $option = $null
    foreach ($control in $controls) {
        $name = $control.Current.Name
        if ($name -like '自动优化双网卡中继线路*') { $option = $control }
        if ($name -like '中继临时线路*' -or $name -like '中继使用系统路由*') { $status += $name }
    }
    if (!$option -or $option.Current.IsOffscreen -or $status.Count -eq 0) { throw 'New routing controls/status are not visible.' }
    $bounds = [RelayUiNative+Rect]::new()
    $null = [RelayUiNative]::GetWindowRect($window, [ref]$bounds)
    $bitmap = [Drawing.Bitmap]::new($bounds.Right-$bounds.Left, $bounds.Bottom-$bounds.Top)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $hdc = $graphics.GetHdc()
        try { if (![RelayUiNative]::PrintWindow($window, $hdc, 2)) { throw 'Window capture failed.' } }
        finally { $graphics.ReleaseHdc($hdc) }
        $bitmap.Save((Join-Path $outputPath 'relay-page.png'), [Drawing.Imaging.ImageFormat]::Png)
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
    $report = [pscustomobject]@{ processId=$TargetProcessId; option=$option.Current.Name;
        enabled=($option.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState.ToString());
        status=$status; screenshot='relay-page.png'; scope='Product relay tab only; no credential values read or edited' }
    $report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputPath 'ui.json') -Encoding UTF8
    $report | ConvertTo-Json
}
finally {
    if ($previousTab) { $previousTab.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select() }
    $window = [RelayUiNative]::Find($TargetProcessId)
    # Never WM_CLOSE: users may have disabled close-to-tray in their preferences.
    if ((!$wasVisible -or $ReturnToTray) -and $window -ne [IntPtr]::Zero) { $null = [RelayUiNative]::ShowWindow($window, 0) }
    if ($previousForeground -ne [IntPtr]::Zero) { $null = [RelayUiNative]::SetForegroundWindow($previousForeground) }
}
