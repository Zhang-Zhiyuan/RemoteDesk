[CmdletBinding(DefaultParameterSetName = 'Controller')]
param(
    [Parameter(
        Mandatory,
        ParameterSetName = 'Controller')]
    [ValidateSet('Extend', 'Internal')]
    [string]$Mode,

    [Parameter(ParameterSetName = 'Controller')]
    [ValidateNotNullOrEmpty()]
    [string]$ExistingInteractiveTaskName = 'RemoteDeskCandidateHost',

    [Parameter(ParameterSetName = 'Controller')]
    [ValidatePattern('^\\(?:[^\\]+\\)*$')]
    [string]$ExistingInteractiveTaskPath = '\',

    [Parameter(ParameterSetName = 'Controller')]
    [ValidateRange(10, 120)]
    [int]$TaskTimeoutSeconds = 45,

    [Parameter(ParameterSetName = 'Controller')]
    [ValidateRange(3, 60)]
    [int]$TopologySettleTimeoutSeconds = 20,

    [Parameter(ParameterSetName = 'Controller')]
    [string]$ResultPath = '',

    [Parameter(ParameterSetName = 'Controller')]
    [switch]$ConfirmDisplayChange,

    [Parameter(
        Mandatory,
        ParameterSetName = 'Worker')]
    [switch]$Worker,

    [Parameter(
        Mandatory,
        ParameterSetName = 'Worker')]
    [ValidateSet('Extend', 'Internal')]
    [string]$WorkerMode,

    [Parameter(
        Mandatory,
        ParameterSetName = 'Worker')]
    [ValidateNotNullOrEmpty()]
    [string]$WorkerResultPath,

    [Parameter(ParameterSetName = 'Worker')]
    [ValidateRange(3, 60)]
    [int]$WorkerTopologySettleTimeoutSeconds = 20
)

<#
.SYNOPSIS
Applies one explicit Windows display topology and records before/after evidence.

.DESCRIPTION
The controller requires -ConfirmDisplayChange. It reads the principal from an
existing InteractiveToken scheduled task, creates a unique temporary task, and
runs this script's worker in the same logged-on desktop session. The worker
captures active WmiMonitorID records, current per-screen resolutions, and the
System.Windows.Forms Screen list before and after invoking DisplaySwitch.exe
with exactly /extend or /internal.

The existing scheduled task is read only. No credentials are accepted or
stored. The unique temporary task is stopped if necessary and unregistered in
finally; its transient worker result is removed after being copied into the
final JSON report.

