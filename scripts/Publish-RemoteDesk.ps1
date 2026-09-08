[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$SkipAndroid,
    [switch]$WindowsOnly,
    [switch]$LinuxOnly,
    [switch]$AndroidRelease,
    [switch]$AllowDirtySource,
    [string]$CandidateName = "",
    [string]$ReleaseName = "",
    [string]$LinuxDistro = "Ubuntu-24.04",
    [string]$GradlePath = "",
    [string]$WindowsSigningCertificateThumbprint = "",
    [string]$WindowsTimestampServer = ""
)

$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "RemoteDesk-ReleaseCommon.ps1")

$LinuxDistro = Assert-RemoteDeskLinuxDistro -Distro $LinuxDistro

if ($WindowsOnly -and $AndroidRelease) {
    throw "-WindowsOnly cannot be combined with -AndroidRelease."
}

if ($WindowsOnly -and $LinuxOnly) {
    throw "-WindowsOnly cannot be combined with -LinuxOnly."
}

if ($LinuxOnly -and $AndroidRelease) {
    throw "-LinuxOnly cannot be combined with -AndroidRelease."
}

if ($SkipAndroid -and $AndroidRelease) {
    throw "-SkipAndroid cannot be combined with -AndroidRelease."
}

if ($WindowsOnly) {
    $SkipAndroid = $true
}

if ($LinuxOnly -and
    -not [string]::IsNullOrWhiteSpace(
        $WindowsSigningCertificateThumbprint)) {
    throw "-WindowsSigningCertificateThumbprint cannot be used with -LinuxOnly."
}
if ([string]::IsNullOrWhiteSpace(
        $WindowsSigningCertificateThumbprint) -xor
    [string]::IsNullOrWhiteSpace($WindowsTimestampServer)) {
    throw (
        "-WindowsSigningCertificateThumbprint and -WindowsTimestampServer " +
        "must be supplied together.")
}

function Write-Step {
    param([string]$Message)

    Write-Host ""
    Write-Host $Message -ForegroundColor Cyan
}

function Resolve-RepoRoot {
    $scriptPath = $PSCommandPath
    if ([string]::IsNullOrWhiteSpace($scriptPath)) {
        return (Get-Location).Path
    }

    return (Resolve-Path (Join-Path (Split-Path -Parent $scriptPath) "..")).Path
}

