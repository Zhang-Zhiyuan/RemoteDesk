[CmdletBinding()]
param(
    [string]$Target = "",
    [ValidateRange(1, 65535)]
    [int]$Port = 56565,
    [string]$Password = "",
    [switch]$PromptForPassword,
    [ValidateRange(1, 65535)]
    [int]$DiscoveryPort = 56566,
    [switch]$RunTests,
    [switch]$InstallAndroid,
    [switch]$StartAndroid,
    [switch]$AndroidStatus,
    [switch]$AndroidPullLog,
    [switch]$AndroidClearLog,
    [switch]$KeepAdbServer,
    [switch]$WindowsOnly,
    [switch]$LinuxOnly,
    [switch]$SkipAndroid,
    [switch]$AllowDirtySource,
    [switch]$ScopeCheck,
    [switch]$StartLinuxSandbox,
    [switch]$LinuxSandboxStatus,
    [switch]$LinuxProtocolProbe,
    [switch]$LinuxProtocolProbeSendFile,
    [switch]$LinuxProtocolProbePullRemoteFiles,
    [string]$LinuxDistro = "Ubuntu-24.04",
    [ValidateRange(1, 255)]
    [int]$LinuxDisplayNumber = 99,
    [ValidateRange(5900, 5999)]
    [int]$LinuxVncPort = 5909,
    [switch]$WriteAcceptanceReport,
    [string]$AcceptanceReportPath = "",
    [string]$GradlePath = ""
)

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "RemoteDesk-ReleaseCommon.ps1")

$LinuxDistro = Assert-RemoteDeskLinuxDistro -Distro $LinuxDistro

$script:CheckResults = [System.Collections.Generic.List[object]]::new()

if ($WindowsOnly -and $LinuxOnly) {
    throw "-WindowsOnly cannot be combined with -LinuxOnly."
}

if ($PromptForPassword -and
    -not [string]::IsNullOrEmpty($Password)) {
    throw "-PromptForPassword cannot be combined with -Password."
}
if ($PromptForPassword) {
    $securePassword = Read-Host `
        "RemoteDesk password" `
        -AsSecureString
    $passwordPointer = [IntPtr]::Zero
    try {
        $passwordPointer =
            [Runtime.InteropServices.Marshal]::SecureStringToBSTR(
                $securePassword)
        $Password =
            [Runtime.InteropServices.Marshal]::PtrToStringBSTR(
                $passwordPointer)
    }
    finally {
        if ($passwordPointer -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR(
                $passwordPointer)
        }
    }
    if ([string]::IsNullOrEmpty($Password)) {
        throw "RemoteDesk password must not be empty."
    }
}
elseif (-not [string]::IsNullOrEmpty($Password)) {
    Write-Warning (
        "-Password can expose the secret in shell history and process " +
        "arguments; prefer -PromptForPassword for interactive checks.")
}

function Write-Check {
    param(
        [string]$Name,
        [bool]$Ok,
        [string]$Detail = ""
    )

    $mark = if ($Ok) { "[OK]" } else { "[!!]" }
    $color = if ($Ok) { "Green" } else { "Yellow" }
    if ([string]::IsNullOrWhiteSpace($Detail)) {
        Write-Host "$mark $Name" -ForegroundColor $color
    }
    else {
        Write-Host "$mark $Name - $Detail" -ForegroundColor $color
    }

    $script:CheckResults.Add([pscustomobject]@{
        Name = $Name
        Ok = $Ok
        Detail = $Detail
    }) | Out-Null
}

function Resolve-RepoRoot {
    $scriptPath = $PSCommandPath
    if ([string]::IsNullOrWhiteSpace($scriptPath)) {
        return (Get-Location).Path
    }

    return (Resolve-Path (Join-Path (Split-Path -Parent $scriptPath) "..")).Path
}

function Get-CommandPath {
    param([string]$Name)

    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        return $null
    }

    return $command.Source
}

function Get-GradleCommand {
    param([string]$ExplicitPath)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        if (-not (Test-Path $ExplicitPath)) {
            throw "GradlePath does not exist: $ExplicitPath"
        }

        return (Resolve-Path $ExplicitPath).Path
    }

    $repositoryWrapper = Join-Path $PSScriptRoot `
        "..\src\RemoteDesk.Android\gradlew.bat"
    if (Test-Path -LiteralPath $repositoryWrapper -PathType Leaf) {
        return (Resolve-Path $repositoryWrapper).Path
    }

    $gradle = Get-CommandPath "gradle"
    if (-not [string]::IsNullOrWhiteSpace($gradle)) {
        return $gradle
    }

    $wrapperRoot = Join-Path $env:USERPROFILE ".gradle\wrapper\dists"
    if (Test-Path $wrapperRoot) {
        $candidates = Get-ChildItem -Path $wrapperRoot -Recurse -Filter "gradle.bat" -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -like "*\bin\gradle.bat" } |
            ForEach-Object {
                $match = [regex]::Match($_.FullName, "gradle-([0-9]+(?:\.[0-9]+){0,2})")
                $version = [version]"0.0.0"
                if ($match.Success) {
                    $versionText = $match.Groups[1].Value
                    if (($versionText.ToCharArray() | Where-Object { $_ -eq "." }).Count -eq 1) {
                        $versionText = "$versionText.0"
                    }

                    $version = [version]$versionText
                }

                [pscustomobject]@{
                    FullName = $_.FullName
                    Version = $version
                    LastWriteTime = $_.LastWriteTime
                }
            }
        $candidate = @($candidates | Where-Object { $_.Version.Major -eq 8 } |
            Sort-Object @{ Expression = "Version"; Descending = $true }, @{ Expression = "LastWriteTime"; Descending = $true } |
            Select-Object -First 1)
        if ($candidate.Count -eq 0) {
            $candidate = @($candidates |
                Sort-Object @{ Expression = "Version"; Descending = $true }, @{ Expression = "LastWriteTime"; Descending = $true } |
                Select-Object -First 1)
        }

        if ($candidate.Count -gt 0) {
            return $candidate[0].FullName
        }
    }

    throw "Gradle was not found. Install Gradle, open the Android project once in Android Studio, or pass -GradlePath."
}

function Get-LocalIPv4Summary {
    $addresses = @()
    try {
        $addresses = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop |
            Where-Object {
                $_.IPAddress -notlike "127.*" -and
                $_.IPAddress -notlike "169.254.*" -and
                $_.PrefixOrigin -ne "WellKnown"
            } |
            Sort-Object IPAddress |
            Select-Object -ExpandProperty IPAddress -Unique
    }
    catch {
        try {
            $addresses = [System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() |
                Where-Object {
                    $_.OperationalStatus -eq [System.Net.NetworkInformation.OperationalStatus]::Up -and
                    $_.NetworkInterfaceType -ne [System.Net.NetworkInformation.NetworkInterfaceType]::Loopback
                } |
                ForEach-Object { $_.GetIPProperties().UnicastAddresses } |
                Where-Object {
                    $_.Address.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetwork -and
                    -not [System.Net.IPAddress]::IsLoopback($_.Address)
                } |
                ForEach-Object { $_.Address.ToString() } |
                Sort-Object -Unique
        }
        catch {
            $addresses = @()
        }
    }

    if ($addresses.Count -eq 0) {
        return ""
    }

    return ($addresses -join ", ")
}

function Test-RemoteDeskTcpHandshake {
    param(
        [string]$Address,
        [int]$TcpPort
    )

    $client = [System.Net.Sockets.TcpClient]::new([System.Net.Sockets.AddressFamily]::InterNetwork)
    try {
        $client.NoDelay = $true
        $connect = $client.BeginConnect($Address, $TcpPort, $null, $null)
        if (-not $connect.AsyncWaitHandle.WaitOne(1000)) {
            return "TCP connect timeout"
        }

        $client.EndConnect($connect)
        $stream = $client.GetStream()
        $stream.ReadTimeout = 1000
        $buffer = New-Object byte[] 4
        $offset = 0
        while ($offset -lt 4) {
            $read = $stream.Read($buffer, $offset, 4 - $offset)
            if ($read -le 0) {
                return "TCP connected, but RemoteDesk magic was incomplete"
            }

            $offset += $read
        }

        $magic = [System.Text.Encoding]::ASCII.GetString($buffer)
        if ($magic -eq "RDK1") {
            return "RemoteDesk TCP handshake OK"
        }

        return "TCP open, but magic was '$magic'"
    }
    catch {
        return $_.Exception.Message
    }
    finally {
        $client.Close()
    }
}

function Resolve-IPv4Targets {
    param([string]$NameOrAddress)

    if ([string]::IsNullOrWhiteSpace($NameOrAddress)) {
        return @()
    }

    $parsed = $null
    if ([System.Net.IPAddress]::TryParse($NameOrAddress.Trim(), [ref]$parsed)) {
        if ($parsed.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetwork) {
            return @($parsed.ToString())
        }

        return @()
    }

    try {
        return [System.Net.Dns]::GetHostAddresses($NameOrAddress.Trim()) |
            Where-Object { $_.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetwork } |
            ForEach-Object { $_.ToString() } |
            Sort-Object -Unique
    }
    catch {
        return @()
    }
}

function Disable-UdpConnectionReset {
    param([System.Net.Sockets.Socket]$Socket)

    try {
        $sioUdpConnReset = -1744830452
        [void]$Socket.IOControl($sioUdpConnReset, [byte[]](0, 0, 0, 0), $null)
    }
    catch {
    }
}

function Test-RemoteDeskUdpDiscovery {
    param(
        [string]$Address,
        [int]$UdpPort
    )

    $client = [System.Net.Sockets.UdpClient]::new()
    try {
        Disable-UdpConnectionReset $client.Client
        $client.Client.ReceiveTimeout = 1000
        $payload = [System.Text.Encoding]::UTF8.GetBytes("RemoteDesk.Discover.v1")
        $endpoint = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Parse($Address), $UdpPort)
        [void]$client.Send($payload, $payload.Length, $endpoint)
        $remote = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, 0)
        $response = $client.Receive([ref]$remote)
        if ($response.Length -gt 0) {
            return "UDP discovery response from $($remote.Address):$($remote.Port)"
        }

        return "UDP discovery returned empty response"
    }
    catch {
        return "UDP discovery failed/no response"
    }
    finally {
        $client.Close()
    }
}

function Read-ExactBytes {
    param(
        [System.Net.Sockets.NetworkStream]$Stream,
        [int]$Length
    )

    $buffer = New-Object byte[] $Length
    $offset = 0
    while ($offset -lt $Length) {
        $read = $Stream.Read($buffer, $offset, $Length - $offset)
        if ($read -le 0) {
            throw "connection closed before $Length bytes were read"
        }

        $offset += $read
    }

    return $buffer
}

function Test-RemoteDeskAuthentication {
    param(
        [string]$Address,
        [int]$TcpPort,
        [string]$Secret
    )

    if ([string]::IsNullOrEmpty($Secret)) {
        return "Authentication skipped; pass -PromptForPassword to verify credentials"
    }

    $client = [System.Net.Sockets.TcpClient]::new([System.Net.Sockets.AddressFamily]::InterNetwork)
    try {
        $client.NoDelay = $true
        $connect = $client.BeginConnect($Address, $TcpPort, $null, $null)
        if (-not $connect.AsyncWaitHandle.WaitOne(3000)) {
            return "TCP connect timeout"
        }

        $client.EndConnect($connect)
        $stream = $client.GetStream()
        $stream.ReadTimeout = 3000
        $stream.WriteTimeout = 3000

        $magic = [System.Text.Encoding]::ASCII.GetString((Read-ExactBytes $stream 4))
        if ($magic -ne "RDK1") {
            return "target is not RemoteDesk"
        }

        $nonce = Read-ExactBytes $stream 32
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            $passwordKey = $sha256.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Secret))
        }
        finally {
            $sha256.Dispose()
        }

        $hmac = [System.Security.Cryptography.HMACSHA256]::new($passwordKey)
        try {
            $proof = $hmac.ComputeHash($nonce)
        }
        finally {
            $hmac.Dispose()
        }

        $marker = [System.Text.Encoding]::ASCII.GetBytes("AUTH")
        $stream.Write($marker, 0, $marker.Length)
        $stream.Write($proof, 0, $proof.Length)
        $result = Read-ExactBytes $stream 1
        if ($result[0] -eq 1) {
            return "RemoteDesk authentication OK"
        }

        return "RemoteDesk authentication rejected"
    }
    catch {
        return $_.Exception.Message
    }
    finally {
        $client.Close()
    }
}

function Write-RemoteDeskLanProbe {
    param(
        [string]$Name,
        [string[]]$Addresses
    )

    foreach ($address in @($Addresses | Sort-Object -Unique)) {
        $tcp = Test-RemoteDeskTcpHandshake -Address $address -TcpPort $Port
        Write-Check "$Name TCP RemoteDesk handshake $address`:$Port" ($tcp -eq "RemoteDesk TCP handshake OK") $tcp
        $udp = Test-RemoteDeskUdpDiscovery -Address $address -UdpPort $DiscoveryPort
        Write-Check "$Name UDP discovery $address`:$DiscoveryPort" ($udp -like "UDP discovery response*") $udp
        if (-not [string]::IsNullOrEmpty($Password)) {
            $auth = Test-RemoteDeskAuthentication -Address $address -TcpPort $Port -Secret $Password
            Write-Check "$Name TCP authentication $address`:$Port" ($auth -eq "RemoteDesk authentication OK") $auth
        }
    }
}

