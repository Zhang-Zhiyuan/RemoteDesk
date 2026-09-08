<#
.SYNOPSIS
Runs a long-duration, release-oriented RemoteDesk stability regression.

.DESCRIPTION
The default mode is observational: it samples the RemoteDesk TCP magic,
optionally performs authenticated protocol probes, watches the remote process
through key-based SSH, and collects Windows/Linux GPU recovery events.

Any state-changing scenario is disabled by default and requires its own
explicit switch:

* -EnableNetworkFault installs one narrowly scoped local outbound firewall
  rule, verifies loss/recovery, and removes the rule in finally.
* -EnableRemoteRestart executes only the caller-supplied
  -RemoteRestartCommand over SSH.
* -EnableDisplaySwitch selects only caller-supplied capture target IDs and
  restores the original target in finally.

The minimum normal duration is 30 minutes. Use -PlanOnly to validate a run and
write reports without connecting to or changing either machine.

.NOTES
Default release gates are at least 99.5% TCP availability and 90% monitoring
sample coverage, no more than two consecutive TCP failures or 15 seconds of
unexpected outage, at least 99% authenticated probe success when configured,
zero unexpected process restarts, successful GPU-event collection with zero
recovery/reset events when SSH observation is configured, and recovery within
45 seconds for every explicitly enabled fault scenario.

Authenticated probes are unattended, periodic end-to-end frame checks. A
RemoteDesk host accepts one active viewer, so do not configure
-PasswordEnvironmentVariable while a separate interactive viewer is expected
to remain connected. Omit it to monitor an externally driven viewer. This
script measures periodic frame delivery and service health; it does not claim
continuous presentation-frame telemetry.

.EXAMPLE
.\scripts\Invoke-RemoteDeskSoakRegression.ps1 `
    -Target 192.0.2.249 `
    -SshTarget 192.0.2.249 `
    -DurationMinutes 30

.EXAMPLE
$env:REMOTEDESK_SOAK_PASSWORD = "<password>"
.\scripts\Invoke-RemoteDeskSoakRegression.ps1 `
    -Target 192.0.2.249 `
    -SshTarget 192.0.2.249 `
    -PasswordEnvironmentVariable REMOTEDESK_SOAK_PASSWORD `
    -EnableDisplaySwitch `
    -CaptureTargetIds monitor0,monitor1 `
    -DisplaySwitchAtMinute 5 `
    -EnableNetworkFault `
    -NetworkFaultAtMinute 10 `
    -EnableRemoteRestart `
    -RemoteRestartAtMinute 20 `
    -RemoteRestartCommand '<explicit remote restart command>'
#>

[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = "Low")]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$Target,

    [ValidateRange(1, 65535)]
    [int]$Port = 56565,

    [ValidateRange(30, 10080)]
    [int]$DurationMinutes = 30,

    [ValidateRange(1, 300)]
    [int]$ProbeIntervalSeconds = 5,

    [ValidateRange(5, 600)]
    [int]$RemoteObservationIntervalSeconds = 30,

    [ValidateRange(1, 60)]
    [int]$ProtocolProbeIntervalMinutes = 5,

    [ValidateRange(100, 10000)]
    [int]$TcpProbeTimeoutMilliseconds = 1500,

    [ValidateRange(0, 100)]
    [double]$MinimumAvailabilityPercent = 99.5,

    [ValidateRange(0, 100)]
    [double]$MinimumProtocolSuccessPercent = 99.0,

    [ValidateRange(0, 100)]
    [double]$MinimumRemoteProcessAvailabilityPercent = 99.0,

    [ValidateRange(0, 100)]
    [int]$MaximumConsecutiveFailures = 2,

    [ValidateRange(1, 600)]
    [int]$MaximumUnexpectedOutageSeconds = 15,

    [ValidateRange(1, 600)]
    [int]$MaximumSamplingGapSeconds = 15,

    [ValidateRange(1, 600)]
    [int]$MaximumRemoteObservationGapSeconds = 90,

    [ValidateRange(1, 600)]
    [int]$MaximumRecoverySeconds = 45,

    [ValidateRange(0, 100)]
    [int]$MaximumGpuRecoveryEvents = 0,

    [ValidateRange(0, 100)]
    [int]$MaximumUnexpectedProcessRestarts = 0,

    [string]$OutputDirectory = "",

    [string]$PasswordEnvironmentVariable = "",

    [string]$PythonCommand = "",

    [ValidatePattern("^(?!-)[A-Za-z0-9_.@:-]*$")]
    [string]$SshTarget = "",

    [ValidateRange(1, 65535)]
    [int]$SshPort = 22,

    [ValidateRange(5, 600)]
    [int]$SshCommandTimeoutSeconds = 30,

    [string]$SshIdentityFile = "",

    [string]$SshUserKnownHostsFile = "",

    [ValidateSet("Auto", "Windows", "Linux")]
    [string]$RemotePlatform = "Auto",

    [ValidateNotNullOrEmpty()]
    [ValidatePattern("^[A-Za-z0-9_.-]+$")]
    [string]$RemoteProcessName = "RemoteDesk",

    [switch]$SkipGpuEventCollection,

    [switch]$SkipIcmp,

    [switch]$EnableNetworkFault,

    [ValidateRange(1, 10079)]
    [int]$NetworkFaultAtMinute = 10,

    [ValidateRange(1, 300)]
    [int]$NetworkFaultSeconds = 10,

    [switch]$EnableRemoteRestart,

    [ValidateRange(1, 10079)]
    [int]$RemoteRestartAtMinute = 20,

    [string]$RemoteRestartCommand = "",

    [switch]$EnableDisplaySwitch,

    [ValidateRange(1, 10079)]
    [int]$DisplaySwitchAtMinute = 5,

    [string[]]$CaptureTargetIds = @(),

    [switch]$PlanOnly
)

$ErrorActionPreference = "Stop"
$script:RunId = "remotedesk-soak-$([Guid]::NewGuid().ToString("N"))"
$script:StartedAtUtc = [DateTimeOffset]::UtcNow
$script:RunStatus = if ($PlanOnly) { "Planned" } else { "Running" }
$script:Samples = [System.Collections.Generic.List[object]]::new()
$script:ProtocolProbes = [System.Collections.Generic.List[object]]::new()
$script:RemoteProcessSamples = [System.Collections.Generic.List[object]]::new()
$script:GpuEvents = [System.Collections.Generic.List[object]]::new()
$script:GpuCollectionErrors = [System.Collections.Generic.List[object]]::new()
$script:ScenarioEvents = [System.Collections.Generic.List[object]]::new()
$script:DisplayChecks = [System.Collections.Generic.List[object]]::new()
$script:Gates = [System.Collections.Generic.List[object]]::new()
$script:NetworkFaultActive = $false
$script:NetworkFaultCleanup = $null
$script:NetworkFaultObserved = $false
$script:NetworkFaultRecovered = $false
$script:NetworkFaultRecoverySeconds = $null
$script:RemoteRestartObserved = $false
$script:RemoteRestartRecovered = $false
$script:RemoteRestartRecoverySeconds = $null
$script:OriginalCaptureTargetId = ""
$script:DisplayRestoreRequired = $false
$script:DisplayRestoreSucceeded = $false
$script:ResolvedRemotePlatform = $RemotePlatform
$script:ResolvedTargetAddress = ""
$script:Interrupted = $false
$script:FailureDetail = ""
$script:JsonReportPath = ""
$script:MarkdownReportPath = ""
$script:CheckpointPath = ""
$script:CleanupStatePath = ""
$script:LastReport = $null
$script:RemotePowerShellLauncher = ""

function Test-IsWindows {
    if ($PSVersionTable.PSEdition -eq "Desktop") {
        return $true
    }

    return [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)
}

function Resolve-RepoRoot {
    if ([string]::IsNullOrWhiteSpace($PSCommandPath)) {
        return (Get-Location).Path
    }

    return (Resolve-Path (
        Join-Path (Split-Path -Parent $PSCommandPath) "..")).Path
}

function ConvertTo-SafeFileName {
    param([string]$Value)

    $safe = [regex]::Replace($Value, "[^A-Za-z0-9_.-]", "_")
    if ([string]::IsNullOrWhiteSpace($safe)) {
        return "target"
    }

    return $safe
}

function Write-Utf8NoBom {
    param(
        [string]$Path,
        [string]$Text
    )

    $encoding = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($Path, $Text, $encoding)
}

function Format-MarkdownCell {
    param([object]$Value)

    if ($null -eq $Value) {
        return ""
    }

    return ([string]$Value).Replace("|", "\|").Replace(
        "`r",
        " ").Replace(
        "`n",
        " ")
}

function Resolve-TargetIPv4 {
    param([string]$NameOrAddress)

    $parsed = $null
    if ([System.Net.IPAddress]::TryParse(
            $NameOrAddress,
            [ref]$parsed)) {
        if ($parsed.AddressFamily -ne
            [System.Net.Sockets.AddressFamily]::InterNetwork) {
            throw "Only IPv4 targets are currently supported: $NameOrAddress"
        }

        return $parsed.ToString()
    }

    $addresses = @(
        [System.Net.Dns]::GetHostAddresses($NameOrAddress) |
            Where-Object {
                $_.AddressFamily -eq
                    [System.Net.Sockets.AddressFamily]::InterNetwork
            } |
            ForEach-Object { $_.ToString() } |
            Sort-Object -Unique)
    if ($addresses.Count -eq 0) {
        throw "Cannot resolve an IPv4 address for $NameOrAddress."
    }

    return $addresses[0]
}

function Get-Password {
    if ([string]::IsNullOrWhiteSpace(
            $PasswordEnvironmentVariable)) {
        return ""
    }

    $value = [Environment]::GetEnvironmentVariable(
        $PasswordEnvironmentVariable)
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw (
            "Password environment variable " +
            "$PasswordEnvironmentVariable is empty or unavailable.")
    }

    return $value
}

