[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string]$Tag,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string]$ExpectedRemoteExeSha256,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$KnownHostsWsl,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9_][A-Za-z0-9._-]*@[A-Za-z0-9][A-Za-z0-9.-]*$')]
    [string]$SshTarget,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z]:\\Users\\[A-Za-z0-9_][A-Za-z0-9 ._-]*$')]
    [string]$RemoteWindowsProfile,

    [ValidateRange(1024, 65535)]
    [int]$Port = 40565,

    [ValidateRange(5, 120)]
    [int]$Seconds = 15,

    [ValidatePattern('^\\\\\.\\DISPLAY[0-9]+$')]
    [string]$TargetId = '\\.\DISPLAY5',

    [ValidateRange(1, 8192)]
    [int]$Width = 3840,

    [ValidateRange(1, 8192)]
    [int]$Height = 2160,

    [ValidateRange(1, 8192)]
    [int]$SourceWidth = 3840,

    [ValidateRange(1, 8192)]
    [int]$SourceHeight = 2160,

    [ValidateRange(1, 120)]
    [int]$ExpectedFps = 60,

    [ValidateRange(1, 120)]
    [int]$MinimumFps = 57,

    [string]$ResultsDirectory = "",

    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if ($MinimumFps -gt $ExpectedFps) {
    throw '-MinimumFps cannot exceed -ExpectedFps.'
}

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($ResultsDirectory)) {
    $ResultsDirectory = Join-Path $root 'artifacts\entity-short'
}
elseif (-not [System.IO.Path]::IsPathFullyQualified($ResultsDirectory)) {
    $ResultsDirectory = Join-Path $root $ResultsDirectory
}
$ResultsDirectory = [System.IO.Path]::GetFullPath($ResultsDirectory)
New-Item -ItemType Directory -Path $ResultsDirectory -Force |
    Out-Null

$motionSourcePath =
    Join-Path $root 'experiments\RemoteDesk-DesktopMotion.ps1'
if (-not (Test-Path -LiteralPath $motionSourcePath -PathType Leaf)) {
    throw "The desktop-motion script is missing: $motionSourcePath"
}
$motionSourceSha256 =
    (Get-FileHash -LiteralPath $motionSourcePath -Algorithm SHA256).
        Hash.ToLowerInvariant()
$motionRunId = [Guid]::NewGuid().ToString('N')
$motionDurationSeconds = $Seconds + 20
$remoteSshUser, $remoteHostAddress = $SshTarget.Split('@', 2)
$motionRemoteWslPath =
    "/home/$remoteSshUser/.remotedesk-motion-$motionRunId.ps1"
$motionWindowsStem =
    "$RemoteWindowsProfile\AppData\Local\Temp\RemoteDeskMotion-$motionRunId"
$motionWindowsScriptPath = "$motionWindowsStem.ps1"
$motionWindowsReadyPath = "$motionWindowsStem.ready.json"
$motionWindowsResultPath = "$motionWindowsStem.result.txt"
$motionWindowsStdoutPath = "$motionWindowsStem.stdout.log"
$motionRunnerStdoutPath =
    Join-Path $ResultsDirectory "$Tag.motion.runner.log"
$motionRunnerStderrPath =
    Join-Path $ResultsDirectory "$Tag.motion.runner.stderr.log"
$motionStdoutEvidencePath =
    Join-Path $ResultsDirectory "$Tag.motion.stdout.log"
$motionResultEvidencePath =
    Join-Path $ResultsDirectory "$Tag.motion.result.txt"

$remotePreflightScript = @"
`$ErrorActionPreference = 'Stop'
`$ProgressPreference = 'SilentlyContinue'
`$expectedHash = '$($ExpectedRemoteExeSha256.ToLowerInvariant())'
`$expectedPort = $Port
`$expectedTarget = '$TargetId'
`$exe = '$RemoteWindowsProfile\Desktop\RemoteDesk.exe'
`$ffmpeg = '$RemoteWindowsProfile\Desktop\ffmpeg.exe'
`$settingsPath =
    '$RemoteWindowsProfile\AppData\Roaming\RemoteDesk\settings.json'
`$listener = @(
    Get-NetTCPConnection -State Listen -LocalPort `$expectedPort -ErrorAction SilentlyContinue)
