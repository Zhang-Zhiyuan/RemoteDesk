using System.Diagnostics;
using System.Text;

namespace RemoteDesk;

internal static class RemoteUpdater
{
    private const string RemoteDeskExecutableName = "RemoteDesk.exe";
    private const int ExitDelayMilliseconds = 3000;
    private const int UpdateHealthTimeoutSeconds = 35;
    internal const string ResumeHostAfterUpdateArgument =
        "--resume-host-after-update";
    private static readonly Lazy<bool> UpdateEligibleCurrentPackage =
        new(HasUpdateEligibleCurrentPackage);

    public static bool CanApplyRemoteUpdate => OperatingSystem.IsWindows() &&
        UpdateEligibleCurrentPackage.Value;

    public static void ValidateRemoteUpdatePackageName(string? fileName)
    {
        if (!string.Equals(Path.GetFileName(fileName ?? string.Empty), RemoteDeskExecutableName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"远程更新包必须命名为 {RemoteDeskExecutableName}。");
        }
    }

    public static void ValidateRemoteUpdatePackageLength(long fileLength)
    {
        if (fileLength <= 0)
        {
            throw new InvalidDataException("远程更新包不能为空。");
        }

        if (fileLength > RemoteMessageCodec.MaxFileTransferBytes)
        {
            throw new InvalidDataException($"远程更新包超过传输上限：{RemoteFileTransfer.FormatBytes(RemoteMessageCodec.MaxFileTransferBytes)}。");
        }
    }

