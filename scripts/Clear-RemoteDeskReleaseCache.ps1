[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ReleaseName,
    [switch]$CompletedRollout
)

$ErrorActionPreference = "Stop"
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifacts = Get-Item -LiteralPath (Join-Path $repo "artifacts") -Force
$reparse = [IO.FileAttributes]::ReparsePoint
if (-not $artifacts.PSIsContainer -or ($artifacts.Attributes -band $reparse)) {
    throw "Expected a local artifacts directory."
}

$versionPattern = '[0-9]+\.[0-9]+\.[0-9]+'
$releasePattern = "^release-(?<version>$versionPattern)(?:-r[0-9]+|-final)?$"
$publishedManifest = Join-Path $artifacts.FullName "RemoteDesk-release-manifest.json"
$publishedDirs = @()
if (Test-Path -LiteralPath $publishedManifest -PathType Leaf) {
    $published = Get-Content -LiteralPath $publishedManifest -Raw | ConvertFrom-Json
    $publishedDirs = @($published.artifacts | ForEach-Object {
        if ($_.path -match '^artifacts[\\/]([^\\/]+)[\\/]') { $Matches[1] }
    } | Sort-Object -Unique)
}
if (-not $ReleaseName) {
    $active = @($publishedDirs | Where-Object { $_ -match $releasePattern })
    if ($active.Count -ne 1) { throw "Cannot identify the published release directory." }
    $ReleaseName = $active[0]
}
if ($ReleaseName -notmatch $releasePattern) { throw "Expected a versioned release name." }
$currentVersion = [version]$Matches.version
$current = Get-Item -LiteralPath (Join-Path $artifacts.FullName $ReleaseName) -Force
if (-not $current.PSIsContainer -or ($current.Attributes -band $reparse)) { throw "Invalid release directory." }
$manifest = Get-Content -LiteralPath (Join-Path $current.FullName "RemoteDesk-release-manifest.json") -Raw | ConvertFrom-Json
if ($manifest.sourceDirty -ne $false -or $manifest.internalCandidate -ne $false -or
    $manifest.androidReleaseRequested -ne $true -or $manifest.buildStamp -notmatch '^[0-9]{14}$' -or
    $manifest.scope -ne "Windows host/viewer; Android; Linux host/viewer package") {
    throw "Cleanup requires a verified, full release."
}
foreach ($name in "RemoteDesk-win-x64/RemoteDesk.exe", "RemoteDesk-android-release.apk", "remotedesk-linux-host_${currentVersion}_amd64.deb") {
    $file = Get-Item -LiteralPath (Join-Path $current.FullName $name) -Force
    $entry = @($manifest.artifacts | Where-Object name -eq $file.Name)
    if ($file.PSIsContainer -or ($file.Attributes -band $reparse) -or $entry.Count -ne 1 -or
        $file.Length -le 0 -or $file.Length -ne $entry[0].length -or
        (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne $entry[0].sha256) {
        throw "Current release package verification failed for $name."
    }
}

$command = $PSCmdlet
$totals = @{ Files = 0; Bytes = [long]0; Pending = 0 }
function Remove-CacheFile([IO.FileInfo]$File) {
    if (($File.Attributes -band $reparse) -or -not $command.ShouldProcess($File.FullName, "Remove obsolete RemoteDesk file")) { return }
    $size = $File.Length
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            Remove-Item -LiteralPath $File.FullName -ErrorAction Stop
            $totals.Files++
            $totals.Bytes += $size
            return
        }
        catch [IO.FileNotFoundException], [Management.Automation.ItemNotFoundException] { return }
        catch [IO.IOException], [UnauthorizedAccessException] {
            if ($attempt -lt 3) { Start-Sleep -Milliseconds 200; continue }
            $totals.Pending++
            Write-Warning "Will retry on the next cleanup: $($File.FullName) ($($_.Exception.Message))"
        }
    }
}
function Remove-PackageFiles([IO.DirectoryInfo]$Directory, [string[]]$Names) {
    if ($Directory.Attributes -band $reparse) { return }
    foreach ($file in (Get-ChildItem -LiteralPath $Directory.FullName -File -Force)) {
        if ($file.Name -in $Names) { Remove-CacheFile $file }
    }
}
function Remove-EmptyDirectory([IO.DirectoryInfo]$Directory) {
    if (-not ($Directory.Attributes -band $reparse) -and -not @(Get-ChildItem -LiteralPath $Directory.FullName -Force).Count -and
        $command.ShouldProcess($Directory.FullName, "Remove empty build directory")) {
        [IO.Directory]::Delete($Directory.FullName, $false)
    }
}