function Test-LinuxSandboxStatus {
    param(
        [string]$Distro,
        [int]$DisplayNumber,
        [int]$VncPort
    )

    if ($null -eq (Get-CommandPath "wsl.exe")) {
        return [pscustomobject]@{
            Ok = $false
            Detail = "wsl.exe not found"
        }
    }

    $script = @'
set -euo pipefail
display_number="$1"
vnc_port="$2"
display=":${display_number}"
export DISPLAY="$display"

missing=""
for command in Xvfb x11vnc xdotool xclip import openbox xdpyinfo ss; do
  if ! command -v "$command" >/dev/null; then
    missing="${missing} ${command}"
  fi
done

if [ -n "$missing" ]; then
  echo "missing:${missing}"
  exit 2
fi

xdpyinfo -display "$display" >/dev/null
import -window root /tmp/remotedesk-xvfb-check.png
image_info="$(identify /tmp/remotedesk-xvfb-check.png)"
vnc_status="$(ss -ltnp | grep ":${vnc_port}" || true)"
if [ -z "$vnc_status" ]; then
  echo "vnc not listening on ${vnc_port}"
  exit 3
fi

printf "display=%s; vnc=127.0.0.1:%s; screenshot=%s" "$display" "$vnc_port" "$image_info"
'@

    try {
        $output = $script | & wsl.exe -d $Distro -- bash -s -- $DisplayNumber.ToString() $VncPort.ToString() 2>&1
        $detail = (($output | ForEach-Object { [string]$_ }) -join " ").Trim()
        if ($LASTEXITCODE -eq 0) {
            return [pscustomobject]@{
                Ok = $true
                Detail = $detail
            }
        }

        return [pscustomobject]@{
            Ok = $false
            Detail = if ([string]::IsNullOrWhiteSpace($detail)) { "failed with exit code $LASTEXITCODE" } else { $detail }
        }
    }
    catch {
        return [pscustomobject]@{
            Ok = $false
            Detail = $_.Exception.Message
        }
    }
}

function ConvertTo-WslPath {
    param(
        [string]$Distro,
        [string]$WindowsPath
    )

    $fullPath = [System.IO.Path]::GetFullPath($WindowsPath)
    $match = [regex]::Match($fullPath, "^([A-Za-z]):[\\/](.*)$")
    if ($match.Success) {
        $drive = $match.Groups[1].Value.ToLowerInvariant()
        $tail = $match.Groups[2].Value.Replace("\", "/")
        return "/mnt/$drive/$tail"
    }

    $output = & wsl.exe -d $Distro -- wslpath -a $fullPath 2>&1
    $detail = (($output | ForEach-Object { [string]$_ }) -join " ").Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($detail)) {
        throw "wslpath failed: $detail"
    }

    return $detail
}

function Invoke-LinuxPythonTests {
    param(
        [string]$Distro,
        [string]$Root
    )

    if ($null -eq (Get-Command "wsl.exe" -ErrorAction SilentlyContinue)) {
        throw "wsl.exe is required to run the Linux Python test suite."
    }

    $wslRoot = ConvertTo-WslPath `
        -Distro $Distro `
        -WindowsPath $Root
    & wsl.exe -d $Distro --cd $wslRoot -- bash -lc `
        'exec python3 -m unittest discover -s tests -p "test_*.py" -v'
    if ($LASTEXITCODE -ne 0) {
        throw "Linux Python tests failed with exit code $LASTEXITCODE"
    }
}

function Invoke-LinuxProtocolProbe {
    param(
        [string]$Distro,
        [string]$Root,
        [string]$Address,
        [int]$TcpPort,
        [string]$Secret,
        [switch]$SendFile,
        [switch]$PullRemoteFiles
    )

    if ([string]::IsNullOrWhiteSpace($Address)) {
        return [pscustomobject]@{
            Ok = $false
            Detail = "target address is required"
        }
    }

    if ([string]::IsNullOrEmpty($Secret)) {
        return [pscustomobject]@{
            Ok = $false
            Detail = "pass -PromptForPassword to run Linux protocol probe"
        }
    }

    $wslPath = Get-CommandPath "wsl.exe"
    $nativePythonPath = Get-CommandPath "python"
    $useWsl = $false
    if ($null -ne $wslPath) {
        try {
            # Some wsl.exe builds emit UTF-16 text through a pipe as strings with
            # embedded NUL characters. Normalize those before matching the requested distro.
            $installedDistros = @(& $wslPath -l -q 2>$null | ForEach-Object {
                ([string]$_).Replace("`0", "").Trim()
            } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
            $useWsl = $installedDistros -contains $Distro
        }
        catch {
            $useWsl = $false
        }
    }

    if (-not $useWsl -and $null -eq $nativePythonPath) {
        return [pscustomobject]@{
            Ok = $false
            Detail = "neither WSL distro '$Distro' nor native python was found"
        }
    }

    $probePath = Join-Path $Root "scripts\linux\remotedesk_protocol_probe.py"
    if (-not (Test-Path -LiteralPath $probePath)) {
        return [pscustomobject]@{
            Ok = $false
            Detail = "missing probe script: $probePath"
        }
    }

    $runner = if ($useWsl) { "wsl:$Distro" } else { "native-python" }
    $temporaryProbeFile = $null
    $temporaryReceiveDirectory = $null

    try {
        $executionProbePath = if ($useWsl) {
            ConvertTo-WslPath -Distro $Distro -WindowsPath $probePath
        }
        else {
            (Resolve-Path -LiteralPath $probePath).Path
        }
        $probeArguments = @(
            $executionProbePath,
            "--host", $Address,
            "--port", $TcpPort.ToString(),
            "--password-fd", "0",
            "--duration", "5",
            "--frames", "1"
        )

        if ($SendFile) {
            $temporaryProbeFile = Join-Path ([System.IO.Path]::GetTempPath()) "RemoteDesk-linux-probe-$([Guid]::NewGuid().ToString("N")).txt"
            $probeText = "RemoteDesk Linux protocol file probe`nTarget=$Address`nGenerated=$([DateTimeOffset]::Now.ToString("O"))`n"
            [System.IO.File]::WriteAllText($temporaryProbeFile, $probeText, [System.Text.Encoding]::UTF8)
            $executionProbeFilePath = if ($useWsl) {
                ConvertTo-WslPath -Distro $Distro -WindowsPath $temporaryProbeFile
            }
            else {
                $temporaryProbeFile
            }
            $probeArguments += @("--send-file", $executionProbeFilePath)
        }

        if ($PullRemoteFiles) {
            $temporaryReceiveDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "RemoteDesk-linux-pull-$([Guid]::NewGuid().ToString("N"))"
            [System.IO.Directory]::CreateDirectory($temporaryReceiveDirectory) | Out-Null
            $executionReceiveDirectory = if ($useWsl) {
                ConvertTo-WslPath -Distro $Distro -WindowsPath $temporaryReceiveDirectory
            }
            else {
                $temporaryReceiveDirectory
            }
            $probeArguments += @("--request-remote-files", "--receive-dir", $executionReceiveDirectory)
        }

        $probeArguments += "--json"
        $output = if ($useWsl) {
            $Secret | & $wslPath -d $Distro -- python3 @probeArguments 2>&1
        }
        else {
            $Secret | & $nativePythonPath @probeArguments 2>&1
        }
        $text = (($output | ForEach-Object { [string]$_ }) -join "`n").Trim()
        if ($LASTEXITCODE -ne 0) {
            return [pscustomobject]@{
                Ok = $false
                Detail = if ([string]::IsNullOrWhiteSpace($text)) {
                    "runner=$runner probe failed with exit code $LASTEXITCODE"
                }
                else {
                    "runner=$runner $text"
                }
            }
        }

        $result = $text | ConvertFrom-Json -ErrorAction Stop
        $device = $result.device
        $targetCount = @($result.captureTargets).Count
        $frame = @($result.frames | Select-Object -First 1)
        $frameDetail = if ($frame.Count -gt 0) {
            "$($frame[0].encoding) $($frame[0].width)x$($frame[0].height), $($frame[0].encodedBytes) bytes"
        }
        else {
            "no frame before timeout"
        }
        $capabilities = if ($null -ne $device -and $null -ne $device.capabilityNames) {
            (@($device.capabilityNames) -join "/")
        }
        else {
            "unknown capabilities"
        }
        $sentFile = $result.sentFile
        $sentFileDetail = if ($null -ne $sentFile) {
            "; sentFile=$($sentFile.name) $($sentFile.bytes) bytes saved=$($sentFile.remoteSaved)"
        }
        else {
            ""
        }
        $receivedFiles = @($result.receivedFiles)
        $pullDetail = if ([bool]$result.remoteFileRequest) {
            $skippedReason = [string]$result.remoteFileRequestSkippedReason
            if ([string]::IsNullOrWhiteSpace($skippedReason)) {
                "; receivedFiles=$($receivedFiles.Count)"
            }
            else {
                "; receivedFiles=$($receivedFiles.Count) pullSkipped=$skippedReason"
            }
        }
        else {
            ""
        }

        return [pscustomobject]@{
            Ok = [bool]$result.ok
            Detail = "runner=$runner device=$($device.machineName) platform=$($device.platform) capabilities=$capabilities targets=$targetCount frame=$frameDetail elapsed=$($result.elapsedMs)ms$sentFileDetail$pullDetail"
        }
    }
    catch {
        return [pscustomobject]@{
            Ok = $false
            Detail = "runner=$runner $($_.Exception.Message)"
        }
    }
    finally {
        if (-not [string]::IsNullOrWhiteSpace($temporaryProbeFile) -and (Test-Path -LiteralPath $temporaryProbeFile)) {
            Remove-Item -LiteralPath $temporaryProbeFile -Force -ErrorAction SilentlyContinue
        }

        if (-not [string]::IsNullOrWhiteSpace($temporaryReceiveDirectory) -and (Test-Path -LiteralPath $temporaryReceiveDirectory)) {
            $resolvedTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
            $resolvedReceive = [System.IO.Path]::GetFullPath($temporaryReceiveDirectory)
            if ($resolvedReceive.StartsWith($resolvedTemp + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
                Remove-Item -LiteralPath $temporaryReceiveDirectory -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }
}

function Invoke-OptionalCommand {
    param(
        [string]$Name,
        [scriptblock]$Command
    )

    try {
        & $Command
        Write-Check $Name $true
    }
    catch {
        Write-Check $Name $false $_.Exception.Message
    }
}

function Format-ProcessSummary {
    param([object[]]$Processes)

    $items = @($Processes | Where-Object { $null -ne $_ })
    if ($items.Count -eq 0) {
        return "clear"
    }

    return (($items | ForEach-Object {
        $nameValue = [string]$_.ProcessName
        if ([string]::IsNullOrWhiteSpace($nameValue)) {
            $nameValue = [string]$_.Name
        }

        if ([string]::IsNullOrWhiteSpace($nameValue)) {
            $nameValue = "process"
        }

        $processIdValue = $_.Id
        if ($null -eq $processIdValue) {
            $processIdValue = $_.ProcessId
        }

        if ($null -eq $processIdValue) {
            $processIdValue = "?"
        }

        "$nameValue`:$processIdValue"
    }) -join ", ")
}

function Get-GradleJavaProcesses {
    $processes = @()
    try {
        foreach ($name in @("java.exe", "javaw.exe")) {
            $processes += Get-CimInstance Win32_Process -Filter "Name = '$name'" -ErrorAction Stop
        }
    }
    catch {
        return @()
    }

    return @($processes | Where-Object {
        $_.CommandLine -like "*gradle*" -or
        $_.CommandLine -like "*Gradle*" -or
        $_.CommandLine -like "*org.gradle*"
    })
}

function Get-AdbProcesses {
    return @(Get-Process adb -ErrorAction SilentlyContinue | Where-Object { $null -ne $_ })
}

function Get-CheckerAdbProcesses {
    $initialIds = @($script:InitialAdbProcessIds)
    return @(Get-AdbProcesses | Where-Object {
        $initialIds -notcontains [int]$_.Id
    })
}

function Write-HelperProcessChecks {
    param([switch]$SkipAdb)

    if ($SkipAdb) {
        Write-Check "adb process" $true "kept by -KeepAdbServer"
    }
    else {
        $adbProcesses = @(Get-CheckerAdbProcesses)
        $adbDetail = if ($adbProcesses.Count -eq 0 -and
            @($script:InitialAdbProcessIds).Count -gt 0) {
            "pre-existing adb server preserved"
        }
        else {
            Format-ProcessSummary $adbProcesses
        }
        Write-Check "No stale adb process" ($adbProcesses.Count -eq 0) $adbDetail
    }

    $gradleJavaProcesses = @(Get-GradleJavaProcesses)
    Write-Check "No stale Gradle java process" ($gradleJavaProcesses.Count -eq 0) (Format-ProcessSummary $gradleJavaProcesses)
}

function Wait-HelperProcessesExit {
    param(
        [switch]$SkipAdb,
        [int]$TimeoutMilliseconds = 10000
    )

    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    do {
        $adbProcesses = if ($SkipAdb) { @() } else { @(Get-CheckerAdbProcesses) }
        $gradleJavaProcesses = @(Get-GradleJavaProcesses)
        if ($adbProcesses.Count -eq 0 -and $gradleJavaProcesses.Count -eq 0) {
            return
        }

        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
}

function Invoke-AdbRaw {
    param([string[]]$Arguments)

    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $lines = @(& adb @Arguments 2>&1 | ForEach-Object { $_.ToString() })
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        Lines = $lines
    }
}

function Get-AdbDevices {
    $result = Invoke-AdbRaw @("devices")
    if ($result.ExitCode -ne 0) {
        throw "adb devices failed with exit code $($result.ExitCode): $($result.Lines -join ' ')"
    }

    $devices = @()
    foreach ($line in $result.Lines) {
        $trimmed = $line.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed) -or
            $trimmed.StartsWith("List of devices") -or
            $trimmed.StartsWith("* ")) {
            continue
        }

        $parts = $trimmed -split "\s+"
        if ($parts.Count -lt 2) {
            continue
        }

        $devices += [pscustomobject]@{
            Serial = $parts[0]
            State = $parts[1]
            Detail = $trimmed
        }
    }

    return $devices
}