function Get-SourceState {
    param([string]$Root)

    $git = Get-Command "git" -ErrorAction SilentlyContinue
    if ($null -eq $git) {
        throw "Cannot record release source revision because git is unavailable."
    }

    try {
        $revisionOutput = @(
            & $git.Source -C $Root rev-parse --verify HEAD 2>$null)
        $revisionExitCode = $LASTEXITCODE
    }
    catch {
        throw "Cannot read release source revision: $($_.Exception.Message)"
    }

    $revision = ([string]($revisionOutput | Select-Object -First 1)).Trim()
    if ($revisionExitCode -ne 0 -or
        $revision -notmatch "^[0-9a-fA-F]{40}$") {
        throw "Cannot record a valid git source revision for release."
    }

    try {
        $statusOutput = @(
            & $git.Source `
                -C $Root `
                status --porcelain=v1 --untracked-files=normal `
                -- . ":(exclude)artifacts/**" ":(exclude)release/**" 2>$null)
        $statusExitCode = $LASTEXITCODE
    }
    catch {
        throw "Cannot inspect release source status: $($_.Exception.Message)"
    }

    if ($statusExitCode -ne 0) {
        throw "Cannot determine whether the release source tree is dirty."
    }

    $fingerprint = Get-RemoteDeskSourceFingerprint -Root $Root
    return [pscustomobject]@{
        Revision = $revision.ToLowerInvariant()
        Dirty = $statusOutput.Count -gt 0
        Fingerprint = $fingerprint
    }
}

function Copy-DotNetRuntimePackNotices {
    param(
        [string]$Root,
        [string]$Destination
    )

    $depsPath = Join-Path $Root (
        "src\RemoteDesk\obj\Release\net8.0-windows\win-x64\" +
        "RemoteDesk.deps.json")
    if (-not (Test-Path -LiteralPath $depsPath -PathType Leaf)) {
        throw "Cannot locate the win-x64 publish dependency manifest: $depsPath"
    }

    try {
        $deps = Get-Content -LiteralPath $depsPath -Raw |
            ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "Cannot read the win-x64 publish dependency manifest: $($_.Exception.Message)"
    }

    $librariesProperty = $deps.PSObject.Properties["libraries"]
    $runtimePackLibraryNames = @()
    if ($null -ne $librariesProperty) {
        $runtimePackLibraryNames = @(
            $librariesProperty.Value.PSObject.Properties.Name |
                Where-Object {
                    $_ -cmatch (
                        "^runtimepack\.Microsoft\.NETCore\.App\.Runtime\." +
                        "win-x64/[^/]+$")
                })
    }
    if ($runtimePackLibraryNames.Count -ne 1) {
        throw (
            "Expected exactly one Microsoft.NETCore.App win-x64 runtime pack " +
            "in $depsPath; found $($runtimePackLibraryNames.Count).")
    }

    $runtimePackLibraryName = $runtimePackLibraryNames[0]
    $runtimePackVersion = $runtimePackLibraryName.Substring(
        $runtimePackLibraryName.LastIndexOf("/") + 1)
    $runtimePackPackageId =
        "microsoft.netcore.app.runtime.win-x64"

    $packageRoots = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    $assetsPath = Join-Path $Root "src\RemoteDesk\obj\project.assets.json"
    if (Test-Path -LiteralPath $assetsPath -PathType Leaf) {
        try {
            $assets = Get-Content -LiteralPath $assetsPath -Raw |
                ConvertFrom-Json -ErrorAction Stop
            $packageFoldersProperty =
                $assets.PSObject.Properties["packageFolders"]
            if ($null -ne $packageFoldersProperty) {
                foreach ($property in
                    $packageFoldersProperty.Value.PSObject.Properties) {
                    if (-not [string]::IsNullOrWhiteSpace($property.Name)) {
                        [void]$packageRoots.Add($property.Name)
                    }
                }
            }
        }
        catch {
            throw "Cannot read NuGet package roots from $assetsPath`: $($_.Exception.Message)"
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
        [void]$packageRoots.Add($env:NUGET_PACKAGES)
    }
    if (-not [string]::IsNullOrWhiteSpace($env:USERPROFILE)) {
        [void]$packageRoots.Add(
            (Join-Path $env:USERPROFILE ".nuget\packages"))
    }

    $runtimePackDirectory = $null
    foreach ($packageRoot in $packageRoots) {
        $candidate = Join-Path (
            Join-Path $packageRoot $runtimePackPackageId) $runtimePackVersion
        if (Test-Path -LiteralPath $candidate -PathType Container) {
            $runtimePackDirectory = $candidate
            break
        }
    }
    if ([string]::IsNullOrWhiteSpace($runtimePackDirectory)) {
        throw (
            "Cannot locate exact runtime pack $runtimePackPackageId/" +
            "$runtimePackVersion under the resolved NuGet package roots.")
    }

    $runtimeNoticeFiles = @(
        @{
            Source = "LICENSE.TXT"
            Destination = "DOTNET-RUNTIME-LICENSE.TXT"
        },
        @{
            Source = "THIRD-PARTY-NOTICES.TXT"
            Destination = "DOTNET-RUNTIME-THIRD-PARTY-NOTICES.TXT"
        })
    foreach ($runtimeNoticeFile in $runtimeNoticeFiles) {
        $sourcePath = Join-Path $runtimePackDirectory $runtimeNoticeFile.Source
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw (
                "Runtime pack $runtimePackPackageId/$runtimePackVersion " +
                "does not contain required $($runtimeNoticeFile.Source).")
        }

        Copy-Item `
            -LiteralPath $sourcePath `
            -Destination (Join-Path $Destination $runtimeNoticeFile.Destination) `
            -Force
    }

    return $runtimePackVersion
}

function Resolve-PublishScopeKind {
    param(
        [bool]$WindowsOnly,
        [bool]$LinuxOnly,
        [bool]$SkipAndroid
    )

    if ($WindowsOnly -and $LinuxOnly) {
        throw "Windows-only and Linux-only scopes are mutually exclusive."
    }

    if ($WindowsOnly) {
        return "Windows"
    }

    if ($LinuxOnly) {
        return "Linux"
    }

    if ($SkipAndroid) {
        return "Desktop"
    }

    return "Full"
}

function Get-PublishManifestScope {
    param(
        [ValidateSet("Full", "Desktop", "Windows", "Linux")]
        [string]$ScopeKind
    )

    switch ($ScopeKind) {
        "Full" {
            return "Windows host/viewer; Android; Linux host/viewer package"
        }
        "Desktop" {
            return "Windows host/viewer; Linux host/viewer package"
        }
        "Windows" {
            return "Windows host/viewer package"
        }
        "Linux" {
            return "Linux host/viewer package"
        }
    }
}

function Assert-UnderRoot {
    param(
        [string]$Path,
        [string]$Root
    )

    $resolvedRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $resolvedPath.StartsWith($resolvedRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify path outside repository: $resolvedPath"
    }
}

function Resolve-PublishArtifactsDirectory {
    param(
        [string]$Root,
        [string]$CandidateName,
        [bool]$AllowDirtySource,
        [string]$ReleaseName = ""
    )

    $canonical = Join-Path $Root "artifacts"
    if (-not [string]::IsNullOrEmpty($ReleaseName)) {
        if ($AllowDirtySource -or -not [string]::IsNullOrEmpty($CandidateName)) {
            throw "ReleaseName requires clean-source mode and cannot be combined with CandidateName."
        }
    }
    if ([string]::IsNullOrEmpty($CandidateName) -and [string]::IsNullOrEmpty($ReleaseName)) {
        return $canonical
    }
    if (-not [string]::IsNullOrEmpty($CandidateName) -and -not $AllowDirtySource) {
        throw "-CandidateName requires -AllowDirtySource; isolated builds remain internal candidates."
    }
    if (-not [string]::IsNullOrEmpty($ReleaseName)) {
        $CandidateName = $ReleaseName
    }
    if ($CandidateName -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$' -or
        $CandidateName.EndsWith(".") -or
        $CandidateName -match '^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)') {
        throw "CandidateName must be a simple, non-reserved directory name, not a path."
    }
    if (Test-Path -LiteralPath $canonical) {
        $parentItem = Get-Item -LiteralPath $canonical -Force
        if (-not $parentItem.PSIsContainer -or
            ($parentItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
            throw "Candidate artifacts parent must be a real directory, not a file or reparse point."
        }
    }
    $candidate = Join-Path $canonical $CandidateName
    if (Test-Path -LiteralPath $candidate) {
        throw "Candidate output already exists; choose a new name. Existing files will not be replaced."
    }
    return $candidate
}

function Remove-ArtifactPaths {
    param(
        [string]$Root,
        [string[]]$Paths,
        [string]$Message = "Cleaning previous publish outputs"
    )

    $existingPaths = @($Paths | Where-Object { Test-Path -LiteralPath $_ })
    if ($existingPaths.Count -eq 0) {
        return
    }

    Write-Step $Message
    foreach ($path in $existingPaths) {
        Assert-UnderRoot -Path $path -Root $Root
        Remove-Item -LiteralPath $path -Recurse -Force
        Write-Host "Removed $path"
    }
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

    $gradle = Get-Command "gradle" -ErrorAction SilentlyContinue
    if ($null -ne $gradle) {
        return $gradle.Source
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

function Invoke-Checked {
    param(
        [string]$Name,
        [scriptblock]$Command
    )

    Write-Step $Name
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE"
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

function Wait-GradleJavaExit {
    param([int]$TimeoutMilliseconds = 10000)

    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    do {
        if (@(Get-GradleJavaProcesses).Count -eq 0) {
            return
        }

        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
}

function Invoke-GradleChecked {
    param(
        [string]$Gradle,
        [string]$ProjectPath,
        [string[]]$Tasks
    )

    $gradleExitCode = 0
    $previousDebugItem = Get-Item Env:DEBUG -ErrorAction SilentlyContinue
    $hadDebug = $null -ne $previousDebugItem
    $previousDebugValue = if ($hadDebug) { $previousDebugItem.Value } else { $null }
    try {
        # Gradle's Windows launcher echoes every batch line when DEBUG is set.
        Remove-Item Env:DEBUG -ErrorAction SilentlyContinue
        try {
            & $Gradle --no-daemon -p $ProjectPath @Tasks
            $gradleExitCode = $LASTEXITCODE
        }
        finally {
            try {
                & $Gradle --stop | Out-Null
            }
            catch {
                Write-Host "Gradle daemon cleanup skipped: $($_.Exception.Message)" -ForegroundColor Yellow
            }

            Wait-GradleJavaExit
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
        throw "Gradle failed with exit code $gradleExitCode"
    }
}

function Get-ReleaseVersion {
    param([string]$Root)

    try {
        $projectPath = Join-Path $Root "src\RemoteDesk\RemoteDesk.csproj"
        [xml]$project = Get-Content -Path $projectPath -Raw
        $version = [string]$project.Project.PropertyGroup.Version
        if (-not [string]::IsNullOrWhiteSpace($version)) {
            return $version.Trim()
        }
    }
    catch {
    }

    return "0.1.0"
}

function ConvertTo-WslPath {
    param(
        [string]$WindowsPath,
        [string]$Distro
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
        [string]$Root,
        [string]$Distro
    )

    if ($null -eq (Get-Command "wsl.exe" -ErrorAction SilentlyContinue)) {
        throw "wsl.exe is required to run the Linux Python test suite."
    }

    $wslRoot = ConvertTo-WslPath -WindowsPath $Root -Distro $Distro
    & wsl.exe -d $Distro --cd $wslRoot -- bash -lc `
        'exec python3 -m unittest discover -s tests -p "test_*.py" -v'
    if ($LASTEXITCODE -ne 0) {
        throw "Linux Python tests failed with exit code $LASTEXITCODE"
    }
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
    $relativeUri = $baseUri.MakeRelativeUri($targetUri)
    return [Uri]::UnescapeDataString($relativeUri.ToString()).Replace("/", [System.IO.Path]::DirectorySeparatorChar)
}

function New-ArtifactManifestEntry {
    param(
        [string]$Path,
        [string]$Root
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Cannot add missing artifact to release manifest: $Path"
    }

    $item = Get-Item $Path
    $relativePath = Get-RelativePathCompat -BasePath $Root -TargetPath $item.FullName
    # Some stripped-down Windows PowerShell hosts disable module auto-loading,
    # which makes Get-FileHash disappear late in an otherwise successful
    # release. Use the BCL implementation so manifest generation has no module
    # dependency and always hashes the exact file stream we package.
    $stream = [System.IO.File]::Open(
        $item.FullName,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read)
    try {
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            $hashBytes = $sha256.ComputeHash($stream)
        }
        finally {
            $sha256.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
    $hashText = [System.BitConverter]::ToString($hashBytes).Replace("-", "").ToLowerInvariant()
    return [pscustomobject]@{
        name = $item.Name
        path = $relativePath
        length = $item.Length
        sha256 = $hashText
        lastWriteTimeUtc = $item.LastWriteTimeUtc.ToString("O")
    }
}

function Write-ArtifactManifest {
    param(
        [string]$OutputFile,
        [string]$Root,
        [string[]]$ArtifactPaths,
        [bool]$AndroidReleaseRequested,
        [string]$BuildStamp,
        [string]$Scope,
        [string]$SourceRevision,
        [bool]$SourceDirty,
        [bool]$InternalCandidate,
        [object]$SourceFingerprint,
        [object]$Toolchain,
        [object]$WindowsAuthenticode
    )

    $entries = @($ArtifactPaths | ForEach-Object {
        New-ArtifactManifestEntry -Path $_ -Root $Root
    })

    $manifest = [pscustomobject]@{
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        buildStamp = $BuildStamp
        scope = $Scope
        androidReleaseRequested = $AndroidReleaseRequested
        sourceRevision = $SourceRevision
        sourceDirty = $SourceDirty
        internalCandidate = $InternalCandidate
        sourceFingerprintVersion = $SourceFingerprint.Version
        sourceFingerprintAlgorithm = $SourceFingerprint.Algorithm
        sourceFingerprint = $SourceFingerprint.Hash
        sourceFileCount = $SourceFingerprint.FileCount
        toolchain = $Toolchain
        windowsAuthenticode = $WindowsAuthenticode
        artifacts = $entries
    }

    $manifest | ConvertTo-Json -Depth 6 | Set-Content -Path $OutputFile -Encoding UTF8
    return $entries
}

function Assert-CanonicalPublishArtifactSet {
    param(
        [ValidateSet("Full", "Desktop", "Windows", "Linux")]
        [string]$ScopeKind,
        [string[]]$ArtifactPaths,
        [string]$WindowsExecutable,
        [string]$WindowsZip,
        [string]$LinuxZip,
        [string]$LinuxTarGz,
        [string]$LinuxDeb,
        [string]$AndroidArtifact
    )

    $expectedPaths = @()
    if ($ScopeKind -in @("Full", "Desktop", "Windows")) {
        $expectedPaths += @($WindowsExecutable, $WindowsZip)
    }
    if ($ScopeKind -in @("Full", "Desktop", "Linux")) {
        $expectedPaths += @($LinuxZip, $LinuxTarGz, $LinuxDeb)
    }
    if ($ScopeKind -eq "Full") {
        $expectedPaths += $AndroidArtifact
    }

    if ($ScopeKind -in @("Full", "Desktop", "Windows")) {
        if (-not [string]::Equals(
                [System.IO.Path]::GetFileName($WindowsExecutable),
                "RemoteDesk.exe",
                [System.StringComparison]::Ordinal) -or
            -not [string]::Equals(
                [System.IO.Path]::GetFileName($WindowsZip),
                "RemoteDesk-win-x64.zip",
                [System.StringComparison]::Ordinal)) {
            throw "Windows canonical artifact names are invalid."
        }
    }
    if ($ScopeKind -in @("Full", "Desktop", "Linux")) {
        if (-not [string]::Equals(
                [System.IO.Path]::GetFileName($LinuxZip),
                "RemoteDesk-linux-host.zip",
                [System.StringComparison]::Ordinal) -or
            -not [string]::Equals(
                [System.IO.Path]::GetFileName($LinuxTarGz),
                "RemoteDesk-linux-host.tar.gz",
                [System.StringComparison]::Ordinal) -or
            [System.IO.Path]::GetFileName($LinuxDeb) -notmatch
                "^remotedesk-linux-host_[^\\]+_[^\\]+\.deb$") {
            throw "Linux canonical artifact names are invalid."
        }
    }
    if ($ScopeKind -eq "Full" -and
        [System.IO.Path]::GetFileName($AndroidArtifact) -notin @(
            "RemoteDesk-android-debug.apk",
            "RemoteDesk-android-release.apk",
            "RemoteDesk-android-release-unsigned.apk")) {
        throw "Android canonical artifact name is invalid."
    }

    $expectedCount = switch ($ScopeKind) {
        "Full" { 6 }
        "Desktop" { 5 }
        "Windows" { 2 }
        "Linux" { 3 }
    }
    if ($expectedPaths.Count -ne $expectedCount -or
        @($ArtifactPaths).Count -ne $expectedCount) {
        throw (
            "Canonical $ScopeKind publish must contain exactly " +
            "$expectedCount artifacts; expected=$($expectedPaths.Count), " +
            "actual=$(@($ArtifactPaths).Count).")
    }

    $expected = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($path in $expectedPaths) {
        if ([string]::IsNullOrWhiteSpace([string]$path)) {
            throw "Canonical $ScopeKind publish contains an unresolved expected artifact path."
        }

        $fullPath = [System.IO.Path]::GetFullPath($path)
        if (-not $expected.Add($fullPath)) {
            throw "Canonical $ScopeKind publish contains a duplicate expected artifact: $fullPath"
        }

        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "Canonical $ScopeKind publish is missing artifact: $fullPath"
        }

        if ((Get-Item -LiteralPath $fullPath).Length -le 0) {
            throw "Canonical $ScopeKind publish contains an empty artifact: $fullPath"
        }
    }

    $actual = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($path in @($ArtifactPaths)) {
        if ([string]::IsNullOrWhiteSpace([string]$path)) {
            throw "Canonical $ScopeKind publish contains an unresolved artifact path."
        }

        $fullPath = [System.IO.Path]::GetFullPath($path)
        if (-not $actual.Add($fullPath)) {
            throw "Canonical $ScopeKind publish contains a duplicate artifact: $fullPath"
        }

        if (-not $expected.Contains($fullPath)) {
            throw "Canonical $ScopeKind publish contains an unexpected artifact: $fullPath"
        }
    }

    foreach ($path in $expected) {
        if (-not $actual.Contains($path)) {
            throw "Canonical $ScopeKind publish omitted expected artifact: $path"
        }
    }
}

function Write-Utf8NoBomContent {
    param(
        [Parameter(ValueFromPipeline = $true)]
        [AllowEmptyString()]
        [string]$Content,
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [switch]$NoNewline
    )

    begin {
        $builder = [System.Text.StringBuilder]::new()
        $first = $true
    }
    process {
        if (-not $first) {
            [void]$builder.Append("`n")
        }

        # Every file produced by this helper is consumed on Linux. Normalize
        # both a CRLF source checkout and PowerShell's platform newline so the
        # temporary WSL build scripts cannot fail with a stray carriage return.
        $normalizedContent = $Content.Replace("`r`n", "`n").Replace("`r", "`n")
        [void]$builder.Append($normalizedContent)
        $first = $false
    }
    end {
        $text = $builder.ToString()
        if (-not $NoNewline) {
            $text += "`n"
        }

        $encoding = [System.Text.UTF8Encoding]::new($false)
        [System.IO.File]::WriteAllText($Path, $text, $encoding)
    }
}

function New-LinuxHostWrapper {
    param([string]$Path)

    @'
#!/usr/bin/env sh
set -eu
SCRIPT_DIR="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
export PYTHONHOME="$SCRIPT_DIR/runtime"
export PYTHONPATH="$SCRIPT_DIR/runtime/lib/python3/dist-packages:$SCRIPT_DIR/runtime/lib/python3.12/dist-packages:/usr/lib/python3/dist-packages:/usr/local/lib/python3.12/dist-packages${PYTHONPATH:+:$PYTHONPATH}"
export LD_LIBRARY_PATH="$SCRIPT_DIR/runtime/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
exec "$SCRIPT_DIR/runtime/bin/python" "$SCRIPT_DIR/app/remotedesk_linux_host.py" "$@"
'@ | Write-Utf8NoBomContent -Path $Path -NoNewline
}

function New-LinuxProbeWrapper {
    param([string]$Path)

    @'
#!/usr/bin/env sh
set -eu
SCRIPT_DIR="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
export PYTHONHOME="$SCRIPT_DIR/runtime"
export PYTHONPATH="$SCRIPT_DIR/runtime/lib/python3/dist-packages:$SCRIPT_DIR/runtime/lib/python3.12/dist-packages:/usr/lib/python3/dist-packages:/usr/local/lib/python3.12/dist-packages${PYTHONPATH:+:$PYTHONPATH}"
export LD_LIBRARY_PATH="$SCRIPT_DIR/runtime/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
exec "$SCRIPT_DIR/runtime/bin/python" "$SCRIPT_DIR/app/remotedesk_protocol_probe.py" "$@"
'@ | Write-Utf8NoBomContent -Path $Path -NoNewline
}

function New-LinuxAppWrapper {
    param([string]$Path)

    @'
#!/usr/bin/env sh
set -u
SCRIPT_DIR="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
HOME_DIR="${HOME:-/tmp}"
LOG_DIR="${XDG_CACHE_HOME:-$HOME_DIR/.cache}/remotedesk"
mkdir -p "$LOG_DIR" 2>/dev/null || LOG_DIR="/tmp"
LOG_FILE="$LOG_DIR/remotedesk-linux-app.log"
export PYTHONHOME="$SCRIPT_DIR/runtime"
export PYTHONPATH="$SCRIPT_DIR/runtime/lib/python3/dist-packages:$SCRIPT_DIR/runtime/lib/python3.12/dist-packages:/usr/lib/python3/dist-packages:/usr/local/lib/python3.12/dist-packages${PYTHONPATH:+:$PYTHONPATH}"
export LD_LIBRARY_PATH="$SCRIPT_DIR/runtime/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
"$SCRIPT_DIR/runtime/bin/python" "$SCRIPT_DIR/app/remotedesk_linux_app.py" "$@" >"$LOG_FILE" 2>&1
code=$?
if [ "$code" -eq 125 ]; then
  exit 0 # User cancelled the dependency installation/authentication dialog.
fi
if [ "$code" -eq 0 ]; then
  exit 0
fi
tail_text="$(tail -n 20 "$LOG_FILE" 2>/dev/null || true)"
message="RemoteDesk failed to start (exit $code).

$tail_text

Log: $LOG_FILE"
if [ -n "${DISPLAY:-}${WAYLAND_DISPLAY:-}" ]; then
  if command -v zenity >/dev/null 2>&1; then
    zenity --error --title="RemoteDesk" --text="$message" 2>/dev/null || true
  elif command -v kdialog >/dev/null 2>&1; then
    kdialog --error "$message" --title "RemoteDesk" 2>/dev/null || true
  elif command -v xmessage >/dev/null 2>&1; then
    xmessage -center "$message" 2>/dev/null || true
  elif command -v notify-send >/dev/null 2>&1; then
    notify-send "RemoteDesk failed to start" "See $LOG_FILE" 2>/dev/null || true
  fi
fi
printf '%s\n' "$message" >&2
exit "$code"
'@ | Write-Utf8NoBomContent -Path $Path -NoNewline
}

function New-LinuxDoctorWrapper {
    param([string]$Path)

    @'
#!/usr/bin/env sh
set -u
SCRIPT_DIR="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
APP_ROOT="$SCRIPT_DIR"
RUNTIME_ROOT="$APP_ROOT/runtime"
APP_DIR="$APP_ROOT/app"
LOG_FILE="${XDG_CACHE_HOME:-${HOME:-/tmp}/.cache}/remotedesk/remotedesk-linux-app.log"
export PYTHONHOME="$RUNTIME_ROOT"
export PYTHONPATH="$RUNTIME_ROOT/lib/python3/dist-packages:$RUNTIME_ROOT/lib/python3.12/dist-packages:$APP_DIR:/usr/lib/python3/dist-packages:/usr/local/lib/python3.12/dist-packages${PYTHONPATH:+:$PYTHONPATH}"
export LD_LIBRARY_PATH="$RUNTIME_ROOT/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
echo "RemoteDesk Linux doctor"
echo "app_root=$APP_ROOT"
echo "date=$(date -Iseconds 2>/dev/null || date)"
echo "kernel=$(uname -a 2>/dev/null || true)"
if [ -r /etc/os-release ]; then
  . /etc/os-release
  echo "os=${PRETTY_NAME:-unknown}"
fi
ldd --version 2>/dev/null | head -n 1 | sed 's/^/libc=/'
echo "display=${DISPLAY:-}"
echo "wayland_display=${WAYLAND_DISPLAY:-}"
echo "session_type=${XDG_SESSION_TYPE:-}"
echo "desktop_session=${XDG_CURRENT_DESKTOP:-}"
for pkg in libc6 libexpat1 libffi8 libgcc-s1 libssl3t64 libssl3 libx11-6 libxtst6 python3-tk python3-pil imagemagick ffmpeg wl-clipboard; do
  if command -v dpkg-query >/dev/null 2>&1; then
    status="$(dpkg-query -W -f='${Status} ${Version}' "$pkg" 2>/dev/null || true)"
    case "$status" in
      *"install ok installed"*)
      echo "$pkg=$status"
      ;;
      *)
      echo "$pkg=not-installed"
      ;;
    esac
  fi
