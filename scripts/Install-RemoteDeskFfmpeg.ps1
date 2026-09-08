[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$DestinationDirectory = "",
    [string]$ArchivePath = ""
)

$ErrorActionPreference = "Stop"

$ffmpegVersion = "8.1.2"
$archiveUrl =
    "https://www.gyan.dev/ffmpeg/builds/packages/" +
    "ffmpeg-8.1.2-essentials_build.zip"
$archiveSha256 =
    "db580001caa24ac104c8cb856cd113a87b0a443f7bdf47d8c12b1d740584a2ec"
$ffmpegSha256 =
    "1326dde4c84ff1f96fe6b8916c5bed29e163e9b5dccf995f6f3db069d143ec5e"
$packageDirectoryName =
    "ffmpeg-8.1.2-essentials_build"

function Get-FileSha256Hex {
    param([string]$Path)

    $stream = [System.IO.File]::Open(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read)
    try {
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            $bytes = $sha256.ComputeHash($stream)
        }
        finally {
            $sha256.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }

    return [System.BitConverter]::ToString($bytes).
        Replace("-", "").
        ToLowerInvariant()
}

function Test-FfmpegListingEntry {
    param(
        [string[]]$Lines,
        [string]$Name
    )

    foreach ($line in $Lines) {
        $fields = @(
            ([string]$line).Split(
                [char[]]$null,
                [System.StringSplitOptions]::RemoveEmptyEntries))
        if ($fields -ccontains $Name) {
            return $true
        }
    }

    return $false
}

if ([string]::IsNullOrWhiteSpace($DestinationDirectory)) {
    $scriptDirectory = Split-Path -Parent $PSCommandPath
    if (-not (Test-Path -LiteralPath (
            Join-Path $scriptDirectory "RemoteDesk.exe") -PathType Leaf)) {
        throw (
            "Run this installer beside RemoteDesk.exe or pass " +
            "-DestinationDirectory explicitly.")
    }

    $DestinationDirectory = $scriptDirectory
}

$destination = [System.IO.Path]::GetFullPath(
    $DestinationDirectory)
if (-not (Test-Path -LiteralPath $destination -PathType Container)) {
    if ($PSCmdlet.ShouldProcess(
            $destination,
            "Create FFmpeg destination directory")) {
        [void][System.IO.Directory]::CreateDirectory($destination)
    }
}

$temporaryRoot = [System.IO.Path]::GetFullPath(
    [System.IO.Path]::GetTempPath()).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar)
$temporaryDirectory = Join-Path $temporaryRoot (
    "RemoteDesk-ffmpeg-install-" +
    [Guid]::NewGuid().ToString("N"))
[void][System.IO.Directory]::CreateDirectory(
    $temporaryDirectory)