foreach ($dir in (Get-ChildItem -LiteralPath $artifacts.FullName -Directory -Force)) {
    if (($dir.Attributes -band $reparse) -or $dir.Name -in $publishedDirs -or $dir.Name -eq $ReleaseName) { continue }
    if ($dir.Name -match $releasePattern) {
        $version = [version]$Matches.version
        $oldManifest = Join-Path $dir.FullName "RemoteDesk-release-manifest.json"
        if ($version -gt $currentVersion -or -not (Test-Path -LiteralPath $oldManifest -PathType Leaf) -or
            @(Get-ChildItem -LiteralPath $dir.FullName -Directory -Force -Filter ".publish-backup-*").Count) { continue }
        if ($version -eq $currentVersion) {
            $stamp = (Get-Content -LiteralPath $oldManifest -Raw | ConvertFrom-Json).buildStamp
            if ($stamp -notmatch '^[0-9]{14}$' -or $stamp -ge $manifest.buildStamp) { continue }
        }
        Remove-PackageFiles $dir @("RemoteDesk-win-x64.zip", "RemoteDesk-android-release.apk",
            "RemoteDesk-linux-host.zip", "RemoteDesk-linux-host.tar.gz", "RemoteDesk-linux-system-python.tar.gz",
            "remotedesk-linux-host_${version}_amd64.deb")
        $win = Get-Item -LiteralPath (Join-Path $dir.FullName "RemoteDesk-win-x64") -Force -ErrorAction SilentlyContinue
        if ($win -is [IO.DirectoryInfo] -and -not ($win.Attributes -band $reparse)) {
            Remove-PackageFiles $win @("RemoteDesk.exe", "Install-RemoteDeskFfmpeg.ps1", "THIRD-PARTY-NOTICES.md",
                "DOTNET-RUNTIME-LICENSE.TXT", "DOTNET-RUNTIME-THIRD-PARTY-NOTICES.TXT")
            Remove-EmptyDirectory $win
        }
    }
    elseif ($dir.Name -match "^rollout-(?<version>$versionPattern)$") {
        $version = [version]$Matches.version
        if ($version -gt $currentVersion -or ($version -eq $currentVersion -and -not $CompletedRollout)) { continue }
        foreach ($zip in (Get-ChildItem -LiteralPath $dir.FullName -File -Filter "RemoteDesk-QA-*.zip")) { Remove-CacheFile $zip }
        foreach ($probe in (Get-ChildItem -LiteralPath $dir.FullName -Directory -Filter "physical-probe*")) {
            if ($probe.Name -notmatch '^physical-probe(?:-r[0-9]+)?$' -or ($probe.Attributes -band $reparse)) { continue }
            $marker = Get-Item -LiteralPath (Join-Path $probe.FullName "RemoteDesk.InteropProbe.deps.json") -Force -ErrorAction SilentlyContinue
            if ($marker -isnot [IO.FileInfo] -or ($marker.Attributes -band $reparse)) { continue }
            $contents = @(Get-ChildItem -LiteralPath $probe.FullName -Recurse -Force)
            if ($contents.Where({ $_.Attributes -band $reparse }).Count) { continue }
            $pendingBefore = $totals.Pending
            foreach ($file in $contents.Where({ -not $_.PSIsContainer })) {
                if ($file.FullName -ne $marker.FullName -and $file.Name -match '\.(?:dll|pdb|exe|deps\.json|runtimeconfig\.json)$') {
                    Remove-CacheFile $file
                }
            }
            # Keep the identifying manifest until any locked binaries can be removed.
            if ($totals.Pending -eq $pendingBefore) { Remove-CacheFile $marker }
            foreach ($subdir in ($contents.Where({ $_.PSIsContainer }) | Sort-Object { $_.FullName.Length } -Descending)) {
                Remove-EmptyDirectory $subdir
            }
            Remove-EmptyDirectory $probe
        }
    }
}
[pscustomobject]@{ Release = $ReleaseName; RemovedFiles = $totals.Files; RemovedBytes = $totals.Bytes; PendingFiles = $totals.Pending }