done
for cmd in convert magick ffmpeg zenity kdialog xmessage notify-send xclip xsel wl-paste xdotool; do
  if command -v "$cmd" >/dev/null 2>&1; then
    echo "$cmd=$(command -v "$cmd")"
  else
    echo "$cmd=missing"
  fi
done
for path in "$APP_ROOT/remotedesk-linux-app" "$APP_ROOT/remotedesk-linux-host" "$APP_DIR/remotedesk_linux_app.py" "$APP_DIR/RemoteDesk.png"; do
  if [ -e "$path" ]; then
    ls -ld "$path" 2>/dev/null | sed 's/^/path=/'
  else
    echo "path=missing $path"
  fi
done
if command -v convert >/dev/null 2>&1; then
  convert --version 2>/dev/null | head -n 1 | sed 's/^/convert_version=/'
fi
if command -v ffmpeg >/dev/null 2>&1; then
  ffmpeg -version 2>/dev/null | head -n 1 | sed 's/^/ffmpeg_version=/'
fi
echo "--- bundled runtime ---"
"$RUNTIME_ROOT/bin/python" - <<'PY'
import os
import sys
print("python=" + sys.version.replace("\n", " "))
checks = []
for name in ("tkinter", "cryptography", "PIL", "remotedesk_linux_app"):
    try:
        module = __import__(name)
        checks.append((name, "ok", getattr(module, "__version__", "")))
    except Exception as ex:
        checks.append((name, "fail", repr(ex)))
for name, status, detail in checks:
    print(f"{name}={status} {detail}")
try:
    import remotedesk_linux_app as app
    print(f"image_converter={app.find_image_converter()}")
except Exception as ex:
    print(f"image_converter=fail {ex!r}")
try:
    import ctypes
    import ctypes.util
    x11 = ctypes.util.find_library("X11") or "libX11.so.6"
    xtst = ctypes.util.find_library("Xtst") or "libXtst.so.6"
    ctypes.CDLL(x11)
    ctypes.CDLL(xtst)
    print(f"native_xtest=ok x11={x11} xtst={xtst}")
except Exception as ex:
    print(f"native_xtest=fail {ex!r}")
try:
    import tkinter as tk
    if os.environ.get("DISPLAY") or os.environ.get("WAYLAND_DISPLAY"):
        root = tk.Tk()
        root.withdraw()
        root.update_idletasks()
        root.destroy()
        print("tk_root=ok")
    else:
        print("tk_root=skipped no-display")
except Exception as ex:
    print(f"tk_root=fail {ex!r}")
PY
code=$?
echo "runtime_check_exit=$code"
if [ -f "$LOG_FILE" ]; then
  echo "--- last GUI log: $LOG_FILE ---"
  tail -n 80 "$LOG_FILE" 2>/dev/null || true
else
  echo "gui_log=missing ($LOG_FILE)"
fi
exit "$code"
'@ | Write-Utf8NoBomContent -Path $Path -NoNewline
}

function New-LinuxHostInstallWrapper {
    param([string]$Path)

    @'
#!/usr/bin/env sh
set -eu
export PYTHONHOME=/opt/remotedesk/runtime
export PYTHONPATH="/opt/remotedesk/runtime/lib/python3/dist-packages:/opt/remotedesk/runtime/lib/python3.12/dist-packages:/usr/lib/python3/dist-packages:/usr/local/lib/python3.12/dist-packages${PYTHONPATH:+:$PYTHONPATH}"
export LD_LIBRARY_PATH="/opt/remotedesk/runtime/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
exec /opt/remotedesk/runtime/bin/python /opt/remotedesk/app/remotedesk_linux_host.py "$@"
'@ | Write-Utf8NoBomContent -Path $Path -NoNewline
}

