function Get-RemoteDeskSourceFingerprint {
    param([string]$Root)

    $git = Get-Command "git" -ErrorAction SilentlyContinue
    if ($null -eq $git) {
        throw "Cannot fingerprint release source because git is unavailable."
    }

    try {
        $pathOutput = @(
            & $git.Source `
                -c core.quotepath=false `
                -C $Root `
                ls-files --cached --others --exclude-standard 2>$null)
        $pathExitCode = $LASTEXITCODE
    }
    catch {
        throw "Cannot enumerate release source files: $($_.Exception.Message)"
    }

    if ($pathExitCode -ne 0) {
        throw "Cannot enumerate release source files; git exited with code $pathExitCode."
    }

    [string[]]$paths = @(
        $pathOutput |
            ForEach-Object {
                ([string]$_).Replace("\", "/")
            } |
            Where-Object {
                -not [string]::IsNullOrWhiteSpace($_) -and
                -not $_.StartsWith(
                    "artifacts/",
                    [System.StringComparison]::OrdinalIgnoreCase) -and
                -not $_.StartsWith(
                    ".claude/worktrees/",
                    [System.StringComparison]::OrdinalIgnoreCase) -and
                -not $_.EndsWith(
                    ".diff",
                    [System.StringComparison]::OrdinalIgnoreCase)
            })
    [System.Array]::Sort(
        $paths,
        [System.StringComparer]::Ordinal)

    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    $hashStream = [System.Security.Cryptography.CryptoStream]::new(
        [System.IO.Stream]::Null,
        $sha256,
        [System.Security.Cryptography.CryptoStreamMode]::Write)
    $writer = [System.IO.BinaryWriter]::new(
        $hashStream,
        [System.Text.UTF8Encoding]::new($false, $true),
        $true)
    try {
        $writer.Write("RemoteDesk source fingerprint v1")
        foreach ($relativePath in $paths) {
            $writer.Write($relativePath)
            $fullPath = Join-Path $Root $relativePath
            $exists = Test-Path -LiteralPath $fullPath -PathType Leaf
            $writer.Write($exists)
            if (-not $exists) {
                continue
            }

            $item = Get-Item -LiteralPath $fullPath
            $writer.Write([int64]$item.Length)
            $writer.Flush()
            $input = [System.IO.File]::Open(
                $item.FullName,
                [System.IO.FileMode]::Open,
                [System.IO.FileAccess]::Read,
                [System.IO.FileShare]::Read)
            try {
                $input.CopyTo($hashStream)
            }
            finally {
                $input.Dispose()
            }
        }

        $writer.Flush()
        $hashStream.FlushFinalBlock()
        $hashText = [System.BitConverter]::ToString(
            $sha256.Hash).Replace("-", "").ToLowerInvariant()
    }
    finally {
        $writer.Dispose()
        $hashStream.Dispose()
        $sha256.Dispose()
    }

    return [pscustomobject]@{
        Version = 1
        Algorithm = "SHA256"
        Hash = $hashText
        FileCount = $paths.Count
    }
}

function Get-RemoteDeskDotNetSdkVersion {
    $dotnet = Get-Command "dotnet" -ErrorAction SilentlyContinue
    if ($null -eq $dotnet) {
        throw ".NET SDK was not found in PATH."
    }

    $output = @(& $dotnet.Source --version 2>&1)
    $exitCode = $LASTEXITCODE
    $version = ([string](
        $output | Select-Object -First 1)).Trim()
    if ($exitCode -ne 0 -or
        $version -notmatch "^\d+\.\d+\.\d+") {
        $detail = (($output | ForEach-Object { [string]$_ }) -join " ").Trim()
        throw "Cannot resolve the required .NET SDK: $detail"
    }

    return $version
}

function Get-RemoteDeskGradleVersion {
    param([string]$Gradle)

    if ([string]::IsNullOrWhiteSpace($Gradle) -or
        -not (Test-Path -LiteralPath $Gradle -PathType Leaf)) {
        throw "Gradle executable is missing: $Gradle"
    }

    $output = @(& $Gradle --version 2>&1)
    $exitCode = $LASTEXITCODE
    $versionLine = @(
        $output |
            ForEach-Object { ([string]$_).Trim() } |
            Where-Object { $_ -match "^Gradle\s+(.+)$" } |
            Select-Object -First 1)
    if ($exitCode -ne 0 -or $versionLine.Count -ne 1) {
        $detail = (($output | ForEach-Object { [string]$_ }) -join " ").Trim()
        throw "Cannot resolve Gradle version: $detail"
    }

    return ([regex]::Match(
        $versionLine[0],
        "^Gradle\s+(.+)$")).Groups[1].Value.Trim()
}

function Get-RemoteDeskJavaVersion {
    $java = Get-Command "java" -ErrorAction SilentlyContinue
    if ($null -eq $java) {
        throw "Java was not found in PATH."
    }

    $output = @(& $java.Source -version 2>&1)
    $exitCode = $LASTEXITCODE
    $version = ([string](
        $output | Select-Object -First 1)).Trim()
    if ($exitCode -ne 0 -or
        [string]::IsNullOrWhiteSpace($version)) {
        $detail = (($output | ForEach-Object { [string]$_ }) -join " ").Trim()
        throw "Cannot resolve Java version: $detail"
    }

    return $version
}

function Assert-RemoteDeskLinuxDistro {
    param([string]$Distro)

    if ([string]::IsNullOrWhiteSpace($Distro)) {
        throw "A WSL distro is required for the Linux release scope."
    }

    if ($Distro -match '[\r\n]') {
        throw "WSL distro names cannot contain newlines."
    }

    return $Distro.Trim()
}

function Get-RemoteDeskLinuxPythonVersion {
    param([string]$Distro = "Ubuntu-24.04")

    if ($null -eq (Get-Command "wsl.exe" -ErrorAction SilentlyContinue)) {
        throw "wsl.exe is required for the Linux release scope."
    }
    $Distro = Assert-RemoteDeskLinuxDistro -Distro $Distro

    $wslArguments = @("-d", $Distro, "--", "python3", "--version")
    $output = @(& wsl.exe @wslArguments 2>&1)
    $exitCode = $LASTEXITCODE
    $versionLines = @(
        $output |
            ForEach-Object { ([string]$_).Trim() } |
            Where-Object { $_ -match "^Python\s+\d+\.\d+\.\d+" } |
            Select-Object -First 1)
    if ($exitCode -ne 0 -or $versionLines.Count -ne 1) {
        $detail = (($output | ForEach-Object { [string]$_ }) -join " ").Trim()
        throw "Cannot resolve WSL Python version: $detail"
    }

    return $versionLines[0]
}

function Get-RemoteDeskCertificateSha256 {
    param(
        [System.Security.Cryptography.X509Certificates.X509Certificate2]
        $Certificate
    )

    if ($null -eq $Certificate) {
        return $null
    }

    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [System.BitConverter]::ToString(
            $sha256.ComputeHash($Certificate.RawData)).Replace(
                "-",
                "").ToUpperInvariant()
    }
    finally {
        $sha256.Dispose()
    }
}

function Get-RemoteDeskWindowsAuthenticodeMetadata {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Cannot inspect missing Windows executable signature: $Path"
    }

    $signature = Get-AuthenticodeSignature `
        -FilePath $Path `
        -ErrorAction Stop
    $signer = $signature.SignerCertificate
    $timestampSigner = $signature.TimeStamperCertificate
    return [pscustomobject]@{
        status = [string]$signature.Status
        signerSubject = if ($null -eq $signer) {
            $null
        }
        else {
            [string]$signer.Subject
        }
        signerThumbprint = if ($null -eq $signer) {
            $null
        }
        else {
            ([string]$signer.Thumbprint).ToUpperInvariant()
        }
        signerCertificateSha256 =
            Get-RemoteDeskCertificateSha256 -Certificate $signer
        timestamped = $null -ne $timestampSigner
        timestampSignerSubject = if ($null -eq $timestampSigner) {
            $null
        }
        else {
            [string]$timestampSigner.Subject
        }
        timestampSignerThumbprint = if ($null -eq $timestampSigner) {
            $null
        }
        else {
            ([string]$timestampSigner.Thumbprint).ToUpperInvariant()
        }
    }
}

function Resolve-RemoteDeskWindowsSigningCertificate {
    param([string]$Thumbprint)

    $normalized = ([string]$Thumbprint -replace '\s', '').ToUpperInvariant()
    if ($normalized -cnotmatch '^[0-9A-F]{40}$') {
        throw (
            "Windows signing certificate thumbprint must contain exactly " +
            "40 hexadecimal characters.")
    }

    $candidates = @(
        foreach ($storePath in @(
            "Cert:\CurrentUser\My\$normalized",
            "Cert:\LocalMachine\My\$normalized")) {
            if (Test-Path -LiteralPath $storePath -PathType Leaf) {
                Get-Item -LiteralPath $storePath -ErrorAction Stop
            }
        })
    $privateKeyCandidates = @(
        $candidates | Where-Object { $_.HasPrivateKey })
    if ($privateKeyCandidates.Count -ne 1) {
        throw (
            "Expected exactly one private-key code-signing certificate " +
            "with thumbprint $normalized in CurrentUser/My or " +
            "LocalMachine/My; found $($privateKeyCandidates.Count).")
    }

    $certificate = $privateKeyCandidates[0]
    $now = [DateTime]::UtcNow
    if ($certificate.NotBefore.ToUniversalTime() -gt $now -or
        $certificate.NotAfter.ToUniversalTime() -lt $now) {
        throw "Windows signing certificate $normalized is not currently valid."
    }

    $enhancedKeyUsage = @(
        $certificate.Extensions |
            Where-Object { $_.Oid.Value -eq '2.5.29.37' } |
            Select-Object -First 1)
    if ($enhancedKeyUsage.Count -eq 1) {
        $codeSigningOid = '1.3.6.1.5.5.7.3.3'
        $allowsCodeSigning = @(
            $enhancedKeyUsage[0].EnhancedKeyUsages |
                Where-Object { $_.Value -eq $codeSigningOid }).Count -gt 0
        if (-not $allowsCodeSigning) {
            throw "Windows signing certificate $normalized does not allow code signing."
        }
    }

    return $certificate
}

function Set-RemoteDeskWindowsAuthenticodeSignature {
    param(
        [string]$Path,
        [string]$CertificateThumbprint,
        [string]$TimestampServer
    )

    $timestampUri = $null
    if (-not [Uri]::TryCreate(
            $TimestampServer,
            [UriKind]::Absolute,
            [ref]$timestampUri) -or
        $timestampUri.Scheme -notin @('http', 'https')) {
        throw (
            "A valid HTTP(S) Authenticode timestamp server URL is required " +
            "when Windows signing is enabled.")
    }

    $certificate = Resolve-RemoteDeskWindowsSigningCertificate `
        -Thumbprint $CertificateThumbprint
    $result = Set-AuthenticodeSignature `
        -FilePath $Path `
        -Certificate $certificate `
        -HashAlgorithm SHA256 `
        -TimestampServer $timestampUri.AbsoluteUri `
        -ErrorAction Stop
    if ($result.Status -ne
        [System.Management.Automation.SignatureStatus]::Valid) {
        throw (
            "Windows Authenticode signing did not produce a trusted valid " +
            "signature: $($result.Status) $($result.StatusMessage)")
    }

    $metadata = Get-RemoteDeskWindowsAuthenticodeMetadata -Path $Path
    $normalized = ($CertificateThumbprint -replace '\s', '').ToUpperInvariant()
    if ($metadata.status -cne 'Valid' -or
        $metadata.signerThumbprint -cne $normalized -or
        -not $metadata.timestamped) {
        throw (
            "Windows Authenticode post-sign verification failed: " +
            "status=$($metadata.status), signer=$($metadata.signerThumbprint), " +
            "timestamped=$($metadata.timestamped).")
    }

    return $metadata
}