if (`$listener.Count -ne 1) {
    throw "Expected exactly one TCP listener on port `$expectedPort."
}
`$ownerPid = [int]`$listener[0].OwningProcess
`$process =
    Get-CimInstance Win32_Process -Filter "ProcessId=`$ownerPid"
if ([string]`$process.ExecutablePath -cne `$exe) {
    throw "Port `$expectedPort is not owned by the canonical executable."
}
`$actualHash =
    (Get-FileHash -LiteralPath `$exe -Algorithm SHA256).
        Hash.ToLowerInvariant()
if (`$actualHash -cne `$expectedHash) {
    throw "Remote executable SHA-256 does not match the release candidate."
}
`$settings =
    Get-Content -LiteralPath `$settingsPath -Raw |
    ConvertFrom-Json
if ([int]`$settings.Host.Port -ne `$expectedPort -or
    [string]`$settings.Host.CaptureTargetId -cne `$expectedTarget -or
    [int]`$settings.Host.Fps -ne $ExpectedFps -or
    [int]`$settings.Host.ScalePercent -ne 100) {
    throw "Remote host settings do not match the 4K60 entity profile."
}
if (-not (Test-Path -LiteralPath `$ffmpeg -PathType Leaf)) {
    throw 'The verified FFmpeg companion is missing.'
}
`$filters =
    & `$ffmpeg -hide_banner -filters 2>&1 |
    ForEach-Object { [string]`$_ } |
    Out-String
`$encoders =
    & `$ffmpeg -hide_banner -encoders 2>&1 |
    ForEach-Object { [string]`$_ } |
    Out-String
if (`$filters -notmatch '(?m)\s+gfxcapture\s' -or
    `$encoders -notmatch '(?m)\s+h264_nvenc\s') {
    throw 'FFmpeg does not expose gfxcapture and h264_nvenc.'
}
`$result = [pscustomobject]@{
    computerName = `$env:COMPUTERNAME
    port = `$expectedPort
    processId = `$ownerPid
    sessionId = [int]`$process.SessionId
    executable = `$exe
    executableSha256 = `$actualHash
    target = [string]`$settings.Host.CaptureTargetId
    fps = [int]`$settings.Host.Fps
    scalePercent = [int]`$settings.Host.ScalePercent
    adaptiveQuality = [bool]`$settings.Host.AdaptiveQuality
    ffmpegSha256 =
        (Get-FileHash -LiteralPath `$ffmpeg -Algorithm SHA256).
            Hash.ToLowerInvariant()
    gfxcapture = `$true
    h264Nvenc = `$true
}
`$json = `$result | ConvertTo-Json -Compress
[Console]::Out.WriteLine(
    'RDPREFLIGHT:' +
    [Convert]::ToBase64String(
        [Text.Encoding]::UTF8.GetBytes(`$json)))
"@

function Invoke-RemoteEncodedPowerShell {
    param(
        [Parameter(Mandatory)]
        [string]$Script,

        [Parameter(Mandatory)]
        [string]$Marker
    )

    $encoded = [Convert]::ToBase64String(
        [Text.Encoding]::Unicode.GetBytes($Script))
    $output = @(
        & wsl.exe `
            -d Ubuntu-24.04 `
            --exec ssh `
            -p 2222 `
            -o "UserKnownHostsFile=$KnownHostsWsl" `
            -o StrictHostKeyChecking=yes `
            -o BatchMode=yes `
            $SshTarget `
            /mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe `
            -NoProfile `
            -NonInteractive `
            -EncodedCommand $encoded 2>$null)
    if ($LASTEXITCODE -ne 0) {
        throw "Remote PowerShell command failed for marker $Marker."
    }

    $markerLine = $output |
        ForEach-Object { [string]$_ } |
        Where-Object {
            $_.StartsWith($Marker, [StringComparison]::Ordinal)
        } |
        Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($markerLine)) {
        throw "Remote marker $Marker was not returned."
    }

    return $markerLine.Substring($Marker.Length)
}

function ConvertTo-WslPath {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $output = @(
        & wsl.exe `
            -d Ubuntu-24.04 `
            --exec wslpath `
            -a `
            -u `
            -- $Path 2>$null)
    if ($LASTEXITCODE -ne 0 -or $output.Count -ne 1) {
        throw "Could not translate the path for WSL: $Path"
    }
    return ([string]$output[0]).Trim()
}

function Copy-MotionScriptToRemoteWsl {
    $localWslPath = ConvertTo-WslPath -Path $motionSourcePath
    & wsl.exe `
        -d Ubuntu-24.04 `
        --exec ssh `
        -p 2222 `
        -o "UserKnownHostsFile=$KnownHostsWsl" `
        -o StrictHostKeyChecking=yes `
        -o BatchMode=yes `
        $SshTarget `
        test '!' -e $motionRemoteWslPath 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw (
            'Refusing to overwrite the remote WSL motion file: ' +
            $motionRemoteWslPath)
    }

    & wsl.exe `
        -d Ubuntu-24.04 `
        --exec scp `
        -q `
        -P 2222 `
        -o "UserKnownHostsFile=$KnownHostsWsl" `
        -o StrictHostKeyChecking=yes `
        -o BatchMode=yes `
        -- `
        $localWslPath `
        "${SshTarget}:$motionRemoteWslPath" 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw 'Uploading the desktop-motion script to remote WSL failed.'
    }
}