function New-LinuxProbeInstallWrapper {
    param([string]$Path)

    @'
#!/usr/bin/env sh
set -eu
export PYTHONHOME=/opt/remotedesk/runtime
export PYTHONPATH="/opt/remotedesk/runtime/lib/python3/dist-packages:/opt/remotedesk/runtime/lib/python3.12/dist-packages:/usr/lib/python3/dist-packages:/usr/local/lib/python3.12/dist-packages${PYTHONPATH:+:$PYTHONPATH}"
export LD_LIBRARY_PATH="/opt/remotedesk/runtime/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
exec /opt/remotedesk/runtime/bin/python /opt/remotedesk/app/remotedesk_protocol_probe.py "$@"
'@ | Write-Utf8NoBomContent -Path $Path -NoNewline
}

function New-LinuxAppInstallWrapper {
    param([string]$Path)

    @'
#!/usr/bin/env sh
set -u
HOME_DIR="${HOME:-/tmp}"
LOG_DIR="${XDG_CACHE_HOME:-$HOME_DIR/.cache}/remotedesk"
mkdir -p "$LOG_DIR" 2>/dev/null || LOG_DIR="/tmp"
LOG_FILE="$LOG_DIR/remotedesk-linux-app.log"
export PYTHONHOME=/opt/remotedesk/runtime
export PYTHONPATH="/opt/remotedesk/runtime/lib/python3/dist-packages:/opt/remotedesk/runtime/lib/python3.12/dist-packages:/usr/lib/python3/dist-packages:/usr/local/lib/python3.12/dist-packages${PYTHONPATH:+:$PYTHONPATH}"
export LD_LIBRARY_PATH="/opt/remotedesk/runtime/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
/opt/remotedesk/runtime/bin/python /opt/remotedesk/app/remotedesk_linux_app.py "$@" >"$LOG_FILE" 2>&1
code=$?
if [ "$code" -eq 125 ]; then
  exit 0 # User cancelled the dependency installation/authentication dialog.
fi
if [ "$code" -eq 0 ]; then
  exit 0
fi
tail_text="$(tail -n 20 "$LOG_FILE" 2>/dev/null || true)"
message="RemoteDesk failed to start (exit $code).

$tail_text

Log: $LOG_FILE"
if [ -n "${DISPLAY:-}${WAYLAND_DISPLAY:-}" ]; then
  if command -v zenity >/dev/null 2>&1; then
    zenity --error --title="RemoteDesk" --text="$message" 2>/dev/null || true
  elif command -v kdialog >/dev/null 2>&1; then
    kdialog --error "$message" --title "RemoteDesk" 2>/dev/null || true
  elif command -v xmessage >/dev/null 2>&1; then
    xmessage -center "$message" 2>/dev/null || true
  elif command -v notify-send >/dev/null 2>&1; then
    notify-send "RemoteDesk failed to start" "See $LOG_FILE" 2>/dev/null || true
  fi
fi
printf '%s\n' "$message" >&2
exit "$code"
'@ | Write-Utf8NoBomContent -Path $Path -NoNewline
}

function New-LinuxDoctorInstallWrapper {
    param([string]$Path)

    @'
#!/usr/bin/env sh
set -u
APP_ROOT=/opt/remotedesk
RUNTIME_ROOT="$APP_ROOT/runtime"
APP_DIR="$APP_ROOT/app"
LOG_FILE="${XDG_CACHE_HOME:-${HOME:-/tmp}/.cache}/remotedesk/remotedesk-linux-app.log"
export PYTHONHOME="$RUNTIME_ROOT"
export PYTHONPATH="$RUNTIME_ROOT/lib/python3/dist-packages:$RUNTIME_ROOT/lib/python3.12/dist-packages:$APP_DIR:/usr/lib/python3/dist-packages:/usr/local/lib/python3.12/dist-packages${PYTHONPATH:+:$PYTHONPATH}"
export LD_LIBRARY_PATH="$RUNTIME_ROOT/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
echo "RemoteDesk Linux doctor"
echo "app_root=$APP_ROOT"
echo "date=$(date -Iseconds 2>/dev/null || date)"
echo "kernel=$(uname -a 2>/dev/null || true)"
if [ -r /etc/os-release ]; then
  . /etc/os-release
  echo "os=${PRETTY_NAME:-unknown}"
fi
ldd --version 2>/dev/null | head -n 1 | sed 's/^/libc=/'
echo "display=${DISPLAY:-}"
echo "wayland_display=${WAYLAND_DISPLAY:-}"
echo "session_type=${XDG_SESSION_TYPE:-}"
echo "desktop_session=${XDG_CURRENT_DESKTOP:-}"
for pkg in libc6 libexpat1 libffi8 libgcc-s1 libssl3t64 libssl3 libx11-6 libxtst6 python3-tk python3-pil imagemagick ffmpeg wl-clipboard; do
  if command -v dpkg-query >/dev/null 2>&1; then
    status="$(dpkg-query -W -f='${Status} ${Version}' "$pkg" 2>/dev/null || true)"
    case "$status" in
      *"install ok installed"*)
      echo "$pkg=$status"
      ;;
      *)
      echo "$pkg=not-installed"
      ;;
    esac
  fi
done
for cmd in convert magick ffmpeg zenity kdialog xmessage notify-send xclip xsel wl-paste xdotool; do
  if command -v "$cmd" >/dev/null 2>&1; then
    echo "$cmd=$(command -v "$cmd")"
  else
    echo "$cmd=missing"
  fi
done
for path in /usr/bin/remotedesk-linux-app /usr/bin/remotedesk-linux-host /usr/share/applications/remotedesk.desktop /usr/share/icons/hicolor/256x256/apps/remotedesk.png "$APP_DIR/remotedesk_linux_app.py"; do
  if [ -e "$path" ]; then
    ls -ld "$path" 2>/dev/null | sed 's/^/path=/'
  else
    echo "path=missing $path"
  fi
done
if command -v convert >/dev/null 2>&1; then
  convert --version 2>/dev/null | head -n 1 | sed 's/^/convert_version=/'
fi
if command -v ffmpeg >/dev/null 2>&1; then
  ffmpeg -version 2>/dev/null | head -n 1 | sed 's/^/ffmpeg_version=/'
fi
echo "--- bundled runtime ---"
"$RUNTIME_ROOT/bin/python" - <<'PY'
import os
import sys
print("python=" + sys.version.replace("\n", " "))
checks = []
for name in ("tkinter", "cryptography", "PIL", "remotedesk_linux_app"):
    try:
        module = __import__(name)
        checks.append((name, "ok", getattr(module, "__version__", "")))
    except Exception as ex:
        checks.append((name, "fail", repr(ex)))
for name, status, detail in checks:
    print(f"{name}={status} {detail}")
try:
    import remotedesk_linux_app as app
    print(f"image_converter={app.find_image_converter()}")
except Exception as ex:
    print(f"image_converter=fail {ex!r}")
try:
    import ctypes
    import ctypes.util
    x11 = ctypes.util.find_library("X11") or "libX11.so.6"
    xtst = ctypes.util.find_library("Xtst") or "libXtst.so.6"
    ctypes.CDLL(x11)
    ctypes.CDLL(xtst)
    print(f"native_xtest=ok x11={x11} xtst={xtst}")
except Exception as ex:
    print(f"native_xtest=fail {ex!r}")
try:
    import tkinter as tk
    if os.environ.get("DISPLAY") or os.environ.get("WAYLAND_DISPLAY"):
        root = tk.Tk()
        root.withdraw()
        root.update_idletasks()
        root.destroy()
        print("tk_root=ok")
    else:
        print("tk_root=skipped no-display")
except Exception as ex:
    print(f"tk_root=fail {ex!r}")
PY
code=$?
echo "runtime_check_exit=$code"
if [ -f "$LOG_FILE" ]; then
  echo "--- last GUI log: $LOG_FILE ---"
  tail -n 80 "$LOG_FILE" 2>/dev/null || true
else
  echo "gui_log=missing ($LOG_FILE)"
fi
exit "$code"
'@ | Write-Utf8NoBomContent -Path $Path -NoNewline
}

function New-LinuxDesktopEntry {
    param([string]$Path)

    @'
[Desktop Entry]
Type=Application
Name=RemoteDesk
Comment=RemoteDesk Linux host and viewer
Exec=/usr/bin/remotedesk-linux-app
Icon=remotedesk
Terminal=false
Categories=Network;RemoteAccess;
StartupNotify=true
StartupWMClass=RemoteDesk
'@ | Write-Utf8NoBomContent -Path $Path -NoNewline
}

