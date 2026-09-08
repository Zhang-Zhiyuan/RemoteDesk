#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$VaultDirectory = (Join-Path $env:LOCALAPPDATA "RemoteDesk\Signing")
)

$ErrorActionPreference = "Stop"
if (-not $IsWindows) {
    throw "This local signing vault uses Windows CurrentUser DPAPI."
}
$vault = [IO.Path]::GetFullPath($VaultDirectory)
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if ($vault.Equals($repoRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $vault.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Keep the signing vault outside the repository and build artifacts."
}
$keyStore = Join-Path $vault "remotedesk-release.p12"
$passwordFile = Join-Path $vault "password.dpapi.xml"
if ((Test-Path -LiteralPath $keyStore) -or (Test-Path -LiteralPath $passwordFile)) {
    if (-not (Test-Path -LiteralPath $keyStore -PathType Leaf) -or
        -not (Test-Path -LiteralPath $passwordFile -PathType Leaf)) {
        throw "Signing vault is incomplete. Existing key material was not changed."
    }
    Write-Output "Existing signing vault preserved: $vault"
    return
}

$keytool = (Get-Command keytool -ErrorAction Stop).Source
if (Test-Path -LiteralPath $vault) {
    if ((Get-Item -LiteralPath $vault).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "Signing vault must not be a reparse point."
    }
} else {
    [void](New-Item -ItemType Directory -Path $vault)
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent().User
$acl = [Security.AccessControl.DirectorySecurity]::new()
$acl.SetOwner($identity)
$acl.SetAccessRuleProtection($true, $false)
foreach ($sid in @($identity, [Security.Principal.SecurityIdentifier]::new("S-1-5-18"))) {
    $rule = [Security.AccessControl.FileSystemAccessRule]::new(
        $sid, "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")
    $acl.AddAccessRule($rule)
}
Set-Acl -LiteralPath $vault -AclObject $acl

$password = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(36))
$securePassword = ConvertTo-SecureString $password -AsPlainText -Force
$credential = [Management.Automation.PSCredential]::new("remotedesk-release", $securePassword)
$credential | Export-Clixml -LiteralPath $passwordFile -NoClobber
$previous = [Environment]::GetEnvironmentVariable("REMOTEDESK_KEYGEN_PASSWORD", "Process")
try {
    $env:REMOTEDESK_KEYGEN_PASSWORD = $password
    & $keytool -genkeypair -noprompt -storetype PKCS12 -keystore $keyStore `
        -alias remotedesk-release -keyalg RSA -keysize 4096 -validity 10950 `
        -dname "CN=RemoteDesk Release" `
        -storepass:env REMOTEDESK_KEYGEN_PASSWORD -keypass:env REMOTEDESK_KEYGEN_PASSWORD
    if ($LASTEXITCODE -ne 0) {
        throw "Release key generation failed. Vault files were retained for recovery."
    }
} finally {
    [Environment]::SetEnvironmentVariable("REMOTEDESK_KEYGEN_PASSWORD", $previous, "Process")
    $password = $null
}
Write-Output "Created dedicated release key: $keyStore"
Write-Output "Password is protected by this Windows account's DPAPI; keep a secure off-machine backup."