function Get-CommandSource {
    param([string[]]$Names)

    foreach ($name in $Names) {
        if ([string]::IsNullOrWhiteSpace($name)) {
            continue
        }

        $command = Get-Command $name -ErrorAction SilentlyContinue
        if ($null -ne $command) {
            return $command.Source
        }
    }

    return $null
}

function Test-RemoteDeskTcpMagic {
    param(
        [string]$Address,
        [int]$TcpPort,
        [int]$TimeoutMilliseconds
    )

    $started = [Diagnostics.Stopwatch]::StartNew()
    $client = [System.Net.Sockets.TcpClient]::new(
        [System.Net.Sockets.AddressFamily]::InterNetwork)
    $asyncWaitHandle = $null
    try {
        $client.NoDelay = $true
        $connect = $client.BeginConnect(
            $Address,
            $TcpPort,
            $null,
            $null)
        $asyncWaitHandle = $connect.AsyncWaitHandle
        if (-not $asyncWaitHandle.WaitOne($TimeoutMilliseconds)) {
            return [pscustomobject]@{
                Success = $false
                LatencyMilliseconds = $started.Elapsed.TotalMilliseconds
                Detail = "TCP connect timeout"
            }
        }

        $client.EndConnect($connect)
        $stream = $client.GetStream()
        $stream.ReadTimeout = $TimeoutMilliseconds
        $magicBytes = New-Object byte[] 4
        $offset = 0
        while ($offset -lt $magicBytes.Length) {
            $read = $stream.Read(
                $magicBytes,
                $offset,
                $magicBytes.Length - $offset)
            if ($read -le 0) {
                break
            }

            $offset += $read
        }

        $magic = if ($offset -eq 4) {
            [Text.Encoding]::ASCII.GetString($magicBytes)
        }
        else {
            ""
        }
        return [pscustomobject]@{
            Success = $magic -eq "RDK1"
            LatencyMilliseconds = $started.Elapsed.TotalMilliseconds
            Detail = if ($magic -eq "RDK1") {
                "RemoteDesk TCP magic OK"
            }
            elseif ($offset -eq 0) {
                "TCP connected without RemoteDesk magic"
            }
            else {
                "Incomplete or unexpected RemoteDesk magic"
            }
        }
    }
    catch {
        return [pscustomobject]@{
            Success = $false
            LatencyMilliseconds = $started.Elapsed.TotalMilliseconds
            Detail = $_.Exception.Message
        }
    }
    finally {
        if ($null -ne $asyncWaitHandle) {
            $asyncWaitHandle.Dispose()
        }

        $client.Dispose()
    }
}

function Test-IcmpReachability {
    param([string]$Address)

    if ($SkipIcmp) {
        return [pscustomobject]@{
            Attempted = $false
            Success = $false
            LatencyMilliseconds = $null
            Detail = "skipped"
        }
    }

    $ping = [System.Net.NetworkInformation.Ping]::new()
    try {
        $reply = $ping.Send($Address, $TcpProbeTimeoutMilliseconds)
        return [pscustomobject]@{
            Attempted = $true
            Success = $reply.Status -eq
                [System.Net.NetworkInformation.IPStatus]::Success
            LatencyMilliseconds = if ($reply.Status -eq
                [System.Net.NetworkInformation.IPStatus]::Success) {
                [double]$reply.RoundtripTime
            }
            else {
                $null
            }
            Detail = [string]$reply.Status
        }
    }
    catch {
        return [pscustomobject]@{
            Attempted = $true
            Success = $false
            LatencyMilliseconds = $null
            Detail = $_.Exception.Message
        }
    }
    finally {
        $ping.Dispose()
    }
}

function Add-ConnectionSample {
    param(
        [bool]$ExpectedFault = $false,
        [string]$Phase = "steady"
    )

    $sampledAt = [DateTimeOffset]::UtcNow
    $tcp = Test-RemoteDeskTcpMagic `
        -Address $script:ResolvedTargetAddress `
        -TcpPort $Port `
        -TimeoutMilliseconds $TcpProbeTimeoutMilliseconds
    $icmp = Test-IcmpReachability -Address $script:ResolvedTargetAddress
    $sample = [pscustomobject]@{
        sampledAtUtc = $sampledAt.ToString("O")
        elapsedSeconds = (
            $sampledAt - $script:StartedAtUtc).TotalSeconds
        phase = $Phase
        expectedFault = $ExpectedFault
        tcpSuccess = [bool]$tcp.Success
        tcpLatencyMilliseconds =
            [Math]::Round([double]$tcp.LatencyMilliseconds, 3)
        tcpDetail = [string]$tcp.Detail
        icmpAttempted = [bool]$icmp.Attempted
        icmpSuccess = [bool]$icmp.Success
        icmpLatencyMilliseconds = if (
            $null -eq $icmp.LatencyMilliseconds) {
            $null
        }
        else {
            [Math]::Round([double]$icmp.LatencyMilliseconds, 3)
        }
        icmpDetail = [string]$icmp.Detail
    }
    $script:Samples.Add($sample) | Out-Null
    return $sample
}

function Get-SshArguments {
    $arguments = @(
        "-o", "BatchMode=yes",
        "-o", "ConnectTimeout=8",
        "-o", "ServerAliveInterval=5",
        "-o", "ServerAliveCountMax=2",
        "-p", [string]$SshPort)
    if (-not [string]::IsNullOrWhiteSpace(
            $SshIdentityFile)) {
        $arguments += @("-i", $SshIdentityFile)
    }
    if (-not [string]::IsNullOrWhiteSpace(
            $SshUserKnownHostsFile)) {
        $knownHostsPath =
            [System.IO.Path]::GetFullPath(
                $SshUserKnownHostsFile)
        $arguments += @(
            "-o",
            "UserKnownHostsFile=$knownHostsPath")
    }

    return $arguments
}

function ConvertTo-NativeCommandLineArgument {
    param([AllowEmptyString()][string]$Value)

    if ($Value.Length -gt 0 -and
        $Value -notmatch '[\s"]') {
        return $Value
    }

    $quoted = '"'
    $backslashCount = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') {
            $backslashCount++
            continue
        }

        if ($character -eq '"') {
            $quoted += ('\' * ($backslashCount * 2 + 1))
            $quoted += '"'
        }
        else {
            $quoted += ('\' * $backslashCount)
            $quoted += [string]$character
        }
        $backslashCount = 0
    }

    $quoted += ('\' * ($backslashCount * 2))
    $quoted += '"'
    return $quoted
}

function Invoke-Ssh {
    param(
        [string]$Command,
        [switch]$AllowFailure
    )

    if ([string]::IsNullOrWhiteSpace($SshTarget)) {
        return [pscustomobject]@{
            Attempted = $false
            Success = $false
            ExitCode = $null
            Lines = @()
            Detail = "SSH target not configured"
        }
    }

    $ssh = Get-CommandSource @("ssh")
    if ([string]::IsNullOrWhiteSpace($ssh)) {
        if ($AllowFailure) {
            return [pscustomobject]@{
                Attempted = $true
                Success = $false
                ExitCode = $null
                Lines = @()
                Detail = "ssh command not found"
            }
        }

        throw "ssh command was not found."
    }

    $process = [Diagnostics.Process]::new()
    $processStarted = $false
    try {
        $sshArguments = @(Get-SshArguments)
        $allArguments = @(
            $sshArguments
            $SshTarget
            $Command)
        $startInfo = [Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = $ssh
        $startInfo.Arguments = (
            $allArguments |
                ForEach-Object {
                    ConvertTo-NativeCommandLineArgument ([string]$_)
                }) -join " "
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        $process.StartInfo = $startInfo
        if (-not $process.Start()) {
            throw "ssh process did not start."
        }
        $processStarted = $true
        $standardOutput = $process.StandardOutput.ReadToEndAsync()
        $standardError = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(
                $SshCommandTimeoutSeconds * 1000)) {
            $process.Kill()
            $process.WaitForExit()
            $lines = @(
                $standardOutput.Result
                $standardError.Result) |
                Where-Object {
                    -not [string]::IsNullOrWhiteSpace($_)
                }
            $result = [pscustomobject]@{
                Attempted = $true
                Success = $false
                ExitCode = $null
                Lines = @(
                    ($lines -join [Environment]::NewLine) -split
                        "\r?\n")
                Detail = (
                    "ssh command timed out after " +
                    "$SshCommandTimeoutSeconds seconds")
            }
            if (-not $AllowFailure) {
                throw $result.Detail
            }

            return $result
        }

        $exitCode = $process.ExitCode
        $combinedOutput = @(
            $standardOutput.Result
            $standardError.Result) |
            Where-Object {
                -not [string]::IsNullOrWhiteSpace($_)
            }
        $lines = @(
            ($combinedOutput -join [Environment]::NewLine) -split
                "\r?\n" |
                Where-Object {
                    -not [string]::IsNullOrWhiteSpace($_)
                })
        $result = [pscustomobject]@{
            Attempted = $true
            Success = $exitCode -eq 0
            ExitCode = $exitCode
            Lines = @($lines | ForEach-Object { [string]$_ })
            Detail = if ($exitCode -eq 0) {
                "OK"
            }
            else {
                "ssh exit code $exitCode"
            }
        }
        if (-not $result.Success -and -not $AllowFailure) {
            throw (
                "$($result.Detail): " +
                ($result.Lines -join " "))
        }

        return $result
    }
    catch {
        if ($processStarted -and
            -not $process.HasExited) {
            $process.Kill()
            $process.WaitForExit()
        }
        if ($AllowFailure) {
            return [pscustomobject]@{
                Attempted = $true
                Success = $false
                ExitCode = $null
                Lines = @()
                Detail = $_.Exception.Message
            }
        }

        throw
    }
    finally {
        $process.Dispose()
    }
}

function ConvertTo-EncodedPowerShellCommand {
    param([string]$Script)

    return [Convert]::ToBase64String(
        [Text.Encoding]::Unicode.GetBytes($Script))
}

function Invoke-RemoteWindowsPowerShell {
    param(
        [string]$Script,
        [switch]$AllowFailure
    )

    $wrappedScript =
        "`$ProgressPreference = 'SilentlyContinue'" +
        [Environment]::NewLine +
        $Script
    $encoded =
        ConvertTo-EncodedPowerShellCommand -Script $wrappedScript
    $launchers = if (-not [string]::IsNullOrWhiteSpace(
            $script:RemotePowerShellLauncher)) {
        @($script:RemotePowerShellLauncher)
    }
    else {
        @(
            "powershell.exe",
            '"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"',
            "/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe"
        )
    }
    $lastResult = $null
    foreach ($launcher in $launchers) {
        $lastResult = Invoke-Ssh `
            -Command (
                "$launcher -NoProfile -NonInteractive " +
                "-EncodedCommand $encoded") `
            -AllowFailure
        if ($lastResult.Success) {
            $script:RemotePowerShellLauncher = $launcher
            return $lastResult
        }
    }

    if ($AllowFailure) {
        return $lastResult
    }

    throw (
        "Remote Windows PowerShell could not be started: " +
        $lastResult.Detail)
}