.EXAMPLE
.\experiments\Invoke-RemoteDeskDisplayTopologyProbe.ps1 `
    -Mode Extend `
    -ConfirmDisplayChange `
    -ExistingInteractiveTaskName RemoteDeskCandidateHost `
    -ResultPath C:\Users\Example\Desktop\display-extend-result.json
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-JsonUtf8NoBom {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [object]$Value
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $parent = [IO.Path]::GetDirectoryName($fullPath)
    if ([string]::IsNullOrWhiteSpace($parent)) {
        throw "A parent directory could not be resolved for '$Path'."
    }

    if (-not [IO.Directory]::Exists($parent)) {
        [void][IO.Directory]::CreateDirectory($parent)
    }

    $json = $Value | ConvertTo-Json -Depth 14
    [IO.File]::WriteAllText(
        $fullPath,
        $json + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
}

function Convert-WmiMonitorText {
    param(
        [AllowNull()]
        [object]$Value
    )

    if ($null -eq $Value) {
        return ''
    }

    $characters = [Collections.Generic.List[char]]::new()
    foreach ($code in @($Value)) {
        if ([int]$code -eq 0) {
            break
        }

        $characters.Add([char][int]$code) | Out-Null
    }

    return -join $characters
}

function Initialize-DisplayTopologyCaptureType {
    Add-Type -AssemblyName System.Drawing -ErrorAction Stop
    Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
    Add-Type `
        -ReferencedAssemblies @(
            'System.Runtime',
            'System.Collections',
            'System.Drawing',
            'System.Drawing.Primitives',
            'System.Windows.Forms',
            'System.Runtime.InteropServices',
            'System.ComponentModel.Primitives'
        ) `
        -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public sealed class RemoteDeskDisplayRectangle
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

public sealed class RemoteDeskDisplayMode
{
    public bool Available { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int BitsPerPixel { get; set; }
    public int FrequencyHz { get; set; }
    public int PositionX { get; set; }
    public int PositionY { get; set; }
    public int Orientation { get; set; }
    public int FixedOutput { get; set; }
    public int DisplayFlags { get; set; }
    public string Error { get; set; }
}

public sealed class RemoteDeskDisplayScreen
{
    public string DeviceName { get; set; }
    public bool Primary { get; set; }
    public int BitsPerPixel { get; set; }
    public RemoteDeskDisplayRectangle Bounds { get; set; }
    public RemoteDeskDisplayRectangle WorkingArea { get; set; }
    public RemoteDeskDisplayMode CurrentMode { get; set; }
}

public static class RemoteDeskDisplayTopologyCapture
{
    private const int EnumCurrentSettings = -1;
    private const int ExpectedDevModeWSize = 220;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevModeW
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        public ushort SpecVersion;
        public ushort DriverVersion;
        public ushort Size;
        public ushort DriverExtra;
        public uint Fields;
        public int PositionX;
        public int PositionY;
        public uint DisplayOrientation;
        public uint DisplayFixedOutput;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TrueTypeOption;
        public short Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FormName;
        public ushort LogPixels;
        public uint BitsPerPixel;
        public uint PelsWidth;
        public uint PelsHeight;
        public uint DisplayFlags;
        public uint DisplayFrequency;
        public uint IcmMethod;
        public uint IcmIntent;
        public uint MediaType;
        public uint DitherType;
        public uint Reserved1;
        public uint Reserved2;
        public uint PanningWidth;
        public uint PanningHeight;
    }

    [DllImport(
        "user32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsW(
        string deviceName,
        int modeNumber,
        ref DevModeW deviceMode);

    public static int DevModeStructureSize
    {
        get { return Marshal.SizeOf(typeof(DevModeW)); }
    }

    public static RemoteDeskDisplayScreen[] Capture()
    {
        int structureSize = DevModeStructureSize;
        if (structureSize != ExpectedDevModeWSize)
        {
            throw new InvalidOperationException(
                "Unexpected DEVMODEW layout: " + structureSize +
                " bytes; expected " + ExpectedDevModeWSize + ".");
        }

        var result = new List<RemoteDeskDisplayScreen>();
        foreach (Screen screen in Screen.AllScreens)
        {
            result.Add(new RemoteDeskDisplayScreen
            {
                DeviceName = screen.DeviceName,
                Primary = screen.Primary,
                BitsPerPixel = screen.BitsPerPixel,
                Bounds = RectangleValue(screen.Bounds),
                WorkingArea = RectangleValue(screen.WorkingArea),
                CurrentMode = ReadCurrentMode(
                    screen.DeviceName,
                    structureSize)
            });
        }

        result.Sort(delegate(
            RemoteDeskDisplayScreen left,
            RemoteDeskDisplayScreen right)
        {
            return StringComparer.OrdinalIgnoreCase.Compare(
                left.DeviceName,
                right.DeviceName);
        });
        return result.ToArray();
    }

    private static RemoteDeskDisplayMode ReadCurrentMode(
        string deviceName,
        int structureSize)
    {
        var mode = new DevModeW();
        mode.Size = checked((ushort)structureSize);
        if (!EnumDisplaySettingsW(
                deviceName,
                EnumCurrentSettings,
                ref mode))
        {
            int error = Marshal.GetLastWin32Error();
            return new RemoteDeskDisplayMode
            {
                Available = false,
                Error = error == 0
                    ? "EnumDisplaySettingsW returned false."
                    : "EnumDisplaySettingsW failed with Win32 error " +
                        error + "."
            };
        }

        return new RemoteDeskDisplayMode
        {
            Available = true,
            Width = checked((int)mode.PelsWidth),
            Height = checked((int)mode.PelsHeight),
            BitsPerPixel = checked((int)mode.BitsPerPixel),
            FrequencyHz = checked((int)mode.DisplayFrequency),
            PositionX = mode.PositionX,
            PositionY = mode.PositionY,
            Orientation = checked((int)mode.DisplayOrientation),
            FixedOutput = checked((int)mode.DisplayFixedOutput),
            DisplayFlags = checked((int)mode.DisplayFlags),
            Error = String.Empty
        };
    }

    private static RemoteDeskDisplayRectangle RectangleValue(
        Rectangle rectangle)
    {
        return new RemoteDeskDisplayRectangle
        {
            X = rectangle.X,
            Y = rectangle.Y,
            Width = rectangle.Width,
            Height = rectangle.Height
        };
    }
}
'@ -ErrorAction Stop

    $size =
        [RemoteDeskDisplayTopologyCapture]::DevModeStructureSize
    if ($size -ne 220) {
        throw "Unexpected DEVMODEW structure size: $size."
    }
}

function Get-DisplayTopologySnapshot {
    $wmiMonitors = [Collections.Generic.List[object]]::new()
    foreach ($monitor in @(
            Get-CimInstance `
                -Namespace 'root\wmi' `
                -ClassName WmiMonitorID `
                -ErrorAction Stop |
                Where-Object { $_.Active } |
                Sort-Object InstanceName)) {
        $wmiMonitors.Add([pscustomobject]@{
            instanceName = [string]$monitor.InstanceName
            manufacturer =
                Convert-WmiMonitorText $monitor.ManufacturerName
            productCode =
                Convert-WmiMonitorText $monitor.ProductCodeID
            serialNumber =
                Convert-WmiMonitorText $monitor.SerialNumberID
            userFriendlyName =
                Convert-WmiMonitorText $monitor.UserFriendlyName
            weekOfManufacture = $monitor.WeekOfManufacture
            yearOfManufacture = $monitor.YearOfManufacture
            active = [bool]$monitor.Active
        }) | Out-Null
    }

    $screens = @(
        [RemoteDeskDisplayTopologyCapture]::Capture())
    return [pscustomobject]@{
        capturedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        sessionId =
            [Diagnostics.Process]::GetCurrentProcess().SessionId
        interactive = [Environment]::UserInteractive
        activeWmiMonitors = @($wmiMonitors)
        screens = @($screens)
    }
}

function Get-DisplayTopologyFingerprint {
    param(
        [Parameter(Mandatory)]
        [object]$Snapshot
    )

    return ([ordered]@{
        activeWmiMonitors = @($Snapshot.activeWmiMonitors)
        screens = @($Snapshot.screens)
    } | ConvertTo-Json -Compress -Depth 10)
}

function Invoke-DisplayTopologyWorker {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('Extend', 'Internal')]
        [string]$RequestedMode,

        [Parameter(Mandatory)]
        [string]$OutputPath,

        [Parameter(Mandatory)]
        [int]$SettleTimeoutSeconds
    )

    $startedAt = [DateTimeOffset]::UtcNow
    $before = $null
    $after = $null
    $displaySwitchStartedAt = $null
    $displaySwitchExitCode = $null
    $stableSamples = 0
    $workerError = ''

    try {
        if (-not [Environment]::UserInteractive -or
            [Diagnostics.Process]::GetCurrentProcess().SessionId -le 0) {
            throw (
                'The display topology worker is not running in an ' +
                'interactive desktop session.')
        }

        Initialize-DisplayTopologyCaptureType
        $displaySwitchPath = Join-Path `
            $env:SystemRoot `
            'System32\DisplaySwitch.exe'
        if (-not [IO.File]::Exists($displaySwitchPath)) {
            throw "DisplaySwitch.exe was not found at '$displaySwitchPath'."
        }

        $before = Get-DisplayTopologySnapshot
        $switchArgument = if ($RequestedMode -eq 'Extend') {
            '/extend'
        }
        else {
            '/internal'
        }
        $startInfo = [Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = $displaySwitchPath
        $startInfo.Arguments = $switchArgument
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $displaySwitchStartedAt = [DateTimeOffset]::UtcNow
        $process = [Diagnostics.Process]::Start($startInfo)
        if ($null -eq $process) {
            throw 'DisplaySwitch.exe did not return a process handle.'
        }

        try {
            if (-not $process.WaitForExit(15000)) {
                $process.Kill()
                [void]$process.WaitForExit(5000)
                throw 'DisplaySwitch.exe did not exit within 15 seconds.'
            }
            $displaySwitchExitCode = $process.ExitCode
        }
        finally {
            $process.Dispose()
        }
        if ($displaySwitchExitCode -ne 0) {
            throw (
                "DisplaySwitch.exe $switchArgument exited with code " +
                "$displaySwitchExitCode.")
        }

        $settleStartedAt = [DateTimeOffset]::UtcNow
        $settleDeadline =
            $settleStartedAt.AddSeconds($SettleTimeoutSeconds)
        $minimumObservationUntil = $settleStartedAt.AddSeconds(2)
        $lastFingerprint = ''
        do {
            Start-Sleep -Milliseconds 500
            $after = Get-DisplayTopologySnapshot
            $fingerprint =
                Get-DisplayTopologyFingerprint -Snapshot $after
            if ($fingerprint -eq $lastFingerprint) {
                $stableSamples++
            }
            else {
                $lastFingerprint = $fingerprint
                $stableSamples = 1
            }
        } while (
            ([DateTimeOffset]::UtcNow -lt
                $minimumObservationUntil -or
                $stableSamples -lt 3) -and
            [DateTimeOffset]::UtcNow -lt $settleDeadline)

        if ($stableSamples -lt 3) {
            throw (
                'The display topology did not remain stable for three ' +
                "samples within $SettleTimeoutSeconds seconds.")
        }
    }
    catch {
        $workerError = $_.Exception.Message
    }
    finally {
        if ($null -eq $after) {
            try {
                $after = Get-DisplayTopologySnapshot
            }
            catch {
                if ([string]::IsNullOrWhiteSpace($workerError)) {
                    $workerError =
                        'After-topology capture failed: ' +
                        $_.Exception.Message
                }
            }
        }

        $payload = [ordered]@{
            schemaVersion = 1
            worker = 'RemoteDeskDisplayTopology'
            success = [string]::IsNullOrWhiteSpace($workerError)
            requestedMode = $RequestedMode
            displaySwitchArgument = if ($RequestedMode -eq 'Extend') {
                '/extend'
            }
            else {
                '/internal'
            }
            startedAtUtc = $startedAt.ToString('O')
            displaySwitchStartedAtUtc =
                if ($null -ne $displaySwitchStartedAt) {
                    $displaySwitchStartedAt.ToString('O')
                }
                else {
                    ''
                }
            completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
            sessionId =
                [Diagnostics.Process]::GetCurrentProcess().SessionId
            interactive = [Environment]::UserInteractive
            identity =
                [Security.Principal.WindowsIdentity]::GetCurrent().Name
            displaySwitchExitCode = $displaySwitchExitCode
            stableSamples = $stableSamples
            before = $before
            after = $after
            error = $workerError
        }
        Write-JsonUtf8NoBom -Path $OutputPath -Value $payload
    }

    if (-not [string]::IsNullOrWhiteSpace($workerError)) {
        throw $workerError
    }
}

if ($PSCmdlet.ParameterSetName -eq 'Worker') {
    Invoke-DisplayTopologyWorker `
        -RequestedMode $WorkerMode `
        -OutputPath $WorkerResultPath `
        -SettleTimeoutSeconds $WorkerTopologySettleTimeoutSeconds
    return
}

function Get-ExactScheduledTask {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [string]$Path
    )

    return Get-ScheduledTask `
        -TaskName $Name `
        -TaskPath $Path `
        -ErrorAction SilentlyContinue
}

if ($env:OS -ne 'Windows_NT') {
    throw 'This display topology experiment is supported only on Windows.'
}

if (-not $ConfirmDisplayChange) {
    throw (
        'No state was changed. Pass -ConfirmDisplayChange to explicitly ' +
        "allow DisplaySwitch.exe for mode '$Mode'.")
}

if ([string]::IsNullOrWhiteSpace($PSCommandPath) -or
    -not [IO.File]::Exists($PSCommandPath)) {
    throw (
        'Run this probe from its .ps1 file; the interactive worker needs ' +
        'the absolute script path.')
}

$runId = [Guid]::NewGuid().ToString('N')
$temporaryTaskName =
    'RemoteDeskDisplayProbe-' + $runId.Substring(0, 12)
$temporaryTaskPath = '\'
$workerResultPath = Join-Path `
    ([IO.Path]::GetTempPath()) `
    ("RemoteDeskDisplayWorker-$runId.json")
if ([string]::IsNullOrWhiteSpace($ResultPath)) {
    $ResultPath = Join-Path `
        ([IO.Path]::GetTempPath()) `
        ("RemoteDeskDisplayProbe-$runId.json")
}
$resultPathFull = [IO.Path]::GetFullPath($ResultPath)

$startedAt = [DateTimeOffset]::UtcNow
$sourceTaskPrincipal = $null
$workerResult = $null
$failureMessage = ''
$cleanupErrors = [Collections.Generic.List[string]]::new()
$temporaryTaskWasFree = $false
$temporaryTaskRegistered = $false
$temporaryTaskRemoved = $false
$workerFileRemoved = $false

try {
    $sourceTask = Get-ExactScheduledTask `
        -Name $ExistingInteractiveTaskName `
        -Path $ExistingInteractiveTaskPath
    if ($null -eq $sourceTask) {
        throw (
            "Interactive source task '$ExistingInteractiveTaskPath" +
            "$ExistingInteractiveTaskName' was not found.")
    }

    $logonType = [string]$sourceTask.Principal.LogonType
    $principalUserId = [string]$sourceTask.Principal.UserId
    if ($logonType -notmatch 'Interactive' -or
        [string]::IsNullOrWhiteSpace($principalUserId)) {
        throw (
            "Task '$ExistingInteractiveTaskName' does not expose an " +
            "InteractiveToken principal (logonType='$logonType').")
    }
    $sourceTaskPrincipal = [ordered]@{
        userId = $principalUserId
        logonType = $logonType
        runLevel = [string]$sourceTask.Principal.RunLevel
    }

    $existingTemporaryTask = Get-ExactScheduledTask `
        -Name $temporaryTaskName `
        -Path $temporaryTaskPath
    if ($null -ne $existingTemporaryTask) {
        throw (
            "Refusing to replace pre-existing task '$temporaryTaskName'.")
    }
    $temporaryTaskWasFree = $true

    $escapedScriptPath = $PSCommandPath.Replace("'", "''")
    $escapedWorkerResultPath = $workerResultPath.Replace("'", "''")
    $workerCommand = (
        "& '$escapedScriptPath' -Worker " +
        "-WorkerMode '$Mode' " +
        "-WorkerResultPath '$escapedWorkerResultPath' " +
        "-WorkerTopologySettleTimeoutSeconds " +
        "$TopologySettleTimeoutSeconds")
    $encodedWorkerCommand = [Convert]::ToBase64String(
        [Text.Encoding]::Unicode.GetBytes($workerCommand))
    $powerShellPath = Join-Path `
        $env:SystemRoot `
        'System32\WindowsPowerShell\v1.0\powershell.exe'
    $action = New-ScheduledTaskAction `
        -Execute $powerShellPath `
        -Argument (
            '-NoLogo -NoProfile -NonInteractive ' +
            '-ExecutionPolicy Bypass -EncodedCommand ' +
            $encodedWorkerCommand)
    $settings = New-ScheduledTaskSettingsSet `
        -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries `
        -ExecutionTimeLimit (New-TimeSpan -Minutes 2) `
        -MultipleInstances IgnoreNew

    Register-ScheduledTask `
        -TaskName $temporaryTaskName `
        -TaskPath $temporaryTaskPath `
        -Action $action `
        -Principal $sourceTask.Principal `
        -Settings $settings `
        -Description (
            'One-shot RemoteDesk display-topology probe; automatically ' +
            'unregistered by its controller.') `
        -ErrorAction Stop | Out-Null
    $temporaryTaskRegistered = $true

    $registeredTask = Get-ExactScheduledTask `
        -Name $temporaryTaskName `
        -Path $temporaryTaskPath
    if ($null -eq $registeredTask -or
        [string]$registeredTask.Principal.UserId -ne $principalUserId -or
        [string]$registeredTask.Principal.LogonType -notmatch
            'Interactive') {
        throw (
            'The temporary task did not retain the expected ' +
            'InteractiveToken principal; no display change was requested.')
    }

    Start-ScheduledTask `
        -TaskName $temporaryTaskName `
        -TaskPath $temporaryTaskPath `
        -ErrorAction Stop

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TaskTimeoutSeconds)
    while (-not [IO.File]::Exists($workerResultPath) -and
        [DateTimeOffset]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 200
    }
    if (-not [IO.File]::Exists($workerResultPath)) {
        $taskInfo = Get-ScheduledTaskInfo `
            -TaskName $temporaryTaskName `
            -TaskPath $temporaryTaskPath `
            -ErrorAction SilentlyContinue
        $lastResult = if ($null -ne $taskInfo) {
            $taskInfo.LastTaskResult
        }
        else {
            'unavailable'
        }
        throw (
            "The interactive worker did not produce a result within " +
            "$TaskTimeoutSeconds seconds (LastTaskResult=$lastResult).")
    }

    $workerResult = Get-Content `
        -LiteralPath $workerResultPath `
        -Raw `
        -ErrorAction Stop |
        ConvertFrom-Json -ErrorAction Stop
    if ($workerResult.success -ne $true) {
        throw (
            'The interactive display worker failed: ' +
            [string]$workerResult.error)
    }
    if ($workerResult.requestedMode -ne $Mode -or
        $workerResult.interactive -ne $true -or
        $null -eq $workerResult.before -or
        $null -eq $workerResult.after) {
        throw (
            'The worker result did not prove the requested interactive ' +
            'before/after topology capture.')
    }

    $completionDeadline = [DateTimeOffset]::UtcNow.AddSeconds(5)
    do {
        $completedTask = Get-ExactScheduledTask `
            -Name $temporaryTaskName `
            -Path $temporaryTaskPath
        if ($null -eq $completedTask -or
            [string]$completedTask.State -ne 'Running') {
            break
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTimeOffset]::UtcNow -lt $completionDeadline)
    if ($null -ne $completedTask -and
        [string]$completedTask.State -eq 'Running') {
        throw (
            'The worker wrote its result but did not finish within five ' +
            'additional seconds.')
    }

    $completedTaskInfo = Get-ScheduledTaskInfo `
        -TaskName $temporaryTaskName `
        -TaskPath $temporaryTaskPath `
        -ErrorAction Stop
    if ($completedTaskInfo.LastTaskResult -ne 0) {
        throw (
            'The interactive worker completed with scheduled-task result ' +
            "$($completedTaskInfo.LastTaskResult).")
    }
}
catch {
    $failureMessage = $_.Exception.Message
}
finally {
    # The task name is unique and cleanup is attempted only after this run
    # proved the slot was initially unused. The existing source task is never
    # stopped, changed, or unregistered.
    if ($temporaryTaskWasFree) {
        try {
            $taskToRemove = Get-ExactScheduledTask `
                -Name $temporaryTaskName `
                -Path $temporaryTaskPath
            if ($null -ne $taskToRemove) {
                if ([string]$taskToRemove.State -eq 'Running') {
                    Stop-ScheduledTask `
                        -TaskName $temporaryTaskName `
                        -TaskPath $temporaryTaskPath `
                        -ErrorAction Stop
                }

                Unregister-ScheduledTask `
                    -TaskName $temporaryTaskName `
                    -TaskPath $temporaryTaskPath `
                    -Confirm:$false `
                    -ErrorAction Stop
            }

            $temporaryTaskRemoved =
                $null -eq (Get-ExactScheduledTask `
                    -Name $temporaryTaskName `
                    -Path $temporaryTaskPath)
        }
        catch {
            $cleanupErrors.Add(
                "Temporary task cleanup failed for '$temporaryTaskName': " +
                $_.Exception.Message) | Out-Null
        }
    }

    try {
        if ([IO.File]::Exists($workerResultPath)) {
            Remove-Item -LiteralPath $workerResultPath -Force
        }
        $workerFileRemoved = -not [IO.File]::Exists($workerResultPath)
    }
    catch {
        $cleanupErrors.Add(
            "Worker result cleanup failed for '$workerResultPath': " +
            $_.Exception.Message) | Out-Null
    }
}