function Get-ReadyAdbDevices {
    param([string]$ActionName)

    $devices = @(Get-AdbDevices)
    if ($devices.Count -eq 0) {
        Write-Check $ActionName $false "no Android device; connect phone and enable USB debugging"
        return @()
    }

    foreach ($device in $devices) {
        $ready = $device.State -eq "device"
        $detail = if ($ready) { $device.Serial } else { "$($device.Serial) is $($device.State)" }
        Write-Check "adb device $($device.Serial)" $ready $detail
    }

    return @($devices | Where-Object { $_.State -eq "device" })
}

function Get-AdbShellText {
    param(
        [string]$Serial,
        [string[]]$ShellArguments
    )

    $arguments = @("-s", $Serial, "shell") + $ShellArguments
    $result = Invoke-AdbRaw $arguments
    if ($result.ExitCode -ne 0) {
        throw "adb shell failed with exit code $($result.ExitCode): $($result.Lines -join ' ')"
    }

    return (($result.Lines | ForEach-Object { $_.Trim() }) -join "`n").Trim()
}

function Get-AndroidDeviceLabel {
    param([string]$Serial)

    try {
        $manufacturer = Get-AdbShellText $Serial @("getprop", "ro.product.manufacturer")
        $model = Get-AdbShellText $Serial @("getprop", "ro.product.model")
        $release = Get-AdbShellText $Serial @("getprop", "ro.build.version.release")
        $sdk = Get-AdbShellText $Serial @("getprop", "ro.build.version.sdk")
        return "$manufacturer $model, Android $release (SDK $sdk)"
    }
    catch {
        return "unable to read device properties: $($_.Exception.Message)"
    }
}

function Get-AndroidIPv4Addresses {
    param([string]$Serial)

    try {
        $text = Get-AdbShellText $Serial @("ip", "-f", "inet", "addr", "show")
        $addresses = @()
        foreach ($match in [regex]::Matches($text, "inet\s+([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)/")) {
            $address = $match.Groups[1].Value
            if ($address -notlike "127.*" -and $address -notlike "169.254.*") {
                $addresses += $address
            }
        }

        $addresses = @($addresses | Sort-Object -Unique)
        return $addresses
    }
    catch {
        return @()
    }
}

function Get-AndroidIPv4Summary {
    param([string]$Serial)

    $addresses = @(Get-AndroidIPv4Addresses $Serial)
    if ($addresses.Count -eq 0) {
        return ""
    }

    return ($addresses -join ", ")
}

function Get-AndroidPackageSummary {
    param([string]$Serial)

    $pathResult = Invoke-AdbRaw @("-s", $Serial, "shell", "pm", "path", "com.remotedesk.agent")
    $installed = $pathResult.ExitCode -eq 0 -and (($pathResult.Lines -join "`n") -like "*package:*")
    if (-not $installed) {
        return [pscustomobject]@{
            Installed = $false
            Detail = "not installed"
        }
    }

    $detail = "installed"
    try {
        $dump = Get-AdbShellText $Serial @("dumpsys", "package", "com.remotedesk.agent")
        $versionName = ([regex]::Match($dump, "versionName=([^\s]+)")).Groups[1].Value
        $versionCode = ([regex]::Match($dump, "versionCode=([0-9]+)")).Groups[1].Value
        if (-not [string]::IsNullOrWhiteSpace($versionName) -or -not [string]::IsNullOrWhiteSpace($versionCode)) {
            $detail = "installed"
            if (-not [string]::IsNullOrWhiteSpace($versionName)) {
                $detail += ", versionName=$versionName"
            }

            if (-not [string]::IsNullOrWhiteSpace($versionCode)) {
                $detail += ", versionCode=$versionCode"
            }
        }
    }
    catch {
    }

    return [pscustomobject]@{
        Installed = $true
        Detail = $detail
    }
}

function Get-AndroidProcessSummary {
    param([string]$Serial)

    $result = Invoke-AdbRaw @("-s", $Serial, "shell", "pidof", "com.remotedesk.agent")
    if ($result.ExitCode -eq 0) {
        $processIdText = (($result.Lines | ForEach-Object { $_.Trim() }) -join " ").Trim()
        if (-not [string]::IsNullOrWhiteSpace($processIdText)) {
            return $processIdText
        }
    }

    return ""
}

function Write-AndroidStatus {
    param([object[]]$Devices)

    foreach ($device in $Devices) {
        Write-Host ""
        Write-Host "Android status: $($device.Serial)" -ForegroundColor Cyan
        Write-Check "Device identity" $true (Get-AndroidDeviceLabel $device.Serial)
        $androidAddresses = @(Get-AndroidIPv4Addresses $device.Serial)
        $androidIp = $androidAddresses -join ", "
        Write-Check "Android IPv4" (-not [string]::IsNullOrWhiteSpace($androidIp)) ($(if ($androidIp) { $androidIp } else { "none found; check Wi-Fi/mobile network" }))

        $package = Get-AndroidPackageSummary $device.Serial
        Write-Check "RemoteDesk Agent package" $package.Installed $package.Detail

        $processIdText = Get-AndroidProcessSummary $device.Serial
        Write-Check "RemoteDesk Agent process" (-not [string]::IsNullOrWhiteSpace($processIdText)) ($(if ($processIdText) { "pid $processIdText" } else { "not running; use -StartAndroid" }))

        if ($package.Installed -and $androidAddresses.Count -gt 0) {
            Write-RemoteDeskLanProbe "Android $($device.Serial)" $androidAddresses
        }
    }
}

function Save-AndroidCurrentLog {
    param([object[]]$Devices)

    $outputDirectory = Join-Path $root "artifacts\AndroidLogs"
    if (-not (Test-Path $outputDirectory)) {
        New-Item -ItemType Directory -Path $outputDirectory | Out-Null
    }

    foreach ($device in $Devices) {
        $safeSerial = $device.Serial -replace '[^A-Za-z0-9_.-]', '_'
        Save-AndroidLogFile `
            -Serial $device.Serial `
            -RemotePath "files/RemoteDeskDiagnostics/remotedesk-android-current.log.old" `
            -OutputFile (Join-Path $outputDirectory "RemoteDesk-android-$safeSerial-previous.log") `
            -Required:$false
        Save-AndroidLogFile `
            -Serial $device.Serial `
            -RemotePath "files/RemoteDeskDiagnostics/remotedesk-android-current.log" `
            -OutputFile (Join-Path $outputDirectory "RemoteDesk-android-$safeSerial-current.log") `
            -Required:$true
    }
}

function Save-AndroidLogFile {
    param(
        [string]$Serial,
        [string]$RemotePath,
        [string]$OutputFile,
        [bool]$Required
    )

    $label = if ($Required) { "current" } else { "previous" }
    $result = Invoke-AdbRaw @(
        "-s",
        $Serial,
        "exec-out",
        "run-as",
        "com.remotedesk.agent",
        "cat",
        $RemotePath)
    if ($result.ExitCode -ne 0) {
        if ($Required) {
            Write-Check "adb pull RemoteDesk Android $label log from $Serial" $false "adb log pull failed with exit code $($result.ExitCode): $($result.Lines -join ' ')"
        }
        else {
            Write-Check "adb pull RemoteDesk Android $label log from $Serial" $true "not present"
        }

        return
    }

    if ($result.Lines.Count -eq 0) {
        $detail = if ($Required) { "diagnostic log is empty or has not been created yet; open the Android app first" } else { "not present" }
        Write-Check "adb pull RemoteDesk Android $label log from $Serial" (-not $Required) $detail
        return
    }

    $result.Lines | Set-Content -Path $OutputFile -Encoding UTF8
    Write-Check "adb pull RemoteDesk Android $label log from $Serial" $true "saved to $OutputFile"
}

function Clear-AndroidDiagnosticLog {
    param([object[]]$Devices)

    foreach ($device in $Devices) {
        $result = Invoke-AdbRaw @(
            "-s",
            $device.Serial,
            "shell",
            "run-as",
            "com.remotedesk.agent",
            "sh",
            "-c",
            "rm -f files/RemoteDeskDiagnostics/remotedesk-android-current.log files/RemoteDeskDiagnostics/remotedesk-android-current.log.old files/RemoteDeskDiagnostics/remotedesk-android-log-*.txt")
        Write-Check "adb clear RemoteDesk Android logs on $($device.Serial)" ($result.ExitCode -eq 0) ($(if ($result.ExitCode -eq 0) { "cleared" } else { "failed with exit code $($result.ExitCode): $($result.Lines -join ' ')" }))
    }
}

function Write-ScopeReadiness {
    param(
        [string]$WindowsExe,
        [string]$WindowsZip,
        [string]$LinuxHostZip,
        [string]$LinuxHostTarGz,
        [string]$LinuxHostDeb,
        [string]$AndroidDebugApk,
        [string]$AndroidReleaseApk,
        [string]$AndroidUnsignedReleaseApk,
        [string]$LocalIpSummary,
        [string]$FfmpegPath,
        [string]$AdbPath,
        [bool]$WindowsOnly,
        [bool]$LinuxOnly,
        [bool]$SkipAndroid,
        [bool]$AndroidReleaseRequested
    )

    Write-Host ""
    Write-Host "Target scope readiness" -ForegroundColor Cyan
    if ($LinuxOnly) {
        Write-Host "Scope: Linux host/viewer package only." -ForegroundColor Cyan
    }
    elseif ($WindowsOnly) {
        Write-Host "Scope: Windows host/viewer package only." -ForegroundColor Cyan
    }
    elseif ($SkipAndroid) {
        Write-Host "Scope: Windows and Linux host/viewer packages; Android excluded." -ForegroundColor Cyan
    }
    else {
        Write-Host "Scope: Windows host/viewer, Android, and Linux host/viewer package." -ForegroundColor Cyan
    }

    Write-Host "Acceptance checklist: docs\Windows-Android-Acceptance.md; docs\Linux-Sandbox.md" -ForegroundColor Cyan

    if (-not $LinuxOnly) {
        Write-Host ""
        Write-Host "Windows-to-Windows" -ForegroundColor Cyan
        Write-Check "Controller/host executable" (Test-Path $WindowsExe) ($(if (Test-Path $WindowsExe) { "$((Get-Item $WindowsExe).Length) bytes" } else { "missing; run .\scripts\Publish-RemoteDesk.ps1" }))
        Write-Check "Distributable zip" (Test-Path $WindowsZip) ($(if (Test-Path $WindowsZip) { "$((Get-Item $WindowsZip).Length) bytes" } else { "missing; run .\scripts\Publish-RemoteDesk.ps1" }))
        Write-Check "Local IPv4 for LAN use" (-not [string]::IsNullOrWhiteSpace($LocalIpSummary)) ($(if ($LocalIpSummary) { $LocalIpSummary } else { "none; check network adapter" }))
        Write-Check "Windows live test requirement" $true "requires two Windows machines or a loopback/manual LAN session; automated tests cover protocol, discovery, settings, diagnostics, video mode, input queue, boundaries, and viewer loopback compatibility"
    }

    if ($WindowsOnly) {
        return
    }

    if (-not $LinuxOnly -and -not $SkipAndroid) {
        Write-Host ""
        Write-Host "Windows-to-Android" -ForegroundColor Cyan
        if ($AndroidReleaseRequested) {
            if (Test-Path -LiteralPath $AndroidReleaseApk -PathType Leaf) {
                Write-Check "Android signed release APK" $true "$((Get-Item $AndroidReleaseApk).Length) bytes"
            }
            elseif (Test-Path -LiteralPath $AndroidUnsignedReleaseApk -PathType Leaf) {
                Write-Check "Android signed release APK" $false "only unsigned release is present; configure release-signing.properties or REMOTEDESK_ANDROID_* before release install"
            }
            else {
                Write-Check "Android release APK" $false "missing; run .\scripts\Publish-RemoteDesk.ps1 -AndroidRelease"
            }
        }
        else {
            Write-Check "Android debug APK" (Test-Path -LiteralPath $AndroidDebugApk -PathType Leaf) ($(if (Test-Path -LiteralPath $AndroidDebugApk -PathType Leaf) { "$((Get-Item $AndroidDebugApk).Length) bytes; use this for current device testing" } else { "missing; run .\scripts\Publish-RemoteDesk.ps1" }))
            Write-Check "Android release signing" $true "not requested for this debug/internal package"
        }

        Write-Check "adb for install/status/logs" (-not [string]::IsNullOrWhiteSpace($AdbPath)) ($(if ($AdbPath) { $AdbPath } else { "not found; install Android platform-tools or copy APK manually" }))
        Write-Check "ffmpeg for Android H.264 viewer mode" (-not [string]::IsNullOrWhiteSpace($FfmpegPath)) ($(if ($FfmpegPath) { $FfmpegPath } else { "not found; use Stable JPEG until ffmpeg or an internal decoder is available" }))
        Write-Check "Android live test requirement" $true "connect a phone, grant screen recording and Accessibility, then run -InstallAndroid -AndroidClearLog -StartAndroid -AndroidStatus"
    }

    Write-Host ""
    Write-Host "Windows-to-Linux host package" -ForegroundColor Cyan
    Write-Check "Linux host helper zip" (Test-Path $LinuxHostZip) ($(if (Test-Path $LinuxHostZip) { "$((Get-Item $LinuxHostZip).Length) bytes; self-contained portable package with runtime/app/docs" } else { "missing; run .\scripts\Publish-RemoteDesk.ps1" }))
    Write-Check "Linux host portable tar.gz" (Test-Path $LinuxHostTarGz) ($(if (Test-Path $LinuxHostTarGz) { "$((Get-Item $LinuxHostTarGz).Length) bytes; self-contained portable Ubuntu/Linux package" } else { "missing; run .\scripts\Publish-RemoteDesk.ps1" }))
    Write-Check "Ubuntu deb package" (Test-Path $LinuxHostDeb) ($(if (Test-Path $LinuxHostDeb) { "$((Get-Item $LinuxHostDeb).Length) bytes; self-contained offline install package for Ubuntu" } else { "missing; run .\scripts\Publish-RemoteDesk.ps1 with WSL available" }))
    Write-Check "Linux host automated proof" $true "WSL roundtrip covers encrypted auth, discovery response, Windows-style send, Linux return, SHA-256, and cancel capability negotiation; desktop tools remain optional integrations"
    Write-Check "Linux host remaining hardening" $true "physical X11/Wayland GPU capture and presentation matrices, full keyboard coverage, and service installation still need target-machine validation or hardening"
}