function Resolve-RemotePlatform {
    if ($RemotePlatform -ne "Auto") {
        return $RemotePlatform
    }

    if ([string]::IsNullOrWhiteSpace($SshTarget)) {
        return "Auto"
    }

    $windows = Invoke-RemoteWindowsPowerShell `
        -Script "[Environment]::OSVersion.Platform.ToString()" `
        -AllowFailure
    if ($windows.Success) {
        return "Windows"
    }

    $unix = Invoke-Ssh -Command "uname -s" -AllowFailure
    if ($unix.Success -and
        (($unix.Lines -join " ") -match
            "Linux")) {
        return "Linux"
    }

    return "Auto"
}

function Get-RemoteProcessSnapshot {
    param(
        [bool]$ExpectedDisruption = $false,
        [string]$Phase = "steady"
    )

    $capturedAt = [DateTimeOffset]::UtcNow
    if ([string]::IsNullOrWhiteSpace($SshTarget)) {
        return [pscustomobject]@{
            sampledAtUtc = $capturedAt.ToString("O")
            phase = $Phase
            expectedDisruption = $ExpectedDisruption
            attempted = $false
            reachable = $false
            processCount = 0
            identities = @()
            detail = "SSH target not configured"
        }
    }

    if ($script:ResolvedRemotePlatform -eq "Windows") {
        $escapedName = $RemoteProcessName.Replace("'", "''")
        if (-not $escapedName.EndsWith(
                ".exe",
                [StringComparison]::OrdinalIgnoreCase)) {
            $escapedName += ".exe"
        }
        $remoteScript = @"
`$items = @(
    Get-CimInstance Win32_Process -ErrorAction Stop |
        Where-Object { `$_.Name -ieq '$escapedName' } |
        Sort-Object ProcessId)
foreach (`$item in `$items) {
    "`$(`$item.ProcessId)|`$(`$item.CreationDate.ToUniversalTime().ToString('O'))|`$(`$item.ExecutablePath)"
}
"@
        $result = Invoke-RemoteWindowsPowerShell `
            -Script $remoteScript `
            -AllowFailure
    }
    elseif ($script:ResolvedRemotePlatform -eq "Linux") {
        $escapedProcessName = [regex]::Escape($RemoteProcessName)
        $processPattern =
            '(^|[[:space:]/]){0}([[:space:]]|$)' -f
                $escapedProcessName
        $result = Invoke-Ssh `
            -Command (
                "command -v pgrep >/dev/null 2>&1 || exit 127; " +
                "pgrep -af -- '$processPattern' 2>/dev/null; " +
                "status=`$?; [ `$status -eq 1 ] && exit 0; " +
                "exit `$status") `
            -AllowFailure
    }
    else {
        return [pscustomobject]@{
            sampledAtUtc = $capturedAt.ToString("O")
            phase = $Phase
            expectedDisruption = $ExpectedDisruption
            attempted = $false
            reachable = $false
            processCount = 0
            identities = @()
            detail = "Remote platform could not be resolved."
        }
    }

    $identities = if (
        $script:ResolvedRemotePlatform -eq "Windows") {
        @(
            $result.Lines |
                Where-Object {
                    $_ -match (
                        '^\d+\|' +
                        '\d{4}-\d{2}-\d{2}T[^|]+\|' +
                        '.*$')
                })
    }
    else {
        @(
            $result.Lines |
                Where-Object {
                    -not [string]::IsNullOrWhiteSpace($_) -and
                    $_ -notmatch "^#< CLIXML" -and
                    $_ -notmatch "^<Objs "
                })
    }
    return [pscustomobject]@{
        sampledAtUtc = $capturedAt.ToString("O")
        phase = $Phase
        expectedDisruption = $ExpectedDisruption
        attempted = [bool]$result.Attempted
        reachable = [bool]$result.Success
        processCount = $identities.Count
        identities = $identities
        detail = [string]$result.Detail
    }
}

function Resolve-PythonCommand {
    if (-not [string]::IsNullOrWhiteSpace($PythonCommand)) {
        $explicit = Get-Command $PythonCommand -ErrorAction SilentlyContinue
        if ($null -eq $explicit) {
            throw "PythonCommand was not found: $PythonCommand"
        }

        return $explicit.Source
    }

    $resolved = Get-CommandSource @("python3", "python")
    if ([string]::IsNullOrWhiteSpace($resolved)) {
        throw "python3/python was not found for the protocol probe."
    }

    return $resolved
}

function Invoke-ProtocolProbe {
    param(
        [string]$SelectTargetId = "",
        [string]$Purpose = "periodic"
    )

    if ([string]::IsNullOrWhiteSpace(
            $PasswordEnvironmentVariable)) {
        throw (
            "Protocol probes require PasswordEnvironmentVariable.")
    }
    $root = Resolve-RepoRoot
    $probePath = Join-Path (
        Join-Path (
            Join-Path $root "scripts") "linux") (
            "remotedesk_protocol_probe.py")
    if (-not (Test-Path -LiteralPath $probePath -PathType Leaf)) {
        throw "Protocol probe is missing: $probePath"
    }

    $python = Resolve-PythonCommand
    $bootstrap = (
        "import os, runpy, sys; " +
        "probe=sys.argv[1]; name=sys.argv[2]; rest=sys.argv[3:]; " +
        "r,w=os.pipe(); os.write(w, os.environ[name].encode('utf-8')); os.close(w); " +
        "sys.argv=[probe, *rest, '--password-fd', str(r)]; " +
        "runpy.run_path(probe, run_name='__main__')")
    $arguments = @(
        "-c", $bootstrap,
        $probePath, $PasswordEnvironmentVariable,
        "--host", $script:ResolvedTargetAddress,
        "--port", [string]$Port,
        "--duration", "8",
        "--frames", "1",
        "--pings", "1",
        "--ping-timeout", "2",
        "--json")
    if (-not [string]::IsNullOrWhiteSpace($SelectTargetId)) {
        $arguments += @("--select-target-id", $SelectTargetId)
    }

    $started = [Diagnostics.Stopwatch]::StartNew()
    try {
        $output = @(& $python @arguments 2>&1)
        $exitCode = $LASTEXITCODE
        $text = ($output | ForEach-Object { [string]$_ }) -join "`n"
        $json = $null
        try {
            $json = $text | ConvertFrom-Json -ErrorAction Stop
        }
        catch {
        }
        $frameCount = if ($null -ne $json) {
            @($json.frames).Count
        }
        else {
            0
        }
        $success = $exitCode -eq 0 -and
            $null -ne $json -and
            $json.ok -eq $true -and
            $frameCount -gt 0
        $record = [pscustomobject]@{
            sampledAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
            purpose = $Purpose
            selectTargetId = $SelectTargetId
            success = $success
            elapsedMilliseconds =
                [Math]::Round($started.Elapsed.TotalMilliseconds, 3)
            exitCode = $exitCode
            error = if ($success) {
                ""
            }
            elseif ($null -ne $json -and
                $null -ne $json.error) {
                [string]$json.error
            }
            elseif ($null -ne $json -and
                $json.ok -eq $true -and
                $frameCount -le 0) {
                "The authenticated probe received no video frame."
            }
            else {
                $text
            }
            device = if ($null -ne $json) { $json.device } else { $null }
            captureTargets = if ($null -ne $json) {
                @($json.captureTargets)
            }
            else {
                @()
            }
            selectedTarget = if ($null -ne $json) {
                $json.selectedTarget
            }
            else {
                $null
            }
            frameCount = $frameCount
            pings = if ($null -ne $json) {
                @($json.pings)
            }
            else {
                @()
            }
        }
        $script:ProtocolProbes.Add($record) | Out-Null
        return [pscustomobject]@{
            Record = $record
            Json = $json
        }
    }
    catch {
        $record = [pscustomobject]@{
            sampledAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
            purpose = $Purpose
            selectTargetId = $SelectTargetId
            success = $false
            elapsedMilliseconds =
                [Math]::Round($started.Elapsed.TotalMilliseconds, 3)
            exitCode = $null
            error = $_.Exception.Message
            device = $null
            captureTargets = @()
            selectedTarget = $null
            frameCount = 0
            pings = @()
        }
        $script:ProtocolProbes.Add($record) | Out-Null
        return [pscustomobject]@{
            Record = $record
            Json = $null
        }
    }
}

function Test-IsAdministrator {
    if (Test-IsWindows) {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        $principal = [Security.Principal.WindowsPrincipal]::new($identity)
        return $principal.IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)
    }

    $id = Get-CommandSource @("id")
    if ([string]::IsNullOrWhiteSpace($id)) {
        return $false
    }

    $uid = (& $id -u 2>$null | Select-Object -First 1)
    return ([string]$uid).Trim() -eq "0"
}