if ($cleanupErrors.Count -gt 0 -and
    [string]::IsNullOrWhiteSpace($failureMessage)) {
    $failureMessage = $cleanupErrors -join '; '
}

$report = [ordered]@{
    schemaVersion = 1
    probe = 'RemoteDeskWindowsDisplayTopology'
    runId = $runId
    success = [string]::IsNullOrWhiteSpace($failureMessage)
    requestedMode = $Mode
    startedAtUtc = $startedAt.ToString('O')
    completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    sourceInteractiveTask = [ordered]@{
        name = $ExistingInteractiveTaskName
        path = $ExistingInteractiveTaskPath
        principal = $sourceTaskPrincipal
        modified = $false
    }
    temporaryTask = [ordered]@{
        name = $temporaryTaskName
        path = $temporaryTaskPath
        registered = $temporaryTaskRegistered
        removed = $temporaryTaskRemoved
    }
    displaySwitch = if ($null -ne $workerResult) {
        [ordered]@{
            argument = $workerResult.displaySwitchArgument
            exitCode = $workerResult.displaySwitchExitCode
            startedAtUtc = $workerResult.displaySwitchStartedAtUtc
            stableSamples = $workerResult.stableSamples
        }
    }
    else {
        $null
    }
    before = if ($null -ne $workerResult) {
        $workerResult.before
    }
    else {
        $null
    }
    after = if ($null -ne $workerResult) {
        $workerResult.after
    }
    else {
        $null
    }
    worker = $workerResult
    cleanup = [ordered]@{
        temporaryTaskRemoved = $temporaryTaskRemoved
        workerResultFileRemoved = $workerFileRemoved
        errors = @($cleanupErrors)
    }
    error = $failureMessage
}
Write-JsonUtf8NoBom -Path $resultPathFull -Value $report

$status = if ($report.success) { 'PASS' } else { 'FAIL' }
$beforeScreens = if ($null -ne $report.before) {
    @($report.before.screens).Count
}
else {
    0
}
$afterScreens = if ($null -ne $report.after) {
    @($report.after.screens).Count
}
else {
    0
}
Write-Output (
    "DISPLAY_TOPOLOGY_PROBE_RESULT status=$status mode=$Mode " +
    "path=$resultPathFull beforeScreens=$beforeScreens " +
    "afterScreens=$afterScreens")

if (-not $report.success) {
    throw (
        "Display topology probe failed: $failureMessage " +
        "(report: $resultPathFull)")
}
