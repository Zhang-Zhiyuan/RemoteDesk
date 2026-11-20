#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^release-\d+\.\d+\.\d+(?:-r\d+|-final)?$')][string]$ReleaseName,
    [string]$LinuxDistro = "Ubuntu-24.04",
    [string]$SigningVault = (Join-Path $env:LOCALAPPDATA "RemoteDesk/Signing")
)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$build = Join-Path $root "artifacts/$ReleaseName"
$manifestPath = Join-Path $build "RemoteDesk-release-manifest.json"
$release = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
[xml]$project = Get-Content -LiteralPath (Join-Path $root "src/RemoteDesk/RemoteDesk.csproj") -Raw
$version = [string]$project.Project.PropertyGroup.Version
if ($release.sourceDirty -ne $false -or $release.internalCandidate -ne $false -or
    $release.sourceRevision -cne (& git -C $root rev-parse HEAD) -or (& git -C $root status --porcelain)) {
    throw "Complete the distribution from the same clean source revision that was tested."
}
$portable = Join-Path $build "RemoteDesk-linux-system-python.tar.gz"
$scratch = Join-Path $build (".fleet-release-" + [Guid]::NewGuid().ToString("N"))
[void][IO.Directory]::CreateDirectory($scratch)
try {
    & (Join-Path $PSScriptRoot "Build-LinuxSystemPackage.ps1") -OutputDirectory $scratch -LinuxDistro $LinuxDistro
    [IO.File]::Move((Join-Path $scratch "RemoteDesk-linux-system-python.tar.gz"), $portable, $true)
    $item = Get-Item -LiteralPath $portable
    $release.artifacts = @($release.artifacts | Where-Object name -NE $item.Name) + @([pscustomobject]@{
        name=$item.Name; path=[IO.Path]::GetRelativePath($root, $item.FullName); length=$item.Length
        sha256=(Get-FileHash -LiteralPath $item.FullName).Hash.ToLowerInvariant(); lastWriteTimeUtc=$item.LastWriteTimeUtc.ToString('o')})
    $release | Add-Member -NotePropertyName version -NotePropertyValue $version -Force
    $stagedManifest = Join-Path $scratch "manifest.json"
    $release | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $stagedManifest -Encoding utf8NoBOM
    $signed = Join-Path $scratch "RemoteDesk-fleet-update.json"
    & (Join-Path $PSScriptRoot "New-RelayUpdateManifest.ps1") -ReleaseManifest $stagedManifest -SigningVault $SigningVault -OutputFile $signed
    $map = [ordered]@{'RemoteDesk-android-release.apk'='RemoteDesk-Android.apk'
        'RemoteDesk-linux-host.tar.gz'='RemoteDesk-linux-amd64.tar.gz'; 'RemoteDesk-linux-system-python.tar.gz'='RemoteDesk-linux-system-python.tar.gz'}
    $map["remotedesk-linux-host_" + $version + "_amd64.deb"] = 'RemoteDesk-linux-amd64.deb'
    foreach ($name in 'RemoteDesk.exe','DOTNET-RUNTIME-LICENSE.TXT','DOTNET-RUNTIME-THIRD-PARTY-NOTICES.TXT','THIRD-PARTY-NOTICES.md','Install-RemoteDeskFfmpeg.ps1') {
        $map["RemoteDesk-win-x64/$name"] = "windows-x64/$name"
    }
    $distribution = foreach ($key in $map.Keys) {
        $source = Join-Path $build $key
        $dest = Join-Path $root ("release/" + $map[$key])
        [IO.File]::Copy($source, $dest, $true)
        $hash = (Get-FileHash -LiteralPath $dest).Hash
        if ($hash -cne (Get-FileHash -LiteralPath $source).Hash) { throw "Distribution copy failed for $key." }
        [pscustomobject]@{path="release/" + $map[$key]; length=(Get-Item -LiteralPath $dest).Length; sha256=$hash.ToLowerInvariant()}
    }
    $release | Add-Member -NotePropertyName distribution -NotePropertyValue @($distribution) -Force
    $release | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
    [IO.File]::Copy($manifestPath, (Join-Path $root "artifacts/RemoteDesk-release-manifest.json"), $true)
    [IO.File]::Copy($signed, (Join-Path $build "RemoteDesk-fleet-update.json"), $true)
    [IO.File]::Copy($signed, (Join-Path $root "release/RemoteDesk-fleet-update.json"), $true)
} finally {
    $tmpDir = Get-Item -LiteralPath $scratch
    if ($tmpDir.Parent.FullName -ne (Resolve-Path -LiteralPath $build).Path -or
        $tmpDir.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Unexpected staging path." }
    Remove-Item -LiteralPath $tmpDir.FullName -Force -Recurse
}