function Write-CleanupState {
    param([string]$Detail)

    $state = [ordered]@{
        schemaVersion = 1
        runId = $script:RunId
        updatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        pending = (
            $script:NetworkFaultActive -or
            $script:DisplayRestoreRequired)
        networkFault = [ordered]@{
            pending = $script:NetworkFaultActive
            platform = if (
                $null -ne $script:NetworkFaultCleanup) {
                [string]$script:NetworkFaultCleanup.Platform
            }
            else {
                ""
            }
            identifier = if (
                $null -ne $script:NetworkFaultCleanup) {
                [string]$script:NetworkFaultCleanup.Identifier
            }
            else {
                ""
            }
            address = if (
                $null -ne $script:NetworkFaultCleanup) {
                [string]$script:NetworkFaultCleanup.Address
            }
            else {
                ""
            }
        }
        displayRestore = [ordered]@{
            pending = $script:DisplayRestoreRequired
            originalCaptureTargetId =
                $script:OriginalCaptureTargetId
        }
        detail = $Detail
    }
    Write-Utf8NoBom `
        -Path $script:CleanupStatePath `
        -Text ($state | ConvertTo-Json -Depth 8)
}

function Start-LocalNetworkFault {
    if (-not $EnableNetworkFault) {
        throw "Network fault injection was not enabled."
    }

    if (-not (Test-IsAdministrator)) {
        throw (
            "Network fault injection requires an elevated controller. " +
            "No firewall change was made.")
    }

    $address = $script:ResolvedTargetAddress

    if (Test-IsWindows) {
        $displayName = "RemoteDesk soak $($script:RunId)"
        if (-not $PSCmdlet.ShouldProcess(
                "$address",
                "Install temporary outbound block rule $displayName")) {
            return $false
        }

        $script:NetworkFaultCleanup = [pscustomobject]@{
            Platform = "Windows"
            Identifier = $displayName
            Address = $address
        }
        $script:NetworkFaultActive = $true
        Write-CleanupState `
            -Detail (
                "If required, run: Remove-NetFirewallRule " +
                "-DisplayName '$displayName'")
        New-NetFirewallRule `
            -DisplayName $displayName `
            -Direction Outbound `
            -Action Block `
            -RemoteAddress $address `
            -Profile Any `
            -ErrorAction Stop | Out-Null
    }
    else {
        $iptables = Get-CommandSource @("iptables")
        if ([string]::IsNullOrWhiteSpace($iptables)) {
            throw (
                "iptables was not found; no Linux network fault was " +
                "injected.")
        }

        if (-not $PSCmdlet.ShouldProcess(
                "$address",
                "Insert temporary iptables OUTPUT drop rule")) {
            return $false
        }

        $script:NetworkFaultCleanup = [pscustomobject]@{
            Platform = "Linux"
            Identifier = $script:RunId
            Address = $address
            Command = $iptables
        }
        $script:NetworkFaultActive = $true
        Write-CleanupState `
            -Detail (
                "If required, run: iptables -D OUTPUT -d $address " +
                "-m comment --comment '$($script:RunId)' -j DROP")
        & $iptables `
            -I OUTPUT 1 `
            -d $address `
            -m comment `
            --comment $script:RunId `
            -j DROP
        if ($LASTEXITCODE -ne 0) {
            throw "iptables failed with exit code $LASTEXITCODE."
        }
    }

    return $true
}

function Stop-LocalNetworkFault {
    if (-not $script:NetworkFaultActive -or
        $null -eq $script:NetworkFaultCleanup) {
        return $true
    }

    $cleanup = $script:NetworkFaultCleanup
    try {
        if ($cleanup.Platform -eq "Windows") {
            $rules = @(
                Get-NetFirewallRule `
                    -DisplayName $cleanup.Identifier `
                    -ErrorAction SilentlyContinue)
            if ($rules.Count -gt 0) {
                $rules |
                    Remove-NetFirewallRule -ErrorAction Stop
            }
        }
        else {
            & $cleanup.Command `
                -C OUTPUT `
                -d $cleanup.Address `
                -m comment `
                --comment $cleanup.Identifier `
                -j DROP
            $checkExitCode = $LASTEXITCODE
            if ($checkExitCode -eq 0) {
                & $cleanup.Command `
                    -D OUTPUT `
                    -d $cleanup.Address `
                    -m comment `
                    --comment $cleanup.Identifier `
                    -j DROP
                if ($LASTEXITCODE -ne 0) {
                    throw (
                        "iptables cleanup failed with exit code " +
                        "$LASTEXITCODE.")
                }
            }
            elseif ($checkExitCode -ne 1) {
                throw (
                    "iptables cleanup check failed with exit code " +
                    "$checkExitCode.")
            }
        }

        $script:NetworkFaultActive = $false
        $script:NetworkFaultCleanup = $null
        Write-CleanupState `
            -Detail "No network cleanup is pending."
        return $true
    }
    catch {
        Write-CleanupState `
            -Detail $_.Exception.Message
        $script:ScenarioEvents.Add([pscustomobject]@{
            type = "networkFaultCleanup"
            occurredAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
            success = $false
            detail = $_.Exception.Message
        }) | Out-Null
        return $false
    }
}

function Invoke-NetworkFaultScenario {
    $startedAt = [DateTimeOffset]::UtcNow
    $eventRecord = [ordered]@{
        type = "networkFault"
        occurredAtUtc = $startedAt.ToString("O")
        requestedDurationSeconds = $NetworkFaultSeconds
        injected = $false
        failureObserved = $false
        cleanupSucceeded = $false
        recovered = $false
        recoverySeconds = $null
        detail = ""
    }
    try {
        $eventRecord.injected = Start-LocalNetworkFault
        if ($eventRecord.injected) {
            $faultClock = [Diagnostics.Stopwatch]::StartNew()
            while ($faultClock.Elapsed.TotalSeconds -lt
                $NetworkFaultSeconds) {
                $sample = Add-ConnectionSample `
                    -ExpectedFault $true `
                    -Phase "network-fault"
                if (-not $sample.tcpSuccess) {
                    $script:NetworkFaultObserved = $true
                    $eventRecord.failureObserved = $true
                }

                Start-Sleep -Milliseconds 500
            }
        }
        else {
            $eventRecord.detail =
                "Network fault injection was not approved."
        }
    }
    catch {
        $eventRecord.detail = $_.Exception.Message
    }
    finally {
        $eventRecord.cleanupSucceeded = Stop-LocalNetworkFault
    }

    if ($eventRecord.injected -and
        $eventRecord.cleanupSucceeded) {
        $recoveryClock = [Diagnostics.Stopwatch]::StartNew()
        while ($recoveryClock.Elapsed.TotalSeconds -lt
            $MaximumRecoverySeconds) {
            $sample = Add-ConnectionSample `
                -Phase "network-recovery"
            if ($sample.tcpSuccess) {
                $script:NetworkFaultRecovered = $true
                $script:NetworkFaultRecoverySeconds =
                    $recoveryClock.Elapsed.TotalSeconds
                $eventRecord.recovered = $true
                $eventRecord.recoverySeconds =
                    [Math]::Round(
                        $script:NetworkFaultRecoverySeconds,
                        3)
                break
            }

            Start-Sleep -Seconds 1
        }
    }

    $script:ScenarioEvents.Add(
        [pscustomobject]$eventRecord) | Out-Null
}

function Invoke-RemoteRestartScenario {
    $eventRecord = [ordered]@{
        type = "remoteRestart"
        occurredAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        commandAttempted = $false
        commandSucceeded = $false
        outageObserved = $false
        processRestartObserved = $false
        recovered = $false
        recoverySeconds = $null
        detail = ""
    }
    try {
        $beforeSnapshot = Get-RemoteProcessSnapshot
        $script:RemoteProcessSamples.Add(
            $beforeSnapshot) | Out-Null
        if (-not $beforeSnapshot.reachable -or
            $beforeSnapshot.processCount -le 0) {
            throw (
                "Cannot verify a remote restart without a live " +
                "pre-restart process sample.")
        }
        $beforeIdentity =
            @($beforeSnapshot.identities) -join "`n"
        $recoveryClock = [Diagnostics.Stopwatch]::StartNew()
        if ($PSCmdlet.ShouldProcess(
                $SshTarget,
                "Execute caller-supplied RemoteDesk restart command")) {
            $eventRecord.commandAttempted = $true
            $result = Invoke-Ssh `
                -Command $RemoteRestartCommand `
                -AllowFailure
            $eventRecord.commandSucceeded = $result.Success
            if (-not $result.Success) {
                $eventRecord.detail = (
                    "$($result.Detail): " +
                    ($result.Lines -join " "))
            }
        }

        if ($eventRecord.commandSucceeded) {
            while ($recoveryClock.Elapsed.TotalSeconds -lt
                $MaximumRecoverySeconds) {
                $sample = Add-ConnectionSample `
                    -ExpectedFault $true `
                    -Phase "remote-restart"
                if (-not $sample.tcpSuccess) {
                    $eventRecord.outageObserved = $true
                }

                $processSnapshot = Get-RemoteProcessSnapshot `
                    -ExpectedDisruption $true `
                    -Phase "remote-restart"
                $script:RemoteProcessSamples.Add(
                    $processSnapshot) | Out-Null
                $currentIdentity =
                    @($processSnapshot.identities) -join "`n"
                if ($processSnapshot.reachable -and
                    $processSnapshot.processCount -gt 0 -and
                    $currentIdentity -cne $beforeIdentity) {
                    $script:RemoteRestartObserved = $true
                    $eventRecord.processRestartObserved = $true
                }

                if ($sample.tcpSuccess -and
                    $script:RemoteRestartObserved) {
                    $script:RemoteRestartRecovered = $true
                    $script:RemoteRestartRecoverySeconds =
                        $recoveryClock.Elapsed.TotalSeconds
                    $eventRecord.recovered = $true
                    $eventRecord.recoverySeconds =
                        [Math]::Round(
                            $script:RemoteRestartRecoverySeconds,
                            3)
                    break
                }

                Start-Sleep -Seconds 1
            }
        }
    }
    catch {
        $eventRecord.detail = $_.Exception.Message
    }

    $script:ScenarioEvents.Add(
        [pscustomobject]$eventRecord) | Out-Null
}