function Format-MarkdownCell {
    param([string]$Text)

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return ""
    }

    return ($Text -replace "\r?\n", " " -replace "\|", "\|").Trim()
}

function Resolve-AcceptanceReportPath {
    param(
        [string]$RequestedPath,
        [string]$Root
    )

    if ([string]::IsNullOrWhiteSpace($RequestedPath)) {
        return (Join-Path $Root "artifacts\RemoteDesk-acceptance-report.md")
    }

    if ([System.IO.Path]::IsPathRooted($RequestedPath)) {
        return [System.IO.Path]::GetFullPath($RequestedPath)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $Root $RequestedPath))
}

function Save-AcceptanceReport {
    param(
        [string]$RequestedPath,
        [string]$Root,
        [bool]$WindowsOnly,
        [bool]$LinuxOnly,
        [bool]$SkipAndroid,
        [string]$Target,
        [int]$Port,
        [int]$DiscoveryPort,
        [string]$LocalIpSummary
    )

    $reportPath = Resolve-AcceptanceReportPath -RequestedPath $RequestedPath -Root $Root
    $reportDirectory = Split-Path -Parent $reportPath
    if (-not [string]::IsNullOrWhiteSpace($reportDirectory) -and -not (Test-Path $reportDirectory)) {
        New-Item -ItemType Directory -Path $reportDirectory | Out-Null
    }

    $scope = if ($LinuxOnly) {
        "Linux host/viewer package"
    }
    elseif ($WindowsOnly) {
        "Windows host/viewer package"
    }
    elseif ($SkipAndroid) {
        "Windows host/viewer; Linux host/viewer package"
    }
    else {
        "Windows host/viewer; Android; Linux host/viewer package"
    }
    $manifestPath = Join-Path $Root "artifacts\RemoteDesk-release-manifest.json"
    $manifest = $null
    $manifestReadError = $null
    if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
        try {
            $manifest = Get-Content -LiteralPath $manifestPath -Raw |
                ConvertFrom-Json -ErrorAction Stop
        }
        catch {
            $manifestReadError = $_.Exception.Message
        }
    }
    else {
        $manifestReadError = "manifest is missing"
    }
    $dirtySourceArgument = if (
        $null -ne $manifest -and
        ([bool]$manifest.sourceDirty -or
         [bool]$manifest.internalCandidate)) {
        " -AllowDirtySource"
    }
    else {
        ""
    }

    $generatedAt = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss zzz")
    $targetText = if ([string]::IsNullOrWhiteSpace($Target)) { "not specified" } else { "$Target, TCP $Port, UDP $DiscoveryPort" }
    $localIpText = if ([string]::IsNullOrWhiteSpace($LocalIpSummary)) { "not detected" } else { $LocalIpSummary }
    $unprovenItems = @()
    if (-not $LinuxOnly) {
        $unprovenItems += "- Full LAN regression between two Windows machines still needs manual confirmation."
        if ($null -eq $manifest -or
            $null -eq $manifest.windowsAuthenticode -or
            [string]$manifest.windowsAuthenticode.status -cne "Valid") {
            $unprovenItems += "- Windows Authenticode signing, timestamping, installation reputation, and signed remote-update validation need a trusted code-signing certificate."
        }
    }
    if (-not $WindowsOnly -and -not $LinuxOnly -and -not $SkipAndroid) {
        $unprovenItems += "- Real Android screen-capture permission, accessibility input, rotation, clipboard, and file regression still need manual confirmation."
        $unprovenItems += "- Android signed release install and upgrade need a user keystore and manual confirmation."
    }
    if (-not $WindowsOnly) {
        $unprovenItems += "- Linux physical X11/Wayland GPU capture and presentation matrices, full keyboard coverage, and service installation still need target-machine validation or hardening."
    }

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("# RemoteDesk Acceptance Report") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add("- Generated at: $generatedAt") | Out-Null
    $lines.Add("- Scope: $scope") | Out-Null
    $lines.Add("- Local IPv4: $localIpText") | Out-Null
    $lines.Add("- Target probe: $targetText") | Out-Null
    $lines.Add("- Acceptance checklist: docs\Windows-Android-Acceptance.md") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add("## Exact Package Identity") | Out-Null
    $lines.Add("") | Out-Null
    if ($null -eq $manifest) {
        $lines.Add("**WARNING:** Could not read the canonical release manifest: $(Format-MarkdownCell -Text $manifestReadError)") | Out-Null
    }
    else {
        $manifestSourceRevision = Format-MarkdownCell -Text ([string]$manifest.sourceRevision)
        $manifestSourceFingerprint = Format-MarkdownCell -Text ([string]$manifest.sourceFingerprint)
        $manifestToolchain = $manifest.toolchain
        $lines.Add("- Manifest: ``artifacts\RemoteDesk-release-manifest.json``") | Out-Null
        $lines.Add("- Build stamp: $(Format-MarkdownCell -Text ([string]$manifest.buildStamp))") | Out-Null
        $lines.Add("- Manifest generated: $(Format-MarkdownCell -Text ([string]$manifest.generatedAtUtc))") | Out-Null
        $lines.Add("- Scope: $(Format-MarkdownCell -Text ([string]$manifest.scope))") | Out-Null
        $lines.Add("- Source: ``$manifestSourceRevision``; fingerprint ``$manifestSourceFingerprint`` ($($manifest.sourceFileCount) files)") | Out-Null
        $lines.Add("- Source state: dirty=$( [bool]$manifest.sourceDirty ); internalCandidate=$( [bool]$manifest.internalCandidate )") | Out-Null
        if ($null -ne $manifest.windowsAuthenticode) {
            $manifestAuthenticode = $manifest.windowsAuthenticode
            $lines.Add("- Windows Authenticode: status=$(Format-MarkdownCell -Text ([string]$manifestAuthenticode.status)); signer=$(Format-MarkdownCell -Text ([string]$manifestAuthenticode.signerThumbprint)); timestamped=$( [bool]$manifestAuthenticode.timestamped )") | Out-Null
        }
        $lines.Add("- Toolchain: .NET $(Format-MarkdownCell -Text ([string]$manifestToolchain.dotnetSdk)); Gradle $(Format-MarkdownCell -Text ([string]$manifestToolchain.gradle)); Java $(Format-MarkdownCell -Text ([string]$manifestToolchain.java)); Linux runtime $(Format-MarkdownCell -Text ([string]$manifestToolchain.linuxPython)); WSL distro $(Format-MarkdownCell -Text ([string]$manifestToolchain.linuxDistro))") | Out-Null
        $lines.Add("") | Out-Null
        $lines.Add("| Package | Bytes | SHA-256 |") | Out-Null
        $lines.Add("| --- | ---: | --- |") | Out-Null
        foreach ($entry in @($manifest.artifacts)) {
            $entryName = Format-MarkdownCell -Text ([string]$entry.name)
            $entryLength = Format-MarkdownCell -Text ([string]$entry.length)
            $entryHash = Format-MarkdownCell -Text ([string]$entry.sha256)
            $lines.Add("| ``$entryName`` | $entryLength | ``$entryHash`` |") | Out-Null
        }
        $lines.Add("") | Out-Null
        $lines.Add("> Package hashes and lengths above are read from the canonical manifest. Any physical-device or interoperability observation from an older build is inherited evidence, not proof for this exact byte set.") | Out-Null
    }
    $lines.Add("") | Out-Null
    $lines.Add("## Automated Checks") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add("| Status | Check | Detail |") | Out-Null
    $lines.Add("| --- | --- | --- |") | Out-Null
    foreach ($check in $script:CheckResults) {
        $status = if ($check.Ok) { "OK" } else { "WARN" }
        $name = Format-MarkdownCell -Text ([string]$check.Name)
        $detail = Format-MarkdownCell -Text ([string]$check.Detail)
        $lines.Add("| $status | $name | $detail |") | Out-Null
    }

    $lines.Add("") | Out-Null
    $lines.Add("## Manual Confirmation Still Needed") | Out-Null
    $lines.Add("") | Out-Null
    foreach ($item in $unprovenItems) {
        $lines.Add($item) | Out-Null
    }

    $lines.Add("") | Out-Null
    $lines.Add("## Suggested Commands") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add('```powershell') | Out-Null
    $lines.Add(".\scripts\Invoke-RemoteDeskCheck.ps1$dirtySourceArgument -ScopeCheck -RunTests -WriteAcceptanceReport") | Out-Null
    $lines.Add(".\scripts\Invoke-RemoteDeskCheck.ps1$dirtySourceArgument -Target <host-ip> -PromptForPassword -WriteAcceptanceReport") | Out-Null
    $lines.Add(".\scripts\Start-RemoteDeskLinuxSandbox.ps1 -InstallDependencies") | Out-Null
    $lines.Add(".\scripts\Invoke-RemoteDeskCheck.ps1$dirtySourceArgument -StartLinuxSandbox -LinuxSandboxStatus -WriteAcceptanceReport") | Out-Null
    $lines.Add(".\scripts\Invoke-RemoteDeskCheck.ps1$dirtySourceArgument -Target <host-ip> -PromptForPassword -LinuxProtocolProbe -WriteAcceptanceReport") | Out-Null
    $lines.Add(".\scripts\Invoke-RemoteDeskCheck.ps1$dirtySourceArgument -Target <host-ip> -PromptForPassword -LinuxProtocolProbe -LinuxProtocolProbeSendFile -WriteAcceptanceReport") | Out-Null
    $lines.Add(".\scripts\Invoke-RemoteDeskCheck.ps1$dirtySourceArgument -Target <host-ip> -PromptForPassword -LinuxProtocolProbe -LinuxProtocolProbePullRemoteFiles -WriteAcceptanceReport") | Out-Null
    if (-not $WindowsOnly -and -not $LinuxOnly -and -not $SkipAndroid) {
        $lines.Add(".\scripts\Invoke-RemoteDeskCheck.ps1$dirtySourceArgument -InstallAndroid -AndroidClearLog -StartAndroid -AndroidStatus -WriteAcceptanceReport") | Out-Null
        $lines.Add(".\scripts\Invoke-RemoteDeskCheck.ps1$dirtySourceArgument -AndroidPullLog -WriteAcceptanceReport") | Out-Null
    }

    $lines.Add('```') | Out-Null

    $lines | Set-Content -Path $reportPath -Encoding UTF8
    Write-Check "Acceptance report" $true "saved to $reportPath"
}

