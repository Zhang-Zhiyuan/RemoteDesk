[CmdletBinding(DefaultParameterSetName = 'Controller')]
param(
    [Parameter(ParameterSetName = 'Controller')]
    [ValidateNotNullOrEmpty()]
    [string]$ExistingInteractiveTaskName = 'RemoteDeskCandidateHost',

    [Parameter(ParameterSetName = 'Controller')]
    [ValidatePattern('^\\(?:[^\\]+\\)*$')]
    [string]$ExistingInteractiveTaskPath = '\',

    [Parameter(ParameterSetName = 'Controller')]
    [ValidateRange(10, 120)]
    [int]$TaskTimeoutSeconds = 30,

    [Parameter(ParameterSetName = 'Controller')]
    [ValidateRange(2, 60)]
    [int]$PostResetObservationSeconds = 10,

    [Parameter(ParameterSetName = 'Controller')]
    [ValidateRange(1, 60)]
    [int]$BaselineEventLookbackMinutes = 5,

    [Parameter(ParameterSetName = 'Controller')]
    [string]$ResultPath = '',

    [Parameter(ParameterSetName = 'Controller')]
    [switch]$AllowNoRemoteDeskProcess,

    [Parameter(ParameterSetName = 'Controller')]
    [switch]$ConfirmGpuReset,

    [Parameter(
        Mandatory,
        ParameterSetName = 'Worker')]
    [switch]$Worker,

    [Parameter(
        Mandatory,
        ParameterSetName = 'Worker')]
    [ValidateNotNullOrEmpty()]
    [string]$WorkerResultPath
)

<#
.SYNOPSIS
Performs one controlled Windows graphics-stack reset shortcut and records
RemoteDesk/GPU recovery evidence.

.DESCRIPTION
The controller must be run on the Windows host with -ConfirmGpuReset. It reads
the principal from an existing interactive scheduled task, creates a unique
temporary task with the same principal, and runs a short worker in the logged-on
desktop session. The worker sends Win+Ctrl+Shift+B with the 64-bit SendInput ABI.

The existing task is never changed. The temporary task is stopped (if needed)
and unregistered in finally. The worker's temporary JSON file is also removed.
The final JSON report retains the worker result plus RemoteDesk PID and Windows
GPU recovery-event snapshots from before and after the shortcut.

This script contains no credential handling. It is intended to be copied to and
invoked on a test host through the repository's existing key-based SSH workflow.