    internal static void ValidateReceivedRemoteUpdatePackagePath(string packagePath)
    {
        if (!File.Exists(packagePath))
        {
            throw new FileNotFoundException("远程更新包不存在。", packagePath);
        }

        ValidateRemoteUpdatePackageLength(new FileInfo(packagePath).Length);

        if (!string.Equals(Path.GetExtension(packagePath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("远程更新包必须是 exe 文件。");
        }
    }

    public static string? TryGetCurrentPackagePath()
    {
        string? processPath = GetCurrentExecutablePath();
        if (string.IsNullOrWhiteSpace(processPath) ||
            !File.Exists(processPath) ||
            !string.Equals(Path.GetExtension(processPath), ".exe", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(processPath), RemoteDeskExecutableName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.GetFullPath(processPath);
    }

    public static string GetCurrentPackagePath()
    {
        return TryGetCurrentPackagePath()
            ?? throw new InvalidOperationException("无法定位当前 RemoteDesk.exe，不能执行远程更新。");
    }

    public static void ScheduleApplyAndRestart(string packagePath, Action<string> log)
    {
        string targetPath = GetCurrentPackagePath();
        int healthPort = new AppSettingsService().Load().Host.Port;
        ScheduleApplyAndRestart(
            packagePath,
            targetPath,
            Environment.ProcessId,
            healthPort,
            log,
            exitCode => Environment.Exit(exitCode));
    }

    internal static void ScheduleApplyAndRestart(
        string packagePath,
        string targetPath,
        int processId,
        Action<string> log,
        Action<int> exitProcess)
    {
        ScheduleApplyAndRestart(
            packagePath,
            targetPath,
            processId,
            Protocol.DefaultPort,
            log,
            exitProcess);
    }

    internal static void ScheduleApplyAndRestart(
        string packagePath,
        string targetPath,
        int processId,
        int healthPort,
        Action<string> log,
        Action<int> exitProcess)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(exitProcess);
        if (healthPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(healthPort));
        }

        string packageFullPath = Path.GetFullPath(packagePath);
        string targetFullPath = Path.GetFullPath(targetPath);
        ValidatePackagePath(packageFullPath, targetFullPath);
        TrustedRemoteUpdatePackage trustedPackage =
            RemoteUpdateTrust.ValidateUpgrade(targetFullPath, packageFullPath);

        string targetDirectory = Path.GetDirectoryName(targetFullPath)
            ?? throw new InvalidOperationException("无法定位当前程序目录，不能执行远程更新。");
        EnsureDirectoryWritable(targetDirectory);

        string stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff");
        string backupPath = Path.Combine(
            targetDirectory,
            $"{Path.GetFileName(targetFullPath)}.{stamp}.{Guid.NewGuid():N}.old");
        string scriptPath = Path.Combine(Path.GetTempPath(), $"RemoteDesk.Update.{Guid.NewGuid():N}.ps1");
        string updateLogPath = Path.Combine(Path.GetTempPath(), $"RemoteDesk.Update.{Guid.NewGuid():N}.log");

        File.WriteAllText(scriptPath, CreateUpdaterScript(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        int updaterProcessId;
        try
        {
            using Process process = StartUpdaterProcess(
                scriptPath,
                processId,
                packageFullPath,
                targetFullPath,
                backupPath,
                updateLogPath,
                targetDirectory,
                healthPort,
                trustedPackage.FileSha256,
                trustedPackage.SignerCertificateSha256,
                trustedPackage.AuthenticodeRequired);
            updaterProcessId = process.Id;
        }
        catch
        {
            TryDeleteFile(scriptPath);
            throw;
        }

        try
        {
            string verificationMode =
                trustedPackage.AuthenticodeRequired
                    ? "同发布者 Authenticode"
                    : "未签名个人内网";
            log(
                $"远程更新已准备完成，候选版本 {trustedPackage.BuildStamp}，" +
                $"校验模式：{verificationMode}，" +
                $"更新脚本 PID={updaterProcessId}，日志：{updateLogPath}");
        }
        catch
        {
            // The updater already owns the transaction. A diagnostic sink
            // must not turn a successfully scheduled update into a deletion
            // of its script or received package.
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(ExitDelayMilliseconds).ConfigureAwait(false);
            exitProcess(0);
        });
    }

    internal static string CreateUpdaterScript() =>
        """
        param(
            [Parameter(Mandatory=$true)][int]$ProcessId,
            [Parameter(Mandatory=$true)][string]$PackagePath,
            [Parameter(Mandatory=$true)][string]$TargetPath,
            [Parameter(Mandatory=$true)][string]$BackupPath,
            [Parameter(Mandatory=$true)][string]$LogPath,
            [Parameter(Mandatory=$true)][ValidatePattern('^[0-9A-Fa-f]{64}$')][string]$ExpectedPackageSha256,
            [Parameter(Mandatory=$true)][ValidatePattern('^[0-9A-Fa-f]{64}$')][string]$ExpectedSignerCertificateSha256,
            [Parameter(Mandatory=$true)][ValidateRange(1, 65535)][int]$HealthPort,
            [ValidateRange(5, 120)][int]$HealthTimeoutSeconds = 35,
            [switch]$AllowUnsignedPersonalUpdate,
            [switch]$ResumeHostAfterUpdate,
            [string[]]$RestartArgument = @()
        )

        $ErrorActionPreference = 'Stop'
        $installedUpdate = $false
        $started = $null
        $effectiveRestartArgument = @($RestartArgument)
        if ($ResumeHostAfterUpdate) {
            $effectiveRestartArgument += '--resume-host-after-update'
        }

        function Write-RemoteDeskUpdateLog {
            param([string]$Message)
            $line = "[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff'), $Message
            Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8
        }

        function Test-RemoteDeskTrustedPackage {
            param([string]$Path)

            if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
                return $false
            }

            try {
                $fileHash = (Get-FileHash `
                    -LiteralPath $Path `
                    -Algorithm SHA256 `
                    -ErrorAction Stop).Hash
                if (-not [string]::Equals(
                    $fileHash,
                    $ExpectedPackageSha256,
                    [StringComparison]::OrdinalIgnoreCase)) {
                    return $false
                }

                $signature = Get-AuthenticodeSignature `
                    -LiteralPath $Path `
                    -ErrorAction Stop
                if ($AllowUnsignedPersonalUpdate) {
                    return [string]$signature.Status -eq 'NotSigned'
                }

                if ([string]$signature.Status -ne 'Valid' -or
                    $null -eq $signature.SignerCertificate) {
                    return $false
                }

                $sha256 = [Security.Cryptography.SHA256]::Create()
                try {
                    $certificateHash = ([BitConverter]::ToString(
                        $sha256.ComputeHash(
                            $signature.SignerCertificate.RawData))).Replace('-', '')
                }
                finally {
                    $sha256.Dispose()
                }

                return [string]::Equals(
                    $certificateHash,
                    $ExpectedSignerCertificateSha256,
                    [StringComparison]::OrdinalIgnoreCase)
            }
            catch {
                return $false
            }
        }

        function Get-RemoteDeskProcessPath {
            param([int]$CandidateProcessId)

            try {
                $runningProcess = Get-Process `
                    -Id $CandidateProcessId `
                    -ErrorAction SilentlyContinue
                if ($null -ne $runningProcess) {
                    $processPath = [string]$runningProcess.Path
                    if ([string]::IsNullOrWhiteSpace($processPath)) {
                        $processPath =
                            [string]$runningProcess.MainModule.FileName
                    }

                    if (-not [string]::IsNullOrWhiteSpace($processPath)) {
                        return [IO.Path]::GetFullPath($processPath)
                    }
                }
            }
            catch {
            }

            try {
                $candidate = Get-CimInstance `
                    -ClassName Win32_Process `
                    -Filter ("ProcessId = {0}" -f $CandidateProcessId) `
                    -ErrorAction SilentlyContinue
                if ($null -eq $candidate -or
                    [string]::IsNullOrWhiteSpace([string]$candidate.ExecutablePath)) {
                    return $null
                }

                return [IO.Path]::GetFullPath([string]$candidate.ExecutablePath)
            }
            catch {
                return $null
            }
        }

        function Test-RemoteDeskTargetProcess {
            param(
                [int]$CandidateProcessId,
                [string]$ExpectedPath
            )

            $actualPath = Get-RemoteDeskProcessPath `
                -CandidateProcessId $CandidateProcessId
            if ([string]::IsNullOrWhiteSpace([string]$actualPath)) {
                return $false
            }

            return [string]::Equals(
                [IO.Path]::GetFullPath($actualPath),
                [IO.Path]::GetFullPath($ExpectedPath),
                [StringComparison]::OrdinalIgnoreCase)
        }

        function Test-RemoteDeskPortOwner {
            param(
                [int]$CandidateProcessId,
                [int]$TcpPort
            )

            try {
                $listeners = @(Get-NetTCPConnection `
                    -State Listen `
                    -LocalPort $TcpPort `
                    -ErrorAction SilentlyContinue)
                return @($listeners | Where-Object {
                    [int64]$_.OwningProcess -eq [int64]$CandidateProcessId
                }).Count -gt 0
            }
            catch {
                return $false
            }
        }

        function Test-RemoteDeskHandshake {
            param([int]$TcpPort)

            $client = [Net.Sockets.TcpClient]::new()
            $waitHandle = $null
            try {
                $connect = $client.BeginConnect(
                    [Net.IPAddress]::Loopback,
                    $TcpPort,
                    $null,
                    $null)
                $waitHandle = $connect.AsyncWaitHandle
                if (-not $waitHandle.WaitOne(1000)) {
                    return $false
                }

                $client.EndConnect($connect)
                $stream = $client.GetStream()
                $stream.ReadTimeout = 1000
                $magic = [byte[]]::new(4)
                $offset = 0
                while ($offset -lt $magic.Length) {
                    $read = $stream.Read(
                        $magic,
                        $offset,
                        $magic.Length - $offset)
                    if ($read -le 0) {
                        return $false
                    }

                    $offset += $read
                }

                return [Text.Encoding]::ASCII.GetString($magic) -eq "RDK1"
            }
            catch {
                return $false
            }
            finally {
                if ($null -ne $waitHandle) {
                    $waitHandle.Dispose()
                }

                $client.Dispose()
            }
        }

        function Wait-RemoteDeskHealthy {
            param(
                [int]$CandidateProcessId,
                [string]$ExpectedPath,
                [int]$TcpPort,
                [int]$TimeoutSeconds
            )

            $deadline = [DateTimeOffset]::Now.AddSeconds($TimeoutSeconds)
            while ([DateTimeOffset]::Now -lt $deadline) {
                if ($null -eq (Get-Process `
                    -Id $CandidateProcessId `
                    -ErrorAction SilentlyContinue)) {
                    return $false
                }

                if ((Test-RemoteDeskTargetProcess `
                        -CandidateProcessId $CandidateProcessId `
                        -ExpectedPath $ExpectedPath) -and
                    (Test-RemoteDeskPortOwner `
                        -CandidateProcessId $CandidateProcessId `
                        -TcpPort $TcpPort) -and
                    (Test-RemoteDeskHandshake -TcpPort $TcpPort)) {
                    return $true
                }

                Start-Sleep -Milliseconds 500
            }

            return $false
        }

        function Stop-RemoteDeskTargetProcess {
            param(
                [int]$CandidateProcessId,
                [string]$ExpectedPath
            )

            if (-not (Test-RemoteDeskTargetProcess `
                -CandidateProcessId $CandidateProcessId `
                -ExpectedPath $ExpectedPath)) {
                return
            }

            Stop-Process `
                -Id $CandidateProcessId `
                -Force `
                -ErrorAction SilentlyContinue
            for ($i = 0; $i -lt 20; $i++) {
                if ($null -eq (Get-Process `
                    -Id $CandidateProcessId `
                    -ErrorAction SilentlyContinue)) {
                    return
                }

                Start-Sleep -Milliseconds 250
            }
        }

        function Move-RemoteDeskFileWithRetry {
            param(
                [string]$Source,
                [string]$Destination
            )

            for ($attempt = 0; $attempt -lt 20; $attempt++) {
                try {
                    Move-Item `
                        -LiteralPath $Source `
                        -Destination $Destination `
                        -Force
                    return
                }
                catch {
                    if ($attempt -eq 19) {
                        throw
                    }

                    Start-Sleep -Milliseconds 250
                }
            }
        }

        function Copy-RemoteDeskFileWithRetry {
            param(
                [string]$Source,
                [string]$Destination
            )

            for ($attempt = 0; $attempt -lt 20; $attempt++) {
                try {
                    Copy-Item `
                        -LiteralPath $Source `
                        -Destination $Destination `
                        -Force
                    return
                }
                catch {
                    if ($attempt -eq 19) {
                        throw
                    }

                    Start-Sleep -Milliseconds 250
                }
            }
        }

        function Remove-RemoteDeskUpdateFile {
            param(
                [string]$Path,
                [string]$Description
            )

            if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
                return
            }

            try {
                Remove-Item -LiteralPath $Path -Force
                Write-RemoteDeskUpdateLog "$Description removed: $Path"
            }
            catch {
                Write-RemoteDeskUpdateLog (
                    "$Description retained because cleanup failed: {0}; {1}" -f
                        $Path,
                        $_.Exception.Message)
            }
        }

        try {
            Write-RemoteDeskUpdateLog "waiting for process $ProcessId to exit"
            for ($i = 0; $i -lt 120; $i++) {
                $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
                if ($null -eq $process -or
                    -not (Test-RemoteDeskTargetProcess `
                        -CandidateProcessId $ProcessId `
                        -ExpectedPath $TargetPath)) {
                    break
                }

                Start-Sleep -Milliseconds 500
            }

            $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
            if ($null -ne $process -and
                (Test-RemoteDeskTargetProcess `
                    -CandidateProcessId $ProcessId `
                    -ExpectedPath $TargetPath)) {
                Write-RemoteDeskUpdateLog "process did not exit in time, stopping it"
                Stop-Process -Id $ProcessId -Force -ErrorAction SilentlyContinue
                Start-Sleep -Milliseconds 500
            }

            if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
                throw "update package is missing: $PackagePath"
            }
            if (-not (Test-RemoteDeskTrustedPackage -Path $PackagePath)) {
                throw "update package changed or no longer matches the approved signature mode"
            }

            $targetDirectory = Split-Path -Parent $TargetPath
            if (-not (Test-Path -LiteralPath $targetDirectory -PathType Container)) {
                throw "target directory is missing: $targetDirectory"
            }

            if (Test-Path -LiteralPath $BackupPath) {
                throw "refusing to overwrite existing backup: $BackupPath"
            }

            if (Test-Path -LiteralPath $TargetPath -PathType Leaf) {
                Write-RemoteDeskUpdateLog "moving old executable to $BackupPath"
                Move-RemoteDeskFileWithRetry `
                    -Source $TargetPath `
                    -Destination $BackupPath
            }
            else {
                throw "current executable is missing before replacement: $TargetPath"
            }

            Write-RemoteDeskUpdateLog "installing update package to $TargetPath"
            # A same-volume move retains the received file's ACL. Preserve the installed
            # executable's permissions, including the protected administrator-startup copy.
            $targetFileSecurity = Get-Acl -LiteralPath $BackupPath
            Move-RemoteDeskFileWithRetry `
                -Source $PackagePath `
                -Destination $TargetPath
            $installedUpdate = $true
            Set-Acl -LiteralPath $TargetPath -AclObject $targetFileSecurity
            if (-not (Test-RemoteDeskTrustedPackage -Path $TargetPath)) {
                throw "installed update failed hash or signature-mode re-verification"
            }

            Write-RemoteDeskUpdateLog "starting updated RemoteDesk"
            $started = Start-Process -FilePath $TargetPath -ArgumentList $effectiveRestartArgument -WorkingDirectory $targetDirectory -PassThru
            Write-RemoteDeskUpdateLog "started PID=$($started.Id)"
            $updateHealthy = Wait-RemoteDeskHealthy `
                -CandidateProcessId $started.Id `
                -ExpectedPath $TargetPath `
                -TcpPort $HealthPort `
                -TimeoutSeconds $HealthTimeoutSeconds
            if (-not $updateHealthy) {
                throw (
                    "updated process failed path/port/RDK1 health verification " +
                    "(PID=$($started.Id), port=$HealthPort)")
            }

            Write-RemoteDeskUpdateLog (
                "updated RemoteDesk is healthy: PID=$($started.Id), " +
                "path=$TargetPath, port=$HealthPort, handshake=RDK1")
            Remove-RemoteDeskUpdateFile `
                -Path $BackupPath `
                -Description "old executable backup"

            Write-RemoteDeskUpdateLog "remote update completed"
            Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
            exit 0
        }
        catch {
            Write-RemoteDeskUpdateLog "remote update failed: $($_.Exception.Message)"
            $failedUpdatePath = $null
            $rollbackHealthy = $false
            try {
                if ($null -ne $started) {
                    Stop-RemoteDeskTargetProcess `
                        -CandidateProcessId $started.Id `
                        -ExpectedPath $TargetPath
                }

                if (Test-Path -LiteralPath $BackupPath -PathType Leaf) {
                    if ($installedUpdate -and
                        (Test-Path -LiteralPath $TargetPath -PathType Leaf)) {
                        $failedUpdatePath = "{0}.{1}.failed" -f `
                            $TargetPath,
                            [Guid]::NewGuid().ToString("N")
                        Move-RemoteDeskFileWithRetry `
                            -Source $TargetPath `
                            -Destination $failedUpdatePath
                        Write-RemoteDeskUpdateLog (
                            "rejected update staged for rollback: " +
                            $failedUpdatePath)
                    }

                    Copy-RemoteDeskFileWithRetry `
                        -Source $BackupPath `
                        -Destination $TargetPath
                    Write-RemoteDeskUpdateLog (
                        "old executable restored from $BackupPath")
                }
                elseif ($installedUpdate) {
                    throw (
                        "rollback backup is unavailable; rejected executable " +
                        "remains at $TargetPath")
                }
                elseif (-not (Test-Path `
                    -LiteralPath $TargetPath `
                    -PathType Leaf)) {
                    throw "rollback target is missing: $TargetPath"
                }

                $rollback = Start-Process `
                    -FilePath $TargetPath `
                    -ArgumentList $effectiveRestartArgument `
                    -WorkingDirectory $targetDirectory `
                    -PassThru
                Write-RemoteDeskUpdateLog "rollback started PID=$($rollback.Id)"
                $rollbackHealthy = Wait-RemoteDeskHealthy `
                    -CandidateProcessId $rollback.Id `
                    -ExpectedPath $TargetPath `
                    -TcpPort $HealthPort `
                    -TimeoutSeconds $HealthTimeoutSeconds
                if (-not $rollbackHealthy) {
                    throw (
                        "rollback process failed path/port/RDK1 health " +
                        "verification (PID=$($rollback.Id), port=$HealthPort)")
                }

                Write-RemoteDeskUpdateLog (
                    "rollback RemoteDesk is healthy: PID=$($rollback.Id), " +
                    "path=$TargetPath, port=$HealthPort, handshake=RDK1")
                Remove-RemoteDeskUpdateFile `
                    -Path $BackupPath `
                    -Description "rollback backup"
                if (-not [string]::IsNullOrWhiteSpace($failedUpdatePath)) {
                    Remove-RemoteDeskUpdateFile `
                        -Path $failedUpdatePath `
                        -Description "rejected update"
                }

                Remove-Item `
                    -LiteralPath $PSCommandPath `
                    -Force `
                    -ErrorAction SilentlyContinue
            }
            catch {
                Write-RemoteDeskUpdateLog "rollback failed: $($_.Exception.Message)"
                Write-RemoteDeskUpdateLog (
                    "recovery files retained when present: " +
                    "backup=$BackupPath; rejected=$failedUpdatePath; " +
                    "target=$TargetPath")
            }

            exit 1
        }
        """;

    private static Process StartUpdaterProcess(
        string scriptPath,
        int processId,
        string packagePath,
        string targetPath,
        string backupPath,
        string logPath,
        string targetDirectory,
        int healthPort,
        string expectedPackageSha256,
        string expectedSignerCertificateSha256,
        bool authenticodeRequired)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = GetPowerShellExecutablePath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = targetDirectory
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-ProcessId");
        startInfo.ArgumentList.Add(processId.ToString());
        startInfo.ArgumentList.Add("-PackagePath");
        startInfo.ArgumentList.Add(packagePath);
        startInfo.ArgumentList.Add("-TargetPath");
        startInfo.ArgumentList.Add(targetPath);
        startInfo.ArgumentList.Add("-BackupPath");
        startInfo.ArgumentList.Add(backupPath);
        startInfo.ArgumentList.Add("-LogPath");
        startInfo.ArgumentList.Add(logPath);
        startInfo.ArgumentList.Add("-ExpectedPackageSha256");
        startInfo.ArgumentList.Add(expectedPackageSha256);
        startInfo.ArgumentList.Add("-ExpectedSignerCertificateSha256");
        startInfo.ArgumentList.Add(expectedSignerCertificateSha256);
        startInfo.ArgumentList.Add("-HealthPort");
        startInfo.ArgumentList.Add(healthPort.ToString());
        startInfo.ArgumentList.Add("-HealthTimeoutSeconds");
        startInfo.ArgumentList.Add(UpdateHealthTimeoutSeconds.ToString());
        if (!authenticodeRequired)
        {
            startInfo.ArgumentList.Add("-AllowUnsignedPersonalUpdate");
        }

        startInfo.ArgumentList.Add("-ResumeHostAfterUpdate");
        startInfo.ArgumentList.Add("-RestartArgument");
        startInfo.ArgumentList.Add("--tray");

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("启动远程更新脚本失败。");
    }

    private static void ValidatePackagePath(string packagePath, string targetPath)
    {
        ValidateRemoteUpdatePackageName(targetPath);
        ValidateReceivedRemoteUpdatePackagePath(packagePath);

        if (!string.Equals(Path.GetExtension(targetPath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("当前进程不是 exe 入口，不能执行远程更新。");
        }

        if (string.Equals(packagePath, targetPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("更新包不能与当前运行中的程序路径相同。");
        }
    }

    private static bool HasUpdateEligibleCurrentPackage()
    {
        string? packagePath = TryGetCurrentPackagePath();
        return packagePath is not null &&
            RemoteUpdateTrust.CanUseAsCurrentPackage(packagePath);
    }

    private static string? GetCurrentExecutablePath()
    {
        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
        {
            return processPath;
        }

        string applicationPath = Application.ExecutablePath;
        return !string.IsNullOrWhiteSpace(applicationPath) && File.Exists(applicationPath)
            ? applicationPath
            : null;
    }

    private static string GetPowerShellExecutablePath()
    {
        string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(windowsDirectory))
        {
            string candidate = Path.Combine(windowsDirectory, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "powershell.exe";
    }

    private static void EnsureDirectoryWritable(string directory)
    {
        string probePath = Path.Combine(directory, $".remotedesk-update-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(probePath, "RemoteDesk update write probe");
        }
        finally
        {
            TryDeleteFile(probePath);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
    }

    internal static void DeleteRejectedReceivedPackage(string packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            return;
        }

        TryDeleteFile(packagePath);
    }
}
