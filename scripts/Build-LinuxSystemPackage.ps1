[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$LinuxDistro = "Ubuntu-24.04"
)
$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifactsRoot = Join-Path $repoRoot "artifacts"
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $outputRoot.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "System package output must be in a named artifacts subdirectory."
}
if (-not (Test-Path -LiteralPath $outputRoot -PathType Container) -or
    ((Get-Item -LiteralPath $outputRoot).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw "Output directory must already exist and must not be a reparse point."
}
$package = Join-Path $outputRoot "RemoteDesk-linux-system-python.tar.gz"
if (Test-Path -LiteralPath $package) { throw "Refusing to overwrite an existing package." }
$staging = Join-Path $outputRoot (".system-package-" + [Guid]::NewGuid().ToString("N"))
function Convert-LocalDrivePathToWsl([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $match = [regex]::Match($full, '^([A-Za-z]):[\\/](.*)$')
    if (-not $match.Success) { throw "System package requires a local Windows drive path: $full" }
    return '/mnt/' + $match.Groups[1].Value.ToLowerInvariant() + '/' + $match.Groups[2].Value.Replace('\','/')
}
[void](New-Item -ItemType Directory -Path (Join-Path $staging "app"))
try {
    foreach ($name in @("remotedesk_linux_app.py", "remotedesk_linux_host.py",
        "remotedesk_linux_dependencies.py", "remotedesk_linux_relay.py", "remotedesk_protocol_probe.py",
        "remotedesk_linux_startup.py")) {
        Copy-Item -LiteralPath (Join-Path $repoRoot "scripts/linux/$name") -Destination (Join-Path $staging "app/$name")
    }
    Copy-Item -LiteralPath (Join-Path $repoRoot "src/RemoteDesk/Assets/RemoteDesk.png") -Destination (Join-Path $staging "app/RemoteDesk.png")
    Copy-Item -LiteralPath (Join-Path $repoRoot "scripts/linux/remotedesk-linux-app") -Destination $staging
    Copy-Item -LiteralPath (Join-Path $repoRoot "docs/Linux-SystemPackage.md") -Destination (Join-Path $staging "README.md")
    Copy-Item -LiteralPath (Join-Path $repoRoot "THIRD-PARTY-NOTICES.md") -Destination $staging
    $wslStaging = Convert-LocalDrivePathToWsl $staging
    $wslPackage = Convert-LocalDrivePathToWsl $package
    & wsl.exe -d $LinuxDistro --exec tar --create --gzip --file $wslPackage --directory $wslStaging --mode=u=rwX,go=rX .
    if ($LASTEXITCODE -ne 0) { throw "System Python package creation failed." }
    Get-FileHash -LiteralPath $package -Algorithm SHA256
} finally {
    $resolvedStaging = [IO.Path]::GetFullPath($staging)
    if (-not $resolvedStaging.StartsWith($outputRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase) -or
        ((Get-Item -LiteralPath $resolvedStaging).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing to clean an unexpected staging path."
    }
    Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
}