function New-LinuxSelfContainedRuntime {
    param(
        [string]$Root,
        [string]$Artifacts,
        [string]$Staging,
        [string]$Distro
    )

    if ($null -eq (Get-Command "wsl.exe" -ErrorAction SilentlyContinue)) {
        throw "wsl.exe is required to build the self-contained Linux runtime."
    }

    New-Item -ItemType Directory -Path (Join-Path $Staging "app") -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $Staging "docs") -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $Root "scripts\linux\remotedesk_linux_host.py") -Destination (Join-Path $Staging "app\remotedesk_linux_host.py") -Force
    Copy-Item -LiteralPath (Join-Path $Root "scripts\linux\remotedesk_protocol_probe.py") -Destination (Join-Path $Staging "app\remotedesk_protocol_probe.py") -Force
    Copy-Item -LiteralPath (Join-Path $Root "scripts\linux\remotedesk_linux_app.py") -Destination (Join-Path $Staging "app\remotedesk_linux_app.py") -Force
    Copy-Item -LiteralPath (Join-Path $Root "scripts\linux\remotedesk_linux_dependencies.py") -Destination (Join-Path $Staging "app\remotedesk_linux_dependencies.py") -Force
    Copy-Item -LiteralPath (Join-Path $Root "scripts\linux\remotedesk_linux_relay.py") -Destination (Join-Path $Staging "app\remotedesk_linux_relay.py") -Force
    Copy-Item -LiteralPath (Join-Path $Root "scripts\linux\remotedesk_linux_startup.py") -Destination (Join-Path $Staging "app\remotedesk_linux_startup.py") -Force
    Copy-Item -LiteralPath (Join-Path $Root "scripts\linux\remotedesk_linux_devices.py") -Destination (Join-Path $Staging "app\remotedesk_linux_devices.py") -Force
    Copy-Item -LiteralPath (Join-Path $Root "scripts\linux\remotedesk_linux_device_panel.py") -Destination (Join-Path $Staging "app\remotedesk_linux_device_panel.py") -Force
    Copy-Item -LiteralPath (Join-Path $Root "src\RemoteDesk\Assets\RemoteDesk.png") -Destination (Join-Path $Staging "app\RemoteDesk.png") -Force
    Copy-Item -LiteralPath (Join-Path $Root "docs\Linux-Sandbox.md") -Destination (Join-Path $Staging "docs\Linux-Sandbox.md") -Force
    Copy-Item -LiteralPath (Join-Path $Root "docs\RemoteDesk-Protocol.md") -Destination (Join-Path $Staging "docs\RemoteDesk-Protocol.md") -Force
    Copy-Item -LiteralPath (Join-Path $Root "THIRD-PARTY-NOTICES.md") -Destination (Join-Path $Staging "THIRD-PARTY-NOTICES.md") -Force
    New-LinuxHostWrapper -Path (Join-Path $Staging "remotedesk-linux-host")
    New-LinuxProbeWrapper -Path (Join-Path $Staging "remotedesk-protocol-probe")
    New-LinuxAppWrapper -Path (Join-Path $Staging "remotedesk-linux-app")
    New-LinuxDoctorWrapper -Path (Join-Path $Staging "remotedesk-linux-doctor")

    Invoke-Checked "Creating self-contained Linux Python runtime" {
        $wslStaging = (ConvertTo-WslPath -WindowsPath $Staging -Distro $Distro).Replace("'", "'\''")
        $buildScriptPath = Join-Path $Artifacts "remotedesk-build-runtime.sh"
        $wslBuildScriptPath = ConvertTo-WslPath -WindowsPath $buildScriptPath -Distro $Distro
        $scriptTemplate = @'
set -eu
staging='__STAGING__'
cd "$staging"
rm -rf runtime
mkdir -p runtime/bin runtime/lib/python3 runtime/lib/python3.12 third-party-licenses
cp /usr/bin/python3 runtime/bin/python
cp /usr/bin/python3 runtime/bin/python3
cp -a /usr/lib/python3.12 runtime/lib/
rm -f runtime/lib/python3.12/sitecustomize.py
rm -rf runtime/lib/python3.12/__pycache__ runtime/lib/python3.12/config-* runtime/lib/python3.12/test runtime/lib/python3.12/ensurepip
if [ -d /usr/lib/python3/dist-packages ]; then
  mkdir -p runtime/lib/python3/dist-packages
  cp -a /usr/lib/python3/dist-packages/cryptography runtime/lib/python3/dist-packages/
  cp -a /usr/lib/python3/dist-packages/cryptography-*.dist-info runtime/lib/python3/dist-packages/ 2>/dev/null || true
  cp -a /usr/lib/python3/dist-packages/_cffi_backend*.so runtime/lib/python3/dist-packages/ 2>/dev/null || true
fi
if [ -d /usr/local/lib/python3.12/dist-packages ]; then
  mkdir -p runtime/lib/python3.12/dist-packages
  cp -a /usr/local/lib/python3.12/dist-packages/cryptography runtime/lib/python3.12/dist-packages/ 2>/dev/null || true
  cp -a /usr/local/lib/python3.12/dist-packages/cryptography-*.dist-info runtime/lib/python3.12/dist-packages/ 2>/dev/null || true
  cp -a /usr/local/lib/python3.12/dist-packages/_cffi_backend*.so runtime/lib/python3.12/dist-packages/ 2>/dev/null || true
fi
for package in python3 python3.12 python3.12-minimal libpython3.12-stdlib python3-cryptography python3-cffi-backend; do
  if [ -f "/usr/share/doc/$package/copyright" ]; then
    cp "/usr/share/doc/$package/copyright" "third-party-licenses/$package-copyright"
  fi
done
if ! PYTHONHOME="$PWD/runtime" PYTHONPATH="$PWD/runtime/lib/python3/dist-packages:$PWD/runtime/lib/python3.12/dist-packages" "$PWD/runtime/bin/python" - <<'PY'
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
print('cryptography-ok')
PY
then
  echo 'Failed to import cryptography from bundled runtime. Install python3-cryptography in WSL before publishing.' >&2
  exit 1
fi
find runtime app -type d -name __pycache__ -prune -exec rm -rf {} +
find runtime -type d -name 'tests' -prune -exec rm -rf {} +
rm -rf runtime/share runtime/lib/python*/site-packages/pip/_vendor/cache 2>/dev/null || true
chmod +x remotedesk-linux-host remotedesk-protocol-probe remotedesk-linux-app remotedesk-linux-doctor
'@
        $scriptContent = $scriptTemplate.Replace("__STAGING__", $wslStaging)
        $scriptContent | Write-Utf8NoBomContent -Path $buildScriptPath -NoNewline
        try {
            & wsl.exe -d $Distro -- bash $wslBuildScriptPath | Write-Host
        }
        finally {
            Remove-Item -LiteralPath $buildScriptPath -Force -ErrorAction SilentlyContinue
        }
    }
}

function Get-WslDpkgArchitecture {
    param([string]$Distro)

    if ($null -eq (Get-Command "wsl.exe" -ErrorAction SilentlyContinue)) {
        return "amd64"
    }

    $output = & wsl.exe -d $Distro -- dpkg --print-architecture 2>$null
    $arch = (($output | ForEach-Object { [string]$_ }) -join " ").Trim()
    if ([string]::IsNullOrWhiteSpace($arch)) {
        return "amd64"
    }

    return $arch
}

function New-LinuxHostTarGz {
    param(
        [string]$Root,
        [string]$Artifacts,
        [string]$Staging,
        [string]$Distro
    )

    $tarGz = Join-Path $Artifacts "RemoteDesk-linux-host.tar.gz"
    Assert-UnderRoot -Path $tarGz -Root $Root
    if (Test-Path $tarGz) {
        Remove-Item -LiteralPath $tarGz -Force
    }

    if ($null -ne (Get-Command "wsl.exe" -ErrorAction SilentlyContinue)) {
        Invoke-Checked "Creating Linux portable tar.gz artifact via WSL" {
            $wslStaging = (ConvertTo-WslPath -WindowsPath $Staging -Distro $Distro).Replace("'", "'\''")
            $wslTarGz = (ConvertTo-WslPath -WindowsPath $tarGz -Distro $Distro).Replace("'", "'\''")
            $buildScriptPath = Join-Path $Artifacts "remotedesk-build-targz.sh"
            $wslBuildScriptPath = ConvertTo-WslPath -WindowsPath $buildScriptPath -Distro $Distro
            $scriptTemplate = @'
set -eu
src='__SRC__'
out='__OUT__'
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
cp -a "$src"/. "$tmp"/
find "$tmp" -type d -exec chmod 755 {} +
find "$tmp" -type f -exec chmod 644 {} +
chmod +x "$tmp/remotedesk-linux-host" "$tmp/remotedesk-protocol-probe" "$tmp/remotedesk-linux-app" "$tmp/remotedesk-linux-doctor"
find "$tmp/runtime/bin" -maxdepth 1 -type f -exec chmod +x {} + 2>/dev/null || true
tar --format=gnu -czf "$out" -C "$tmp" .
'@
            $scriptContent = $scriptTemplate.Replace("__SRC__", $wslStaging).Replace("__OUT__", $wslTarGz)
            $scriptContent | Write-Utf8NoBomContent -Path $buildScriptPath -NoNewline
            try {
                & wsl.exe -d $Distro -- bash $wslBuildScriptPath | Write-Host
            }
            finally {
                Remove-Item -LiteralPath $buildScriptPath -Force -ErrorAction SilentlyContinue
            }
        }
        return $tarGz
    }

    $tar = Get-Command "tar" -ErrorAction SilentlyContinue
    if ($null -eq $tar) {
        Write-Host "tar was not found; skipping Linux portable tar.gz artifact." -ForegroundColor Yellow
        return $null
    }

    Invoke-Checked "Creating Linux portable tar.gz artifact" {
        & $tar.Source -czf $tarGz -C $Staging .
    }
    return $tarGz
}