function ConvertFrom-Base64Json {
    param(
        [Parameter(Mandatory)]
        [string]$Base64
    )

    $json =
        [Text.Encoding]::UTF8.GetString(
            [Convert]::FromBase64String($Base64))
    return $json | ConvertFrom-Json -ErrorAction Stop
}

function Write-Utf8NoBomText {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [AllowEmptyString()]
        [string]$Text
    )

    [IO.File]::WriteAllText(
        $Path,
        $Text,
        [Text.UTF8Encoding]::new($false))
}

function ConvertFrom-MotionResult {
    param(
        [Parameter(Mandatory)]
        [string]$Result
    )

    $parts = @($Result.Trim() -split '\s+')
    if ($parts.Count -lt 2 -or $parts[0] -cne 'MOTION_RESULT') {
        throw 'The motion result does not begin with MOTION_RESULT.'
    }
    $values = @{}
    foreach ($part in $parts | Select-Object -Skip 1) {
        $pair = @($part -split '=', 2)
        if ($pair.Count -ne 2 -or $values.ContainsKey($pair[0])) {
            throw "Malformed or duplicate motion token '$part'."
        }
        $values[$pair[0]] = $pair[1]
    }
    foreach ($required in @(
        'device',
        'source',
        'timerHz',
        'tickP95Ms'
    )) {
        if (-not $values.ContainsKey($required)) {
            throw "The motion result omitted '$required'."
        }
    }

    $timerHz = 0.0
    $tickP95Ms = 0.0
    $style = [Globalization.NumberStyles]::Float
    $culture = [Globalization.CultureInfo]::InvariantCulture
    if (-not [double]::TryParse(
            [string]$values.timerHz,
            $style,
            $culture,
            [ref]$timerHz) -or
        -not [double]::TryParse(
            [string]$values.tickP95Ms,
            $style,
            $culture,
            [ref]$tickP95Ms)) {
        throw 'The motion timerHz or tickP95Ms value is not numeric.'
    }

    return [pscustomobject]@{
        device = [string]$values.device
        source = [string]$values.source
        timerHz = $timerHz
        tickP95Ms = $tickP95Ms
    }
}

$preflightBase64 = Invoke-RemoteEncodedPowerShell `
    -Script $remotePreflightScript `
    -Marker 'RDPREFLIGHT:'
$preflight = ConvertFrom-Base64Json -Base64 $preflightBase64
$preflightPath =
    Join-Path $ResultsDirectory "$Tag.preflight.json"
$preflight |
    ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath $preflightPath -Encoding UTF8
Write-Host (
    "Preflight passed: $($preflight.computerName) " +
    "$($preflight.target)@$($preflight.fps), " +
    "session $($preflight.sessionId), port $($preflight.port), exe " +
    "$($preflight.executableSha256.Substring(0, 12))...")

$testProject =
    Join-Path $root 'tests\RemoteDesk.Tests\RemoteDesk.Tests.csproj'
$filter =
    'FullyQualifiedName=RemoteDesk.Tests.' +
    'WindowsRemoteViewerRealMachineTests.' +
    'ForceH264MaintainsUdpFeedbackAndFecNegotiationForThirtySeconds'
$stdoutPath =
    Join-Path $ResultsDirectory "$Tag.stdout.log"

if (-not $SkipBuild) {
    & dotnet build $testProject -c Release --nologo
    if ($LASTEXITCODE -ne 0) {
        throw 'Release test build failed.'
    }
}