function Invoke-DisplaySwitchScenario {
    if ([string]::IsNullOrWhiteSpace(
            $script:OriginalCaptureTargetId)) {
        throw (
            "The original capture target is unknown; refusing to " +
            "change the remote display selection.")
    }
    foreach ($targetId in $CaptureTargetIds) {
        if (-not $PSCmdlet.ShouldProcess(
                $targetId,
                "Select remote capture target")) {
            $script:DisplayChecks.Add([pscustomobject]@{
                checkedAtUtc =
                    [DateTimeOffset]::UtcNow.ToString("O")
                targetId = $targetId
                selectedTargetId = ""
                frameCount = 0
                success = $false
                detail = "Display selection was not approved."
            }) | Out-Null
            continue
        }
        if (-not $script:DisplayRestoreRequired) {
            $script:DisplayRestoreRequired = $true
            Write-CleanupState `
                -Detail (
                    "Restore capture target " +
                    "'$($script:OriginalCaptureTargetId)' if this run " +
                    "is terminated before automatic cleanup.")
        }
        $probe = Invoke-ProtocolProbe `
            -SelectTargetId $targetId `
            -Purpose "display-switch"
        $selectedId = if (
            $null -ne $probe.Record.selectedTarget) {
            [string]$probe.Record.selectedTarget.id
        }
        else {
            ""
        }
        $passed = $probe.Record.success -and
            $selectedId -ceq $targetId -and
            $probe.Record.frameCount -gt 0
        $script:DisplayChecks.Add([pscustomobject]@{
            checkedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
            targetId = $targetId
            selectedTargetId = $selectedId
            frameCount = $probe.Record.frameCount
            success = $passed
            detail = if ($passed) {
                "target selected and produced a frame"
            }
            else {
                [string]$probe.Record.error
            }
        }) | Out-Null
    }

}

function Restore-OriginalCaptureTarget {
    if (-not $script:DisplayRestoreRequired) {
        return $true
    }

    $probe = Invoke-ProtocolProbe `
        -SelectTargetId $script:OriginalCaptureTargetId `
        -Purpose "display-restore"
    $selectedId = if (
        $null -ne $probe.Record.selectedTarget) {
        [string]$probe.Record.selectedTarget.id
    }
    else {
        ""
    }
    $script:DisplayRestoreSucceeded =
        $probe.Record.success -and
        $selectedId -ceq $script:OriginalCaptureTargetId
    if ($script:DisplayRestoreSucceeded) {
        $script:DisplayRestoreRequired = $false
    }
    Write-CleanupState `
        -Detail $(if ($script:DisplayRestoreSucceeded) {
            "The original capture target was restored."
        }
        else {
            "Restore capture target " +
            "'$($script:OriginalCaptureTargetId)' manually."
        })

    $script:ScenarioEvents.Add([pscustomobject]@{
        type = "displayRestore"
        occurredAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        success = $script:DisplayRestoreSucceeded
        detail = if ($script:DisplayRestoreSucceeded) {
            "restored $($script:OriginalCaptureTargetId)"
        }
        else {
            "failed to restore $($script:OriginalCaptureTargetId)"
        }
    }) | Out-Null
    return $script:DisplayRestoreSucceeded
}

function Collect-GpuRecoveryEvents {
    if ($SkipGpuEventCollection -or
        [string]::IsNullOrWhiteSpace($SshTarget)) {
        return
    }

    if ($script:ResolvedRemotePlatform -eq "Windows") {
        $startText =
            $script:StartedAtUtc.UtcDateTime.ToString("O")
        $remoteScript = @"
`$start = [DateTime]::Parse(
    '$startText',
    [Globalization.CultureInfo]::InvariantCulture,
    [Globalization.DateTimeStyles]::RoundtripKind)
`$pattern = 'display|nvlddmkm|amdkmdag|amdwddmg|igfx|dxgkrnl|gpu'
`$messagePattern = 'reset|recover|stopped responding|xid|hang|tdr|device removed'
try {
    `$allEvents = @(
        Get-WinEvent -FilterHashtable @{
            LogName = 'System'
            StartTime = `$start
        } -ErrorAction Stop)
    `$events = @(
        `$allEvents |
            Where-Object {
                `$_.ProviderName -match `$pattern -and
                (`$_.Id -eq 4101 -or
                    `$_.Message -match `$messagePattern)
            } |
            Select-Object TimeCreated, Id, ProviderName,
                LevelDisplayName, Message)
    [ordered]@{
        ok = `$true
        error = ''
        events = `$events
    } | ConvertTo-Json -Compress -Depth 5
}
catch {
    if (`$_.FullyQualifiedErrorId -match
        '^NoMatchingEventsFound') {
        [ordered]@{
            ok = `$true
            error = ''
            events = @()
        } | ConvertTo-Json -Compress -Depth 5
    }
    else {
        [ordered]@{
            ok = `$false
            error = `$_.Exception.Message
            events = @()
        } | ConvertTo-Json -Compress -Depth 5
    }
}
"@
        $result = Invoke-RemoteWindowsPowerShell `
            -Script $remoteScript `
            -AllowFailure
        if ($result.Success) {
            $text = @(
                $result.Lines |
                    Where-Object {
                        $_ -notmatch "^#< CLIXML" -and
                        -not [string]::IsNullOrWhiteSpace($_)
                    }) -join "`n"
            if ([string]::IsNullOrWhiteSpace($text)) {
                $script:GpuCollectionErrors.Add([pscustomobject]@{
                    platform = "Windows"
                    occurredAtUtc =
                        [DateTimeOffset]::UtcNow.ToString("O")
                    message = "GPU event query returned no result."
                }) | Out-Null
            }
            else {
                try {
                    $parsed = $text | ConvertFrom-Json -ErrorAction Stop
                    if ($parsed.ok -ne $true) {
                        $script:GpuCollectionErrors.Add(
                            [pscustomobject]@{
                                platform = "Windows"
                                occurredAtUtc =
                                    [DateTimeOffset]::UtcNow.ToString("O")
                                message = [string]$parsed.error
                            }) | Out-Null
                    }
                    else {
                        foreach ($eventItem in @($parsed.events)) {
                            $script:GpuEvents.Add([pscustomobject]@{
                                platform = "Windows"
                                occurredAt =
                                    [string]$eventItem.TimeCreated
                                id = $eventItem.Id
                                provider =
                                    [string]$eventItem.ProviderName
                                level =
                                    [string]$eventItem.LevelDisplayName
                                message = [string]$eventItem.Message
                            }) | Out-Null
                        }
                    }
                }
                catch {
                    $script:GpuCollectionErrors.Add([pscustomobject]@{
                        platform = "Windows"
                        occurredAtUtc =
                            [DateTimeOffset]::UtcNow.ToString("O")
                        message = (
                            "GPU event JSON parse failed: " +
                            $_.Exception.Message)
                    }) | Out-Null
                }
            }
        }
        else {
            $script:GpuCollectionErrors.Add([pscustomobject]@{
                platform = "Windows"
                occurredAtUtc =
                    [DateTimeOffset]::UtcNow.ToString("O")
                message = $result.Detail
            }) | Out-Null
        }
    }
    elseif ($script:ResolvedRemotePlatform -eq "Linux") {
        $sinceEpoch = $script:StartedAtUtc.ToUnixTimeSeconds()
        $command = (
            "command -v journalctl >/dev/null 2>&1 || exit 127; " +
            "journalctl --since '@$sinceEpoch' -k --no-pager " +
            "-o short-iso")
        $result = Invoke-Ssh -Command $command -AllowFailure
        if ($result.Success) {
            $collectionWarnings = @(
                $result.Lines |
                    Where-Object {
                        $_ -match (
                            "permission denied|not permitted|" +
                            "no journal files were opened")
                    })
            if ($collectionWarnings.Count -gt 0) {
                $script:GpuCollectionErrors.Add([pscustomobject]@{
                    platform = "Linux"
                    occurredAtUtc =
                        [DateTimeOffset]::UtcNow.ToString("O")
                    message = $collectionWarnings -join " "
                }) | Out-Null
            }
            else {
                foreach ($line in @($result.Lines)) {
                    if ($line -match
                            "drm|nvrm|nvidia|xid|amdgpu|i915|nouveau" -and
                        $line -match
                            "reset|recover|hang|xid|fault|timeout|wedged") {
                        $script:GpuEvents.Add([pscustomobject]@{
                            platform = "Linux"
                            occurredAt = ""
                            id = $null
                            provider = "kernel"
                            level = ""
                            message = [string]$line
                        }) | Out-Null
                    }
                }
            }
        }
        else {
            $script:GpuCollectionErrors.Add([pscustomobject]@{
                platform = "Linux"
                occurredAtUtc =
                    [DateTimeOffset]::UtcNow.ToString("O")
                message = $result.Detail
            }) | Out-Null
        }
    }
    else {
        $script:GpuCollectionErrors.Add([pscustomobject]@{
            platform = $script:ResolvedRemotePlatform
            occurredAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
            message = "Remote platform could not be resolved."
        }) | Out-Null
    }
}