function Assert-LinuxPortableZipModes {
    param([string]$ZipPath)

    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop |
        Out-Null
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $requiredExecutableEntries = @(
            "remotedesk-linux-host",
            "remotedesk-protocol-probe",
            "remotedesk-linux-app",
            "remotedesk-linux-doctor",
            "runtime/bin/python")
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
                throw (
                    "Linux portable zip must contain exactly one " +
                    "$entryName; found $($entries.Count).")
            }

            $unixMode =
                ($entries[0].ExternalAttributes -shr 16) -band 0xFFFF
            if (($unixMode -band 0x1FF) -ne 0x1ED) {
                throw (
                    "Linux portable zip entry $entryName must have Unix " +
                    "mode 0755; found 0$([Convert]::ToString(($unixMode -band 0x1FF), 8)).")
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

function New-LinuxHostZip {
    param(
        [string]$Root,
        [string]$Artifacts,
        [string]$Staging,
        [string]$Distro
    )

    if ($null -eq (Get-Command "wsl.exe" -ErrorAction SilentlyContinue)) {
        throw "wsl.exe is required to build the Linux portable zip."
    }

    $zipPath = Join-Path $Artifacts "RemoteDesk-linux-host.zip"
    Assert-UnderRoot -Path $zipPath -Root $Root
    if (Test-Path -LiteralPath $zipPath -PathType Leaf) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    Invoke-Checked "Creating Linux portable zip artifact via WSL" {
        $wslStaging =
            (ConvertTo-WslPath -WindowsPath $Staging -Distro $Distro).Replace("'", "'\''")
        $wslZipPath =
            (ConvertTo-WslPath -WindowsPath $zipPath -Distro $Distro).Replace("'", "'\''")
        $buildScriptPath =
            Join-Path $Artifacts "remotedesk-build-linux-zip.sh"
        $wslBuildScriptPath =
            ConvertTo-WslPath -WindowsPath $buildScriptPath -Distro $Distro
        $scriptTemplate = @'
set -eu
src='__SRC__'
out='__OUT__'
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
cp -a "$src"/. "$tmp"/
find "$tmp" -type d -exec chmod 755 {} +
find "$tmp" -type f -exec chmod 644 {} +
chmod 755 "$tmp/remotedesk-linux-host" "$tmp/remotedesk-protocol-probe" "$tmp/remotedesk-linux-app" "$tmp/remotedesk-linux-doctor"
find "$tmp/runtime/bin" -maxdepth 1 -type f -exec chmod 755 {} + 2>/dev/null || true
python3 - "$tmp" "$out" <<'PY'
import os
from pathlib import Path
import sys
import zipfile

source = Path(sys.argv[1])
output = Path(sys.argv[2])
with zipfile.ZipFile(
    output,
    mode="w",
    compression=zipfile.ZIP_DEFLATED,
    compresslevel=9,
) as archive:
    for path in sorted(
        source.rglob("*"),
        key=lambda candidate: candidate.relative_to(source).as_posix(),
    ):
        archive.write(path, path.relative_to(source).as_posix())
PY
'@
        $scriptContent =
            $scriptTemplate.Replace("__SRC__", $wslStaging).
                Replace("__OUT__", $wslZipPath)
        $scriptContent |
            Write-Utf8NoBomContent -Path $buildScriptPath -NoNewline
        try {
            & wsl.exe -d $Distro -- bash $wslBuildScriptPath | Write-Host
        }
        finally {
            Remove-Item -LiteralPath $buildScriptPath -Force `
                -ErrorAction SilentlyContinue
        }
    }

    Assert-LinuxPortableZipModes -ZipPath $zipPath
    return $zipPath
}

function New-UbuntuDebArtifact {
    param(
        [string]$Root,
        [string]$Artifacts,
        [string]$Version,
        [string]$PortableStaging,
        [string]$Distro
    )

    $safeVersion = if ([string]::IsNullOrWhiteSpace($Version)) { "0.1.0" } else { $Version -replace "[^0-9A-Za-z.+~-]", "-" }
    $architecture = Get-WslDpkgArchitecture -Distro $Distro
    $debPath = Join-Path $Artifacts "remotedesk-linux-host_${safeVersion}_${architecture}.deb"
    $packageRoot = Join-Path $Artifacts "remotedesk-linux-host-deb"
    Assert-UnderRoot -Path $debPath -Root $Root
    Assert-UnderRoot -Path $packageRoot -Root $Root

    if (Test-Path $debPath) {
        Remove-Item -LiteralPath $debPath -Force
    }

    Get-ChildItem -Path $Artifacts -Filter "remotedesk-linux-host_${safeVersion}_*.deb" -File -ErrorAction SilentlyContinue |
        Remove-Item -Force

    if (Test-Path $packageRoot) {
        Remove-Item -LiteralPath $packageRoot -Recurse -Force
    }

    New-Item -ItemType Directory -Path (Join-Path $packageRoot "DEBIAN") -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $packageRoot "opt\remotedesk") -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $packageRoot "usr\bin") -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $packageRoot "usr\share\applications") -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $packageRoot "usr\share\icons\hicolor\256x256\apps") -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PortableStaging "app") -Destination (Join-Path $packageRoot "opt\remotedesk\app") -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $PortableStaging "docs") -Destination (Join-Path $packageRoot "opt\remotedesk\docs") -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $PortableStaging "runtime") -Destination (Join-Path $packageRoot "opt\remotedesk\runtime") -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $PortableStaging "THIRD-PARTY-NOTICES.md") -Destination (Join-Path $packageRoot "opt\remotedesk\THIRD-PARTY-NOTICES.md") -Force
    Copy-Item -LiteralPath (Join-Path $PortableStaging "third-party-licenses") -Destination (Join-Path $packageRoot "opt\remotedesk\third-party-licenses") -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $PortableStaging "app\RemoteDesk.png") -Destination (Join-Path $packageRoot "usr\share\icons\hicolor\256x256\apps\remotedesk.png") -Force
    New-LinuxHostInstallWrapper -Path (Join-Path $packageRoot "usr\bin\remotedesk-linux-host")
    New-LinuxProbeInstallWrapper -Path (Join-Path $packageRoot "usr\bin\remotedesk-protocol-probe")
    New-LinuxAppInstallWrapper -Path (Join-Path $packageRoot "usr\bin\remotedesk-linux-app")
    New-LinuxDoctorInstallWrapper -Path (Join-Path $packageRoot "usr\bin\remotedesk-linux-doctor")
    New-LinuxDesktopEntry -Path (Join-Path $packageRoot "usr\share\applications\remotedesk.desktop")

    @"
Package: remotedesk-linux-host
Version: $safeVersion
Section: net
Priority: optional
Architecture: $architecture
Maintainer: RemoteDesk <noreply@example.invalid>
Depends: libc6 (>= 2.38), libexpat1, libffi8, libgcc-s1, libssl3t64 | libssl3, libx11-6, libxtst6, python3-tk, python3-pil, imagemagick, ffmpeg, xdotool, zlib1g
Recommends: xclip | xsel, wl-clipboard, xvfb, openbox, x11vnc, wmctrl, dbus-x11, mpv, pkexec, zenity
Description: RemoteDesk self-contained Linux host and viewer
 RemoteDesk Linux app speaks the encrypted RemoteDesk protocol for LAN testing.
 It bundles a private Python runtime, a graphical launcher, and Python dependencies for offline install.
 Desktop capture and clipboard tools remain optional system integrations.
"@ | Write-Utf8NoBomContent -Path (Join-Path $packageRoot "DEBIAN\control")

    if ($null -ne (Get-Command "wsl.exe" -ErrorAction SilentlyContinue)) {
        Invoke-Checked "Creating self-contained Ubuntu deb artifact via WSL" {
            $wslPackageRoot = (ConvertTo-WslPath -WindowsPath $packageRoot -Distro $Distro).Replace("'", "'\''")
            $wslDebPath = (ConvertTo-WslPath -WindowsPath $debPath -Distro $Distro).Replace("'", "'\''")
            $buildScriptPath = Join-Path $Artifacts "remotedesk-build-deb.sh"
            $wslBuildScriptPath = ConvertTo-WslPath -WindowsPath $buildScriptPath -Distro $Distro
            $scriptTemplate = @'
set -eu
src='__SRC__'
out='__OUT__'
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
cp -a "$src"/. "$tmp"/
find "$tmp" -type d -exec chmod 755 {} +
find "$tmp" -type f -exec chmod 644 {} +
chmod +x "$tmp/usr/bin/remotedesk-linux-host" "$tmp/usr/bin/remotedesk-protocol-probe" "$tmp/usr/bin/remotedesk-linux-app" "$tmp/usr/bin/remotedesk-linux-doctor"
find "$tmp/opt/remotedesk/runtime/bin" -maxdepth 1 -type f -exec chmod +x {} + 2>/dev/null || true
dpkg-deb --build --root-owner-group "$tmp" "$out"
'@
            $scriptContent = $scriptTemplate.Replace("__SRC__", $wslPackageRoot).Replace("__OUT__", $wslDebPath)
            $scriptContent | Write-Utf8NoBomContent -Path $buildScriptPath -NoNewline
            try {
                & wsl.exe -d $Distro -- bash $wslBuildScriptPath | Write-Host
            }
            finally {
                Remove-Item -LiteralPath $buildScriptPath -Force -ErrorAction SilentlyContinue
            }
        }
        Remove-Item -LiteralPath $packageRoot -Recurse -Force
        return $debPath
    }

    Remove-Item -LiteralPath $packageRoot -Recurse -Force
    Write-Host "wsl.exe was not found; skipping self-contained Ubuntu .deb artifact." -ForegroundColor Yellow
    return $null
}

function New-LinuxHostArtifact {
    param(
        [string]$Root,
        [string]$Artifacts,
        [string]$Version,
        [string]$Distro
    )

    $linuxHostZip = Join-Path $Artifacts "RemoteDesk-linux-host.zip"
    $staging = Join-Path $Artifacts "RemoteDesk-linux-host"
    Assert-UnderRoot -Path $linuxHostZip -Root $Root
    Assert-UnderRoot -Path $staging -Root $Root

    if (Test-Path $linuxHostZip) {
        Remove-Item -LiteralPath $linuxHostZip -Force
    }

    if (Test-Path $staging) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }

    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    New-LinuxSelfContainedRuntime -Root $Root -Artifacts $Artifacts -Staging $staging -Distro $Distro

    $linuxHostZip = New-LinuxHostZip `
        -Root $Root `
        -Artifacts $Artifacts `
        -Staging $staging `
        -Distro $Distro
    $linuxHostTarGz = New-LinuxHostTarGz -Root $Root -Artifacts $Artifacts -Staging $staging -Distro $Distro
    $ubuntuDeb = New-UbuntuDebArtifact -Root $Root -Artifacts $Artifacts -Version $Version -PortableStaging $staging -Distro $Distro
    Remove-Item -LiteralPath $staging -Recurse -Force
    return @(($linuxHostZip, $linuxHostTarGz, $ubuntuDeb) | Where-Object { $null -ne $_ })
}

function Start-PublishArtifactTransaction {
    param(
        [string]$Root,
        [string]$Artifacts,
        [string[]]$Paths
    )

    $backupDirectory = Join-Path $Artifacts (
        ".publish-backup-" + [Guid]::NewGuid().ToString("N"))
    Assert-UnderRoot -Path $backupDirectory -Root $Root
    New-Item -ItemType Directory -Path $backupDirectory | Out-Null
    $entries = [System.Collections.Generic.List[object]]::new()
    $targets = [System.Collections.Generic.List[string]]::new()
    $uniquePaths = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    try {
        foreach ($path in $Paths) {
            if ([string]::IsNullOrWhiteSpace([string]$path)) {
                continue
            }

            $fullPath = [System.IO.Path]::GetFullPath($path)
            Assert-UnderRoot -Path $fullPath -Root $Root
            if (-not $uniquePaths.Add($fullPath)) {
                continue
            }

            $targets.Add($fullPath) | Out-Null
            if (-not (Test-Path -LiteralPath $fullPath)) {
                continue
            }

            $relativePath = Get-RelativePathCompat `
                -BasePath $Root `
                -TargetPath $fullPath
            $backupPath = Join-Path $backupDirectory $relativePath
            $backupParent = Split-Path -Parent $backupPath
            New-Item `
                -ItemType Directory `
                -Path $backupParent `
                -Force | Out-Null
            Move-Item `
                -LiteralPath $fullPath `
                -Destination $backupPath
            $entries.Add([pscustomobject]@{
                OriginalPath = $fullPath
                BackupPath = $backupPath
            }) | Out-Null
        }
    }
    catch {
        $startFailure = $_
        $rollbackErrors = [System.Collections.Generic.List[string]]::new()
        for ($index = $entries.Count - 1; $index -ge 0; $index--) {
            $entry = $entries[$index]
            try {
                $originalParent = Split-Path -Parent $entry.OriginalPath
                New-Item `
                    -ItemType Directory `
                    -Path $originalParent `
                    -Force | Out-Null
                Move-Item `
                    -LiteralPath $entry.BackupPath `
                    -Destination $entry.OriginalPath `
                    -Force
            }
            catch {
                $rollbackErrors.Add(
                    "$($entry.OriginalPath): $($_.Exception.Message)") | Out-Null
            }
        }

        try {
            Remove-Item `
                -LiteralPath $backupDirectory `
                -Recurse `
                -Force `
                -ErrorAction Stop
        }
        catch {
            $rollbackErrors.Add(
                "${backupDirectory}: $($_.Exception.Message)") | Out-Null
        }

        if ($rollbackErrors.Count -gt 0) {
            throw (
                "Could not start the artifact transaction: " +
                "$($startFailure.Exception.Message). Rollback also failed: " +
                ($rollbackErrors -join "; "))
        }

        throw $startFailure
    }

    return [pscustomobject]@{
        BackupDirectory = $backupDirectory
        Entries = @($entries)
        TargetPaths = @($targets)
    }
}

function Restore-PublishArtifactTransaction {
    param(
        [string]$Root,
        [object]$Transaction
    )

    if ($null -eq $Transaction) {
        return
    }

    Write-Step "Restoring previous canonical artifacts after publish failure"
    $restoreErrors = [System.Collections.Generic.List[string]]::new()
    foreach ($targetPath in @($Transaction.TargetPaths)) {
        try {
            Assert-UnderRoot -Path $targetPath -Root $Root
            if (Test-Path -LiteralPath $targetPath) {
                Remove-Item `
                    -LiteralPath $targetPath `
                    -Recurse `
                    -Force
            }
        }
        catch {
            $restoreErrors.Add(
                "$($targetPath): $($_.Exception.Message)") | Out-Null
        }
    }

    $entries = @($Transaction.Entries)
    for ($index = $entries.Count - 1; $index -ge 0; $index--) {
        $entry = $entries[$index]
        try {
            $originalParent = Split-Path -Parent $entry.OriginalPath
            New-Item `
                -ItemType Directory `
                -Path $originalParent `
                -Force | Out-Null
            if (-not (Test-Path -LiteralPath $entry.BackupPath)) {
                throw "backup entry is missing"
            }
            Move-Item `
                -LiteralPath $entry.BackupPath `
                -Destination $entry.OriginalPath `
                -Force
            Write-Host "Restored $($entry.OriginalPath)"
        }
        catch {
            $restoreErrors.Add(
                "$($entry.OriginalPath): $($_.Exception.Message)") | Out-Null
        }
    }

    try {
        Remove-Item `
            -LiteralPath $Transaction.BackupDirectory `
            -Recurse `
            -Force `
            -ErrorAction Stop
    }
    catch {
        if (Test-Path -LiteralPath $Transaction.BackupDirectory) {
            $restoreErrors.Add(
                "$($Transaction.BackupDirectory): $($_.Exception.Message)") | Out-Null
        }
    }

    if ($restoreErrors.Count -gt 0) {
        throw (
            "Canonical artifact rollback was incomplete: " +
            ($restoreErrors -join "; "))
    }
}

function Complete-PublishArtifactTransaction {
    param([object]$Transaction)

    if ($null -eq $Transaction) {
        return
    }

    $cleanupErrors = [System.Collections.Generic.List[string]]::new()
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            if (Test-Path -LiteralPath $Transaction.BackupDirectory) {
                Remove-Item `
                    -LiteralPath $Transaction.BackupDirectory `
                    -Recurse `
                    -Force `
                    -ErrorAction Stop
            }
            return
        }
        catch {
            $cleanupErrors.Add($_.Exception.Message) | Out-Null
            if ($attempt -lt 3) {
                Start-Sleep -Milliseconds 100
            }
        }
    }

    throw (
        "Canonical artifacts were committed, but the temporary transaction " +
        "backup could not be removed: $($Transaction.BackupDirectory). " +
        ($cleanupErrors -join "; "))
}

$root = Resolve-RepoRoot
Set-Location $root
$sourceState = Get-SourceState -Root $root
if ($sourceState.Dirty -and -not $AllowDirtySource) {
    throw (
        "Release source tree is dirty. Commit or stash all source changes " +
        "before publishing, or pass -AllowDirtySource to create an " +
        "explicitly marked internal candidate.")
}
$internalCandidate = [bool]$AllowDirtySource
if ($internalCandidate) {
    Write-Host (
        "Internal candidate mode enabled; this build is not eligible for " +
        "clean-source release promotion.") -ForegroundColor Yellow
}
$publishScopeKind = Resolve-PublishScopeKind `
    -WindowsOnly:$WindowsOnly `
    -LinuxOnly:$LinuxOnly `
    -SkipAndroid:$SkipAndroid
$manifestScope = Get-PublishManifestScope -ScopeKind $publishScopeKind

$artifacts = Resolve-PublishArtifactsDirectory `
    -Root $root `
    -CandidateName $CandidateName `
    -AllowDirtySource:$AllowDirtySource `
    -ReleaseName $ReleaseName
if (-not (Test-Path $artifacts)) {
    New-Item -ItemType Directory -Path $artifacts | Out-Null
}

$releaseVersion = Get-ReleaseVersion -Root $root
$buildStamp = [DateTimeOffset]::UtcNow.ToString("yyyyMMddHHmmss")
$manifestPath = Join-Path $artifacts "RemoteDesk-release-manifest.json"
$windowsOutput = Join-Path $artifacts "RemoteDesk-win-x64"
$windowsZip = Join-Path $artifacts "RemoteDesk-win-x64.zip"
$linuxZip = Join-Path $artifacts "RemoteDesk-linux-host.zip"
$linuxTarGz = Join-Path $artifacts "RemoteDesk-linux-host.tar.gz"
$androidProject = Join-Path $root "src\RemoteDesk.Android"
$apkRoot = Join-Path $androidProject "app\build\outputs\apk"
$androidArtifact = $null
$windowsAuthenticode = $null


Write-Step "Validating release toolchain before touching canonical artifacts"
$dotNetSdkVersion = $null
$gradleVersion = $null
$javaVersion = $null
$linuxPythonVersion = $null
$gradle = $null
if (-not $LinuxOnly) {
    $dotNetSdkVersion = Get-RemoteDeskDotNetSdkVersion
    Write-Host ".NET SDK: $dotNetSdkVersion"
}
if (-not $WindowsOnly) {
    $linuxPythonVersion = Get-RemoteDeskLinuxPythonVersion -Distro $LinuxDistro
    Write-Host "Linux Python ($LinuxDistro): $linuxPythonVersion"
}
if (-not $SkipAndroid -and -not $LinuxOnly) {
    $gradle = Get-GradleCommand -ExplicitPath $GradlePath
    $gradleVersion = Get-RemoteDeskGradleVersion -Gradle $gradle
    $javaVersion = Get-RemoteDeskJavaVersion
    Write-Host "Gradle: $gradle ($gradleVersion)"
    Write-Host "Java: $javaVersion"
}
$toolchain = [pscustomobject]@{
    dotnetSdk = $dotNetSdkVersion
    gradle = $gradleVersion
    java = $javaVersion
    linuxPython = $linuxPythonVersion
    linuxDistro = if (-not $WindowsOnly) { $LinuxDistro } else { $null }
}

if (-not $SkipTests) {
    if (-not $LinuxOnly) {
        Invoke-Checked "Running Windows tests" {
            dotnet test .\RemoteDesk.sln -c Release
        }
    }
    else {
        Write-Step "Skipping Windows tests for Linux-only package"
    }

    if (-not $WindowsOnly) {
        Invoke-Checked "Running Linux Python tests" {
            Invoke-LinuxPythonTests -Root $root -Distro $LinuxDistro
        }
    }
    else {
        Write-Step "Skipping Linux tests for Windows-only package"
    }
}
else {
    Write-Step "Skipping automated tests by request"
}


if (-not $SkipAndroid -and -not $LinuxOnly) {
    $androidVariantOutput = Join-Path `
        $apkRoot `
        $(if ($AndroidRelease) { "release" } else { "debug" })
    Remove-ArtifactPaths `
        -Root $root `
        -Paths @($androidVariantOutput) `
        -Message "Cleaning selected Android build output before compilation"

    $gradle = Get-GradleCommand -ExplicitPath $GradlePath
    $androidTask = if ($AndroidRelease) { "assembleRelease" } else { "assembleDebug" }
    $androidTasks = @($androidTask)
    if (-not $SkipTests) {
        $androidTasks += "testDebugUnitTest"
        $androidTasks += $(if ($AndroidRelease) { "lintRelease" } else { "lintDebug" })
        if ($AndroidRelease) {
            $androidTasks += "testReleaseUnitTest"
        }
    }
    Write-Host "Gradle: $gradle"

    $androidStep = if ($SkipTests) {
        "Building Android APK ($androidTask; tests skipped)"
    }
    else {
        "Building Android APK with unit tests and lint ($($androidTasks -join ', '))"
    }
    Invoke-Checked $androidStep {
        Invoke-GradleChecked `
            -Gradle $gradle `
            -ProjectPath $androidProject `
            -Tasks $androidTasks
    }

    if ($AndroidRelease -and -not $internalCandidate -and
        -not (Test-Path -LiteralPath (Join-Path $apkRoot "release\app-release.apk") -PathType Leaf)) {
        throw "Final Android release requires a signed APK; configure release signing before publishing."
    }

    $gradleJavaProcesses = @(Get-GradleJavaProcesses)
    $gradleClear = $gradleJavaProcesses.Count -eq 0
    $gradleColor = if ($gradleClear) { "Green" } else { "Yellow" }
    Write-Host "Gradle helper cleanup: $(Format-ProcessSummary $gradleJavaProcesses)" -ForegroundColor $gradleColor
}

$previousLinuxDebs = if (-not $WindowsOnly) {
    @(
        Get-ChildItem `
            -LiteralPath $artifacts `
            -Filter "remotedesk-linux-host_*_*.deb" `
            -File `
            -ErrorAction SilentlyContinue |
            Select-Object -ExpandProperty FullName)
}
else {
    @()
}
$transactionPaths = [System.Collections.Generic.List[string]]::new()
$transactionPaths.Add($manifestPath) | Out-Null
if (-not $LinuxOnly) {
    foreach ($path in @(
        $windowsOutput,
        $windowsZip,
        (Join-Path $artifacts "RemoteDesk.exe"),
        (Join-Path $artifacts "RemoteDesk.zip"),
        (Join-Path $artifacts "RemoteDesk-win-x64-updated.zip"),
        (Join-Path $artifacts "RemoteDesk-win-x64-updated"),
        (Join-Path $artifacts "RemoteDesk-win-x64-fd"),
        (Join-Path $artifacts "RemoteDesk-android-debug.apk"),
        (Join-Path $artifacts "RemoteDesk-android-release.apk"),
        (Join-Path $artifacts "RemoteDesk-android-release-unsigned.apk"))) {
        $transactionPaths.Add($path) | Out-Null
    }
}
if (-not $WindowsOnly) {
    foreach ($path in @(
        $linuxZip,
        $linuxTarGz,
        (Join-Path $artifacts "RemoteDesk-linux-host"),
        (Join-Path $artifacts "remotedesk-linux-host-deb"),
        (Join-Path $artifacts "remotedesk-build-runtime.sh"),
        (Join-Path $artifacts "remotedesk-build-targz.sh"),
        (Join-Path $artifacts "remotedesk-build-deb.sh")) +
        $previousLinuxDebs) {
        $transactionPaths.Add($path) | Out-Null
    }
}
$transaction = Start-PublishArtifactTransaction `
    -Root $root `
    -Artifacts $artifacts `
    -Paths @($transactionPaths)
$transactionCommitted = $false

try {

if (-not $LinuxOnly) {
    Assert-UnderRoot -Path $windowsOutput -Root $root

    Write-Step "Publishing Windows win-x64 single-file package"
    dotnet publish .\src\RemoteDesk\RemoteDesk.csproj `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:PublishReadyToRun=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -p:RemoteDeskBuildStamp=$buildStamp `
        -o $windowsOutput
    if ($LASTEXITCODE -ne 0) {
        throw "Publishing the required self-contained win-x64 single-file package failed with exit code $LASTEXITCODE. Remote update replaces only RemoteDesk.exe, so a framework-dependent fallback cannot be released safely."
    }
    $windowsExecutable = Join-Path $windowsOutput "RemoteDesk.exe"
    if (-not [string]::IsNullOrWhiteSpace(
            $WindowsSigningCertificateThumbprint)) {
        Write-Step "Signing Windows executable with Authenticode"
        $windowsAuthenticode =
            Set-RemoteDeskWindowsAuthenticodeSignature `
                -Path $windowsExecutable `
                -CertificateThumbprint `
                    $WindowsSigningCertificateThumbprint `
                -TimestampServer $WindowsTimestampServer
    }
    else {
        $windowsAuthenticode =
            Get-RemoteDeskWindowsAuthenticodeMetadata `
                -Path $windowsExecutable
    }
    Write-Host (
        "Windows Authenticode: status=" +
        "$($windowsAuthenticode.status), signer=" +
        "$($windowsAuthenticode.signerThumbprint), timestamped=" +
        "$($windowsAuthenticode.timestamped)")
    Copy-Item `
        -LiteralPath (Join-Path $root "THIRD-PARTY-NOTICES.md") `
        -Destination (Join-Path $windowsOutput "THIRD-PARTY-NOTICES.md") `
        -Force
    Copy-Item `
        -LiteralPath (
            Join-Path $root "scripts\Install-RemoteDeskFfmpeg.ps1") `
        -Destination (
            Join-Path $windowsOutput "Install-RemoteDeskFfmpeg.ps1") `
        -Force
    $dotNetRuntimePackVersion = Copy-DotNetRuntimePackNotices `
        -Root $root `
        -Destination $windowsOutput
    Write-Host (
        "Bundled .NET runtime pack notices: " +
        "Microsoft.NETCore.App.Runtime.win-x64/$dotNetRuntimePackVersion")
    Get-ChildItem -LiteralPath $windowsOutput -Filter "*.pdb" -File -ErrorAction SilentlyContinue |
        Remove-Item -Force

    $expectedWindowsFiles = @(
        "RemoteDesk.exe",
        "Install-RemoteDeskFfmpeg.ps1",
        "THIRD-PARTY-NOTICES.md",
        "DOTNET-RUNTIME-LICENSE.TXT",
        "DOTNET-RUNTIME-THIRD-PARTY-NOTICES.TXT")
    $windowsEntries = @(
        Get-ChildItem -LiteralPath $windowsOutput -Force)
    $unexpectedWindowsEntries = @(
        $windowsEntries |
            Where-Object {
                $_.PSIsContainer -or
                $_.Name -notin $expectedWindowsFiles
            } |
            Select-Object -ExpandProperty Name)
    $missingWindowsFiles = @(
        $expectedWindowsFiles |
            Where-Object {
                -not (Test-Path -LiteralPath (
                    Join-Path $windowsOutput $_) -PathType Leaf)
            })
    if ($unexpectedWindowsEntries.Count -gt 0 -or
        $missingWindowsFiles.Count -gt 0) {
        throw (
            "Windows canonical output must contain only: " +
            "$($expectedWindowsFiles -join ', '). " +
            "Unexpected=[$($unexpectedWindowsEntries -join ', ')]; " +
            "missing=[$($missingWindowsFiles -join ', ')].")
    }

    Assert-UnderRoot -Path $windowsZip -Root $root
    Write-Step "Creating Windows zip artifact"
    Compress-Archive -Path (Join-Path $windowsOutput "*") -DestinationPath $windowsZip -Force
}
else {
    Write-Step "Skipping Windows package for Linux-only publish"
}

if (-not $WindowsOnly) {
    Write-Step "Creating Linux host helper artifacts"
    $linuxHostArtifacts = @(New-LinuxHostArtifact -Root $root -Artifacts $artifacts -Version $releaseVersion -Distro $LinuxDistro)
}
else {
    Write-Step "Skipping Linux package for Windows-only publish"
    $linuxHostArtifacts = @()
}

if (-not $SkipAndroid -and -not $LinuxOnly) {
    if ($AndroidRelease) {
        $signedRelease = Join-Path $apkRoot "release\app-release.apk"
        $unsignedRelease = Join-Path $apkRoot "release\app-release-unsigned.apk"
        $releaseCandidates = @(
            @($signedRelease, $unsignedRelease) |
                Where-Object {
                    Test-Path -LiteralPath $_ -PathType Leaf
                })
        if ($releaseCandidates.Count -ne 1) {
            $candidateSummary = if ($releaseCandidates.Count -eq 0) {
                "none"
            }
            else {
                $releaseCandidates -join ", "
            }
            throw "Expected exactly one APK from the clean Android release output, found $($releaseCandidates.Count): $candidateSummary"
        }

        $releaseApk = $releaseCandidates[0]
        if ([string]::Equals(
                $releaseApk,
                $signedRelease,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            $androidArtifact = Join-Path $artifacts "RemoteDesk-android-release.apk"
            Copy-Item -LiteralPath $releaseApk -Destination $androidArtifact -Force
            Write-Host "Android release APK: $androidArtifact"
        }
        else {
            $androidArtifact = Join-Path $artifacts "RemoteDesk-android-release-unsigned.apk"
            Copy-Item -LiteralPath $releaseApk -Destination $androidArtifact -Force
            Write-Host "Android unsigned release APK: $androidArtifact" -ForegroundColor Yellow
            Write-Host "Configure src\RemoteDesk.Android\release-signing.properties or REMOTEDESK_ANDROID_* environment variables to create a signed release APK." -ForegroundColor Yellow
        }
    }
    else {
        $debugApk = Join-Path $apkRoot "debug\app-debug.apk"
        if (-not (Test-Path $debugApk)) {
            throw "Android debug APK was not found: $debugApk"
        }

        $androidArtifact = Join-Path $artifacts "RemoteDesk-android-debug.apk"
        Copy-Item -LiteralPath $debugApk -Destination $androidArtifact -Force
        Write-Host "Android debug APK: $androidArtifact"
    }
}

Assert-UnderRoot -Path $manifestPath -Root $root
$linuxDebArtifact = $null
if (-not $WindowsOnly) {
    $linuxDebCandidates = @(
        $linuxHostArtifacts |
            Where-Object {
                [System.IO.Path]::GetFileName([string]$_) -match
                    "^remotedesk-linux-host_[^\\]+_[^\\]+\.deb$"
            })
    if ($linuxDebCandidates.Count -ne 1) {
        throw (
            "Canonical Linux publish requires exactly one deb artifact; " +
            "found $($linuxDebCandidates.Count).")
    }

    $linuxDebArtifact = $linuxDebCandidates[0]
}

$artifactPaths = @()
if (-not $LinuxOnly) {
    $artifactPaths += @(
        (Join-Path $windowsOutput "RemoteDesk.exe"),
        $windowsZip)
}
if (-not $WindowsOnly) {
    $artifactPaths += $linuxHostArtifacts
}
if (-not $SkipAndroid -and -not $LinuxOnly) {
    if ([string]::IsNullOrWhiteSpace([string]$androidArtifact)) {
        throw "Android build completed without selecting an artifact."
    }

    $artifactPaths += $androidArtifact
}
Assert-CanonicalPublishArtifactSet `
    -ScopeKind $publishScopeKind `
    -ArtifactPaths $artifactPaths `
    -WindowsExecutable (Join-Path $windowsOutput "RemoteDesk.exe") `
    -WindowsZip $windowsZip `
    -LinuxZip $linuxZip `
    -LinuxTarGz $linuxTarGz `
    -LinuxDeb $linuxDebArtifact `
    -AndroidArtifact $androidArtifact

# Tests and packaging may take minutes. Never label mixed or changed sources
# with the fingerprint captured before the build; rollback instead.
$finalSourceState = Get-SourceState -Root $root
if ($finalSourceState.Revision -cne $sourceState.Revision -or
    $finalSourceState.Fingerprint.Hash -cne $sourceState.Fingerprint.Hash -or
    $finalSourceState.Dirty -ne $sourceState.Dirty) {
    throw "Source changed during publishing; candidate artifacts cannot be certified. Retry from a stable source tree."
}

$manifestEntries = Write-ArtifactManifest `
    -OutputFile $manifestPath `
    -Root $root `
    -ArtifactPaths $artifactPaths `
    -AndroidReleaseRequested:$AndroidRelease `
    -BuildStamp $buildStamp `
    -Scope $manifestScope `
    -SourceRevision $sourceState.Revision `
    -SourceDirty:$sourceState.Dirty `
    -InternalCandidate:$internalCandidate `
    -SourceFingerprint $sourceState.Fingerprint `
    -Toolchain $toolchain `
    -WindowsAuthenticode $windowsAuthenticode

    # The complete canonical set and manifest have passed validation. A
    # cleanup failure must not roll back an otherwise valid publication.
    $transactionCommitted = $true
    Complete-PublishArtifactTransaction -Transaction $transaction
    $transaction = $null

Write-Step "Artifacts"
Get-Item -LiteralPath (@($artifactPaths) + @($manifestPath)) -ErrorAction SilentlyContinue |
    Select-Object Name, Length, LastWriteTime |
    Format-Table -AutoSize

Write-Step "Artifact SHA256"
$manifestEntries |
    Select-Object name, sha256 |
    Format-Table -AutoSize
}
catch {
    $publishFailure = $_
    if (-not $transactionCommitted -and $null -ne $transaction) {
        try {
            Restore-PublishArtifactTransaction `
                -Root $root `
                -Transaction $transaction
        }
        catch {
            Write-Error (
                "Publish failed and canonical artifact rollback was incomplete: " +
                $_.Exception.Message)
        }
    }

    throw $publishFailure
}