$secretScript = @'
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Add-Type -AssemblyName System.Security
$settings =
    Get-Content -LiteralPath (
        Join-Path $env:APPDATA 'RemoteDesk\settings.json') -Raw |
    ConvertFrom-Json
$protected = [Convert]::FromBase64String(
    [string]$settings.Host.ProtectedPassword)
$entropy = [Text.Encoding]::UTF8.GetBytes(
    'RemoteDesk.HostPassword.v1')
$plain = $null
try {
    $plain =
        [Security.Cryptography.ProtectedData]::Unprotect(
            $protected,
            $entropy,
            [Security.Cryptography.DataProtectionScope]::CurrentUser)
    [Console]::Out.WriteLine(
        'RDSECRET:' +
        [Convert]::ToBase64String($plain))
}
finally {
    if ($null -ne $plain) {
        [Array]::Clear($plain, 0, $plain.Length)
    }
    [Array]::Clear($protected, 0, $protected.Length)
    [Array]::Clear($entropy, 0, $entropy.Length)
}
'@

$secretBase64 = Invoke-RemoteEncodedPowerShell `
    -Script $secretScript `
    -Marker 'RDSECRET:'
$secretBytes = [Convert]::FromBase64String($secretBase64)
$password = [Text.Encoding]::UTF8.GetString($secretBytes)
[Array]::Clear($secretBytes, 0, $secretBytes.Length)
$secretBytes = $null
$secretBase64 = $null

$environmentNames = @(
    'REMOTEDESK_REAL_MACHINE_TESTS',
    'REMOTEDESK_REAL_MACHINE_HOST',
    'REMOTEDESK_REAL_MACHINE_PASSWORD',
    'REMOTEDESK_REAL_MACHINE_PORT',
    'REMOTEDESK_REAL_MACHINE_TARGET_ID',
    'REMOTEDESK_REAL_MACHINE_EXPECTED_WIDTH',
    'REMOTEDESK_REAL_MACHINE_EXPECTED_HEIGHT',
    'REMOTEDESK_REAL_MACHINE_SOURCE_WIDTH',
    'REMOTEDESK_REAL_MACHINE_SOURCE_HEIGHT',
    'REMOTEDESK_REAL_MACHINE_EXPECTED_FPS',
    'REMOTEDESK_REAL_MACHINE_MINIMUM_FPS',
    'REMOTEDESK_REAL_MACHINE_OBSERVATION_SECONDS'
)

$motionWslWindowsPath =
    "\\wsl.localhost\Ubuntu-24.04\home\$remoteSshUser\" +
    ".remotedesk-motion-$motionRunId.ps1"
$motionBootstrapScript = @"
`$ErrorActionPreference = 'Stop'
`$ProgressPreference = 'SilentlyContinue'
`$source = '$motionWslWindowsPath'
`$script = '$motionWindowsScriptPath'
`$ready = '$motionWindowsReadyPath'
`$result = '$motionWindowsResultPath'
`$stdout = '$motionWindowsStdoutPath'
`$paths = @(`$script, `$ready, `$result, `$stdout)
foreach (`$path in `$paths) {
    if ([IO.File]::Exists(`$path)) {
        throw "Refusing to overwrite motion file '`$path'."
    }
}
Copy-Item -LiteralPath `$source -Destination `$script
`$hash =
    (Get-FileHash -LiteralPath `$script -Algorithm SHA256).
        Hash.ToLowerInvariant()
