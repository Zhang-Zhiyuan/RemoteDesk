param(
    [ValidateRange(1, 65535)]
    [int]$Port = 56565,

    [ValidateRange(5, 120)]
    [int]$StartupTimeoutSeconds = 35,

    [ValidateRange(30, 300)]
    [int]$UpdateStartTimeoutSeconds = 120
)

$ErrorActionPreference = "Stop"
$logPath = Join-Path $env:TEMP "RemoteDesk.UpdateWatchdog.log"

function Write-WatchdogLog {
    param([string]$Message)

    $line = "[{0:O}] {1}" -f [DateTimeOffset]::Now, $Message
    Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
}

function Test-RemoteDeskHandshake {
    param([int]$TcpPort)

    $client = [Net.Sockets.TcpClient]::new()
    try {
        $connect = $client.BeginConnect(
            [Net.IPAddress]::Loopback,
            $TcpPort,
            $null,
            $null)
        if (-not $connect.AsyncWaitHandle.WaitOne(1000)) {
            return $false
        }

        $client.EndConnect($connect)
        $stream = $client.GetStream()
        $stream.ReadTimeout = 1000
        $magic = [byte[]]::new(4)
        $offset = 0
        while ($offset -lt $magic.Length) {
            $read = $stream.Read(
                $magic,
                $offset,
                $magic.Length - $offset)
            if ($read -le 0) {
                return $false
            }

            $offset += $read
        }

        return [Text.Encoding]::ASCII.GetString($magic) -eq "RDK1"
    }
    catch {
        return $false
    }
    finally {
        $client.Dispose()
    }
}

$current = Get-CimInstance Win32_Process -Filter "Name='RemoteDesk.exe'" |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_.ExecutablePath) } |
    Sort-Object CreationDate |
    Select-Object -First 1
if ($null -eq $current) {
    Write-WatchdogLog "No running RemoteDesk.exe with a resolvable path."
    exit 2
}

$targetPath = [IO.Path]::GetFullPath([string]$current.ExecutablePath)
$oldProcessId = [int]$current.ProcessId
$backupPath = Join-Path $env:TEMP (
    "RemoteDesk.UpdateWatchdog.{0}.exe" -f [Guid]::NewGuid().ToString("N"))
Copy-Item -LiteralPath $targetPath -Destination $backupPath -Force
Write-WatchdogLog (
    "Armed for PID={0}; target={1}; backup={2}" -f
        $oldProcessId,
        $targetPath,
        $backupPath)

$updateDeadline = [DateTimeOffset]::Now.AddSeconds(
    $UpdateStartTimeoutSeconds)
while ([DateTimeOffset]::Now -lt $updateDeadline) {
    if ($null -eq (Get-Process -Id $oldProcessId -ErrorAction SilentlyContinue)) {
        break
    }

    Start-Sleep -Milliseconds 250
}

if ($null -ne (Get-Process -Id $oldProcessId -ErrorAction SilentlyContinue)) {
    Write-WatchdogLog "Update did not start before the arm timeout; disarming."
    Remove-Item -LiteralPath $backupPath -Force -ErrorAction SilentlyContinue
    exit 0
}

$startupDeadline = [DateTimeOffset]::Now.AddSeconds(
    $StartupTimeoutSeconds)
while ([DateTimeOffset]::Now -lt $startupDeadline) {
    if (Test-RemoteDeskHandshake -TcpPort $Port) {
        Write-WatchdogLog "New RemoteDesk handshake is healthy; disarming."
        Remove-Item -LiteralPath $backupPath -Force -ErrorAction SilentlyContinue
        exit 0
    }

    Start-Sleep -Milliseconds 500
}

Write-WatchdogLog "New RemoteDesk did not become healthy; restoring backup."
Get-CimInstance Win32_Process -Filter "Name='RemoteDesk.exe'" |
    Where-Object {
        [string]::Equals(
            [IO.Path]::GetFullPath([string]$_.ExecutablePath),
            $targetPath,
            [StringComparison]::OrdinalIgnoreCase)
    } |
    ForEach-Object {
        Stop-Process -Id ([int]$_.ProcessId) -Force -ErrorAction SilentlyContinue
    }

$restored = $false
for ($attempt = 0; $attempt -lt 20; $attempt++) {
    try {
        Copy-Item -LiteralPath $backupPath -Destination $targetPath -Force
        $restored = $true
        break
    }
    catch {
        Start-Sleep -Milliseconds 500
    }
}

if (-not $restored) {
    Write-WatchdogLog "Rollback copy failed; backup remains at $backupPath."
    exit 3
}

Start-Process -FilePath $targetPath -ArgumentList "--tray"
Write-WatchdogLog "Rollback executable restored and started."
Remove-Item -LiteralPath $backupPath -Force -ErrorAction SilentlyContinue
exit 1