function Get-RelativePathCompat {
    param(
        [string]$BasePath,
        [string]$TargetPath
    )

    $baseFullPath = [System.IO.Path]::GetFullPath($BasePath)
    $targetFullPath = [System.IO.Path]::GetFullPath($TargetPath)
    if ([System.IO.Path].GetMethod("GetRelativePath", [type[]]@([string], [string])) -ne $null) {
        return [System.IO.Path]::GetRelativePath($baseFullPath, $targetFullPath)
    }

    if (-not $baseFullPath.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
        $baseFullPath += [System.IO.Path]::DirectorySeparatorChar
    }

    $baseUri = [Uri]$baseFullPath
    $targetUri = [Uri]$targetFullPath
    return [Uri]::UnescapeDataString($baseUri.MakeRelativeUri($targetUri).ToString()).Replace("/", [System.IO.Path]::DirectorySeparatorChar)
}

function Get-Sha256Hex {
    param([System.IO.Stream]$Stream)

    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hashBytes = $sha256.ComputeHash($Stream)
    }
    finally {
        $sha256.Dispose()
    }

    return [System.BitConverter]::ToString($hashBytes).Replace("-", "").ToLowerInvariant()
}

function Get-FileSha256Hex {
    param([string]$Path)

    $stream = [System.IO.File]::Open(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read)
    try {
        return Get-Sha256Hex -Stream $stream
    }
    finally {
        $stream.Dispose()
    }
}

function Get-GitRepositoryHead {
    param([string]$Root)

    $git = Get-Command "git" -ErrorAction SilentlyContinue
    if ($null -eq $git) {
        return [pscustomobject]@{
            IsRepository = $false
            Revision = ""
            Error = ""
        }
    }

    try {
        $insideOutput = @(
            & $git.Source -C $Root rev-parse --is-inside-work-tree 2>$null)
        $insideExitCode = $LASTEXITCODE
    }
    catch {
        return [pscustomobject]@{
            IsRepository = $false
            Revision = ""
            Error = ""
        }
    }

    $insideWorkTree =
        ([string]($insideOutput | Select-Object -First 1)).Trim()
    if ($insideExitCode -ne 0 -or $insideWorkTree -cne "true") {
        return [pscustomobject]@{
            IsRepository = $false
            Revision = ""
            Error = ""
        }
    }

    try {
        $revisionOutput = @(
            & $git.Source -C $Root rev-parse --verify HEAD 2>$null)
        $revisionExitCode = $LASTEXITCODE
    }
    catch {
        return [pscustomobject]@{
            IsRepository = $true
            Revision = ""
            Error = "cannot read the current git HEAD: $($_.Exception.Message)"
        }
    }

    $revision = ([string](
        $revisionOutput | Select-Object -First 1)).Trim()
    if ($revisionExitCode -ne 0 -or
        $revision -notmatch "^[0-9a-fA-F]{40}$") {
        return [pscustomobject]@{
            IsRepository = $true
            Revision = ""
            Error = "cannot read a valid current git HEAD"
        }
    }

    return [pscustomobject]@{
        IsRepository = $true
        Revision = $revision.ToLowerInvariant()
        Error = ""
    }
}

function Test-WindowsFfmpegCompanionInstaller {
    param([string]$InstallerPath)

    if (-not (Test-Path -LiteralPath $InstallerPath -PathType Leaf)) {
        return [pscustomobject]@{
            Ok = $false
            Detail = "missing pinned companion installer: $InstallerPath"
        }
    }

    try {
        $tokens = $null
        $parseErrors = $null
        [void][System.Management.Automation.Language.Parser]::ParseFile(
            $InstallerPath,
            [ref]$tokens,
            [ref]$parseErrors)
        if (@($parseErrors).Count -gt 0) {
            return [pscustomobject]@{
                Ok = $false
                Detail = "companion installer has PowerShell parse errors"
            }
        }

        $script = Get-Content -LiteralPath $InstallerPath -Raw
        $requiredText = @(
            "ffmpeg-8.1.2-essentials_build.zip",
            "db580001caa24ac104c8cb856cd113a87b0a443f7bdf47d8c12b1d740584a2ec",
            "1326dde4c84ff1f96fe6b8916c5bed29e163e9b5dccf995f6f3db069d143ec5e",
            "gfxcapture",
            "FFMPEG-LICENSE.txt",
            "FFMPEG-DEPENDENCY.json")
        $missing = @(
            $requiredText |
                Where-Object {
                    $script.IndexOf(
                        $_,
                        [System.StringComparison]::Ordinal) -lt 0
                })
        if ($missing.Count -gt 0) {
            return [pscustomobject]@{
                Ok = $false
                Detail = (
                    "companion installer is missing pinned gates: " +
                    ($missing -join ", "))
            }
        }

        return [pscustomobject]@{
            Ok = $true
            Detail = (
                "FFmpeg 8.1.2 archive/executable SHA-256, gfxcapture, " +
                "hardware encoder and GPLv3 notice installation are pinned")
        }
    }
    catch {
        return [pscustomobject]@{
            Ok = $false
            Detail = $_.Exception.Message
        }
    }
}