if (`$hash -cne '$motionSourceSha256') {
    throw 'The WSL-to-Windows motion copy changed SHA-256.'
}
`$utf8 = [Text.UTF8Encoding]::new(`$false)
`$state = [ordered]@{
    processId = `$PID
    sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
    interactive = [Environment]::UserInteractive
}
[IO.File]::WriteAllText(
    `$ready,
    (`$state | ConvertTo-Json -Compress),
    `$utf8)
try {
    `$lines = @(
        & `$script -Seconds $motionDurationSeconds -DeviceName '$TargetId' -ResultPath `$result 2>&1 |
        ForEach-Object { [string]`$_ })
    [IO.File]::WriteAllLines(`$stdout, `$lines, `$utf8)
}
catch {
    [IO.File]::WriteAllText(
        `$stdout,
        (`$_.Exception.ToString() + [Environment]::NewLine),
        `$utf8)
    throw
}
finally {
    if ([IO.File]::Exists(`$script)) {
        Remove-Item -LiteralPath `$script -Force
    }
}
"@
$motionBootstrapEncoded =
    [Convert]::ToBase64String(
        [Text.Encoding]::Unicode.GetBytes($motionBootstrapScript))

$motionReadyReadScript = @"
`$path = '$motionWindowsReadyPath'
`$valueBase64 = if ([IO.File]::Exists(`$path)) {
    [Convert]::ToBase64String(
        [IO.File]::ReadAllBytes(`$path))
}
else {
    ''
}
`$json = [ordered]@{
    exists = -not [string]::IsNullOrWhiteSpace(`$valueBase64)
    valueBase64 = `$valueBase64
} | ConvertTo-Json -Compress
[Console]::Out.WriteLine(
    'RDMOTIONREADY:' +
    [Convert]::ToBase64String(
        [Text.Encoding]::UTF8.GetBytes(`$json)))
"@

$motionEvidenceReadScript = @"
`$resultPath = '$motionWindowsResultPath'
`$stdoutPath = '$motionWindowsStdoutPath'
`$json = [ordered]@{
    resultExists = [IO.File]::Exists(`$resultPath)
    stdoutExists = [IO.File]::Exists(`$stdoutPath)
    resultBase64 = if ([IO.File]::Exists(`$resultPath)) {
        [Convert]::ToBase64String(
            [IO.File]::ReadAllBytes(`$resultPath))
    } else { '' }
    stdoutBase64 = if ([IO.File]::Exists(`$stdoutPath)) {
        [Convert]::ToBase64String(
            [IO.File]::ReadAllBytes(`$stdoutPath))
    } else { '' }
} | ConvertTo-Json -Compress
[Console]::Out.WriteLine(
    'RDMOTIONEVIDENCE:' +
    [Convert]::ToBase64String(
        [Text.Encoding]::UTF8.GetBytes(`$json)))
"@

$motionCleanupScript = @"
`$paths = @(
    '$motionWindowsScriptPath',
    '$motionWindowsReadyPath',
    '$motionWindowsResultPath',
    '$motionWindowsStdoutPath'
)
foreach (`$path in `$paths) {
    if ([IO.File]::Exists(`$path)) {
        Remove-Item -LiteralPath `$path -Force -ErrorAction SilentlyContinue
    }
}
`$remaining = @(`$paths | Where-Object { [IO.File]::Exists(`$_) })
`$json = [ordered]@{
    paths = `$paths
    remaining = `$remaining
} | ConvertTo-Json -Compress
[Console]::Out.WriteLine(
    'RDMOTIONCLEANUP:' +
    [Convert]::ToBase64String(
        [Text.Encoding]::UTF8.GetBytes(`$json)))
"@

$motionProcess = $null
$motionEvidence = $null
$failures = [Collections.Generic.List[string]]::new()

