[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string]$Tag,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string]$ExpectedRemoteExeSha256,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$KnownHostsWsl,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9_][A-Za-z0-9._-]*@[A-Za-z0-9][A-Za-z0-9.-]*$')]
    [string]$SshTarget,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z]:\\Users\\[A-Za-z0-9_][A-Za-z0-9 ._-]*$')]
    [string]$RemoteWindowsProfile,

    [ValidateRange(1024, 65535)]
    [int]$Port = 40565,

    [ValidatePattern('^[A-Za-z0-9._-]*$')]
    [string]$ForegroundProcessToMinimize = '',

    [string]$ResultsDirectory = '',

    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($ResultsDirectory)) {
    $ResultsDirectory =
        Join-Path $root 'artifacts\entity-keyboard-short'
}
elseif (-not [System.IO.Path]::IsPathFullyQualified(
        $ResultsDirectory)) {
    $ResultsDirectory =
        Join-Path $root $ResultsDirectory
}
$ResultsDirectory =
    [System.IO.Path]::GetFullPath($ResultsDirectory)
New-Item -ItemType Directory -Path $ResultsDirectory -Force |
    Out-Null

function Invoke-RemoteEncodedPowerShell {
    param(
        [Parameter(Mandatory)]
        [string]$Script,

        [Parameter(Mandatory)]
        [string]$Marker
    )

    $encoded = [Convert]::ToBase64String(
        [Text.Encoding]::Unicode.GetBytes($Script))
    $output = @(
        & wsl.exe `
            -d Ubuntu-24.04 `
            --exec ssh `
            -p 2222 `
            -o "UserKnownHostsFile=$KnownHostsWsl" `
            -o StrictHostKeyChecking=yes `
            -o BatchMode=yes `
            $SshTarget `
            /mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe `
            -NoProfile `
            -NonInteractive `
            -EncodedCommand $encoded 2>&1)
    if ($LASTEXITCODE -ne 0) {
        $detail =
            if ([string]::Equals(
                    $Marker,
                    'RDSECRET:',
                    [StringComparison]::Ordinal)) {
                'Remote secret retrieval returned an error.'
            }
            else {
                ($output |
                    ForEach-Object { [string]$_ } |
                    Where-Object {
                        -not $_.StartsWith(
                            $Marker,
                            [StringComparison]::Ordinal)
                    } |
                    Select-Object -Last 4) -join ' '
            }
        throw (
            "Remote PowerShell command failed for marker $Marker " +
            "($detail)")
    }

    $markerLine = $output |
        ForEach-Object { [string]$_ } |
        Where-Object {
            $_.StartsWith(
                $Marker,
                [StringComparison]::Ordinal)
        } |
        Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($markerLine)) {
        throw "Remote marker $Marker was not returned."
    }

    return $markerLine.Substring($Marker.Length)
}

function ConvertFrom-RemoteBase64Json {
    param(
        [Parameter(Mandatory)]
        [string]$Value
    )

    return [Text.Encoding]::UTF8.GetString(
        [Convert]::FromBase64String($Value)) |
        ConvertFrom-Json
}

$testProject =
    Join-Path $root 'tests\RemoteDesk.Tests\RemoteDesk.Tests.csproj'
if (-not $SkipBuild) {
    & dotnet build $testProject -c Release --nologo
    if ($LASTEXITCODE -ne 0) {
        throw 'Release test build failed.'
    }
}

$remotePreflightScript = @"
`$ErrorActionPreference = 'Stop'
`$ProgressPreference = 'SilentlyContinue'
`$expectedHash =
    '$($ExpectedRemoteExeSha256.ToLowerInvariant())'
`$expectedPort = $Port
`$expectedForeground = '$ForegroundProcessToMinimize'
`$exe = '$RemoteWindowsProfile\Desktop\RemoteDesk.exe'
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class RemoteDeskKeyboardFocus {
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(
        IntPtr window,
        out uint processId);
}
'@
`$listeners = @(
    Get-NetTCPConnection -State Listen -LocalPort `$expectedPort -ErrorAction SilentlyContinue)
if (`$listeners.Count -ne 1) {
    throw "Expected exactly one TCP listener on port `$expectedPort."
}
`$ownerPid = [int]`$listeners[0].OwningProcess
`$hostProcess =
    Get-CimInstance Win32_Process -Filter "ProcessId=`$ownerPid"
if ([string]`$hostProcess.ExecutablePath -cne `$exe) {
    throw "Port `$expectedPort is not owned by the canonical executable."
}
`$actualHash =
    (Get-FileHash -LiteralPath `$exe -Algorithm SHA256).
        Hash.ToLowerInvariant()
if (`$actualHash -cne `$expectedHash) {
    throw "Remote executable SHA-256 does not match the release candidate."
}
`$foregroundWindow =
    [RemoteDeskKeyboardFocus]::GetForegroundWindow()
if (`$foregroundWindow -eq [IntPtr]::Zero) {
    throw 'The remote interactive session has no foreground window.'
}
[uint32]`$foregroundPid = 0
[void][RemoteDeskKeyboardFocus]::GetWindowThreadProcessId(
    `$foregroundWindow,
    [ref]`$foregroundPid)
`$foregroundProcess =
    Get-Process -Id `$foregroundPid -ErrorAction Stop
`$blockerProcess = `$null
if (-not [string]::IsNullOrWhiteSpace(
        `$expectedForeground)) {
    `$blockerCandidates = @(
        Get-Process -Name `$expectedForeground -ErrorAction SilentlyContinue |
            Where-Object {
                `$_.MainWindowHandle -ne 0
            })
    if (`$blockerCandidates.Count -ne 1) {
        throw (
            "Expected exactly one visible blocker process " +
            "`$expectedForeground; found " +
            "`$(`$blockerCandidates.Count).")
    }
    `$blockerProcess = `$blockerCandidates[0]
}
`$blockerPidValue = 0
`$blockerWindowValue = 0L
`$blockerProcessValue = ''
`$blockerStartFileTimeValue = 0L
`$blockerWasMinimized = `$false
if (`$null -ne `$blockerProcess) {
    `$blockerPidValue = `$blockerProcess.Id
    `$blockerWindowValue =
        `$blockerProcess.MainWindowHandle.ToInt64()
    `$blockerProcessValue =
        [string]`$blockerProcess.ProcessName
    `$blockerStartFileTimeValue =
        `$blockerProcess.StartTime.ToUniversalTime().ToFileTimeUtc()
    `$blockerWasMinimized =
        [RemoteDeskKeyboardFocus]::IsIconic(
            `$blockerProcess.MainWindowHandle)
}
`$result = [pscustomobject]@{
    computerName = `$env:COMPUTERNAME
    port = `$expectedPort
    hostProcessId = `$ownerPid
    executable = `$exe
    executableSha256 = `$actualHash
    foregroundProcessId = [uint32]`$foregroundPid
    foregroundWindow = `$foregroundWindow.ToInt64()
    foregroundProcess =
        [string]`$foregroundProcess.ProcessName
    foregroundTitle =
        [string]`$foregroundProcess.MainWindowTitle
    blockerProcessId = `$blockerPidValue
    blockerWindow = `$blockerWindowValue
    blockerProcess = `$blockerProcessValue
    blockerStartFileTime = `$blockerStartFileTimeValue
    blockerWasMinimized = `$blockerWasMinimized
}
`$json = `$result | ConvertTo-Json -Compress
[Console]::Out.WriteLine(
    'RDKBPRE:' +
    [Convert]::ToBase64String(
        [Text.Encoding]::UTF8.GetBytes(`$json)))