.EXAMPLE
.\experiments\Invoke-RemoteDeskGpuResetProbe.ps1 `
    -ConfirmGpuReset `
    -ExistingInteractiveTaskName RemoteDeskCandidateHost `
    -ResultPath C:\Users\Example\Desktop\gpu-reset-result.json
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

    $json = $Value | ConvertTo-Json -Depth 12
    [IO.File]::WriteAllText(
        $fullPath,
        $json + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
}

function Invoke-GpuResetWorker {
    param(
        [Parameter(Mandatory)]
        [string]$OutputPath
    )

    $startedAt = [DateTimeOffset]::UtcNow
    $workerError = ''
    $sendResult = $null

    try {
        if ([IntPtr]::Size -ne 8) {
            throw (
                'The GPU reset worker requires 64-bit PowerShell so the ' +
                'SendInput structure layout is unambiguous.')
        }

        if (-not [Environment]::UserInteractive -or
            [Diagnostics.Process]::GetCurrentProcess().SessionId -le 0) {
            throw (
                'The GPU reset worker is not running in an interactive ' +
                'desktop session.')
        }

        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

public static class RemoteDeskGpuResetSendInput
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const ushort VkLeftWindows = 0x5B;
    private const ushort VkLeftControl = 0xA2;
    private const ushort VkLeftShift = 0xA0;
    private const ushort VkB = 0x42;

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    // INPUT's native union is sized by MOUSEINPUT, which is 32 bytes on
    // x64. KEYBDINPUT itself is only 24 bytes, but shrinking the managed
    // union would make INPUT 32 bytes instead of the required native 40.
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    public sealed class Result
    {
        public int PointerSize { get; set; }
        public int InputStructureSize { get; set; }
        public uint KeyDownInputsSent { get; set; }
        public uint KeyUpInputsSent { get; set; }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(
        uint inputCount,
        [In] Input[] inputs,
        int inputSize);

    public static Result SendGraphicsResetShortcut()
    {
        int inputSize = Marshal.SizeOf(typeof(Input));
        if (IntPtr.Size != 8 || inputSize != 40)
        {
            throw new InvalidOperationException(
                "Unexpected x64 SendInput layout: pointer=" +
                IntPtr.Size + ", INPUT=" + inputSize + ".");
        }

        Input[] keyDown = new Input[]
        {
            Keyboard(VkLeftWindows, 0),
            Keyboard(VkLeftControl, 0),
            Keyboard(VkLeftShift, 0),
            Keyboard(VkB, 0)
        };
        Input[] keyUp = new Input[]
        {
            Keyboard(VkB, KeyEventKeyUp),
            Keyboard(VkLeftShift, KeyEventKeyUp),
            Keyboard(VkLeftControl, KeyEventKeyUp),
            Keyboard(VkLeftWindows, KeyEventKeyUp)
        };

        uint keyDownSent = 0;
        uint keyUpSent = 0;
        Exception sendFailure = null;
        try
        {
            keyDownSent = SendInput(
                (uint)keyDown.Length,
                keyDown,
                inputSize);
            if (keyDownSent != keyDown.Length)
            {
                throw NewSendInputException(
                    "key-down",
                    keyDown.Length,
                    keyDownSent);
            }

            Thread.Sleep(75);
        }
        catch (Exception error)
        {
            sendFailure = error;
        }
        finally
        {
            // Always release every modifier, even if a partial key-down
            // injection failed, so the interactive desktop cannot be left
            // with a logically stuck Win/Ctrl/Shift key.
            keyUpSent = SendInput(
                (uint)keyUp.Length,
                keyUp,
                inputSize);
        }

        if (sendFailure != null)
        {
            throw sendFailure;
        }

        if (keyUpSent != keyUp.Length)
        {
            throw NewSendInputException(
                "key-up",
                keyUp.Length,
                keyUpSent);
        }

        return new Result
        {
            PointerSize = IntPtr.Size,
            InputStructureSize = inputSize,
            KeyDownInputsSent = keyDownSent,
            KeyUpInputsSent = keyUpSent
        };
    }

    private static Input Keyboard(ushort virtualKey, uint flags)
    {
        return new Input
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = virtualKey,
                    ScanCode = 0,
                    Flags = flags,
                    Time = 0,
                    ExtraInfo = UIntPtr.Zero
                }
            }
        };
    }

    private static Exception NewSendInputException(
        string phase,
        int expected,
        uint actual)
    {
        int error = Marshal.GetLastWin32Error();
        string detail = error == 0
            ? "No Win32 error was reported."
            : new Win32Exception(error).Message;
        return new InvalidOperationException(
            "SendInput " + phase + " injection sent " + actual +
            " of " + expected + " inputs. " + detail);
    }
}
'@ -ErrorAction Stop

        $sendResult =
            [RemoteDeskGpuResetSendInput]::SendGraphicsResetShortcut()
    }
    catch {
        $workerError = $_.Exception.Message
    }

    $payload = [ordered]@{
        schemaVersion = 1
        worker = 'RemoteDeskGpuReset'
        success = [string]::IsNullOrWhiteSpace($workerError)
        startedAtUtc = $startedAt.ToString('O')
        completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
        interactive = [Environment]::UserInteractive
        identity =
            [Security.Principal.WindowsIdentity]::GetCurrent().Name
        pointerSize = if ($null -ne $sendResult) {
            $sendResult.PointerSize
        }
        else {
            [IntPtr]::Size
        }
        inputStructureSize = if ($null -ne $sendResult) {
            $sendResult.InputStructureSize
        }
        else {
            $null
        }
        keyDownInputsSent = if ($null -ne $sendResult) {
            $sendResult.KeyDownInputsSent
        }
        else {
            0
        }
        keyUpInputsSent = if ($null -ne $sendResult) {
            $sendResult.KeyUpInputsSent
        }
        else {
            0
        }
        error = $workerError
    }
    Write-JsonUtf8NoBom -Path $OutputPath -Value $payload

    if (-not [string]::IsNullOrWhiteSpace($workerError)) {
        throw $workerError
    }
}

if ($PSCmdlet.ParameterSetName -eq 'Worker') {
    Invoke-GpuResetWorker -OutputPath $WorkerResultPath
    return
}

function Get-RemoteDeskProcessSnapshot {
    $result = [Collections.Generic.List[object]]::new()
    foreach ($process in @(
            Get-Process -Name 'RemoteDesk' -ErrorAction SilentlyContinue)) {
        $startedAt = ''
        $imagePath = ''
        try {
            $startedAt = $process.StartTime.ToUniversalTime().ToString('O')
        }
        catch {
            $startedAt = ''
        }
        try {
            $imagePath = $process.Path
        }
        catch {
            $imagePath = ''
        }

        $result.Add([pscustomobject]@{
            id = $process.Id
            startedAtUtc = $startedAt
            responding = $process.Responding
            imagePath = $imagePath
        }) | Out-Null
    }

    return @($result)
}

function Get-GpuRecoveryEventSnapshot {
    param(
        [Parameter(Mandatory)]
        [DateTimeOffset]$StartTimeUtc,

        [Parameter(Mandatory)]
        [DateTimeOffset]$EndTimeUtc
    )

    $providerPattern =
        'display|nvlddmkm|amdkmdag|amdwddmg|igfx|dxgkrnl|gpu'
    $messagePattern =
        'reset|recover|stopped responding|xid|hang|tdr|device removed'
    try {
        $allEvents = @(
            Get-WinEvent -FilterHashtable @{
                LogName = 'System'
                StartTime = $StartTimeUtc.LocalDateTime
                EndTime = $EndTimeUtc.LocalDateTime
            } -ErrorAction Stop)
    }
    catch {
        if ($_.FullyQualifiedErrorId -match '^NoMatchingEventsFound') {
            return @()
        }

        throw
    }

    $result = [Collections.Generic.List[object]]::new()
    foreach ($eventItem in @(
            $allEvents |
                Where-Object {
                    $_.ProviderName -match $providerPattern -and
                    ($_.Id -eq 4101 -or
                        $_.Message -match $messagePattern)
                } |
                Sort-Object TimeCreated, RecordId)) {
        $message = [string]$eventItem.Message
        if ($message.Length -gt 2000) {
            $message = $message.Substring(0, 2000)
        }

        $result.Add([pscustomobject]@{
            recordId = $eventItem.RecordId
            timeCreatedUtc =
                $eventItem.TimeCreated.ToUniversalTime().ToString('O')
            id = $eventItem.Id
            provider = [string]$eventItem.ProviderName
            level = [string]$eventItem.LevelDisplayName
            message = $message
        }) | Out-Null
    }

    return @($result)
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
    throw 'This GPU reset experiment is supported only on Windows.'
}

if (-not $ConfirmGpuReset) {
    throw (
        'No state was changed. Pass -ConfirmGpuReset to explicitly allow ' +
        'one Win+Ctrl+Shift+B graphics-stack reset.')
}

if ([string]::IsNullOrWhiteSpace($PSCommandPath) -or
    -not [IO.File]::Exists($PSCommandPath)) {
    throw (
        'Run this probe from its .ps1 file; the interactive worker needs ' +
        'the absolute script path.')
}

$runId = [Guid]::NewGuid().ToString('N')
$temporaryTaskName =
    'RemoteDeskGpuResetProbe-' + $runId.Substring(0, 12)
$temporaryTaskPath = '\'
$workerResultPath = Join-Path `
    ([IO.Path]::GetTempPath()) `
    ("RemoteDeskGpuResetWorker-$runId.json")
if ([string]::IsNullOrWhiteSpace($ResultPath)) {
    $ResultPath = Join-Path `
        ([IO.Path]::GetTempPath()) `
        ("RemoteDeskGpuResetProbe-$runId.json")
}
$resultPathFull = [IO.Path]::GetFullPath($ResultPath)

$startedAt = [DateTimeOffset]::UtcNow
$resetRequestedAt = $null
$workerResult = $null
$beforeProcesses = @()
$afterProcesses = @()
$beforeGpuEvents = @()
$afterGpuEvents = @()
$sourceTaskPrincipal = $null
$failureMessage = ''
$cleanupErrors = [Collections.Generic.List[string]]::new()
$temporaryTaskWasFree = $false
$temporaryTaskRegistered = $false
$temporaryTaskRemoved = $false
$workerFileRemoved = $false

try {
    $beforeProcesses = @(Get-RemoteDeskProcessSnapshot)
    $beforeGpuEvents = @(
        Get-GpuRecoveryEventSnapshot `
            -StartTimeUtc $startedAt.AddMinutes(
                -$BaselineEventLookbackMinutes) `
            -EndTimeUtc $startedAt)
    if ($beforeProcesses.Count -eq 0 -and
        -not $AllowNoRemoteDeskProcess) {
        throw (
            'RemoteDesk is not running. No GPU reset was sent. Use ' +
            '-AllowNoRemoteDeskProcess only for an intentional graphics-' +
            'stack-only probe.')
    }

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
            "interactive-token principal (logonType='$logonType').")
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
        "-WorkerResultPath '$escapedWorkerResultPath'")
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
        -ExecutionTimeLimit (New-TimeSpan -Minutes 1) `
        -MultipleInstances IgnoreNew

    Register-ScheduledTask `
        -TaskName $temporaryTaskName `
        -TaskPath $temporaryTaskPath `
        -Action $action `
        -Principal $sourceTask.Principal `
        -Settings $settings `
        -Description (
            'One-shot RemoteDesk graphics-reset recovery probe; ' +
            'automatically unregistered by its controller.') `
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
            'The temporary task did not retain the expected interactive ' +
            'principal; no reset was requested.')
    }

    $resetRequestedAt = [DateTimeOffset]::UtcNow
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
            'The interactive SendInput worker failed: ' +
            [string]$workerResult.error)
    }
    if ($workerResult.pointerSize -ne 8 -or
        $workerResult.inputStructureSize -ne 40 -or
        $workerResult.keyDownInputsSent -ne 4 -or
        $workerResult.keyUpInputsSent -ne 4) {
        throw (
            'The worker result did not prove the required x64 SendInput ' +
            'layout and complete key-down/key-up injection.')
    }

    Start-Sleep -Seconds $PostResetObservationSeconds
}
catch {
    $failureMessage = $_.Exception.Message
}
finally {
    try {
        $afterProcesses = @(Get-RemoteDeskProcessSnapshot)
    }
    catch {
        $cleanupErrors.Add(
            'RemoteDesk after-snapshot failed: ' +
            $_.Exception.Message) | Out-Null
    }

    try {
        $afterGpuEvents = @(
            Get-GpuRecoveryEventSnapshot `
                -StartTimeUtc $startedAt `
                -EndTimeUtc ([DateTimeOffset]::UtcNow))
    }
    catch {
        $cleanupErrors.Add(
            'GPU after-snapshot failed: ' +
            $_.Exception.Message) | Out-Null
    }

    # The name is unique and is removed only when this run proved that the
    # slot was initially unused. This avoids touching any user-owned task.
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

$beforePids = @($beforeProcesses | ForEach-Object { $_.id })
$afterPids = @($afterProcesses | ForEach-Object { $_.id })
$preservedPids = @(
    $beforePids |
        Where-Object { $afterPids -contains $_ })
$report = [ordered]@{
    schemaVersion = 1
    probe = 'RemoteDeskWindowsGpuReset'
    runId = $runId
    success = [string]::IsNullOrWhiteSpace($failureMessage)
    startedAtUtc = $startedAt.ToString('O')
    completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    resetRequestedAtUtc = if ($null -ne $resetRequestedAt) {
        $resetRequestedAt.ToString('O')
    }
    else {
        ''
    }
    shortcut = 'Win+Ctrl+Shift+B'
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
    before = [ordered]@{
        remoteDeskProcesses = @($beforeProcesses)
        gpuRecoveryEvents = @($beforeGpuEvents)
    }
    worker = $workerResult
    after = [ordered]@{
        remoteDeskProcesses = @($afterProcesses)
        gpuRecoveryEventsSinceStart = @($afterGpuEvents)
        remoteDeskAvailable = $afterProcesses.Count -gt 0
        preservedProcessIds = $preservedPids
    }
    cleanup = [ordered]@{
        temporaryTaskRemoved = $temporaryTaskRemoved
        workerResultFileRemoved = $workerFileRemoved
        errors = @($cleanupErrors)
    }
    error = $failureMessage
}
Write-JsonUtf8NoBom -Path $resultPathFull -Value $report

$status = if ($report.success) { 'PASS' } else { 'FAIL' }
Write-Output (
    "GPU_RESET_PROBE_RESULT status=$status path=$resultPathFull " +
    "beforePids=$($beforePids -join ',') " +
    "afterPids=$($afterPids -join ',') " +
    "gpuEvents=$($afterGpuEvents.Count)")

if (-not $report.success) {
    throw (
        "GPU reset probe failed: $failureMessage " +
        "(report: $resultPathFull)")
}