try {
    # The runner must not inherit the real-machine password.
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable(
            $name,
            $null,
            [EnvironmentVariableTarget]::Process)
    }
    Copy-MotionScriptToRemoteWsl
    foreach ($path in @(
        $motionRunnerStdoutPath,
        $motionRunnerStderrPath
    )) {
        if ([IO.File]::Exists($path)) {
            [IO.File]::Delete($path)
        }
    }

    $motionProcess =
        Start-Process `
            -FilePath 'wsl.exe' `
            -ArgumentList @(
                '-d',
                'Ubuntu-24.04',
                '--exec',
                'ssh',
                '-p',
                '2222',
                '-o',
                "UserKnownHostsFile=$KnownHostsWsl",
                '-o',
                'StrictHostKeyChecking=yes',
                '-o',
                'BatchMode=yes',
                $SshTarget,
                '/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe',
                '-NoProfile',
                '-NonInteractive',
                '-EncodedCommand',
                $motionBootstrapEncoded
            ) `
            -WindowStyle Hidden `
            -RedirectStandardOutput $motionRunnerStdoutPath `
            -RedirectStandardError $motionRunnerStderrPath `
            -PassThru

    $readyState = $null
    $readyDeadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
    do {
        $readyBase64 =
            Invoke-RemoteEncodedPowerShell `
                -Script $motionReadyReadScript `
                -Marker 'RDMOTIONREADY:'
        $readyEnvelope =
            ConvertFrom-Base64Json -Base64 $readyBase64
        if ($readyEnvelope.exists -eq $true) {
            $readyJson =
                [Text.Encoding]::UTF8.GetString(
                    [Convert]::FromBase64String(
                        [string]$readyEnvelope.valueBase64))
            $readyState =
                $readyJson |
                ConvertFrom-Json -ErrorAction Stop
            break
        }
        if ($motionProcess.HasExited) {
            break
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $readyDeadline)
    if ($null -eq $readyState) {
        throw 'The remote desktop-motion runner did not become ready.'
    }
    if ([int]$readyState.sessionId -ne [int]$preflight.sessionId) {
        throw (
            'Desktop motion is not in the host interactive session ' +
            "(host=$($preflight.sessionId), " +
            "motion=$($readyState.sessionId)).")
    }
    Write-Host (
        "Desktop motion ready in session $($readyState.sessionId), " +
        "$motionDurationSeconds seconds.")

    try {
        $env:REMOTEDESK_REAL_MACHINE_TESTS = '1'
        $env:REMOTEDESK_REAL_MACHINE_HOST = $remoteHostAddress
        $env:REMOTEDESK_REAL_MACHINE_PASSWORD = $password
        $env:REMOTEDESK_REAL_MACHINE_PORT = [string]$Port
        $env:REMOTEDESK_REAL_MACHINE_TARGET_ID = $TargetId
        $env:REMOTEDESK_REAL_MACHINE_EXPECTED_WIDTH = [string]$Width
        $env:REMOTEDESK_REAL_MACHINE_EXPECTED_HEIGHT = [string]$Height
        $env:REMOTEDESK_REAL_MACHINE_SOURCE_WIDTH = [string]$SourceWidth
        $env:REMOTEDESK_REAL_MACHINE_SOURCE_HEIGHT = [string]$SourceHeight
        $env:REMOTEDESK_REAL_MACHINE_EXPECTED_FPS = [string]$ExpectedFps
        $env:REMOTEDESK_REAL_MACHINE_MINIMUM_FPS = [string]$MinimumFps
        $env:REMOTEDESK_REAL_MACHINE_OBSERVATION_SECONDS = [string]$Seconds

        & dotnet test `
            $testProject `
            -c Release `
            --no-build `
            --filter $filter `
            --logger "trx;LogFileName=$Tag.trx" `
            --results-directory $ResultsDirectory `
            --logger 'console;verbosity=normal' 2>&1 |
            Tee-Object -LiteralPath $stdoutPath
        if ($LASTEXITCODE -ne 0) {
            $failures.Add("$Tag failed.") |
                Out-Null
        }
    }
    catch {
        $failures.Add(
            'The 4K60 viewer test invocation failed: ' +
            $_.Exception.Message) |
            Out-Null
    }
    finally {
        $password = $null
        foreach ($name in $environmentNames) {
            [Environment]::SetEnvironmentVariable(
                $name,
                $null,
                [EnvironmentVariableTarget]::Process)
        }
    }

    if (-not $motionProcess.WaitForExit(
            [int](($motionDurationSeconds + 30) * 1000))) {
        $failures.Add('The desktop-motion runner timed out.') |
            Out-Null
    }
    elseif ($motionProcess.ExitCode -ne 0) {
        $failures.Add(
            "The desktop-motion runner exited with $($motionProcess.ExitCode).") |
            Out-Null
    }

    $evidenceBase64 =
        Invoke-RemoteEncodedPowerShell `
            -Script $motionEvidenceReadScript `
            -Marker 'RDMOTIONEVIDENCE:'
    $motionEvidence =
        ConvertFrom-Base64Json -Base64 $evidenceBase64
    if ($motionEvidence.resultExists -ne $true -or
        $motionEvidence.stdoutExists -ne $true) {
        throw 'The remote motion result or stdout evidence is missing.'
    }
    $motionResultText =
        [Text.Encoding]::UTF8.GetString(
            [Convert]::FromBase64String(
                [string]$motionEvidence.resultBase64))
    $motionStdoutText =
        [Text.Encoding]::UTF8.GetString(
            [Convert]::FromBase64String(
                [string]$motionEvidence.stdoutBase64))
    Write-Utf8NoBomText `
        -Path $motionResultEvidencePath `
        -Text (
            $motionResultText.TrimEnd() +
            [Environment]::NewLine)
    Write-Utf8NoBomText `
        -Path $motionStdoutEvidencePath `
        -Text (
            $motionStdoutText.TrimEnd() +
            [Environment]::NewLine)

    $metrics =
        ConvertFrom-MotionResult -Result $motionResultText
    # Keep the WM_TIMER tail below 20 ms while the independent average-rate
    # gate remains at 57 Hz. This accommodates normal timer coalescing without
    # accepting a 50 Hz average.
    $maximumTickP95Ms = 20.0
    if ($metrics.device -cne $TargetId) {
        throw (
            "Motion device '$($metrics.device)' is not '$TargetId'.")
    }
    if ($metrics.source -cne "$($SourceWidth)x$($SourceHeight)") {
        throw (
            "Motion source '$($metrics.source)' is not native " +
            "$($SourceWidth)x$($SourceHeight).")
    }
    if ($metrics.timerHz -lt [double]$MinimumFps) {
        throw (
            "Motion timer $($metrics.timerHz) Hz is below " +
            "$MinimumFps Hz.")
    }
    if ($metrics.tickP95Ms -le 0 -or
        $metrics.tickP95Ms -gt $maximumTickP95Ms) {
        throw (
            "Motion tick p95 $($metrics.tickP95Ms) ms exceeds " +
            ('{0:F2} ms.' -f $maximumTickP95Ms))
    }
    if (-not $motionStdoutText.Contains($motionResultText.Trim())) {
        throw 'Motion stdout does not contain the exact MOTION_RESULT.'
    }
    Write-Host (
        "Desktop motion passed: $($metrics.device) " +
        "$($metrics.source), $($metrics.timerHz) Hz, " +
        "tick p95 $($metrics.tickP95Ms) ms.")
}
catch {
    $failures.Add($_.Exception.Message) |
        Out-Null
}
finally {
    $password = $null
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable(
            $name,
            $null,
            [EnvironmentVariableTarget]::Process)
    }

    if ($null -ne $motionProcess) {
        try {
            if (-not $motionProcess.HasExited) {
                Stop-Process `
                    -Id $motionProcess.Id `
                    -Force `
                    -ErrorAction Stop
                [void]$motionProcess.WaitForExit(5000)
            }
        }
        catch {
            $failures.Add(
                "Local runner PID $($motionProcess.Id) cleanup failed: " +
                $_.Exception.Message) |
                Out-Null
        }
        finally {
            $motionProcess.Dispose()
        }
    }

    try {
        $cleanupBase64 =
            Invoke-RemoteEncodedPowerShell `
                -Script $motionCleanupScript `
                -Marker 'RDMOTIONCLEANUP:'
        $cleanup =
            ConvertFrom-Base64Json -Base64 $cleanupBase64
        if (@($cleanup.remaining).Count -ne 0) {
            throw 'Exact Windows Temp motion files remain.'
        }
    }
    catch {
        $failures.Add(
            'Remote Windows motion cleanup failed: ' +
            $_.Exception.Message) |
            Out-Null
    }

    try {
        & wsl.exe `
            -d Ubuntu-24.04 `
            --exec ssh `
            -p 2222 `
            -o "UserKnownHostsFile=$KnownHostsWsl" `
            -o StrictHostKeyChecking=yes `
            -o BatchMode=yes `
            $SshTarget `
            rm -f -- $motionRemoteWslPath 2>$null
        if ($LASTEXITCODE -ne 0) {
            throw 'Remote WSL motion file removal failed.'
        }
        & wsl.exe `
            -d Ubuntu-24.04 `
            --exec ssh `
            -p 2222 `
            -o "UserKnownHostsFile=$KnownHostsWsl" `
            -o StrictHostKeyChecking=yes `
            -o BatchMode=yes `
            $SshTarget `
            test '!' -e $motionRemoteWslPath 2>$null
        if ($LASTEXITCODE -ne 0) {
            throw 'The exact remote WSL motion file remains.'
        }
    }
    catch {
        $failures.Add($_.Exception.Message) |
            Out-Null
    }
}

if ($failures.Count -ne 0) {
    throw (
        'The strict 4K60 entity gate failed: ' +
        ($failures -join ' | '))
}

Write-Host "4K60 entity gate passed. Evidence: $ResultsDirectory"