"@

$preflight = ConvertFrom-RemoteBase64Json (
    Invoke-RemoteEncodedPowerShell `
        -Script $remotePreflightScript `
        -Marker 'RDKBPRE:')
$preflightPath =
    Join-Path $ResultsDirectory "$Tag.preflight.json"
$preflight |
    ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath $preflightPath -Encoding UTF8

$foregroundPid = [uint32]$preflight.foregroundProcessId
$foregroundWindow = [long]$preflight.foregroundWindow
$foregroundProcess = [string]$preflight.foregroundProcess
$blockerPid = [uint32]$preflight.blockerProcessId
$blockerWindow = [long]$preflight.blockerWindow
$blockerProcess = [string]$preflight.blockerProcess
$blockerStartFileTime =
    [long]$preflight.blockerStartFileTime
$blockerWasMinimized =
    [bool]$preflight.blockerWasMinimized
$blockerWasMinimizedLiteral =
    if ($blockerWasMinimized) {
        '$true'
    }
    else {
        '$false'
    }
$focusChangeAttempted = $true
$keyboardToken =
    [Guid]::NewGuid().ToString('N')
$guardianPid = 0
$guardianStartFileTime = 0L
$password = $null
$workflowFailure = $null
$restoreFailure = $null
$environmentNames = @(
    'REMOTEDESK_REAL_MACHINE_TESTS',
    'REMOTEDESK_REAL_MACHINE_HOST',
    'REMOTEDESK_REAL_MACHINE_KEYBOARD_TESTS',
    'REMOTEDESK_REAL_MACHINE_KEYBOARD_TOKEN',
    'REMOTEDESK_REAL_MACHINE_KEYBOARD_PREPARED',
    'REMOTEDESK_REAL_MACHINE_PASSWORD',
    'REMOTEDESK_REAL_MACHINE_PORT'
)

