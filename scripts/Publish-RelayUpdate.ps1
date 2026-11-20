#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^(?!-)[A-Za-z0-9_.:@\[\]-]+$')][string]$Server,
    [string]$ReleaseDirectory = (Join-Path $PSScriptRoot "../release")
)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path -LiteralPath $ReleaseDirectory).Path
$manifest = [IO.File]::ReadAllBytes((Join-Path $root "RemoteDesk-fleet-update.json"))
if ($manifest.Length -gt 16384) { throw "Invalid update manifest." }
$release = [Text.Encoding]::UTF8.GetString($manifest) | ConvertFrom-Json -AsHashtable
# Ordinal ordering is the binary upload protocol. SSH supplies authenticated transport.
$files = [ordered]@{'android'='RemoteDesk-Android.apk'; 'linux-amd64'='RemoteDesk-linux-amd64.tar.gz'
    'linux-deb-amd64'='RemoteDesk-linux-amd64.deb'; 'linux-python'='RemoteDesk-linux-system-python.tar.gz'
    'win-x64'='windows-x64/RemoteDesk.exe'}
$cert = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
    (Join-Path $PSScriptRoot '../src/RemoteDesk.Android/app/src/main/assets/fleet-publisher.cer'))
$rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($cert)
try {
    foreach ($platform in $files.Keys) {
        $ticket = $release.tickets[$platform]
        $path = Join-Path $root $files[$platform]
        $message = @("RemoteDesk/fleet/v1", $ticket.version, $ticket.build, $platform, [string]$ticket.size, $ticket.sha256) -join [char]10
        if ($ticket.platform -cne $platform -or !$rsa.VerifyData([Text.Encoding]::ASCII.GetBytes($message),
            [Convert]::FromBase64String($ticket.signature), [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pkcs1) -or
            (Get-Item -LiteralPath $path).Length -ne $ticket.size -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $ticket.sha256) {
            throw "Signature or file verification failed for $platform."
        }
    }
} finally { $rsa.Dispose(); $cert.Dispose() }
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = (Get-Command ssh -CommandType Application | Select-Object -First 1).Source
$start.UseShellExecute = $false
$start.RedirectStandardInput = $true
$start.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
foreach ($arg in @('-T','-o','StrictHostKeyChecking=yes','-o','NumberOfPasswordPrompts=1','-o','ConnectTimeout=12','--',
    $Server, '/usr/bin/python3 /usr/local/lib/remotedesk-relay/remotedesk_relay_server.py --publish-updates')) {
    $start.ArgumentList.Add($arg)
}
$process = [Diagnostics.Process]::Start($start)
try {
    $pipe = $process.StandardInput.BaseStream
    $length = [BitConverter]::GetBytes([Net.IPAddress]::HostToNetworkOrder([int]$manifest.Length))
    $pipe.Write($length)
    $pipe.Write($manifest)
    foreach ($name in $files.Values) {
        $stream = [IO.File]::OpenRead((Join-Path $root $name))
        try { $stream.CopyTo($pipe) } finally { $stream.Dispose() }
    }
    $process.StandardInput.Close()
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "Relay publication failed (SSH exit $($process.ExitCode))." }
} finally {
    try { $process.StandardInput.Close() } catch [IO.IOException] { }
    $process.Dispose()
}
