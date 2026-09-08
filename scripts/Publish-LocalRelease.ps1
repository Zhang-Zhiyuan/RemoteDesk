#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$SigningVault = (Join-Path $env:LOCALAPPDATA "RemoteDesk\Signing"),
    [string]$LinuxDistro = "Ubuntu-24.04",
    [string]$ReleaseName = ""
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrEmpty($ReleaseName)) {
    [xml]$project = Get-Content -LiteralPath (Join-Path $PSScriptRoot "..\src\RemoteDesk\RemoteDesk.csproj") -Raw
    $ReleaseName = "release-" + [string]$project.Project.PropertyGroup.Version
}
if (Test-Path -LiteralPath (Join-Path $PSScriptRoot "..\src\RemoteDesk.Android\release-signing.properties")) {
    throw "An explicit release-signing.properties would override this vault; use Publish-RemoteDesk.ps1 with that configuration."
}
$keyStore = Join-Path $SigningVault "remotedesk-release.p12"
$passwordFile = Join-Path $SigningVault "password.dpapi.xml"
if (-not (Test-Path -LiteralPath $keyStore -PathType Leaf) -or
    -not (Test-Path -LiteralPath $passwordFile -PathType Leaf)) {
    throw "Initialize a release signing vault first; no keys are regenerated during publishing."
}
$credential = Import-Clixml -LiteralPath $passwordFile
if ($credential -isnot [Management.Automation.PSCredential]) {
    throw "Invalid local signing credential."
}
$values = @{
    REMOTEDESK_ANDROID_STORE_FILE = [IO.Path]::GetFullPath($keyStore)
    REMOTEDESK_ANDROID_STORE_PASSWORD = $credential.GetNetworkCredential().Password
    REMOTEDESK_ANDROID_KEY_ALIAS = $credential.UserName
    REMOTEDESK_ANDROID_KEY_PASSWORD = $credential.GetNetworkCredential().Password
}
$previous = @{}
try {
    foreach ($name in $values.Keys) {
        $previous[$name] = [Environment]::GetEnvironmentVariable($name, "Process")
        [Environment]::SetEnvironmentVariable($name, $values[$name], "Process")
    }
    # No dirty-source or test-skipping bypasses in the local final-release path.
    & (Join-Path $PSScriptRoot "Publish-RemoteDesk.ps1") -AndroidRelease -LinuxDistro $LinuxDistro -ReleaseName $ReleaseName
} finally {
    foreach ($name in $previous.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previous[$name], "Process")
    }
    $values.Clear()
}