try {
    $archive = if ([string]::IsNullOrWhiteSpace($ArchivePath)) {
        Join-Path $temporaryDirectory (
            "ffmpeg-$ffmpegVersion-essentials_build.zip")
    }
    else {
        [System.IO.Path]::GetFullPath($ArchivePath)
    }

    if ([string]::IsNullOrWhiteSpace($ArchivePath)) {
        Write-Host "Downloading FFmpeg $ffmpegVersion from $archiveUrl"
        Invoke-WebRequest `
            -UseBasicParsing `
            -Uri $archiveUrl `
            -OutFile $archive
    }
    elseif (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
        throw "FFmpeg archive was not found: $archive"
    }

    $actualArchiveSha256 = Get-FileSha256Hex -Path $archive
    if ($actualArchiveSha256 -cne $archiveSha256) {
        throw (
            "FFmpeg archive SHA-256 mismatch. Expected " +
            "$archiveSha256, got $actualArchiveSha256.")
    }

    $expanded = Join-Path $temporaryDirectory "expanded"
    Expand-Archive `
        -LiteralPath $archive `
        -DestinationPath $expanded `
        -Force
    $packageRoot = Join-Path $expanded $packageDirectoryName
    $sourceExecutable =
        Join-Path $packageRoot "bin\ffmpeg.exe"
    $sourceLicense =
        Join-Path $packageRoot "LICENSE"
    $sourceReadme =
        Join-Path $packageRoot "README.txt"
    foreach ($requiredPath in @(
            $sourceExecutable,
            $sourceLicense,
            $sourceReadme)) {
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "FFmpeg package is missing: $requiredPath"
        }
    }

    $actualFfmpegSha256 =
        Get-FileSha256Hex -Path $sourceExecutable
    if ($actualFfmpegSha256 -cne $ffmpegSha256) {
        throw (
            "ffmpeg.exe SHA-256 mismatch. Expected " +
            "$ffmpegSha256, got $actualFfmpegSha256.")
    }

    $filterLines = @(
        & $sourceExecutable -hide_banner -filters 2>&1 |
            ForEach-Object { [string]$_ })
    if ($LASTEXITCODE -ne 0 -or
        -not (Test-FfmpegListingEntry `
            -Lines $filterLines `
            -Name "gfxcapture")) {
        throw (
            "The verified FFmpeg package does not expose the required " +
            "gfxcapture filter.")
    }

    $encoderLines = @(
        & $sourceExecutable -hide_banner -encoders 2>&1 |
            ForEach-Object { [string]$_ })
    if ($LASTEXITCODE -ne 0) {
        throw "FFmpeg encoder capability probe failed."
    }

    $hardwareEncoders = @(
        "h264_nvenc",
        "h264_mf",
        "h264_qsv",
        "h264_amf") |
        Where-Object {
            Test-FfmpegListingEntry `
                -Lines $encoderLines `
                -Name $_
        }
    if ($hardwareEncoders.Count -eq 0) {
        throw (
            "The verified FFmpeg package exposes no supported Windows " +
            "hardware H.264 encoder.")
    }

    $targetExecutable =
        Join-Path $destination "ffmpeg.exe"
    $targetLicense =
        Join-Path $destination "FFMPEG-LICENSE.txt"
    $targetReadme =
        Join-Path $destination "FFMPEG-README.txt"
    $targetManifest =
        Join-Path $destination "FFMPEG-DEPENDENCY.json"
    if ($PSCmdlet.ShouldProcess(
            $destination,
            "Install verified FFmpeg $ffmpegVersion companion")) {
        Copy-Item `
            -LiteralPath $sourceExecutable `
            -Destination $targetExecutable `
            -Force
        Copy-Item `
            -LiteralPath $sourceLicense `
            -Destination $targetLicense `
            -Force
        Copy-Item `
            -LiteralPath $sourceReadme `
            -Destination $targetReadme `
            -Force

        $dependency = [ordered]@{
            name = "Gyan FFmpeg release essentials"
            version = $ffmpegVersion
            archiveUrl = $archiveUrl
            archiveSha256 = $archiveSha256
            executableSha256 = $ffmpegSha256
            license = "GPLv3"
            installedAtUtc =
                [DateTimeOffset]::UtcNow.ToString("O")
            hardwareH264Encoders =
                @($hardwareEncoders)
            requiredFilter = "gfxcapture"
        }
        $dependency |
            ConvertTo-Json -Depth 4 |
            Set-Content `
                -LiteralPath $targetManifest `
                -Encoding UTF8

        if ((Get-FileSha256Hex -Path $targetExecutable) -cne
                $ffmpegSha256) {
            throw "Installed ffmpeg.exe failed final SHA-256 verification."
        }
    }

    [pscustomobject]@{
        Version = $ffmpegVersion
        Destination = $destination
        ExecutableSha256 = $ffmpegSha256
        GfxCapture = $true
        HardwareH264Encoders =
            @($hardwareEncoders) -join ","
        License = "GPLv3"
        RestartRemoteDesk = $true
    }
    Write-Host (
        "FFmpeg companion installed. Restart RemoteDesk so the running " +
        "process refreshes its verified executable list.")
}
finally {
    $resolvedTemporaryDirectory =
        [System.IO.Path]::GetFullPath($temporaryDirectory)
    if ($resolvedTemporaryDirectory.StartsWith(
            $temporaryRoot +
                [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedTemporaryDirectory)) {
        Remove-Item `
            -LiteralPath $resolvedTemporaryDirectory `
            -Recurse `
            -Force `
            -ErrorAction SilentlyContinue
    }
}