function Add-Gate {
    param(
        [string]$Name,
        [bool]$Passed,
        [object]$Actual,
        [object]$Threshold,
        [string]$Detail = ""
    )

    $script:Gates.Add([pscustomobject]@{
        name = $Name
        passed = $Passed
        actual = $Actual
        threshold = $Threshold
        detail = $Detail
    }) | Out-Null
}

function Get-MaximumConsecutiveFailures {
    param([object[]]$ConnectionSamples)

    $maximum = 0
    $current = 0
    foreach ($sample in $ConnectionSamples) {
        if ($sample.expectedFault) {
            continue
        }

        if ($sample.tcpSuccess) {
            $current = 0
        }
        else {
            $current++
            $maximum = [Math]::Max($maximum, $current)
        }
    }

    return $maximum
}

function Get-MaximumUnexpectedOutageSeconds {
    param([object[]]$ConnectionSamples)

    $maximum = 0.0
    $outageStartedAt = $null
    $previousAt = $null
    foreach ($sample in $ConnectionSamples) {
        if ($sample.expectedFault) {
            continue
        }

        $sampledAt = [DateTimeOffset]::Parse($sample.sampledAtUtc)
        if (-not $sample.tcpSuccess) {
            if ($null -eq $outageStartedAt) {
                $outageStartedAt = $sampledAt
            }
        }
        elseif ($null -ne $outageStartedAt) {
            $maximum = [Math]::Max(
                $maximum,
                ($sampledAt - $outageStartedAt).TotalSeconds)
            $outageStartedAt = $null
        }

        $previousAt = $sampledAt
    }

    if ($null -ne $outageStartedAt -and
        $null -ne $previousAt) {
        $maximum = [Math]::Max(
            $maximum,
            ($previousAt - $outageStartedAt).TotalSeconds +
                $ProbeIntervalSeconds)
    }

    return $maximum
}

function Get-UnexpectedProcessRestartCount {
    $count = 0
    $previousIdentity = ""
    foreach ($sample in $script:RemoteProcessSamples) {
        if (-not $sample.reachable -or
            $sample.processCount -le 0) {
            continue
        }

        $identity = @($sample.identities) -join "`n"
        if ($sample.expectedDisruption) {
            $previousIdentity = $identity
            continue
        }
        if (-not [string]::IsNullOrWhiteSpace($previousIdentity) -and
            $identity -cne $previousIdentity) {
            $count++
        }

        $previousIdentity = $identity
    }

    return $count
}

function Get-MaximumObservationGapSeconds {
    param(
        [object[]]$Observations,
        [string]$TimestampProperty
    )

    $maximum = 0.0
    $previousAt = $script:StartedAtUtc
    foreach ($observation in @(
            $Observations |
                Sort-Object -Property $TimestampProperty)) {
        $observedAt = [DateTimeOffset]::Parse(
            [string]$observation.$TimestampProperty)
        $maximum = [Math]::Max(
            $maximum,
            ($observedAt - $previousAt).TotalSeconds)
        $previousAt = $observedAt
    }

    $maximum = [Math]::Max(
        $maximum,
        ([DateTimeOffset]::UtcNow - $previousAt).TotalSeconds)
    return $maximum
}

