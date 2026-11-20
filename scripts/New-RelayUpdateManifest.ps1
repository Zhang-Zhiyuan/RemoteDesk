#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$ReleaseManifest = (Join-Path $PSScriptRoot "../artifacts/RemoteDesk-release-manifest.json"),
    [string]$SigningVault = (Join-Path $env:LOCALAPPDATA "RemoteDesk/Signing"),
    [string]$OutputFile = (Join-Path $PSScriptRoot "../artifacts/RemoteDesk-fleet-update.json")
)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$release = Get-Content -LiteralPath $ReleaseManifest -Raw | ConvertFrom-Json
if ($release.sourceDirty -ne $false -or $release.internalCandidate -ne $false -or
    $release.androidReleaseRequested -ne $true -or $release.buildStamp -cnotmatch '^[0-9]{14}$') {
    throw "Fleet updates require a complete clean release."
}
[xml]$project = Get-Content -LiteralPath (Join-Path $root "src/RemoteDesk/RemoteDesk.csproj") -Raw
$version = [string]$project.Project.PropertyGroup.Version
if ($version -cnotmatch '^(0|[1-9][0-9]{0,4})(\.(0|[1-9][0-9]{0,4})){2}$' -or
    ($release.version -and $release.version -ne $version)) { throw "Release version mismatch." }
$names = [ordered]@{
    'win-x64' = 'RemoteDesk.exe'
    'android' = 'RemoteDesk-android-release.apk'
    'linux-deb-amd64' = "remotedesk-linux-host_${version}_amd64.deb"
    'linux-amd64' = 'RemoteDesk-linux-host.tar.gz'
    'linux-python' = 'RemoteDesk-linux-system-python.tar.gz'
}
$certPath = Join-Path $root "src/RemoteDesk.Android/app/src/main/assets/fleet-publisher.cer"
$credential = Import-Clixml -LiteralPath (Join-Path $SigningVault "password.dpapi.xml")
$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
    (Join-Path $SigningVault "remotedesk-release.p12"), $credential.Password,
    [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
$rsa = $null
try {
    if ([Convert]::ToBase64String($certificate.RawData) -cne [Convert]::ToBase64String([IO.File]::ReadAllBytes($certPath))) {
        throw "The signing vault does not match the pinned RemoteDesk publisher."
    }
    $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
    $tickets = [ordered]@{}
    foreach ($platform in $names.Keys) {
        $entries = @($release.artifacts | Where-Object name -CEQ $names[$platform])
        if ($entries.Count -ne 1) { throw "Missing or ambiguous release artifact for $platform." }
        $entry = $entries[0]
        $path = (Resolve-Path -LiteralPath (Join-Path $root $entry.path)).Path
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        $length = (Get-Item -LiteralPath $path).Length
        if ($length -ne $entry.length -or $hash -cne $entry.sha256 -or $length -lt 1 -or $length -gt 256MB) {
            throw "Release artifact failed integrity verification for $platform."
        }
        if ($platform -eq "win-x64") {
            $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($path)
            if ($info.ProductVersion -notlike "$version+remotedesk.$($release.buildStamp)*") { throw "Executable version mismatch." }
        }
        $fields = @("RemoteDesk/fleet/v1", $version, $release.buildStamp, $platform, [string]$length, $hash)
        $signature = $rsa.SignData([Text.Encoding]::ASCII.GetBytes(($fields -join [char]10)),
            [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $tickets[$platform] = [ordered]@{version=$version; build=$release.buildStamp; platform=$platform
            size=$length; sha256=$hash; signature=[Convert]::ToBase64String($signature)}
    }
    $outputPath = [IO.Path]::GetFullPath($OutputFile)
    $tmpPath = "$outputPath.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText($tmpPath, (@{tickets=$tickets} | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
        [IO.File]::Move($tmpPath, $outputPath, $true)
    } finally { [IO.File]::Delete($tmpPath) }
    Write-Host "Signed RemoteDesk $version for $($tickets.Count) package types."
} finally {
    if ($null -ne $rsa) { $rsa.Dispose() }
    $certificate.Dispose()
}
