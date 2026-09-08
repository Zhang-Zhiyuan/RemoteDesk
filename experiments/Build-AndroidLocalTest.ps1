#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../src/RemoteDesk.Android'))
$vault = Join-Path $env:LOCALAPPDATA 'RemoteDesk/Signing'
if (Test-Path -LiteralPath (Join-Path $project 'release-signing.properties')) {
    throw 'Explicit signing properties would override the local test signing vault.'
}
$credential = Import-Clixml -LiteralPath (Join-Path $vault 'password.dpapi.xml')
$values = @{
    REMOTEDESK_ANDROID_STORE_FILE = Join-Path $vault 'remotedesk-release.p12'
    REMOTEDESK_ANDROID_STORE_PASSWORD = $credential.GetNetworkCredential().Password
    REMOTEDESK_ANDROID_KEY_ALIAS = $credential.UserName
    REMOTEDESK_ANDROID_KEY_PASSWORD = $credential.GetNetworkCredential().Password
}
$previous = @{}
try {
    foreach ($name in $values.Keys) {
        $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $values[$name], 'Process')
    }
    & (Join-Path $project 'gradlew.bat') --no-daemon -p $project assembleRelease testReleaseUnitTest lintRelease
    if ($LASTEXITCODE) { throw 'Signed Android test package did not pass its checks.' }
} finally {
    foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    $values.Clear()
    $credential = $null
}