try {
    if (-not [string]::IsNullOrWhiteSpace(
            $ForegroundProcessToMinimize)) {
        $remoteMinimizeScript = @"
`$ErrorActionPreference = 'Stop'
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class RemoteDeskKeyboardFocus {
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindowAsync(
        IntPtr window,
        int command);
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(
        IntPtr window,
        out uint processId);
}
'@
`$window = [IntPtr]$blockerWindow
if (-not [RemoteDeskKeyboardFocus]::IsWindow(`$window)) {
    throw 'The preflight blocker window no longer exists.'
}
[uint32]`$actualPid = 0
[void][RemoteDeskKeyboardFocus]::GetWindowThreadProcessId(
    `$window,
    [ref]`$actualPid)
if (`$actualPid -ne [uint32]$blockerPid) {
    throw 'The preflight blocker window changed owners.'
}
`$process = Get-Process -Id `$actualPid -ErrorAction Stop
if (-not [string]::Equals(
        `$process.ProcessName,
        '$blockerProcess',
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The preflight blocker process identity changed.'
}
if (-not $blockerWasMinimizedLiteral) {
    if (-not [RemoteDeskKeyboardFocus]::ShowWindowAsync(
            `$window,
            6)) {
        throw 'ShowWindowAsync(SW_MINIMIZE) failed.'
    }
    `$deadline = [DateTime]::UtcNow.AddSeconds(4)
    while (-not [RemoteDeskKeyboardFocus]::IsIconic(`$window) -and
           [DateTime]::UtcNow -lt `$deadline) {
        Start-Sleep -Milliseconds 50
    }
    if (-not [RemoteDeskKeyboardFocus]::IsIconic(`$window)) {
        throw 'The blocker window did not enter the minimized state.'
    }
}
[Console]::Out.WriteLine('RDKBMIN:ok')
"@
        [void](Invoke-RemoteEncodedPowerShell `
            -Script $remoteMinimizeScript `
            -Marker 'RDKBMIN:')
    }

    $secretScript = @'
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Add-Type -AssemblyName System.Security
$settings =
    Get-Content -LiteralPath (
        Join-Path $env:APPDATA 'RemoteDesk\settings.json') -Raw |
    ConvertFrom-Json
$protected = [Convert]::FromBase64String(
    [string]$settings.Host.ProtectedPassword)
$entropy = [Text.Encoding]::UTF8.GetBytes(
    'RemoteDesk.HostPassword.v1')
$plain = $null
try {
    $plain =
        [Security.Cryptography.ProtectedData]::Unprotect(
            $protected,
            $entropy,
            [Security.Cryptography.DataProtectionScope]::CurrentUser)
    [Console]::Out.WriteLine(
        'RDSECRET:' +
        [Convert]::ToBase64String($plain))
}
finally {
    if ($null -ne $plain) {
        [Array]::Clear($plain, 0, $plain.Length)
    }
    [Array]::Clear($protected, 0, $protected.Length)
    [Array]::Clear($entropy, 0, $entropy.Length)
}
'@
    $secretBase64 = Invoke-RemoteEncodedPowerShell `
        -Script $secretScript `
        -Marker 'RDSECRET:'
    $secretBytes =
        [Convert]::FromBase64String($secretBase64)
    try {
        $password =
            [Text.Encoding]::UTF8.GetString($secretBytes)
    }
    finally {
        [Array]::Clear(
            $secretBytes,
            0,
            $secretBytes.Length)
        $secretBytes = $null
        $secretBase64 = $null
    }

    $remoteFixtureScript = @"
`$ErrorActionPreference = 'Stop'
`$token = '$keyboardToken'
`$readyMarker = 'RD-READY-$keyboardToken'
`$scratchPath =
    Join-Path `$env:TEMP ('rdkbd-' + `$token + '.txt')
`$existingWindows = @(
    Get-Process notepad -ErrorAction SilentlyContinue |
        Where-Object {
            `$_.MainWindowTitle -like ('*rdkbd-' + `$token + '*')
        })
if (`$existingWindows.Count -ne 0 -or
    (Test-Path -LiteralPath `$scratchPath)) {
    throw 'The keyboard fixture token unexpectedly already exists.'
}
`$utf8 = New-Object Text.UTF8Encoding(`$false)
[IO.File]::WriteAllText(
    `$scratchPath,
    `$readyMarker + [Environment]::NewLine,
    `$utf8)
`$notepad =
    Join-Path `$env:WINDIR 'System32\notepad.exe'
[void](Start-Process -FilePath `$notepad -ArgumentList @(
    ('"' + `$scratchPath + '"')) -PassThru)
`$deadline = [DateTime]::UtcNow.AddSeconds(10)
do {
    `$windows = @(
        Get-Process notepad -ErrorAction SilentlyContinue |
            Where-Object {
                `$_.MainWindowHandle -ne 0 -and
                `$_.MainWindowTitle -like ('*rdkbd-' + `$token + '*')
            })
    if (`$windows.Count -eq 1) {
        break
    }
    Start-Sleep -Milliseconds 50
}
while ([DateTime]::UtcNow -lt `$deadline)
if (`$windows.Count -ne 1) {
    throw (
        'The exact keyboard fixture Notepad window was not ready; found ' +
        `$windows.Count + '.')
}
`$result = [pscustomobject]@{
    processId = `$windows[0].Id
    window = `$windows[0].MainWindowHandle.ToInt64()
    title = [string]`$windows[0].MainWindowTitle
    scratchPath = `$scratchPath
    scratchLength =
        (Get-Item -LiteralPath `$scratchPath).Length
}
`$json = `$result | ConvertTo-Json -Compress
[Console]::Out.WriteLine(
    'RDKBFIXTURE:' +
    [Convert]::ToBase64String(
        [Text.Encoding]::UTF8.GetBytes(`$json)))
"@
    $fixture = ConvertFrom-RemoteBase64Json (
        Invoke-RemoteEncodedPowerShell `
            -Script $remoteFixtureScript `
            -Marker 'RDKBFIXTURE:')
    $fixturePath =
        Join-Path $ResultsDirectory "$Tag.fixture.json"
    $fixture |
        ConvertTo-Json -Depth 4 |
        Set-Content -LiteralPath $fixturePath -Encoding UTF8

    $guardianScript = @"
`$ErrorActionPreference = 'Stop'
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class RemoteDeskKeyboardTargetFocus {
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindowAsync(
        IntPtr window,
        int command);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BringWindowToTop(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(
        IntPtr window,
        out uint processId);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachThreadInput(
        uint attach,
        uint attachTo,
        [MarshalAs(UnmanagedType.Bool)] bool attachInput);
    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();
}
'@
`$titlePattern = '*rdkbd-$keyboardToken*'
`$waitDeadline = [DateTime]::UtcNow.AddSeconds(30)
`$target = `$null
while ([DateTime]::UtcNow -lt `$waitDeadline) {
    `$target = Get-Process notepad -ErrorAction SilentlyContinue |
        Where-Object {
            `$_.MainWindowHandle -ne 0 -and
            `$_.MainWindowTitle -like `$titlePattern
        } |
        Select-Object -First 1
    if (`$null -ne `$target) {
        break
    }
    Start-Sleep -Milliseconds 25
}
if (`$null -eq `$target) {
    exit 2
}
`$window = [IntPtr]`$target.MainWindowHandle
if (-not [RemoteDeskKeyboardTargetFocus]::IsWindow(`$window)) {
    exit 3
}
`$currentForeground =
    [RemoteDeskKeyboardTargetFocus]::GetForegroundWindow()
if (`$currentForeground -ne `$window) {
    [uint32]`$foregroundProcessId = 0
    `$foregroundThread =
        if (`$currentForeground -eq [IntPtr]::Zero) {
            0
        }
        else {
            [RemoteDeskKeyboardTargetFocus]::GetWindowThreadProcessId(
                `$currentForeground,
                [ref]`$foregroundProcessId)
        }
    `$currentThread =
        [RemoteDeskKeyboardTargetFocus]::GetCurrentThreadId()
    `$attached =
        `$foregroundThread -ne 0 -and
        `$foregroundThread -ne `$currentThread -and
        [RemoteDeskKeyboardTargetFocus]::AttachThreadInput(
            `$currentThread,
            `$foregroundThread,
            `$true)
    try {
        [void][RemoteDeskKeyboardTargetFocus]::ShowWindowAsync(
            `$window,
            9)
        [void][RemoteDeskKeyboardTargetFocus]::BringWindowToTop(
            `$window)
        [void][RemoteDeskKeyboardTargetFocus]::SetForegroundWindow(
            `$window)
    }
    finally {
        if (`$attached) {
            [void][RemoteDeskKeyboardTargetFocus]::AttachThreadInput(
                `$currentThread,
                `$foregroundThread,
                `$false)
        }
    }
}
if ([RemoteDeskKeyboardTargetFocus]::GetForegroundWindow() -ne
        `$window) {
    exit 4
}
[Console]::Out.WriteLine('RDKBFOCUS:ok')
"@
    [void](Invoke-RemoteEncodedPowerShell `
        -Script $guardianScript `
        -Marker 'RDKBFOCUS:')

    $env:REMOTEDESK_REAL_MACHINE_TESTS = '1'
    $env:REMOTEDESK_REAL_MACHINE_HOST = $SshTarget.Split('@', 2)[1]
    $env:REMOTEDESK_REAL_MACHINE_KEYBOARD_TESTS = '1'
    $env:REMOTEDESK_REAL_MACHINE_KEYBOARD_TOKEN =
        $keyboardToken
    $env:REMOTEDESK_REAL_MACHINE_KEYBOARD_PREPARED = '1'
    $env:REMOTEDESK_REAL_MACHINE_PASSWORD = $password
    $env:REMOTEDESK_REAL_MACHINE_PORT = [string]$Port
    $filter =
        'FullyQualifiedName=RemoteDesk.Tests.' +
        'WindowsRemoteKeyboardRealMachineTests.' +
        'PhysicalKeyboardSupportsClipboardImeAndDisconnectRelease'
    $stdoutPath =
        Join-Path $ResultsDirectory "$Tag.stdout.log"

    & dotnet test `
        $testProject `
        -c Release `
        --no-build `
        --filter $filter `
        --logger "trx;LogFileName=$Tag.trx" `
        --results-directory $ResultsDirectory `
        --logger 'console;verbosity=normal' 2>&1 |
        Tee-Object -LiteralPath $stdoutPath
    if ($LASTEXITCODE -ne 0) {
        throw "$Tag failed."
    }
}
catch {
    $workflowFailure = $_
}
finally {
    $password = $null
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable(
            $name,
            $null,
            [EnvironmentVariableTarget]::Process)
    }

    if ($focusChangeAttempted) {
        try {
            $remoteRestoreScript = @"
`$ErrorActionPreference = 'Stop'
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class RemoteDeskKeyboardFocus {
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(
        IntPtr window,
        out uint processId);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindowAsync(
        IntPtr window,
        int command);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BringWindowToTop(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachThreadInput(
        uint attach,
        uint attachTo,
        [MarshalAs(UnmanagedType.Bool)] bool attachInput);
    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();
}
'@
if ($guardianPid -gt 0) {
    `$guardian =
        Get-Process -Id $guardianPid -ErrorAction SilentlyContinue
    if (`$null -ne `$guardian) {
        `$actualGuardianStart =
            `$guardian.StartTime.ToUniversalTime().ToFileTimeUtc()
        if (`$guardian.ProcessName -notin @(
                'powershell',
                'pwsh') -or
            `$actualGuardianStart -ne
                [long]$guardianStartFileTime) {
            throw 'The keyboard focus guardian identity changed.'
        }
        Stop-Process -Id `$guardian.Id -Force
        `$guardian.WaitForExit(5000)
        if (Get-Process -Id `$guardian.Id -ErrorAction SilentlyContinue) {
            throw 'The keyboard focus guardian did not exit.'
        }
    }
}
`$ownedNotepadRecovered = 0
`$ownedScratchRecovered = 0
`$cleanupDeadline = [DateTime]::UtcNow.AddSeconds(5)
do {
    `$ownedNotepad = @(
        Get-Process notepad -ErrorAction SilentlyContinue |
            Where-Object {
                `$_.MainWindowTitle -like '*rdkbd-$keyboardToken*'
            })
    foreach (`$notepad in `$ownedNotepad) {
        `$ownedNotepadRecovered++
        Stop-Process -Id `$notepad.Id -Force -ErrorAction SilentlyContinue
        try {
            `$notepad.WaitForExit(1000)
        }
        catch {
        }
    }
    `$ownedScratch = @(
        Get-ChildItem -LiteralPath `$env:TEMP -Filter 'rdkbd-$keyboardToken*.txt' -File -ErrorAction SilentlyContinue)
    foreach (`$scratch in `$ownedScratch) {
        `$ownedScratchRecovered++
        Remove-Item -LiteralPath `$scratch.FullName -Force -ErrorAction SilentlyContinue
    }
    if (`$ownedNotepad.Count -eq 0 -and
        `$ownedScratch.Count -eq 0) {
        break
    }
    Start-Sleep -Milliseconds 100
}
while ([DateTime]::UtcNow -lt `$cleanupDeadline)
`$blocker = `$null
`$blockerRestarted = `$false
if ($blockerPid -gt 0) {
    `$blocker = Get-Process -Id $blockerPid -ErrorAction SilentlyContinue
    if (`$null -ne `$blocker) {
        `$actualBlockerStart =
            `$blocker.StartTime.ToUniversalTime().ToFileTimeUtc()
        if (-not [string]::Equals(
                `$blocker.ProcessName,
                '$blockerProcess',
                [StringComparison]::OrdinalIgnoreCase) -or
            `$actualBlockerStart -ne [long]$blockerStartFileTime) {
            throw 'The original blocker process identity changed.'
        }
    }
    else {
        `$replacementBlockers = @(
            Get-Process -Name '$blockerProcess' -ErrorAction SilentlyContinue |
                Where-Object {
                    `$_.MainWindowHandle -ne 0 -and
                    `$_.StartTime.ToUniversalTime().ToFileTimeUtc() -gt
                        [long]$blockerStartFileTime
                })
        if (`$replacementBlockers.Count -ne 1) {
            throw (
                'The original blocker exited and no unique healthy ' +
                'replacement was found.')
        }
        `$blocker = `$replacementBlockers[0]
        `$blockerRestarted = `$true
    }
    `$blockerWindowHandle = [IntPtr]`$blocker.MainWindowHandle
    if (`$blockerWindowHandle -eq [IntPtr]::Zero -or
        -not [RemoteDeskKeyboardFocus]::IsWindow(
            `$blockerWindowHandle)) {
        throw 'The blocker has no restorable window.'
    }
    [uint32]`$actualBlockerPid = 0
    [void][RemoteDeskKeyboardFocus]::GetWindowThreadProcessId(
        `$blockerWindowHandle,
        [ref]`$actualBlockerPid)
    if (`$actualBlockerPid -ne [uint32]`$blocker.Id) {
        throw 'The blocker window changed owners.'
    }
    if (-not $blockerWasMinimizedLiteral) {
        [void][RemoteDeskKeyboardFocus]::ShowWindowAsync(
            `$blockerWindowHandle,
            9)
        `$blockerDeadline =
            [DateTime]::UtcNow.AddSeconds(5)
        while (
            [RemoteDeskKeyboardFocus]::IsIconic(
                `$blockerWindowHandle) -and
            [DateTime]::UtcNow -lt `$blockerDeadline) {
            [void][RemoteDeskKeyboardFocus]::ShowWindowAsync(
                `$blockerWindowHandle,
                9)
            Start-Sleep -Milliseconds 50
        }
        if ([RemoteDeskKeyboardFocus]::IsIconic(
                `$blockerWindowHandle)) {
            throw 'The original blocker window was not restored.'
        }
    }
}
`$window = [IntPtr]$foregroundWindow
if (-not [RemoteDeskKeyboardFocus]::IsWindow(`$window)) {
    throw 'The original foreground window no longer exists.'
}
[uint32]`$actualPid = 0
[void][RemoteDeskKeyboardFocus]::GetWindowThreadProcessId(
    `$window,
    [ref]`$actualPid)
if (`$actualPid -ne [uint32]$foregroundPid) {
    throw 'The original foreground window changed owners.'
}
`$process = Get-Process -Id `$actualPid -ErrorAction Stop
if (-not [string]::Equals(
        `$process.ProcessName,
        '$foregroundProcess',
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The original foreground process identity changed.'
}
function Set-RestoreForeground {
    `$currentForeground =
        [RemoteDeskKeyboardFocus]::GetForegroundWindow()
    [uint32]`$currentForegroundPid = 0
    `$foregroundThread =
        if (`$currentForeground -eq [IntPtr]::Zero) {
            0
        }
        else {
            [RemoteDeskKeyboardFocus]::GetWindowThreadProcessId(
                `$currentForeground,
                [ref]`$currentForegroundPid)
        }
    `$currentThread =
        [RemoteDeskKeyboardFocus]::GetCurrentThreadId()
    `$attached =
        `$foregroundThread -ne 0 -and
        `$foregroundThread -ne `$currentThread -and
        [RemoteDeskKeyboardFocus]::AttachThreadInput(
            `$currentThread,
            `$foregroundThread,
            `$true)
    try {
        [void][RemoteDeskKeyboardFocus]::ShowWindowAsync(`$window, 9)
        [void][RemoteDeskKeyboardFocus]::BringWindowToTop(`$window)
        [void][RemoteDeskKeyboardFocus]::SetForegroundWindow(`$window)
    }
    finally {
        if (`$attached) {
            [void][RemoteDeskKeyboardFocus]::AttachThreadInput(
                `$currentThread,
                `$foregroundThread,
                `$false)
        }
    }
}
Set-RestoreForeground
`$deadline = [DateTime]::UtcNow.AddSeconds(5)
do {
    if (-not [RemoteDeskKeyboardFocus]::IsIconic(`$window) -and
        [RemoteDeskKeyboardFocus]::IsWindowVisible(`$window) -and
        [RemoteDeskKeyboardFocus]::GetForegroundWindow() -eq `$window) {
        break
    }
    Set-RestoreForeground
    Start-Sleep -Milliseconds 50
}
while ([DateTime]::UtcNow -lt `$deadline)
if ([RemoteDeskKeyboardFocus]::IsIconic(`$window) -or
    -not [RemoteDeskKeyboardFocus]::IsWindowVisible(`$window) -or
    [RemoteDeskKeyboardFocus]::GetForegroundWindow() -ne `$window) {
    throw 'The original foreground window was not fully restored.'
}
`$residualNotepad = @(
    Get-Process notepad -ErrorAction SilentlyContinue |
        Where-Object {
            `$_.MainWindowTitle -like '*rdkbd-*'
        })
`$residualScratch = @(
    Get-ChildItem -LiteralPath `$env:TEMP -Filter 'rdkbd-*.txt' -File -ErrorAction SilentlyContinue)
if (`$residualNotepad.Count -ne 0 -or
    `$residualScratch.Count -ne 0) {
    throw (
        "Keyboard probe cleanup left " +
        "`$(`$residualNotepad.Count) Notepad window(s) and " +
        "`$(`$residualScratch.Count) scratch file(s).")
}
`$listeners = @(
    Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue)
if (`$listeners.Count -ne 1) {
    throw 'The RemoteDesk listener was not healthy after the probe.'
}
`$result = [pscustomobject]@{
    restoredProcessId = `$actualPid
    restoredWindow = `$window.ToInt64()
    restoredProcess = [string]`$process.ProcessName
    foregroundWindow =
        [RemoteDeskKeyboardFocus]::GetForegroundWindow().
            ToInt64()
    visible =
        [RemoteDeskKeyboardFocus]::IsWindowVisible(`$window)
    minimized =
        [RemoteDeskKeyboardFocus]::IsIconic(`$window)
    residualNotepad = `$residualNotepad.Count
    residualScratch = `$residualScratch.Count
    ownedNotepadRecovered = `$ownedNotepadRecovered
    ownedScratchRecovered = `$ownedScratchRecovered
    blockerProcessId =
        if (`$null -eq `$blocker) { 0 } else { `$blocker.Id }
    blockerRestarted =
        if ($blockerPid -gt 0) { `$blockerRestarted } else { `$false }
}
`$json = `$result | ConvertTo-Json -Compress
[Console]::Out.WriteLine(
    'RDKBRESTORE:' +
    [Convert]::ToBase64String(
        [Text.Encoding]::UTF8.GetBytes(`$json)))
"@
            $restore = ConvertFrom-RemoteBase64Json (
                Invoke-RemoteEncodedPowerShell `
                    -Script $remoteRestoreScript `
                    -Marker 'RDKBRESTORE:')
            $restorePath =
                Join-Path `
                    $ResultsDirectory `
                    "$Tag.restore.json"
            $restore |
                ConvertTo-Json -Depth 4 |
                Set-Content `
                    -LiteralPath $restorePath `
                    -Encoding UTF8
        }
        catch {
            $restoreFailure = $_
        }
    }
}

if ($null -ne $restoreFailure) {
    $workflowDetail =
        if ($null -eq $workflowFailure) {
            'The keyboard test itself completed.'
        }
        else {
            "Keyboard test failure: $($workflowFailure.Exception.Message)"
        }
    throw (
        "$workflowDetail Foreground restoration failed: " +
        "$($restoreFailure.Exception.Message)")
}
if ($null -ne $workflowFailure) {
    throw $workflowFailure
}

Write-Host (
    "Keyboard entity gate passed; original foreground restored. " +
    "Evidence: $ResultsDirectory")