function Complete-Gates {
    $script:Gates.Clear()
    if ($PlanOnly) {
        Add-Gate `
            -Name "Plan validation" `
            -Passed $true `
            -Actual "valid" `
            -Threshold "valid" `
            -Detail "No connection or state-changing action was executed."
        return
    }

    $normalSamples = @(
        $script:Samples |
            Where-Object { -not $_.expectedFault })
    $successfulSamples = @(
        $normalSamples |
            Where-Object { $_.tcpSuccess }).Count
    $availability = if ($normalSamples.Count -gt 0) {
        100.0 * $successfulSamples / $normalSamples.Count
    }
    else {
        0.0
    }
    Add-Gate `
        -Name "TCP availability" `
        -Passed ($availability -ge $MinimumAvailabilityPercent) `
        -Actual ([Math]::Round($availability, 4)) `
        -Threshold ">= $MinimumAvailabilityPercent%"

    $minimumSamples = [Math]::Max(
        1,
        [Math]::Floor(
            ($DurationMinutes * 60.0 / $ProbeIntervalSeconds) *
                0.90))
    Add-Gate `
        -Name "Monitoring sample coverage" `
        -Passed ($normalSamples.Count -ge $minimumSamples) `
        -Actual $normalSamples.Count `
        -Threshold ">= $minimumSamples"
    $maximumSamplingGap = Get-MaximumObservationGapSeconds `
        -Observations @($script:Samples) `
        -TimestampProperty "sampledAtUtc"
    Add-Gate `
        -Name "Maximum monitoring sample gap" `
        -Passed (
            $maximumSamplingGap -le $MaximumSamplingGapSeconds) `
        -Actual ([Math]::Round($maximumSamplingGap, 3)) `
        -Threshold "<= $MaximumSamplingGapSeconds seconds"

    $consecutiveFailures =
        Get-MaximumConsecutiveFailures $normalSamples
    Add-Gate `
        -Name "Maximum consecutive TCP failures" `
        -Passed (
            $consecutiveFailures -le
                $MaximumConsecutiveFailures) `
        -Actual $consecutiveFailures `
        -Threshold "<= $MaximumConsecutiveFailures"

    $maximumOutage =
        Get-MaximumUnexpectedOutageSeconds $normalSamples
    Add-Gate `
        -Name "Maximum unexpected outage" `
        -Passed (
            $maximumOutage -le
                $MaximumUnexpectedOutageSeconds) `
        -Actual ([Math]::Round($maximumOutage, 3)) `
        -Threshold "<= $MaximumUnexpectedOutageSeconds seconds"

    if ($script:ProtocolProbes.Count -gt 0) {
        $protocolSuccesses = @(
            $script:ProtocolProbes |
                Where-Object { $_.success }).Count
        $protocolPercent =
            100.0 * $protocolSuccesses /
                $script:ProtocolProbes.Count
        Add-Gate `
            -Name "Authenticated protocol probe success" `
            -Passed (
                $protocolPercent -ge
                    $MinimumProtocolSuccessPercent) `
            -Actual ([Math]::Round($protocolPercent, 4)) `
            -Threshold ">= $MinimumProtocolSuccessPercent%"
    }

    if (-not [string]::IsNullOrWhiteSpace($SshTarget)) {
        $normalProcessSamples = @(
            $script:RemoteProcessSamples |
                Where-Object {
                    -not $_.expectedDisruption
                })
        $reachableProcessSamples = @(
            $normalProcessSamples |
                Where-Object {
                    $_.reachable -and
                    $_.processCount -gt 0
                })
        $minimumRemoteSamples = [Math]::Max(
            1,
            [Math]::Floor(
                ($DurationMinutes * 60.0 /
                    $RemoteObservationIntervalSeconds) * 0.90))
        Add-Gate `
            -Name "Remote process sample coverage" `
            -Passed (
                $normalProcessSamples.Count -ge
                    $minimumRemoteSamples) `
            -Actual $normalProcessSamples.Count `
            -Threshold ">= $minimumRemoteSamples"
        $remoteProcessAvailability = if (
            $normalProcessSamples.Count -gt 0) {
            100.0 * $reachableProcessSamples.Count /
                $normalProcessSamples.Count
        }
        else {
            0.0
        }
        Add-Gate `
            -Name "Remote process availability" `
            -Passed (
                $remoteProcessAvailability -ge
                    $MinimumRemoteProcessAvailabilityPercent) `
            -Actual (
                [Math]::Round($remoteProcessAvailability, 4)) `
            -Threshold (
                ">= $MinimumRemoteProcessAvailabilityPercent%")
        $maximumRemoteObservationGap =
            Get-MaximumObservationGapSeconds `
                -Observations @($script:RemoteProcessSamples) `
                -TimestampProperty "sampledAtUtc"
        Add-Gate `
            -Name "Maximum remote observation gap" `
            -Passed (
                $maximumRemoteObservationGap -le
                    $MaximumRemoteObservationGapSeconds) `
            -Actual (
                [Math]::Round(
                    $maximumRemoteObservationGap,
                    3)) `
            -Threshold (
                "<= $MaximumRemoteObservationGapSeconds seconds")

        $unexpectedRestarts =
            Get-UnexpectedProcessRestartCount
        Add-Gate `
            -Name "Unexpected remote process restarts" `
            -Passed (
                $unexpectedRestarts -le
                    $MaximumUnexpectedProcessRestarts) `
            -Actual $unexpectedRestarts `
            -Threshold "<= $MaximumUnexpectedProcessRestarts"
    }

    if (-not $SkipGpuEventCollection -and
        -not [string]::IsNullOrWhiteSpace($SshTarget)) {
        Add-Gate `
            -Name "GPU event collection" `
            -Passed ($script:GpuCollectionErrors.Count -eq 0) `
            -Actual $script:GpuCollectionErrors.Count `
            -Threshold "0 collection errors"
        Add-Gate `
            -Name "GPU recovery/reset events" `
            -Passed (
                $script:GpuEvents.Count -le
                    $MaximumGpuRecoveryEvents) `
            -Actual $script:GpuEvents.Count `
            -Threshold "<= $MaximumGpuRecoveryEvents"
    }

    if ($EnableNetworkFault) {
        Add-Gate `
            -Name "Network fault was observed" `
            -Passed $script:NetworkFaultObserved `
            -Actual $script:NetworkFaultObserved `
            -Threshold $true
        Add-Gate `
            -Name "Network fault recovery" `
            -Passed (
                $script:NetworkFaultRecovered -and
                $script:NetworkFaultRecoverySeconds -le
                    $MaximumRecoverySeconds) `
            -Actual $script:NetworkFaultRecoverySeconds `
            -Threshold "<= $MaximumRecoverySeconds seconds"
        Add-Gate `
            -Name "Network fault cleanup" `
            -Passed (-not $script:NetworkFaultActive) `
            -Actual (-not $script:NetworkFaultActive) `
            -Threshold $true
    }

    if ($EnableRemoteRestart) {
        Add-Gate `
            -Name "Remote process restart observed" `
            -Passed $script:RemoteRestartObserved `
            -Actual $script:RemoteRestartObserved `
            -Threshold $true
        Add-Gate `
            -Name "Remote restart recovery" `
            -Passed (
                $script:RemoteRestartRecovered -and
                $script:RemoteRestartRecoverySeconds -le
                    $MaximumRecoverySeconds) `
            -Actual $script:RemoteRestartRecoverySeconds `
            -Threshold "<= $MaximumRecoverySeconds seconds"
    }

    if ($EnableDisplaySwitch) {
        $displayFailures = @(
            $script:DisplayChecks |
                Where-Object { -not $_.success })
        $displayPassedCount =
            $script:DisplayChecks.Count - $displayFailures.Count
        Add-Gate `
            -Name "Multi-display switching" `
            -Passed (
                $script:DisplayChecks.Count -eq
                    $CaptureTargetIds.Count -and
                $displayFailures.Count -eq 0) `
            -Actual "$displayPassedCount/$($CaptureTargetIds.Count)" `
            -Threshold "all requested targets produce a frame"
        Add-Gate `
            -Name "Original display restored" `
            -Passed (
                $script:DisplayRestoreSucceeded -and
                -not $script:DisplayRestoreRequired) `
            -Actual $script:DisplayRestoreSucceeded `
            -Threshold $true
    }
}

function New-ReportObject {
    Complete-Gates
    $finishedAt = [DateTimeOffset]::UtcNow
    $passed = @(
        $script:Gates |
            Where-Object { -not $_.passed }).Count -eq 0 -and
        $script:RunStatus -notin @("Failed", "Interrupted")
    return [ordered]@{
        schemaVersion = 1
        runId = $script:RunId
        status = $script:RunStatus
        passed = $passed
        startedAtUtc = $script:StartedAtUtc.ToString("O")
        finishedAtUtc = $finishedAt.ToString("O")
        elapsedSeconds = (
            $finishedAt - $script:StartedAtUtc).TotalSeconds
        target = [ordered]@{
            requested = $Target
            resolvedAddress = $script:ResolvedTargetAddress
            port = $Port
            sshTarget = $SshTarget
            sshUsesDedicatedKnownHostsFile =
                -not [string]::IsNullOrWhiteSpace(
                    $SshUserKnownHostsFile)
            remotePlatform = $script:ResolvedRemotePlatform
            remoteProcessName = $RemoteProcessName
        }
        plan = [ordered]@{
            durationMinutes = $DurationMinutes
            probeIntervalSeconds = $ProbeIntervalSeconds
            protocolProbeIntervalMinutes =
                $ProtocolProbeIntervalMinutes
            networkFaultEnabled = [bool]$EnableNetworkFault
            networkFaultAtMinute = $NetworkFaultAtMinute
            networkFaultSeconds = $NetworkFaultSeconds
            remoteRestartEnabled = [bool]$EnableRemoteRestart
            remoteRestartAtMinute = $RemoteRestartAtMinute
            displaySwitchEnabled = [bool]$EnableDisplaySwitch
            displaySwitchAtMinute = $DisplaySwitchAtMinute
            captureTargetIds = @($CaptureTargetIds)
            gpuEventCollectionEnabled =
                -not $SkipGpuEventCollection
            planOnly = [bool]$PlanOnly
        }
        thresholds = [ordered]@{
            minimumAvailabilityPercent =
                $MinimumAvailabilityPercent
            minimumProtocolSuccessPercent =
                $MinimumProtocolSuccessPercent
            minimumRemoteProcessAvailabilityPercent =
                $MinimumRemoteProcessAvailabilityPercent
            maximumConsecutiveFailures =
                $MaximumConsecutiveFailures
            maximumUnexpectedOutageSeconds =
                $MaximumUnexpectedOutageSeconds
            maximumSamplingGapSeconds =
                $MaximumSamplingGapSeconds
            maximumRemoteObservationGapSeconds =
                $MaximumRemoteObservationGapSeconds
            maximumRecoverySeconds =
                $MaximumRecoverySeconds
            maximumGpuRecoveryEvents =
                $MaximumGpuRecoveryEvents
            maximumUnexpectedProcessRestarts =
                $MaximumUnexpectedProcessRestarts
        }
        gates = @($script:Gates)
        connectionSamples = @($script:Samples)
        protocolProbes = @($script:ProtocolProbes)
        remoteProcessSamples = @($script:RemoteProcessSamples)
        gpuEvents = @($script:GpuEvents)
        gpuCollectionErrors = @($script:GpuCollectionErrors)
        displayChecks = @($script:DisplayChecks)
        scenarioEvents = @($script:ScenarioEvents)
        cleanup = [ordered]@{
            networkFaultActive = $script:NetworkFaultActive
            displayRestoreRequired =
                $script:DisplayRestoreRequired
            cleanupStatePath = $script:CleanupStatePath
        }
        failureDetail = $script:FailureDetail
    }
}

function Convert-ReportToMarkdown {
    param([object]$Report)

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("# RemoteDesk Long-Soak Regression") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add(
        "- Result: " +
        $(if ($Report.passed) { "PASS" } else { "FAIL" })) |
        Out-Null
    $lines.Add("- Status: $($Report.status)") | Out-Null
    $lines.Add("- Run ID: $($Report.runId)") | Out-Null
    $lines.Add(
        "- Target: $($Report.target.resolvedAddress):" +
        "$($Report.target.port)") | Out-Null
    $lines.Add(
        "- Window: $($Report.startedAtUtc) to " +
        "$($Report.finishedAtUtc)") | Out-Null
    $lines.Add(
        "- Planned duration: " +
        "$($Report.plan.durationMinutes) minutes") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add("## Gates") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add("| Gate | Result | Actual | Threshold | Detail |") |
        Out-Null
    $lines.Add("|---|---:|---:|---:|---|") | Out-Null
    foreach ($gate in $Report.gates) {
        $lines.Add(
            "| $(Format-MarkdownCell $gate.name) | " +
            "$(if ($gate.passed) { "PASS" } else { "FAIL" }) | " +
            "$(Format-MarkdownCell $gate.actual) | " +
            "$(Format-MarkdownCell $gate.threshold) | " +
            "$(Format-MarkdownCell $gate.detail) |") | Out-Null
    }

    $lines.Add("") | Out-Null
    $lines.Add("## Evidence summary") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add(
        "- Connection samples: " +
        "$(@($Report.connectionSamples).Count)") | Out-Null
    $lines.Add(
        "- Authenticated protocol probes: " +
        "$(@($Report.protocolProbes).Count)") | Out-Null
    $lines.Add(
        "- Remote process samples: " +
        "$(@($Report.remoteProcessSamples).Count)") | Out-Null
    $lines.Add(
        "- GPU recovery/reset events: " +
        "$(@($Report.gpuEvents).Count)") | Out-Null
    $lines.Add(
        "- GPU collection errors: " +
        "$(@($Report.gpuCollectionErrors).Count)") | Out-Null
    $lines.Add(
        "- Display checks: " +
        "$(@($Report.displayChecks).Count)") | Out-Null
    $lines.Add(
        "- Scenario events: " +
        "$(@($Report.scenarioEvents).Count)") | Out-Null
    if (-not [string]::IsNullOrWhiteSpace(
            $Report.failureDetail)) {
        $lines.Add("") | Out-Null
        $lines.Add("## Failure detail") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add($Report.failureDetail) | Out-Null
    }

    $lines.Add("") | Out-Null
    $lines.Add("## Safety") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add(
        "- State-changing scenarios require explicit enable switches.") |
        Out-Null
    $lines.Add(
        "- Temporary network rules and display selection are restored in finally.") |
        Out-Null
    $lines.Add(
        "- Cleanup checkpoint: $($Report.cleanup.cleanupStatePath)") |
        Out-Null
    return $lines -join [Environment]::NewLine
}

function Save-Report {
    $report = New-ReportObject
    $script:LastReport = $report
    Write-Utf8NoBom `
        -Path $script:JsonReportPath `
        -Text ($report | ConvertTo-Json -Depth 20)
    Write-Utf8NoBom `
        -Path $script:MarkdownReportPath `
        -Text (Convert-ReportToMarkdown -Report $report)
    Write-Utf8NoBom `
        -Path $script:CheckpointPath `
        -Text ($report | ConvertTo-Json -Depth 20)
}

function Save-Checkpoint {
    $checkpoint = [ordered]@{
        schemaVersion = 1
        runId = $script:RunId
        status = $script:RunStatus
        updatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        samples = @($script:Samples)
        protocolProbes = @($script:ProtocolProbes)
        remoteProcessSamples =
            @($script:RemoteProcessSamples)
        scenarioEvents = @($script:ScenarioEvents)
        cleanup = [ordered]@{
            networkFaultActive =
                $script:NetworkFaultActive
            displayRestoreRequired =
                $script:DisplayRestoreRequired
        }
    }
    Write-Utf8NoBom `
        -Path $script:CheckpointPath `
        -Text ($checkpoint | ConvertTo-Json -Depth 16)
}

function Assert-ValidPlan {
    if ($EnableNetworkFault -and
        $NetworkFaultAtMinute -ge $DurationMinutes) {
        throw (
            "NetworkFaultAtMinute must be earlier than " +
            "DurationMinutes.")
    }

    if ($EnableRemoteRestart) {
        if ($RemoteRestartAtMinute -ge $DurationMinutes) {
            throw (
                "RemoteRestartAtMinute must be earlier than " +
                "DurationMinutes.")
        }

        if ([string]::IsNullOrWhiteSpace(
                $RemoteRestartCommand)) {
            throw (
                "-EnableRemoteRestart requires the explicit " +
                "-RemoteRestartCommand.")
        }

        if ([string]::IsNullOrWhiteSpace($SshTarget)) {
            throw (
                "-EnableRemoteRestart requires -SshTarget.")
        }
    }
    elseif (-not [string]::IsNullOrWhiteSpace(
            $RemoteRestartCommand)) {
        throw (
            "-RemoteRestartCommand is ignored unless " +
            "-EnableRemoteRestart is set.")
    }

    if ($EnableDisplaySwitch) {
        if ($DisplaySwitchAtMinute -ge $DurationMinutes) {
            throw (
                "DisplaySwitchAtMinute must be earlier than " +
                "DurationMinutes.")
        }

        if ($CaptureTargetIds.Count -lt 2) {
            throw (
                "-EnableDisplaySwitch requires at least two explicit " +
                "-CaptureTargetIds.")
        }

        $invalidTargetIds = @(
            $CaptureTargetIds |
                Where-Object {
                    [string]::IsNullOrWhiteSpace($_)
                })
        if ($invalidTargetIds.Count -gt 0 -or
            @($CaptureTargetIds | Sort-Object -Unique).Count -ne
                $CaptureTargetIds.Count) {
            throw (
                "-CaptureTargetIds must be non-empty and unique.")
        }

        if ([string]::IsNullOrWhiteSpace(
                $PasswordEnvironmentVariable)) {
            throw (
                "-EnableDisplaySwitch requires " +
                "-PasswordEnvironmentVariable.")
        }
    }

    $eventMinutes = @()
    if ($EnableDisplaySwitch) {
        $eventMinutes += $DisplaySwitchAtMinute
    }
    if ($EnableNetworkFault) {
        $eventMinutes += $NetworkFaultAtMinute
    }
    if ($EnableRemoteRestart) {
        $eventMinutes += $RemoteRestartAtMinute
    }
    if (@($eventMinutes | Sort-Object -Unique).Count -ne
        $eventMinutes.Count) {
        throw (
            "State-changing scenarios must use distinct minute offsets.")
    }
}

$root = Resolve-RepoRoot
$safeTarget = ConvertTo-SafeFileName $Target
$stamp = $script:StartedAtUtc.ToString("yyyyMMdd-HHmmss")
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path (
        Join-Path $root "artifacts") (
        Join-Path "soak" "$stamp-$safeTarget")
}
$outputFullPath = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputFullPath -Force |
    Out-Null
$script:JsonReportPath = Join-Path (
    $outputFullPath) "RemoteDesk-soak-report.json"
$script:MarkdownReportPath = Join-Path (
    $outputFullPath) "RemoteDesk-soak-report.md"
$script:CheckpointPath = Join-Path (
    $outputFullPath) "RemoteDesk-soak-checkpoint.json"
$script:CleanupStatePath = Join-Path (
    $outputFullPath) "RemoteDesk-soak-cleanup.json"

$password = ""
$caughtException = $null
try {
    Assert-ValidPlan
    $script:ResolvedTargetAddress =
        Resolve-TargetIPv4 $Target
    $password = Get-Password
    if (-not $PlanOnly) {
        $script:ResolvedRemotePlatform =
            Resolve-RemotePlatform
    }
    Write-CleanupState `
        -Detail "No cleanup is pending."

    if ($PlanOnly) {
        $script:RunStatus = "Planned"
    }
    else {
        Write-Host (
            "RemoteDesk soak started: target=" +
            "$($script:ResolvedTargetAddress):$Port, " +
            "duration=$DurationMinutes minutes, " +
            "runId=$($script:RunId)") -ForegroundColor Cyan

        if (-not [string]::IsNullOrWhiteSpace($password)) {
            $baseline = Invoke-ProtocolProbe `
                -Purpose "baseline"
            if ($null -ne $baseline.Record.selectedTarget) {
                $script:OriginalCaptureTargetId =
                    [string]$baseline.Record.selectedTarget.id
            }
        }
        if ($EnableDisplaySwitch -and
            [string]::IsNullOrWhiteSpace(
                $script:OriginalCaptureTargetId)) {
            throw (
                "The baseline probe did not identify the original " +
                "capture target; no display selection was changed.")
        }

        if (-not [string]::IsNullOrWhiteSpace($SshTarget)) {
            $processSample = Get-RemoteProcessSnapshot
            $script:RemoteProcessSamples.Add(
                $processSample) | Out-Null
        }

        $clock = [Diagnostics.Stopwatch]::StartNew()
        $nextConnectionSampleAt = [TimeSpan]::Zero
        $nextProtocolProbeAt =
            [TimeSpan]::FromMinutes(
                $ProtocolProbeIntervalMinutes)
        $nextRemoteObservationAt =
            [TimeSpan]::FromSeconds(
                $RemoteObservationIntervalSeconds)
        $nextCheckpointAt = [TimeSpan]::FromMinutes(1)
        $displaySwitchExecuted = $false
        $networkFaultExecuted = $false
        $remoteRestartExecuted = $false
        $duration = [TimeSpan]::FromMinutes($DurationMinutes)

        while ($clock.Elapsed -lt $duration) {
            $elapsed = $clock.Elapsed
            if ($elapsed -ge $nextConnectionSampleAt) {
                $sample = Add-ConnectionSample
                Write-Host (
                    "[{0,7:F1}s] TCP={1} {2:F1}ms ICMP={3}" -f
                        $elapsed.TotalSeconds,
                        $(if ($sample.tcpSuccess) { "OK" } else { "FAIL" }),
                        $sample.tcpLatencyMilliseconds,
                        $(if (-not $sample.icmpAttempted) {
                            "skip"
                        }
                        elseif ($sample.icmpSuccess) {
                            "$($sample.icmpLatencyMilliseconds)ms"
                        }
                        else {
                            "fail"
                        }))
                $nextConnectionSampleAt =
                    $elapsed +
                    [TimeSpan]::FromSeconds(
                        $ProbeIntervalSeconds)
            }

            if (-not [string]::IsNullOrWhiteSpace($password) -and
                $elapsed -ge $nextProtocolProbeAt) {
                Invoke-ProtocolProbe `
                    -Purpose "periodic" | Out-Null
                $nextProtocolProbeAt =
                    $elapsed +
                    [TimeSpan]::FromMinutes(
                        $ProtocolProbeIntervalMinutes)
            }

            if (-not [string]::IsNullOrWhiteSpace($SshTarget) -and
                $elapsed -ge $nextRemoteObservationAt) {
                $processSample = Get-RemoteProcessSnapshot
                $script:RemoteProcessSamples.Add(
                    $processSample) | Out-Null
                $nextRemoteObservationAt =
                    $elapsed +
                    [TimeSpan]::FromSeconds(
                        $RemoteObservationIntervalSeconds)
            }

            if ($EnableDisplaySwitch -and
                -not $displaySwitchExecuted -and
                $elapsed.TotalMinutes -ge
                    $DisplaySwitchAtMinute) {
                $displaySwitchExecuted = $true
                Invoke-DisplaySwitchScenario
            }

            if ($EnableNetworkFault -and
                -not $networkFaultExecuted -and
                $elapsed.TotalMinutes -ge
                    $NetworkFaultAtMinute) {
                $networkFaultExecuted = $true
                Invoke-NetworkFaultScenario
            }

            if ($EnableRemoteRestart -and
                -not $remoteRestartExecuted -and
                $elapsed.TotalMinutes -ge
                    $RemoteRestartAtMinute) {
                $remoteRestartExecuted = $true
                Invoke-RemoteRestartScenario
            }

            if ($elapsed -ge $nextCheckpointAt) {
                Save-Checkpoint
                $nextCheckpointAt =
                    $elapsed +
                    [TimeSpan]::FromMinutes(1)
            }

            Start-Sleep -Milliseconds 200
        }

        $script:RunStatus = "Completed"
    }
}
catch [System.Management.Automation.PipelineStoppedException] {
    $script:Interrupted = $true
    $script:RunStatus = "Interrupted"
    $script:FailureDetail = "The run was interrupted by the operator."
    $caughtException = $_
}
catch {
    $script:RunStatus = "Failed"
    $script:FailureDetail = $_.Exception.Message
    $caughtException = $_
}
finally {
    [void](Stop-LocalNetworkFault)
    if ($EnableDisplaySwitch -and
        -not [string]::IsNullOrWhiteSpace($password)) {
        try {
            [void](Restore-OriginalCaptureTarget)
        }
        catch {
            $script:DisplayRestoreRequired = $true
            Write-CleanupState `
                -Detail (
                    "Restore capture target " +
                    "'$($script:OriginalCaptureTargetId)' manually: " +
                    $_.Exception.Message)
            $script:ScenarioEvents.Add([pscustomobject]@{
                type = "displayRestore"
                occurredAtUtc =
                    [DateTimeOffset]::UtcNow.ToString("O")
                success = $false
                detail = $_.Exception.Message
            }) | Out-Null
        }
    }

    if (-not $PlanOnly) {
        try {
            Collect-GpuRecoveryEvents
        }
        catch {
            $script:GpuCollectionErrors.Add([pscustomobject]@{
                platform = $script:ResolvedRemotePlatform
                occurredAtUtc =
                    [DateTimeOffset]::UtcNow.ToString("O")
                message = $_.Exception.Message
            }) | Out-Null
        }
    }

    try {
        Save-Report
        Write-Host (
            "JSON report: $($script:JsonReportPath)") `
            -ForegroundColor Cyan
        Write-Host (
            "Markdown report: $($script:MarkdownReportPath)") `
            -ForegroundColor Cyan
    }
    catch {
        if ($null -eq $caughtException) {
            $caughtException = $_
        }
    }
}

if ($null -ne $caughtException) {
    throw $caughtException
}

if ($null -eq $script:LastReport -or
    -not $script:LastReport.passed) {
    throw (
        "RemoteDesk soak regression did not pass. See " +
        "$($script:MarkdownReportPath)")
}