function Test-WindowsZipExecutable {
    param(
        [string]$ZipPath,
        [string]$ExecutablePath
    )

    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop | Out-Null
        $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    }
    catch {
        return [pscustomobject]@{
            Ok = $false
            Detail = "cannot open Windows zip: $($_.Exception.Message)"
        }
    }

    try {
        $expectedEntryNames = @(
            "RemoteDesk.exe",
            "Install-RemoteDeskFfmpeg.ps1",
            "THIRD-PARTY-NOTICES.md",
            "DOTNET-RUNTIME-LICENSE.TXT",
            "DOTNET-RUNTIME-THIRD-PARTY-NOTICES.TXT")
        $outputDirectory = Split-Path -Parent $ExecutablePath
        if (-not (Test-Path -LiteralPath $outputDirectory -PathType Container)) {
            return [pscustomobject]@{
                Ok = $false
                Detail = "missing canonical Windows output directory: $outputDirectory"
            }
        }

        $outputEntries = @(
            Get-ChildItem -LiteralPath $outputDirectory -Force)
        $unexpectedOutputEntries = @(
            $outputEntries |
                Where-Object {
                    $_.PSIsContainer -or
                    $_.Name -cnotin $expectedEntryNames
                } |
                Select-Object -ExpandProperty Name)
        $missingOutputFiles = @(
            $expectedEntryNames |
                Where-Object {
                    -not (Test-Path -LiteralPath (
                        Join-Path $outputDirectory $_) -PathType Leaf)
                })
        if ($unexpectedOutputEntries.Count -gt 0 -or
            $missingOutputFiles.Count -gt 0) {
            return [pscustomobject]@{
                Ok = $false
                Detail = (
                    "Canonical Windows output must contain only: " +
                    "$($expectedEntryNames -join ', '); " +
                    "unexpected=[$($unexpectedOutputEntries -join ', ')]; " +
                    "missing=[$($missingOutputFiles -join ', ')]")
            }
        }

        $entryNames = @(
            $archive.Entries |
                Select-Object -ExpandProperty FullName)
        $unexpectedEntryNames = @(
            $entryNames |
                Where-Object { $_ -cnotin $expectedEntryNames })
        $missingEntryNames = @(
            $expectedEntryNames |
                Where-Object { $_ -cnotin $entryNames })
        if ($entryNames.Count -ne $expectedEntryNames.Count -or
            $unexpectedEntryNames.Count -gt 0 -or
            $missingEntryNames.Count -gt 0) {
            return [pscustomobject]@{
                Ok = $false
                Detail = (
                    "Windows zip must contain only these root files: " +
                    "$($expectedEntryNames -join ', '); " +
                    "unexpected=[$($unexpectedEntryNames -join ', ')]; " +
                    "missing=[$($missingEntryNames -join ', ')]")
            }
        }

        foreach ($expectedEntryName in $expectedEntryNames) {
            $matchingEntries = @(
                $archive.Entries |
                    Where-Object {
                        [string]::Equals(
                            $_.FullName,
                            $expectedEntryName,
                            [System.StringComparison]::Ordinal)
                    })
            if ($matchingEntries.Count -ne 1) {
                return [pscustomobject]@{
                    Ok = $false
                    Detail = (
                        "Windows zip must contain exactly one root " +
                        "$expectedEntryName; found $($matchingEntries.Count)")
                }
            }

            $canonicalFile = Get-Item -LiteralPath (
                Join-Path $outputDirectory $expectedEntryName)
            $zipEntry = $matchingEntries[0]
            if ([int64]$zipEntry.Length -ne $canonicalFile.Length) {
                return [pscustomobject]@{
                    Ok = $false
                    Detail = (
                        "Windows zip $expectedEntryName length does not " +
                        "match the canonical output")
                }
            }

            $zipStream = $zipEntry.Open()
            try {
                $zipHash = Get-Sha256Hex -Stream $zipStream
            }
            finally {
                $zipStream.Dispose()
            }
            $canonicalHash =
                Get-FileSha256Hex -Path $canonicalFile.FullName
            if ($zipHash -ne $canonicalHash) {
                return [pscustomobject]@{
                    Ok = $false
                    Detail = (
                        "Windows zip $expectedEntryName SHA256 does not " +
                        "match the canonical output")
                }
            }
        }

        return [pscustomobject]@{
            Ok = $true
            Detail = (
                "Windows zip contains the canonical executable, project " +
                "notice, and exact .NET runtime pack license notices")
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Test-LinuxPortableZipModes {
    param([string]$ZipPath)

    if (-not (Test-Path -LiteralPath $ZipPath -PathType Leaf)) {
        return [pscustomobject]@{
            Ok = $false
            Detail = "missing Linux portable zip"
        }
    }

    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem `
            -ErrorAction Stop | Out-Null
        $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    }
    catch {
        return [pscustomobject]@{
            Ok = $false
            Detail = "cannot open Linux portable zip: $($_.Exception.Message)"
        }
    }

    try {
        $requiredExecutableEntries = @(
            "remotedesk-linux-host",
            "remotedesk-protocol-probe",
            "remotedesk-linux-app",
            "remotedesk-linux-doctor")
        foreach ($entryName in $requiredExecutableEntries) {
            $entries = @(
                $archive.Entries |
                    Where-Object {
                        [string]::Equals(
                            $_.FullName,
                            $entryName,
                            [System.StringComparison]::Ordinal)
                    })
            if ($entries.Count -ne 1) {
                return [pscustomobject]@{
                    Ok = $false
                    Detail = (
                        "Linux portable zip must contain exactly one " +
                        "$entryName; found $($entries.Count)")
                }
            }

            $unixMode =
                ($entries[0].ExternalAttributes -shr 16) -band 0xFFFF
            if (($unixMode -band 0x1FF) -ne 0x1ED) {
                return [pscustomobject]@{
                    Ok = $false
                    Detail = (
                        "Linux portable zip entry $entryName is not mode " +
                        "0755; found 0$([Convert]::ToString(($unixMode -band 0x1FF), 8))")
                }
            }
        }

        return [pscustomobject]@{
            Ok = $true
            Detail = "all four Linux portable entry scripts have Unix mode 0755"
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Resolve-ReleaseManifestScopeKind {
    param([string]$Scope)

    switch ($Scope.Trim()) {
        "Windows host/viewer; Android; Linux host/viewer package" {
            return "Full"
        }
        "Windows host/viewer; Linux host/viewer package" {
            return "Desktop"
        }
        "Windows host/viewer package" {
            return "Windows"
        }
        "Linux host/viewer package" {
            return "Linux"
        }
        default {
            return ""
        }
    }
}

function Resolve-RequestedReleaseScopeKind {
    param(
        [bool]$WindowsOnly,
        [bool]$LinuxOnly,
        [bool]$SkipAndroid
    )

    if ($LinuxOnly) {
        return "Linux"
    }

    if ($WindowsOnly) {
        return "Windows"
    }

    if ($SkipAndroid) {
        return "Desktop"
    }

    return "Full"
}

function Test-ReleaseManifest {
    param(
        [string]$ManifestPath,
        [string]$Root,
        [bool]$WindowsOnly,
        [bool]$LinuxOnly,
        [bool]$SkipAndroid,
        [bool]$AllowDirtySource
    )

    if (-not (Test-Path $ManifestPath)) {
        return [pscustomobject]@{
            Ok = $false
            Detail = "missing; run .\scripts\Publish-RemoteDesk.ps1"
        }
    }

    try {
        $manifest = Get-Content -Path $ManifestPath -Raw | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        return [pscustomobject]@{
            Ok = $false
            Detail = "invalid JSON: $($_.Exception.Message)"
        }
    }

    $manifestBuildStamp = ([string]$manifest.buildStamp).Trim()
    try {
        $buildStartedAtUtc = [DateTime]::ParseExact(
            $manifestBuildStamp,
            "yyyyMMddHHmmss",
            [System.Globalization.CultureInfo]::InvariantCulture,
            [System.Globalization.DateTimeStyles]::AssumeUniversal -bor
                [System.Globalization.DateTimeStyles]::AdjustToUniversal)
        $manifestGeneratedAt = [DateTimeOffset]::Parse(
            [string]$manifest.generatedAtUtc,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [System.Globalization.DateTimeStyles]::AssumeUniversal)
    }
    catch {
        return [pscustomobject]@{
            Ok = $false
            Detail = "invalid build stamp or generation time: build='$($manifest.buildStamp)', generated='$($manifest.generatedAtUtc)'"
        }
    }
    if ($manifestGeneratedAt.UtcDateTime -lt $buildStartedAtUtc -or
        $manifestGeneratedAt.UtcDateTime -gt
            $buildStartedAtUtc.AddHours(24)) {
        return [pscustomobject]@{
            Ok = $false
            Detail = "manifest generation time is inconsistent with build stamp $manifestBuildStamp"
        }
    }

    $sourceRevisionProperty =
        $manifest.PSObject.Properties["sourceRevision"]
    $manifestSourceRevision =
        if ($null -eq $sourceRevisionProperty) {
            ""
        }
        else {
            ([string]$sourceRevisionProperty.Value).Trim()
        }
    if ($null -eq $sourceRevisionProperty -or
        $sourceRevisionProperty.Value -isnot [string] -or
        $manifestSourceRevision -cnotmatch "^[0-9a-f]{40}$") {
        return [pscustomobject]@{
            Ok = $false
            Detail = "manifest sourceRevision must be a 40-character lowercase git revision"
        }
    }

    $repositoryHead = Get-GitRepositoryHead -Root $Root
    if ($repositoryHead.IsRepository) {
        if (-not [string]::IsNullOrWhiteSpace($repositoryHead.Error)) {
            return [pscustomobject]@{
                Ok = $false
                Detail = $repositoryHead.Error
            }
        }

        if ($repositoryHead.Revision -cne $manifestSourceRevision) {
            return [pscustomobject]@{
                Ok = $false
                Detail = (
                    "manifest source revision does not match current git " +
                    "HEAD: manifest=$manifestSourceRevision, " +
                    "HEAD=$($repositoryHead.Revision)")
            }
        }
    }

    $sourceFingerprintVersionProperty =
        $manifest.PSObject.Properties["sourceFingerprintVersion"]
    $sourceFingerprintAlgorithmProperty =
        $manifest.PSObject.Properties["sourceFingerprintAlgorithm"]
    $sourceFingerprintProperty =
        $manifest.PSObject.Properties["sourceFingerprint"]
    $sourceFileCountProperty =
        $manifest.PSObject.Properties["sourceFileCount"]
    if ($null -eq $sourceFingerprintVersionProperty -or
        [int]$sourceFingerprintVersionProperty.Value -ne 1 -or
        $null -eq $sourceFingerprintAlgorithmProperty -or
        [string]$sourceFingerprintAlgorithmProperty.Value -cne "SHA256" -or
        $null -eq $sourceFingerprintProperty -or
        [string]$sourceFingerprintProperty.Value -cnotmatch "^[0-9a-f]{64}$" -or
        $null -eq $sourceFileCountProperty -or
        [int]$sourceFileCountProperty.Value -le 0) {
        return [pscustomobject]@{
            Ok = $false
            Detail = "manifest source fingerprint metadata is missing or invalid"
        }
    }

    try {
        $currentFingerprint =
            Get-RemoteDeskSourceFingerprint -Root $Root
    }
    catch {
        return [pscustomobject]@{
            Ok = $false
            Detail = "cannot calculate current source fingerprint: $($_.Exception.Message)"
        }
    }
    $manifestSourceFingerprint =
        [string]$sourceFingerprintProperty.Value
    $manifestSourceFileCount =
        [int]$sourceFileCountProperty.Value
    if ($currentFingerprint.Hash -cne
            $manifestSourceFingerprint -or
        $currentFingerprint.FileCount -ne
            $manifestSourceFileCount) {
        return [pscustomobject]@{
            Ok = $false
            Detail = (
                "manifest source fingerprint does not match the current " +
                "source tree: manifest=$manifestSourceFingerprint/" +
                "$manifestSourceFileCount files, current=" +
                "$($currentFingerprint.Hash)/" +
                "$($currentFingerprint.FileCount) files")
        }
    }

    $sourceDirtyProperty =
        $manifest.PSObject.Properties["sourceDirty"]
    if ($null -eq $sourceDirtyProperty -or
        $sourceDirtyProperty.Value -isnot [bool]) {
        return [pscustomobject]@{
            Ok = $false
            Detail = "manifest sourceDirty must be a Boolean"
        }
    }
    $manifestSourceDirty = [bool]$sourceDirtyProperty.Value

    $internalCandidateProperty =
        $manifest.PSObject.Properties["internalCandidate"]
    if ($null -eq $internalCandidateProperty -or
        $internalCandidateProperty.Value -isnot [bool]) {
        return [pscustomobject]@{
            Ok = $false
            Detail = "manifest internalCandidate must be a Boolean"
        }
    }
    $manifestInternalCandidate =
        [bool]$internalCandidateProperty.Value
    if ($manifestSourceDirty -and -not $manifestInternalCandidate) {
        return [pscustomobject]@{
            Ok = $false
            Detail = (
                "dirty-source manifest is invalid unless it is explicitly " +
                "marked as an internal candidate")
        }
    }
    if ($manifestSourceDirty -and -not $AllowDirtySource) {
        return [pscustomobject]@{
            Ok = $false
            Detail = (
                "manifest was built from dirty source; pass " +
                "-AllowDirtySource only to inspect this internal candidate")
        }
    }
    if ($manifestInternalCandidate -and -not $AllowDirtySource) {
        return [pscustomobject]@{
            Ok = $false
            Detail = (
                "manifest is marked as an internal candidate; pass " +
                "-AllowDirtySource only for explicit internal validation")
        }
    }

    $entries = @($manifest.artifacts)
    if ($entries.Count -eq 0) {
        return [pscustomobject]@{
            Ok = $false
            Detail = "no artifact entries"
        }
    }

    $manifestScope = ([string]$manifest.scope).Trim()
    $scopeKind =
        Resolve-ReleaseManifestScopeKind -Scope $manifestScope

    if ([string]::IsNullOrWhiteSpace($scopeKind)) {
        return [pscustomobject]@{
            Ok = $false
            Detail = "unexpected scope: $($manifest.scope)"
        }
    }

    $toolchainProperty =
        $manifest.PSObject.Properties["toolchain"]
    if ($null -eq $toolchainProperty -or
        $null -eq $toolchainProperty.Value) {
        return [pscustomobject]@{
            Ok = $false
            Detail = "manifest toolchain metadata is missing"
        }
    }
    $toolchain = $toolchainProperty.Value
    $dotNetSdkVersion = $null
    $gradleVersion = $null
    $javaVersion = $null
    $linuxPythonVersion = $null
    try {
        if ($scopeKind -in @("Full", "Desktop", "Windows")) {
            $dotNetSdkVersion = Get-RemoteDeskDotNetSdkVersion
        }
        if ($scopeKind -in @("Full", "Desktop", "Linux")) {
            $linuxPythonVersion = Get-RemoteDeskLinuxPythonVersion -Distro $LinuxDistro
        }
        if ($scopeKind -eq "Full") {
            $gradlePath = Get-GradleCommand -ExplicitPath $GradlePath
            $gradleVersion = Get-RemoteDeskGradleVersion -Gradle $gradlePath
            $javaVersion = Get-RemoteDeskJavaVersion
        }
    }
    catch {
        return [pscustomobject]@{
            Ok = $false
            Detail = "cannot resolve current release toolchain: $($_.Exception.Message)"
        }
    }
    $toolchainChecks = @(
        @("dotnetSdk", $dotNetSdkVersion),
        @("gradle", $gradleVersion),
        @("java", $javaVersion),
        @("linuxPython", $linuxPythonVersion))
    foreach ($toolchainCheck in $toolchainChecks) {
        $field = [string]$toolchainCheck[0]
        $expected = $toolchainCheck[1]
        $actual = [string]$toolchain.$field
        if (-not [string]::IsNullOrWhiteSpace($expected) -and
            $actual -cne $expected) {
            return [pscustomobject]@{
                Ok = $false
                Detail = (
                    "manifest toolchain $field does not match current " +
                    "environment: manifest='$actual', current='$expected'")
            }
        }
    }
    if ($scopeKind -in @("Full", "Desktop", "Linux")) {
        $manifestLinuxDistro = [string]$toolchain.linuxDistro
        if ([string]::IsNullOrWhiteSpace($manifestLinuxDistro)) {
            return [pscustomobject]@{
                Ok = $false
                Detail = "manifest Linux WSL distro is missing"
            }
        }
        if ($manifestLinuxDistro -cne $LinuxDistro) {
            return [pscustomobject]@{
                Ok = $false
                Detail = (
                    "manifest toolchain linuxDistro does not match current " +
                    "environment: manifest='$manifestLinuxDistro', current='$LinuxDistro'")
            }
        }
    }
    if ($scopeKind -in @("Full", "Desktop", "Windows") -and
        [string]::IsNullOrWhiteSpace(
            [string]$toolchain.dotnetSdk)) {
        return [pscustomobject]@{
            Ok = $false
            Detail = "manifest .NET SDK version is missing"
        }
    }
    if ($scopeKind -in @("Full", "Desktop", "Linux") -and
        [string]::IsNullOrWhiteSpace(
            [string]$toolchain.linuxPython)) {
        return [pscustomobject]@{
            Ok = $false
            Detail = "manifest Linux Python version is missing"
        }
    }
    if ($scopeKind -eq "Full" -and
        ([string]::IsNullOrWhiteSpace(
                [string]$toolchain.gradle) -or
            [string]::IsNullOrWhiteSpace(
                [string]$toolchain.java))) {
        return [pscustomobject]@{
            Ok = $false
            Detail = "manifest Gradle or Java version is missing"
        }
    }

    $requestedScopeKind = Resolve-RequestedReleaseScopeKind `
        -WindowsOnly:$WindowsOnly `
        -LinuxOnly:$LinuxOnly `
        -SkipAndroid:$SkipAndroid
    if ($scopeKind -ne $requestedScopeKind) {
        return [pscustomobject]@{
            Ok = $false
            Detail = "manifest scope '$manifestScope' does not match requested $requestedScopeKind check"
        }
    }

    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd([char[]]@(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar))
    $entryPaths = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)

    foreach ($entry in $entries) {
        if ([string]::IsNullOrWhiteSpace($entry.path)) {
            return [pscustomobject]@{
                Ok = $false
                Detail = "artifact entry has no path"
            }
        }

        $entryPath = [string]$entry.path
        if ([System.IO.Path]::IsPathRooted($entryPath)) {
            return [pscustomobject]@{
                Ok = $false
                Detail = "artifact path must be repository-relative: $entryPath"
            }
        }

        $candidatePath = Join-Path $Root $entryPath
        $artifactFull = [System.IO.Path]::GetFullPath($candidatePath)
        $isUnderRoot = $artifactFull.StartsWith(
            $rootFull + [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)
        if (-not $isUnderRoot) {
            return [pscustomobject]@{
                Ok = $false
                Detail = "artifact path escapes repo: $entryPath"
            }
        }

        if (-not (Test-Path -LiteralPath $artifactFull -PathType Leaf)) {
            return [pscustomobject]@{
                Ok = $false
                Detail = "missing artifact: $entryPath"
            }
        }

        $item = Get-Item -LiteralPath $artifactFull
        $relativeArtifactPath = (Get-RelativePathCompat -BasePath $Root -TargetPath $artifactFull).Replace("/", "\")
        $entryPathForComparison = $entryPath.Replace("/", "\")
        if (-not [string]::Equals(
                $entryPathForComparison,
                $relativeArtifactPath,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            return [pscustomobject]@{
                Ok = $false
                Detail = "artifact path is not canonical: $entryPath"
            }
        }

        if (-not [string]::Equals(
                [string]$entry.name,
                $item.Name,
                [System.StringComparison]::Ordinal)) {
            return [pscustomobject]@{
                Ok = $false
                Detail = "artifact name does not match path: $entryPath"
            }
        }

        if (-not $entryPaths.Add($relativeArtifactPath)) {
            return [pscustomobject]@{
                Ok = $false
                Detail = "duplicate artifact entry: $relativeArtifactPath"
            }
        }

        if ([int64]$entry.length -ne $item.Length) {
            return [pscustomobject]@{
                Ok = $false
                Detail = "length mismatch for $($item.Name): manifest $($entry.length), actual $($item.Length)"
            }
        }

        $expectedHash = ([string]$entry.sha256).ToLowerInvariant()
        if ($expectedHash -notmatch "^[0-9a-f]{64}$") {
            return [pscustomobject]@{
                Ok = $false
                Detail = "invalid SHA256 for $($item.Name)"
            }
        }

        $actualHash = Get-FileSha256Hex -Path $artifactFull
        if ($actualHash -ne $expectedHash) {
            return [pscustomobject]@{
                Ok = $false
                Detail = "SHA256 mismatch for $($item.Name)"
            }
        }
    }

    $expectedArtifacts = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    if (-not $LinuxOnly) {
        [void]$expectedArtifacts.Add(
            "artifacts\RemoteDesk-win-x64\RemoteDesk.exe")
        [void]$expectedArtifacts.Add(
            "artifacts\RemoteDesk-win-x64.zip")
    }
    if (-not $WindowsOnly) {
        [void]$expectedArtifacts.Add(
            "artifacts\RemoteDesk-linux-host.zip")
        [void]$expectedArtifacts.Add(
            "artifacts\RemoteDesk-linux-host.tar.gz")
        $linuxDebArtifacts = @(
            $entryPaths |
                Where-Object {
                    $_ -match "^artifacts\\remotedesk-linux-host_[^\\]+_[^\\]+\.deb$"
                })
        if ($linuxDebArtifacts.Count -ne 1) {
            return [pscustomobject]@{
                Ok = $false
                Detail = "manifest must contain exactly one canonical Linux deb artifact; found $($linuxDebArtifacts.Count)"
            }
        }

        [void]$expectedArtifacts.Add($linuxDebArtifacts[0])
    }

    if ($scopeKind -eq "Full") {
        $androidReleaseProperty =
            $manifest.PSObject.Properties["androidReleaseRequested"]
        if ($null -eq $androidReleaseProperty -or
            $androidReleaseProperty.Value -isnot [bool]) {
            return [pscustomobject]@{
                Ok = $false
                Detail = "manifest androidReleaseRequested must be a Boolean"
            }
        }

        $androidReleaseRequested =
            [bool]$androidReleaseProperty.Value
        $androidArtifactPaths = @(
            "artifacts\RemoteDesk-android-debug.apk",
            "artifacts\RemoteDesk-android-release.apk",
            "artifacts\RemoteDesk-android-release-unsigned.apk")
        if ($androidReleaseRequested) {
            $selectedAndroidArtifacts = @(
                $androidArtifactPaths[1..2] |
                    Where-Object {
                        $entryPaths.Contains($_)
                    })
            if ($selectedAndroidArtifacts.Count -ne 1) {
                return [pscustomobject]@{
                    Ok = $false
                    Detail = "manifest must contain exactly one requested Android release APK; found $($selectedAndroidArtifacts.Count)"
                }
            }
        }
        else {
            $selectedAndroidArtifacts = @(
                $androidArtifactPaths[0] |
                    Where-Object {
                        $entryPaths.Contains($_)
                    })
            if ($selectedAndroidArtifacts.Count -ne 1) {
                return [pscustomobject]@{
                    Ok = $false
                    Detail = "manifest missing required Android debug APK"
                }
            }
        }

        [void]$expectedArtifacts.Add(
            $selectedAndroidArtifacts[0])
        foreach ($androidPath in $androidArtifactPaths) {
            $androidFullPath = Join-Path $Root $androidPath
            if (Test-Path -LiteralPath $androidFullPath -PathType Leaf) {
                if (-not $entryPaths.Contains($androidPath)) {
                    return [pscustomobject]@{
                        Ok = $false
                        Detail = "stale unlisted canonical Android artifact: $androidPath"
                    }
                }
            }
        }
    }

    foreach ($expectedArtifact in $expectedArtifacts) {
        if (-not $entryPaths.Contains($expectedArtifact)) {
            return [pscustomobject]@{
                Ok = $false
                Detail = "manifest missing required artifact: $expectedArtifact"
            }
        }
    }
    foreach ($entryPath in $entryPaths) {
        if (-not $expectedArtifacts.Contains($entryPath)) {
            return [pscustomobject]@{
                Ok = $false
                Detail = "unexpected non-canonical manifest artifact: $entryPath"
            }
        }
    }

    if (-not $LinuxOnly) {
        $canonicalExecutable = Get-Item -LiteralPath (
            Join-Path $Root "artifacts\RemoteDesk-win-x64\RemoteDesk.exe")
        if ($canonicalExecutable.LastWriteTimeUtc -lt
                $buildStartedAtUtc.AddMinutes(-5) -or
            $canonicalExecutable.LastWriteTimeUtc -gt
                $manifestGeneratedAt.UtcDateTime.AddMinutes(5)) {
            return [pscustomobject]@{
                Ok = $false
                Detail = "canonical RemoteDesk.exe timestamp is inconsistent with build $manifestBuildStamp"
            }
        }

        $zipCheck = Test-WindowsZipExecutable `
            -ZipPath (Join-Path $Root "artifacts\RemoteDesk-win-x64.zip") `
            -ExecutablePath (Join-Path $Root "artifacts\RemoteDesk-win-x64\RemoteDesk.exe")
        if (-not $zipCheck.Ok) {
            return $zipCheck
        }

        $authenticodeProperty =
            $manifest.PSObject.Properties["windowsAuthenticode"]
        if ($null -eq $authenticodeProperty -or
            $null -eq $authenticodeProperty.Value) {
            return [pscustomobject]@{
                Ok = $false
                Detail = "manifest Windows Authenticode metadata is missing"
            }
        }
        $expectedAuthenticode = $authenticodeProperty.Value
        if ([string]$expectedAuthenticode.status -notin @(
                "Valid",
                "NotSigned") -or
            $expectedAuthenticode.timestamped -isnot [bool]) {
            return [pscustomobject]@{
                Ok = $false
                Detail = "manifest Windows Authenticode metadata is invalid"
            }
        }
        if ([string]$expectedAuthenticode.status -ceq "Valid" -and
            (-not [bool]$expectedAuthenticode.timestamped -or
             [string]$expectedAuthenticode.signerThumbprint -cnotmatch
                "^[0-9A-F]{40}$" -or
             [string]$expectedAuthenticode.signerCertificateSha256 -cnotmatch
                "^[0-9A-F]{64}$")) {
            return [pscustomobject]@{
                Ok = $false
                Detail = "valid Windows signature metadata must include a timestamp and signer identity"
            }
        }
        try {
            $actualAuthenticode =
                Get-RemoteDeskWindowsAuthenticodeMetadata `
                    -Path $canonicalExecutable.FullName
        }
        catch {
            return [pscustomobject]@{
                Ok = $false
                Detail = "cannot verify Windows Authenticode metadata: $($_.Exception.Message)"
            }
        }
        foreach ($field in @(
            "status",
            "signerThumbprint",
            "signerCertificateSha256",
            "timestamped",
            "timestampSignerThumbprint")) {
            if ([string]$expectedAuthenticode.$field -cne
                [string]$actualAuthenticode.$field) {
                return [pscustomobject]@{
                    Ok = $false
                    Detail = "manifest Windows Authenticode $field does not match the canonical executable"
                }
            }
        }
    }

    $sourceIdentity =
        "$($manifestSourceRevision.Substring(0, 12))/$($manifestSourceFingerprint.Substring(0, 12))"
    $toolchainIdentity =
        "dotnet=$($toolchain.dotnetSdk) gradle=$($toolchain.gradle) " +
        "linuxPython=$($toolchain.linuxPython) distro=$($toolchain.linuxDistro)"
    $verificationDetail = if ($LinuxOnly) {
        "$($entries.Count) canonical artifacts, build $manifestBuildStamp, source $sourceIdentity dirty=$manifestSourceDirty internalCandidate=$manifestInternalCandidate, $toolchainIdentity verified for $requestedScopeKind scope"
    }
    else {
        "$($entries.Count) canonical artifacts, build $manifestBuildStamp, source $sourceIdentity dirty=$manifestSourceDirty internalCandidate=$manifestInternalCandidate, $toolchainIdentity, and Windows zip payload verified for $requestedScopeKind scope"
    }
    return [pscustomobject]@{
        Ok = $true
        Detail = $verificationDetail
    }
}

$root = Resolve-RepoRoot
Set-Location $root
$script:InitialAdbProcessIds = @(
    Get-AdbProcesses |
        ForEach-Object { [int]$_.Id })
$runWindowsChecks = -not $LinuxOnly
$runLinuxChecks = -not $WindowsOnly
$runAndroidChecks =
    -not $WindowsOnly -and
    -not $LinuxOnly -and
    -not $SkipAndroid
$androidScopeReason = if ($WindowsOnly) {
    "-WindowsOnly"
}
elseif ($LinuxOnly) {
    "-LinuxOnly"
}
else {
    "-SkipAndroid"
}

Write-Host "RemoteDesk environment check" -ForegroundColor Cyan
Write-Host "Repo: $root"
Write-Host ""

$winExe = Join-Path $root "artifacts\RemoteDesk-win-x64\RemoteDesk.exe"
$winZip = Join-Path $root "artifacts\RemoteDesk-win-x64.zip"
$linuxHostZip = Join-Path $root "artifacts\RemoteDesk-linux-host.zip"
$linuxHostTarGz = Join-Path $root "artifacts\RemoteDesk-linux-host.tar.gz"
$linuxHostDeb = @(Get-ChildItem -Path (Join-Path $root "artifacts") -Filter "remotedesk-linux-host_*_*.deb" -File -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1).FullName
if ($null -eq $linuxHostDeb) {
    $linuxHostDeb = Join-Path $root "artifacts\remotedesk-linux-host_0.1.0_all.deb"
}
$androidApk = Join-Path $root "artifacts\RemoteDesk-android-debug.apk"
$androidReleaseApk = Join-Path $root "artifacts\RemoteDesk-android-release.apk"
$androidUnsignedReleaseApk = Join-Path $root "artifacts\RemoteDesk-android-release-unsigned.apk"
$releaseManifest = Join-Path $root "artifacts\RemoteDesk-release-manifest.json"
$manifestAndroidReleaseRequested = $false
if ($runAndroidChecks -and
    (Test-Path -LiteralPath $releaseManifest -PathType Leaf)) {
    try {
        $manifestMetadata =
            Get-Content -LiteralPath $releaseManifest -Raw |
                ConvertFrom-Json -ErrorAction Stop
        $androidReleaseProperty =
            $manifestMetadata.PSObject.Properties[
                "androidReleaseRequested"]
        if ($null -ne $androidReleaseProperty -and
            $androidReleaseProperty.Value -is [bool]) {
            $manifestAndroidReleaseRequested =
                [bool]$androidReleaseProperty.Value
        }
    }
    catch {
    }
}
$obsoleteArtifactPaths = @(
    (Join-Path $root "artifacts\RemoteDesk-win-x64-updated.zip"),
    (Join-Path $root "artifacts\RemoteDesk-win-x64-updated"))

if ($runWindowsChecks) {
    Write-Check "Windows exe artifact" (Test-Path $winExe) ($(if (Test-Path $winExe) { "$((Get-Item $winExe).Length) bytes" } else { "missing" }))
    Write-Check "Windows zip artifact" (Test-Path $winZip) ($(if (Test-Path $winZip) { "$((Get-Item $winZip).Length) bytes" } else { "missing" }))
    $ffmpegInstallerCheck =
        Test-WindowsFfmpegCompanionInstaller `
            -InstallerPath (
                Join-Path (
                    Split-Path -Parent $winExe) `
                    "Install-RemoteDeskFfmpeg.ps1")
    Write-Check `
        "Windows gfxcapture companion installer" `
        $ffmpegInstallerCheck.Ok `
        $ffmpegInstallerCheck.Detail
    try {
        $windowsAuthenticode =
            Get-RemoteDeskWindowsAuthenticodeMetadata -Path $winExe
        $windowsAuthenticodeOk =
            $windowsAuthenticode.status -in @("Valid", "NotSigned")
        $windowsAuthenticodeDetail = if (
            $windowsAuthenticode.status -ceq "Valid") {
            "Valid; signer=$($windowsAuthenticode.signerThumbprint); timestamped=$($windowsAuthenticode.timestamped)"
        }
        else {
            "NotSigned; authenticated personal-LAN remote update mode available"
        }
        Write-Check `
            "Windows Authenticode" `
            $windowsAuthenticodeOk `
            $windowsAuthenticodeDetail
    }
    catch {
        Write-Check `
            "Windows Authenticode" `
            $false `
            $_.Exception.Message
    }
}
else {
    Write-Check "Windows artifacts" $true "skipped by -LinuxOnly"
}
if ($runLinuxChecks) {
    Write-Check "Linux host helper zip" (Test-Path $linuxHostZip) ($(if (Test-Path $linuxHostZip) { "$((Get-Item $linuxHostZip).Length) bytes" } else { "missing" }))
    $linuxZipModeCheck =
        Test-LinuxPortableZipModes -ZipPath $linuxHostZip
    Write-Check `
        "Linux portable zip executable modes" `
        $linuxZipModeCheck.Ok `
        $linuxZipModeCheck.Detail
    Write-Check "Linux host portable tar.gz" (Test-Path $linuxHostTarGz) ($(if (Test-Path $linuxHostTarGz) { "$((Get-Item $linuxHostTarGz).Length) bytes" } else { "missing" }))
    Write-Check "Ubuntu deb package" (Test-Path $linuxHostDeb) ($(if (Test-Path $linuxHostDeb) { "$((Get-Item $linuxHostDeb).Length) bytes" } else { "missing" }))
}
else {
    Write-Check "Linux artifacts" $true "skipped by -WindowsOnly"
}
if ($runAndroidChecks) {
    if ($manifestAndroidReleaseRequested) {
        if (Test-Path -LiteralPath $androidReleaseApk -PathType Leaf) {
            Write-Check "Android release APK" $true "$((Get-Item $androidReleaseApk).Length) bytes, signed"
        }
        elseif (Test-Path -LiteralPath $androidUnsignedReleaseApk -PathType Leaf) {
            Write-Check "Android release APK" $true "$((Get-Item $androidUnsignedReleaseApk).Length) bytes, unsigned; configure release signing before installing as release"
        }
        else {
            Write-Check "Android release APK" $false "missing"
        }
    }
    else {
        Write-Check "Android debug APK" (Test-Path -LiteralPath $androidApk -PathType Leaf) ($(if (Test-Path -LiteralPath $androidApk -PathType Leaf) { "$((Get-Item $androidApk).Length) bytes" } else { "missing" }))
    }
}
else {
    Write-Check "Android checks" $true "skipped by $androidScopeReason"
}
$manifestCheck = Test-ReleaseManifest `
    -ManifestPath $releaseManifest `
    -Root $root `
    -WindowsOnly:$WindowsOnly `
    -LinuxOnly:$LinuxOnly `
    -SkipAndroid:$SkipAndroid `
    -AllowDirtySource:$AllowDirtySource
Write-Check "Release manifest" $manifestCheck.Ok $manifestCheck.Detail
$obsoleteArtifacts = @($obsoleteArtifactPaths | Where-Object { Test-Path -LiteralPath $_ })
Write-Check "No obsolete artifact aliases" ($obsoleteArtifacts.Count -eq 0) ($(if ($obsoleteArtifacts.Count -eq 0) { "clear" } else { ($obsoleteArtifacts | ForEach-Object { [System.IO.Path]::GetFileName($_) }) -join ", " }))

$dotnet = Get-CommandPath "dotnet"
Write-Check ".NET SDK" ($null -ne $dotnet) ($(if ($dotnet) { $dotnet } else { "not found in PATH" }))

$ffmpeg = Get-CommandPath "ffmpeg"
if ($runAndroidChecks) {
    Write-Check "ffmpeg for H.264 viewer mode" ($null -ne $ffmpeg) ($(if ($ffmpeg) { $ffmpeg } else { "not found; use Stable JPEG or install ffmpeg" }))
}
else {
    Write-Check "ffmpeg for Android H.264 viewer mode" $true "skipped by $androidScopeReason"
}

$adb = if ($runAndroidChecks) { Get-CommandPath "adb" } else { $null }
if ($runAndroidChecks) {
    Write-Check "adb for Android install/test" ($null -ne $adb) ($(if ($adb) { $adb } else { "not found; APK can still be copied manually" }))
}
else {
    Write-Check "adb for Android install/test" $true "skipped by $androidScopeReason"
}

$localIp = Get-LocalIPv4Summary
Write-Check "Local IPv4" (-not [string]::IsNullOrWhiteSpace($localIp)) ($(if ($localIp) { $localIp } else { "none" }))

$processes = Get-Process RemoteDesk, ffmpeg -ErrorAction SilentlyContinue
Write-Check "No stale RemoteDesk/ffmpeg process" ($null -eq $processes) (Format-ProcessSummary $processes)

if ($StartLinuxSandbox) {
    Write-Host ""
    Write-Host "Starting Linux sandbox" -ForegroundColor Cyan
    Invoke-OptionalCommand "Start WSL Linux desktop sandbox" {
        $sandboxScript = Join-Path $root "scripts\Start-RemoteDeskLinuxSandbox.ps1"
        & $sandboxScript `
            -Distro $LinuxDistro `
            -DisplayNumber $LinuxDisplayNumber `
            -VncPort $LinuxVncPort | Write-Host
        if ($LASTEXITCODE -ne 0) {
            throw "Linux sandbox start failed with exit code $LASTEXITCODE"
        }
    }

    $LinuxSandboxStatus = $true
}

if ($LinuxSandboxStatus) {
    $linuxSandbox = Test-LinuxSandboxStatus `
        -Distro $LinuxDistro `
        -DisplayNumber $LinuxDisplayNumber `
        -VncPort $LinuxVncPort
    Write-Check "Linux sandbox X11/VNC status" $linuxSandbox.Ok $linuxSandbox.Detail
}

if ($ScopeCheck) {
    Write-ScopeReadiness `
        -WindowsExe $winExe `
        -WindowsZip $winZip `
        -LinuxHostZip $linuxHostZip `
        -LinuxHostTarGz $linuxHostTarGz `
        -LinuxHostDeb $linuxHostDeb `
        -AndroidDebugApk $androidApk `
        -AndroidReleaseApk $androidReleaseApk `
        -AndroidUnsignedReleaseApk $androidUnsignedReleaseApk `
        -LocalIpSummary $localIp `
        -FfmpegPath $ffmpeg `
        -AdbPath $adb `
        -WindowsOnly:$WindowsOnly `
        -LinuxOnly:$LinuxOnly `
        -SkipAndroid:$SkipAndroid `
        -AndroidReleaseRequested:$manifestAndroidReleaseRequested
}

if (-not [string]::IsNullOrWhiteSpace($Target)) {
    Write-Host ""
    Write-Host "Target diagnostics: $Target" -ForegroundColor Cyan
    $resolvedTargets = @(Resolve-IPv4Targets $Target)
    Write-Check "Resolve target IPv4" ($resolvedTargets.Count -gt 0) ($(if ($resolvedTargets.Count -gt 0) { $resolvedTargets -join ", " } else { "no IPv4 address" }))
    if ($resolvedTargets.Count -gt 0) {
        Write-RemoteDeskLanProbe "Target" $resolvedTargets
        if ($LinuxProtocolProbe) {
            foreach ($address in @($resolvedTargets | Sort-Object -Unique)) {
                $linuxProbe = Invoke-LinuxProtocolProbe `
                    -Distro $LinuxDistro `
                    -Root $root `
                    -Address $address `
                    -TcpPort $Port `
                    -Secret $Password `
                    -SendFile:$LinuxProtocolProbeSendFile `
                    -PullRemoteFiles:$LinuxProtocolProbePullRemoteFiles
                Write-Check "Linux protocol probe $address`:$Port" $linuxProbe.Ok $linuxProbe.Detail
            }
        }
    }
}

if ($RunTests) {
    Write-Host ""
    Write-Host "Running automated tests" -ForegroundColor Cyan
    if ($runWindowsChecks) {
        Invoke-OptionalCommand "dotnet test Release" {
            dotnet test .\RemoteDesk.sln -c Release | Write-Host
            if ($LASTEXITCODE -ne 0) {
                throw "dotnet test failed with exit code $LASTEXITCODE"
            }
        }
    }
    else {
        Write-Check "dotnet test Release" $true "skipped by -LinuxOnly"
    }

    if ($runLinuxChecks) {
        Invoke-OptionalCommand "Linux Python unit tests" {
            Invoke-LinuxPythonTests `
                -Distro $LinuxDistro `
                -Root $root
        }
    }
    else {
        Write-Check "Linux Python unit tests" $true "skipped by -WindowsOnly"
    }

    if ($runAndroidChecks) {
        Invoke-OptionalCommand "Android assembleDebug and unit tests" {
            $gradle = Get-GradleCommand -ExplicitPath $GradlePath
            Write-Host "Gradle: $gradle"
            $gradleExitCode = 0
            $previousDebugItem = Get-Item Env:DEBUG -ErrorAction SilentlyContinue
            $hadDebug = $null -ne $previousDebugItem
            $previousDebugValue = if ($hadDebug) { $previousDebugItem.Value } else { $null }
            try {
                # Gradle's Windows launcher echoes every batch line when DEBUG is set.
                Remove-Item Env:DEBUG -ErrorAction SilentlyContinue
                try {
                    & $gradle --no-daemon -p (Join-Path $root "src\RemoteDesk.Android") assembleDebug testDebugUnitTest
                    $gradleExitCode = $LASTEXITCODE
                }
                finally {
                    try {
                        & $gradle --stop | Out-Null
                    }
                    catch {
                        Write-Host "Gradle daemon cleanup skipped: $($_.Exception.Message)" -ForegroundColor Yellow
                    }
                }
            }
            finally {
                if ($hadDebug) {
                    $env:DEBUG = $previousDebugValue
                }
                else {
                    Remove-Item Env:DEBUG -ErrorAction SilentlyContinue
                }
            }

            if ($gradleExitCode -ne 0) {
                throw "Android Gradle tests failed with exit code $gradleExitCode"
            }
        }
    }
    else {
        Write-Check "Android assembleDebug and unit tests" $true "skipped by $androidScopeReason"
    }
}

$androidDeviceActionsRequested = $runAndroidChecks -and ($InstallAndroid -or $StartAndroid -or $AndroidStatus -or $AndroidPullLog -or $AndroidClearLog)
if (($WindowsOnly -or $LinuxOnly) -and ($InstallAndroid -or $StartAndroid -or $AndroidStatus -or $AndroidPullLog -or $AndroidClearLog)) {
    $scopeSwitch = if ($WindowsOnly) { "-WindowsOnly" } else { "-LinuxOnly" }
    Write-Check "Android device actions" $true "skipped by $scopeSwitch"
}

if ($androidDeviceActionsRequested) {
    Write-Host ""
    Write-Host "Android device actions" -ForegroundColor Cyan
    if ($null -eq $adb) {
        Write-Check "adb device actions" $false "adb not found"
    }
    else {
        $readyDevices = @(Get-ReadyAdbDevices "adb connected device")
        if ($readyDevices.Count -gt 0 -and $InstallAndroid) {
            if (-not (Test-Path $androidApk)) {
                Write-Check "adb install" $false "APK missing"
            }
            else {
                foreach ($device in $readyDevices) {
                    Invoke-OptionalCommand "adb install RemoteDesk Android APK on $($device.Serial)" {
                        $result = Invoke-AdbRaw @("-s", $device.Serial, "install", "-r", $androidApk)
                        $result.Lines | Write-Host
                        if ($result.ExitCode -ne 0) {
                            throw "adb install failed with exit code $($result.ExitCode)"
                        }
                    }
                }
            }
        }

        if ($readyDevices.Count -gt 0 -and $AndroidClearLog) {
            Clear-AndroidDiagnosticLog $readyDevices
        }

        if ($readyDevices.Count -gt 0 -and $StartAndroid) {
            foreach ($device in $readyDevices) {
                Invoke-OptionalCommand "adb start RemoteDesk Android on $($device.Serial)" {
                    $result = Invoke-AdbRaw @("-s", $device.Serial, "shell", "am", "start", "-n", "com.remotedesk.agent/.MainActivity")
                    $result.Lines | Write-Host
                    if ($result.ExitCode -ne 0) {
                        throw "adb start failed with exit code $($result.ExitCode)"
                    }
                }
            }
        }

        if ($readyDevices.Count -gt 0 -and ($AndroidStatus -or $StartAndroid)) {
            Write-AndroidStatus $readyDevices
        }

        if ($readyDevices.Count -gt 0 -and $AndroidPullLog) {
            Save-AndroidCurrentLog $readyDevices
        }
    }
}

$androidChecksMayHaveStartedAdb =
    ($RunTests -and $runAndroidChecks) -or
    $androidDeviceActionsRequested
if ($androidChecksMayHaveStartedAdb -and
    $null -ne $adb -and
    -not $KeepAdbServer) {
    Write-Host ""
    if (@($script:InitialAdbProcessIds).Count -eq 0) {
        Write-Host "Android helper cleanup" -ForegroundColor Cyan
        Invoke-OptionalCommand "adb server cleanup" {
            $result = Invoke-AdbRaw @("kill-server")
            if ($result.ExitCode -ne 0) {
                throw "adb kill-server failed with exit code $($result.ExitCode): $($result.Lines -join ' ')"
            }
        }
    }
    else {
        Write-Host "Preserving pre-existing adb server; it was not started by this check." -ForegroundColor Yellow
    }
}

if (($RunTests -and $runAndroidChecks) -or $androidDeviceActionsRequested) {
    Wait-HelperProcessesExit -SkipAdb:$KeepAdbServer
    Write-Host ""
    Write-Host "Helper process cleanup" -ForegroundColor Cyan
    Write-HelperProcessChecks -SkipAdb:$KeepAdbServer
}

if ($WriteAcceptanceReport) {
    Save-AcceptanceReport `
        -RequestedPath $AcceptanceReportPath `
        -Root $root `
        -WindowsOnly:$WindowsOnly `
        -LinuxOnly:$LinuxOnly `
        -SkipAndroid:$SkipAndroid `
        -Target $Target `
        -Port $Port `
        -DiscoveryPort $DiscoveryPort `
        -LocalIpSummary $localIp
}

$failedChecks = @(
    $script:CheckResults |
        Where-Object { -not $_.Ok })
if ($failedChecks.Count -gt 0) {
    Write-Host ""
    Write-Host "Required checks failed: $($failedChecks.Count)" -ForegroundColor Red
    foreach ($failedCheck in $failedChecks) {
        Write-Host " - $($failedCheck.Name): $($failedCheck.Detail)" -ForegroundColor Red
    }

    exit 1
}

Write-Host ""
Write-Host "Done. If TCP is OK but UDP is not, connect by IP still works; allow UDP $DiscoveryPort in the local firewall to improve discovery." -ForegroundColor Cyan
