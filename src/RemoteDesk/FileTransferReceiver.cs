using System.Security.Cryptography;

namespace RemoteDesk;

internal readonly record struct ReturnedClipboardFileCommitResult(
    string Message,
    IReadOnlyList<string> LocalPaths,
    bool ClipboardUpdated);

internal sealed class FileTransferReceiver : IDisposable
{
    private const string ReceiveFolderName = "RemoteDeskReceived";
    private const string TemporaryFilePrefix = ".RemoteDesk-transfer-";
    private const string TemporaryFileSuffix = ".rdtransfer";
    private const int MaxSafeFileNameLength = 180;
    private const int MaxSafeExtensionLength = 32;
    private const int MaxUniqueFilePathAttempts = 10_000;
    internal const int MaxFilesPerSession = 128;
    internal const long MaxDeclaredBytesPerSession =
        2L * RemoteMessageCodec.MaxFileTransferBytes;
    internal const long MinimumFreeSpaceReserveBytes =
        256L * 1024L * 1024L;
    private const int ClipboardPasteSettleDelayMs = 50;
    private static readonly TimeSpan StaleTemporaryFileAge = TimeSpan.FromDays(1);
    private static readonly TimeSpan StaleTemporaryFileCleanupInterval = TimeSpan.FromHours(1);
    private static readonly HashSet<string> ReservedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON",
        "PRN",
        "AUX",
        "NUL",
        "COM1",
        "COM2",
        "COM3",
        "COM4",
        "COM5",
        "COM6",
        "COM7",
        "COM8",
        "COM9",
        "LPT1",
        "LPT2",
        "LPT3",
        "LPT4",
        "LPT5",
        "LPT6",
        "LPT7",
        "LPT8",
        "LPT9"
    };

    private readonly Action<string> _log;
    private readonly Func<string> _receiveDirectoryProvider;
    private readonly Func<IEnumerable<string>, Task> _setFileDropListAsync;
    private readonly Action _sendPasteShortcut;
    private readonly Func<string, long> _availableFreeSpaceProvider;
    private readonly string _savedLocationName;
    private readonly object _returnedClipboardFilesLock = new();
    private readonly SemaphoreSlim _returnedClipboardCommitLock = new(1, 1);
    private readonly List<string> _completedClipboardPasteFiles = [];
    private readonly List<string> _completedReturnedClipboardFiles = [];
    private IncomingTransfer? _currentTransfer;
    private bool _collectClipboardPasteFiles;
    private bool _collectReturnedClipboardFiles;
    private bool _returnedClipboardCommitRetryPending;
    private long _returnedClipboardBatchVersion;
    private int _acceptedTransferCount;
    private long _acceptedDeclaredBytes;
    private DateTimeOffset _lastStaleTemporaryFileCleanupAt = DateTimeOffset.MinValue;

    public bool RequireChecksum { get; set; }

    public string? LastCompletedFilePath { get; private set; }

    internal bool HasActiveTransfer => _currentTransfer is not null;

    internal bool HasPendingReturnedClipboardFileCommit
    {
        get
        {
            lock (_returnedClipboardFilesLock)
            {
                return _returnedClipboardCommitRetryPending &&
                    _completedReturnedClipboardFiles.Count > 0;
            }
        }
    }

    // Kept as a compatibility alias for focused tests and older internal callers. A partially
    // received batch must never be advertised as a clipboard retry after a disconnect.
    internal bool HasCompletedReturnedClipboardFiles => HasPendingReturnedClipboardFileCommit;

    internal int CompletedReturnedClipboardFileCount
    {
        get
        {
            lock (_returnedClipboardFilesLock)
            {
                return _completedReturnedClipboardFiles.Count;
            }
        }
    }

    public FileTransferReceiver(Action<string> log) : this(log, GetReceiveDirectory)
    {
    }

    public FileTransferReceiver(Action<string> log, string savedLocationName) : this(log, GetReceiveDirectory, savedLocationName)
    {
    }

    internal FileTransferReceiver(
        Action<string> log,
        Func<string> receiveDirectoryProvider,
        string savedLocationName = "被控端",
        Func<IEnumerable<string>, Task>? setFileDropListAsync = null,
        Action? sendPasteShortcut = null,
        Func<string, long>? availableFreeSpaceProvider = null)
    {
        _log = log;
        _receiveDirectoryProvider = receiveDirectoryProvider;
        _setFileDropListAsync = setFileDropListAsync ?? ClipboardTextService.SetFileDropListAsync;
        _sendPasteShortcut = sendPasteShortcut ?? InputInjector.SendPasteShortcut;
        _availableFreeSpaceProvider =
            availableFreeSpaceProvider ?? GetAvailableFreeSpace;
        _savedLocationName = string.IsNullOrWhiteSpace(savedLocationName) ? "本机" : savedLocationName.Trim();
    }

    public string Start(RemoteControlMessage control)
    {
        string transferId = RequireText(control.TransferId, "文件传输编号缺失。");
        string fileName = SanitizeFileName(RequireText(control.FileName, "文件名缺失。"));
        if (control.FileLength < 0 || control.FileLength > RemoteMessageCodec.MaxFileTransferBytes)
        {
            throw new InvalidDataException("文件大小超出允许范围。");
        }

        string receiveDirectory = _receiveDirectoryProvider();
        if (string.IsNullOrWhiteSpace(receiveDirectory))
        {
            throw new InvalidOperationException("文件接收目录不可用。");
        }

        Directory.CreateDirectory(receiveDirectory);
        ValidateReceiveBudget(receiveDirectory, control.FileLength);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (ShouldCleanupStaleTemporaryFiles(_lastStaleTemporaryFileCleanupAt, now))
        {
            _lastStaleTemporaryFileCleanupAt = now;
            CleanupStaleTemporaryFiles(receiveDirectory, now);
        }
        (string finalPath, string temporaryPath, FileStream stream) =
            ReserveUniqueTransferFile(receiveDirectory, fileName);
        IncomingTransfer replacement;
        try
        {
            replacement = new IncomingTransfer(
                transferId,
                fileName,
                control.FileLength,
                finalPath,
                temporaryPath,
                stream,
                RequireChecksum);
        }
        catch
        {
            TryDisposeTransferStream(stream);
            TryDeleteFile(temporaryPath);
            throw;
        }

        IncomingTransfer? previous = _currentTransfer;
        _currentTransfer = replacement;
        _acceptedTransferCount++;
        _acceptedDeclaredBytes += control.FileLength;
        LastCompletedFilePath = null;
        if (previous is not null)
        {
            DisposeAndDeleteTransfer(previous);
        }

        string message = $"开始接收文件：{fileName} ({RemoteFileTransfer.FormatBytes(control.FileLength)})";
        _log(message);
        return message;
    }

    public async Task<string?> WriteChunkAsync(RemoteControlMessage control, CancellationToken cancellationToken)
    {
        IncomingTransfer transfer = GetCurrentTransfer(control.TransferId);
        try
        {
            ReadOnlyMemory<byte> bytes = control.FileBytes;

            if (control.FileOffset != transfer.BytesReceived)
            {
                throw new InvalidDataException("文件分块顺序异常。");
            }

            if (bytes.Length <= 0 ||
                bytes.Length > RemoteMessageCodec.FileTransferChunkBytes ||
                transfer.BytesReceived + bytes.Length > transfer.FileLength)
            {
                throw new InvalidDataException("文件分块大小异常。");
            }

            await transfer.Stream.WriteAsync(bytes, cancellationToken);
            transfer.Hash.AppendData(bytes.Span);
            transfer.BytesReceived += bytes.Length;
            return TryFormatProgressMessage(transfer);
        }
        catch
        {
            AbortTransferOnFailure(transfer);
            throw;
        }
    }

    public void SetExpectedChecksum(RemoteControlMessage control)
    {
        IncomingTransfer transfer = GetCurrentTransfer(control.TransferId);
        try
        {
            string algorithm = RequireText(control.ChecksumAlgorithm, "文件校验算法缺失。")
                .Replace("-", string.Empty, StringComparison.Ordinal)
                .ToUpperInvariant();
            if (!string.Equals(algorithm, RemoteMessageCodec.FileTransferChecksumAlgorithm, StringComparison.Ordinal))
            {
                throw new InvalidDataException("文件校验算法不受支持。");
            }

            string checksumHex = RequireText(control.ChecksumHex, "文件校验值缺失。");
            byte[] checksum;
            try
            {
                checksum = Convert.FromHexString(checksumHex);
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException("文件校验值格式异常。", ex);
            }

            if (checksum.Length != 32)
            {
                throw new InvalidDataException("文件校验值长度异常。");
            }

            transfer.ExpectedSha256 = checksum;
        }
        catch
        {
            AbortTransferOnFailure(transfer);
            throw;
        }
    }

    public async Task<string> CompleteAsync(RemoteControlMessage control, CancellationToken cancellationToken)
    {
        IncomingTransfer transfer = GetCurrentTransfer(control.TransferId);
        bool checksumVerified;
        try
        {
            if (transfer.BytesReceived != transfer.FileLength)
            {
                throw new InvalidDataException("文件传输未完整完成。");
            }

            await transfer.Stream.FlushAsync(cancellationToken);
            checksumVerified = VerifyChecksumIfPresent(transfer);
            transfer.Stream.Dispose();
            transfer.Hash.Dispose();
            _currentTransfer = null;
        }
        catch
        {
            AbortTransferOnFailure(transfer);
            throw;
        }

        string savedPath;
        try
        {
            savedPath = MoveTemporaryToUniqueFinalPath(
                transfer.TemporaryPath,
                transfer.FinalPath,
                transfer.FileName);
        }
        catch
        {
            TryDeleteFile(transfer.TemporaryPath);
            throw;
        }

        LastCompletedFilePath = savedPath;
        string message = $"文件已保存到{_savedLocationName}：{savedPath}";
        if (_collectClipboardPasteFiles)
        {
            _completedClipboardPasteFiles.Add(savedPath);
        }

        lock (_returnedClipboardFilesLock)
        {
            if (_collectReturnedClipboardFiles)
            {
                _completedReturnedClipboardFiles.Add(savedPath);
            }
        }

        if (checksumVerified)
        {
            message += "（SHA-256 已校验）";
        }

        _log(message);
        return message;
    }

    public string Cancel(RemoteControlMessage control)
    {
        IncomingTransfer transfer = GetCurrentTransfer(control.TransferId);
        string fileName = transfer.FileName;
        string reason = string.IsNullOrWhiteSpace(control.StatusMessage)
            ? "对端取消了文件传输。"
            : control.StatusMessage.Trim();

        AbortActiveTransfer();
        string message = $"文件传输已取消：{fileName} - {reason}";
        _log(message);
        return message;
    }

    public string BeginClipboardFilePasteBatch()
    {
        _collectClipboardPasteFiles = true;
        _completedClipboardPasteFiles.Clear();
        return "已准备接收拖放文件并粘贴到远端当前位置。";
    }

    public string CancelClipboardFilePasteBatch()
    {
        _collectClipboardPasteFiles = false;
        _completedClipboardPasteFiles.Clear();
        return "已取消远端当前位置拖放粘贴。";
    }

    public async Task<string> CommitClipboardFilePasteBatchAsync()
    {
        string[] originalFiles = _completedClipboardPasteFiles.ToArray();
        string[] files = FilterExistingFileDropPaths(originalFiles);
        int missingFiles = originalFiles.Length - files.Length;
        _collectClipboardPasteFiles = false;

        if (files.Length == 0)
        {
            _completedClipboardPasteFiles.Clear();
            return missingFiles > 0
                ? "拖放文件已接收，但本地暂存文件已不存在，无法粘贴到远端当前位置。"
                : "拖放文件已发送，但没有可粘贴到远端当前位置的文件。";
        }

        await _setFileDropListAsync(files);
        await Task.Delay(ClipboardPasteSettleDelayMs);
        _sendPasteShortcut();
        _completedClipboardPasteFiles.Clear();
        string message = $"已把 {files.Length} 个拖放文件放入远程剪贴板，并触发当前位置粘贴。";
        return missingFiles > 0 ? $"{message}（跳过 {missingFiles} 个已不存在的暂存文件）" : message;
    }

    public string BeginReturnedClipboardFileBatch()
    {
        lock (_returnedClipboardFilesLock)
        {
            _returnedClipboardBatchVersion++;
            _collectReturnedClipboardFiles = true;
            _returnedClipboardCommitRetryPending = false;
            _completedReturnedClipboardFiles.Clear();
        }

        return "已准备接收远端剪贴板文件。";
    }

    public string CancelReturnedClipboardFileBatch()
    {
        lock (_returnedClipboardFilesLock)
        {
            CancelReturnedClipboardFileBatchCore();
        }

        return "已取消远端剪贴板文件接收。";
    }

    internal bool CancelReturnedClipboardFileBatchUnlessCommitRetryPending()
    {
        lock (_returnedClipboardFilesLock)
        {
            if (_returnedClipboardCommitRetryPending &&
                _completedReturnedClipboardFiles.Count > 0)
            {
                return false;
            }

            CancelReturnedClipboardFileBatchCore();
            return true;
        }
    }

    public async Task<string> CommitReturnedClipboardFileBatchAsync()
    {
        ReturnedClipboardFileCommitResult result =
            await CommitReturnedClipboardFileBatchWithResultAsync();
        return result.Message;
    }

    internal async Task<ReturnedClipboardFileCommitResult> CommitReturnedClipboardFileBatchWithResultAsync(
        bool allowClipboardFailure = false)
    {
        await _returnedClipboardCommitLock.WaitAsync().ConfigureAwait(false);
        try
        {
            string[] originalFiles;
            long batchVersion;
            lock (_returnedClipboardFilesLock)
            {
                batchVersion = _returnedClipboardBatchVersion;
                originalFiles = _completedReturnedClipboardFiles.ToArray();
                _collectReturnedClipboardFiles = false;
                _returnedClipboardCommitRetryPending = false;
            }

            string[] files = FilterExistingFileDropPaths(originalFiles);
            int missingFiles = originalFiles.Length - files.Length;

            if (files.Length == 0)
            {
                ClearReturnedClipboardFilesIfCurrentBatch(batchVersion);

                string emptyMessage = missingFiles > 0
                    ? "已收到远端文件，但本地暂存文件已不存在，无法放入本机剪贴板。"
                    : "没有收到可放入本机剪贴板的远端文件。";
                return new ReturnedClipboardFileCommitResult(
                    emptyMessage,
                    Array.Empty<string>(),
                    ClipboardUpdated: false);
            }

            Exception? clipboardFailure = null;
            try
            {
                await _setFileDropListAsync(files).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsRecoverableClipboardWriteException(ex))
            {
                if (!allowClipboardFailure)
                {
                    PreserveReturnedClipboardCommitRetryIfCurrentBatch(batchVersion, files);
                    throw;
                }

                clipboardFailure = ex;
            }

            ClearReturnedClipboardFilesIfCurrentBatch(batchVersion);

            string message = clipboardFailure is null
                ? $"已把 {files.Length} 个回传文件放入本机剪贴板，可在资源管理器直接粘贴。"
                : $"已接收 {files.Length} 个远端文件；本机剪贴板暂时不可用（{clipboardFailure.Message}），但仍可直接拖出。";
            if (missingFiles > 0)
            {
                message = $"{message}（跳过 {missingFiles} 个已不存在的暂存文件）";
            }

            return new ReturnedClipboardFileCommitResult(
                message,
                Array.AsReadOnly(files),
                ClipboardUpdated: clipboardFailure is null);
        }
        finally
        {
            _returnedClipboardCommitLock.Release();
        }
    }

    private void CancelReturnedClipboardFileBatchCore()
    {
        _returnedClipboardBatchVersion++;
        _collectReturnedClipboardFiles = false;
        _returnedClipboardCommitRetryPending = false;
        _completedReturnedClipboardFiles.Clear();
    }

    private void ClearReturnedClipboardFilesIfCurrentBatch(long batchVersion)
    {
        lock (_returnedClipboardFilesLock)
        {
            if (_returnedClipboardBatchVersion != batchVersion)
            {
                return;
            }

            _returnedClipboardCommitRetryPending = false;
            _completedReturnedClipboardFiles.Clear();
        }
    }

    private void PreserveReturnedClipboardCommitRetryIfCurrentBatch(
        long batchVersion,
        IReadOnlyList<string> files)
    {
        lock (_returnedClipboardFilesLock)
        {
            if (_returnedClipboardBatchVersion != batchVersion)
            {
                return;
            }

            _collectReturnedClipboardFiles = false;
            _returnedClipboardCommitRetryPending = true;
            _completedReturnedClipboardFiles.Clear();
            _completedReturnedClipboardFiles.AddRange(files);
        }
    }

    public void AbortActiveTransfer()
    {
        IncomingTransfer? transfer = _currentTransfer;
        _currentTransfer = null;
        LastCompletedFilePath = null;
        if (transfer is null)
        {
            return;
        }

        DisposeAndDeleteTransfer(transfer);
    }

    private void AbortTransferOnFailure(IncomingTransfer transfer)
    {
        if (ReferenceEquals(_currentTransfer, transfer))
        {
            AbortActiveTransfer();
            return;
        }

        DisposeAndDeleteTransfer(transfer);
    }

    public void Dispose()
    {
        AbortActiveTransfer();
    }

    private IncomingTransfer GetCurrentTransfer(string? transferId)
    {
        IncomingTransfer? transfer = _currentTransfer;
        string requestedTransferId = RequireText(transferId, "文件传输编号缺失。");
        if (transfer is null || !string.Equals(transfer.TransferId, requestedTransferId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("没有匹配的文件传输会话。");
        }

        return transfer;
    }

    internal static string GetReceiveDirectory()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string downloads = string.IsNullOrWhiteSpace(userProfile)
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            : Path.Combine(userProfile, "Downloads");

        if (string.IsNullOrWhiteSpace(downloads) || !Directory.Exists(downloads))
        {
            downloads = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        if (string.IsNullOrWhiteSpace(downloads))
        {
            downloads = AppContext.BaseDirectory;
        }

        return Path.Combine(downloads, ReceiveFolderName);
    }

    private static (string FinalPath, string TemporaryPath, FileStream Stream) ReserveUniqueTransferFile(
        string directory,
        string fileName)
    {
        string baseName = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        string? finalPath = null;
        for (int index = 0; index < MaxUniqueFilePathAttempts; index++)
        {
            string candidate = index == 0
                ? Path.Combine(directory, fileName)
                : Path.Combine(directory, $"{baseName} ({index}){extension}");
            if (PathExists(candidate))
            {
                continue;
            }

            finalPath = candidate;
            break;
        }

        if (finalPath is null)
        {
            throw new IOException("无法为接收文件保留唯一保存路径。");
        }

        for (int attempt = 0; attempt < MaxUniqueFilePathAttempts; attempt++)
        {
            string temporaryPath = Path.Combine(directory, CreateTemporaryFileName());
            try
            {
                var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    RemoteMessageCodec.FileTransferChunkBytes,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                return (finalPath, temporaryPath, stream);
            }
            catch (IOException) when (PathExists(temporaryPath))
            {
            }
        }

        throw new IOException("无法为接收文件保留唯一临时路径。");
    }

    private static string MoveTemporaryToUniqueFinalPath(string temporaryPath, string desiredFinalPath, string fileName)
    {
        string directory = Path.GetDirectoryName(desiredFinalPath)
            ?? throw new InvalidOperationException("文件接收目录不可用。");
        string baseName = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);

        for (int index = 0; index < MaxUniqueFilePathAttempts; index++)
        {
            string candidate = index == 0
                ? desiredFinalPath
                : Path.Combine(directory, $"{baseName} ({index}){extension}");
            if (PathExists(candidate))
            {
                continue;
            }

            try
            {
                File.Move(temporaryPath, candidate);
                return candidate;
            }
            catch (IOException) when (PathExists(candidate))
            {
            }
        }

        throw new IOException("无法为接收文件保留唯一保存路径。");
    }

    private static string[] FilterExistingFileDropPaths(IEnumerable<string> paths)
    {
        return paths
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .ToArray();
    }

    private void ValidateReceiveBudget(string receiveDirectory, long fileLength)
    {
        if (_acceptedTransferCount >= MaxFilesPerSession)
        {
            throw new InvalidDataException(
                $"本次连接已达到 {MaxFilesPerSession} 个接收文件上限，请重新确认后再连接。");
        }

        if (_acceptedDeclaredBytes > MaxDeclaredBytesPerSession - fileLength)
        {
            throw new InvalidDataException(
                $"本次连接累计接收声明大小超过 {RemoteFileTransfer.FormatBytes(MaxDeclaredBytesPerSession)} 上限。");
        }

        long availableBytes;
        try
        {
            availableBytes = _availableFreeSpaceProvider(receiveDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new IOException("无法确认文件接收目录的剩余空间，已拒绝接收。", ex);
        }

        long requiredBytes = fileLength + MinimumFreeSpaceReserveBytes;
        if (availableBytes < requiredBytes)
        {
            throw new IOException(
                $"接收空间不足：需要为文件及安全余量保留 {RemoteFileTransfer.FormatBytes(requiredBytes)}，" +
                $"当前仅可用 {RemoteFileTransfer.FormatBytes(Math.Max(0, availableBytes))}。");
        }
    }

    private static long GetAvailableFreeSpace(string directory)
    {
        string fullPath = Path.GetFullPath(directory);
        string root = Path.GetPathRoot(fullPath)
            ?? throw new IOException("无法定位文件接收目录所在磁盘。");
        return new DriveInfo(root).AvailableFreeSpace;
    }

    private static bool IsRecoverableClipboardWriteException(Exception ex)
    {
        return ex is InvalidOperationException or
            TimeoutException or
            System.Runtime.InteropServices.ExternalException;
    }

    private static bool PathExists(string path)
    {
        return File.Exists(path) || Directory.Exists(path);
    }

    private static string? TryFormatProgressMessage(IncomingTransfer transfer)
    {
        if (transfer.FileLength <= 0 || transfer.BytesReceived >= transfer.FileLength)
        {
            return null;
        }

        int percent = (int)(transfer.BytesReceived * 100 / transfer.FileLength);
        int progressBucket = percent / 10;
        if (progressBucket <= transfer.LastReportedProgressBucket)
        {
            return null;
        }

        transfer.LastReportedProgressBucket = progressBucket;
        return $"正在接收文件：{transfer.FileName} {percent}% " +
            $"({RemoteFileTransfer.FormatBytes(transfer.BytesReceived)} / {RemoteFileTransfer.FormatBytes(transfer.FileLength)})";
    }

    private static bool VerifyChecksumIfPresent(IncomingTransfer transfer)
    {
        byte[]? expectedSha256 = transfer.ExpectedSha256;
        if (expectedSha256 is null)
        {
            if (transfer.RequireChecksum)
            {
                throw new InvalidDataException("远端未发送文件 SHA-256 校验值，已拒绝保存。");
            }

            return false;
        }

        byte[] actualSha256 = transfer.Hash.GetHashAndReset();
        if (!CryptographicOperations.FixedTimeEquals(actualSha256, expectedSha256))
        {
            throw new InvalidDataException("文件 SHA-256 校验失败，已拒绝保存。");
        }

        return true;
    }

    internal static int CleanupStaleTemporaryFiles(string directory, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return 0;
        }

        int deleted = 0;
        foreach (string path in Directory.EnumerateFiles(
            directory,
            $"{TemporaryFilePrefix}*{TemporaryFileSuffix}",
            SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (!IsOwnedTemporaryFileName(Path.GetFileName(path)))
                {
                    continue;
                }

                var lastWrite = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
                if (now - lastWrite < StaleTemporaryFileAge)
                {
                    continue;
                }

                File.Delete(path);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
            }
        }

        return deleted;
    }

    internal static bool ShouldCleanupStaleTemporaryFiles(
        DateTimeOffset lastCleanupAt,
        DateTimeOffset now)
    {
        return lastCleanupAt == DateTimeOffset.MinValue ||
            now < lastCleanupAt ||
            now - lastCleanupAt >= StaleTemporaryFileCleanupInterval;
    }

    internal static string CreateTemporaryFileName()
    {
        return $"{TemporaryFilePrefix}{Guid.NewGuid():N}{TemporaryFileSuffix}";
    }

    internal static bool IsOwnedTemporaryFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            !fileName.StartsWith(TemporaryFilePrefix, StringComparison.Ordinal) ||
            !fileName.EndsWith(TemporaryFileSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        int tokenStart = TemporaryFilePrefix.Length;
        int tokenLength = fileName.Length - TemporaryFilePrefix.Length - TemporaryFileSuffix.Length;
        return tokenLength == 32 &&
            Guid.TryParseExact(fileName.AsSpan(tokenStart, tokenLength), "N", out _);
    }

    internal static string SanitizeFileName(string fileName)
    {
        string sanitized = Path.GetFileName(fileName.Trim());
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            sanitized = sanitized.Replace(invalid, '_');
        }

        sanitized = sanitized.Trim().TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return "remote-file";
        }

        string extension = Path.GetExtension(sanitized);
        if (extension.Length > MaxSafeExtensionLength)
        {
            extension = extension[..MaxSafeExtensionLength];
        }

        string baseName = Path.GetFileNameWithoutExtension(sanitized).Trim().TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "remote-file";
        }

        if (ReservedFileNames.Contains(baseName))
        {
            baseName = $"_{baseName}";
        }

        int maxBaseLength = Math.Max(1, MaxSafeFileNameLength - extension.Length);
        if (baseName.Length > maxBaseLength)
        {
            baseName = baseName[..maxBaseLength];
        }

        return $"{baseName}{extension}";
    }

    private static string RequireText(string? value, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException(message);
        }

        return value;
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
        catch (Exception ex) when (ex is IOException or
            UnauthorizedAccessException or
            NotSupportedException or
            ArgumentException or
            System.Security.SecurityException)
        {
        }
    }

    private void DisposeAndDeleteTransfer(IncomingTransfer transfer)
    {
        TryDisposeTransferStream(transfer.Stream);
        try
        {
            transfer.Hash.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        TryDeleteFile(transfer.TemporaryPath);
    }

    private void TryDisposeTransferStream(FileStream stream)
    {
        try
        {
            stream.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class IncomingTransfer
    {
        public IncomingTransfer(
            string transferId,
            string fileName,
            long fileLength,
            string finalPath,
            string temporaryPath,
            FileStream stream,
            bool requireChecksum)
        {
            TransferId = transferId;
            FileName = fileName;
            FileLength = fileLength;
            FinalPath = finalPath;
            TemporaryPath = temporaryPath;
            Stream = stream;
            Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            RequireChecksum = requireChecksum;
        }

        public string TransferId { get; }

        public string FileName { get; }

        public long FileLength { get; }

        public string FinalPath { get; }

        public string TemporaryPath { get; }

        public FileStream Stream { get; }

        public IncrementalHash Hash { get; }

        public bool RequireChecksum { get; }

        public long BytesReceived { get; set; }

        public int LastReportedProgressBucket { get; set; }

        public byte[]? ExpectedSha256 { get; set; }
    }
}
