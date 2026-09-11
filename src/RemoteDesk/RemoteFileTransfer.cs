using System.Buffers;
using System.IO.Compression;

namespace RemoteDesk;

internal enum RemoteFilePasteItemKind
{
    File,
    Directory
}

internal sealed record RemoteFilePasteItem(string Path, RemoteFilePasteItemKind Kind);

internal sealed record RemoteFilePastePlan(
    IReadOnlyList<string> Files,
    int SkippedDirectories,
    int SkippedMissing,
    bool Truncated,
    IReadOnlyList<RemoteFilePasteItem>? Items = null)
{
    public IReadOnlyList<RemoteFilePasteItem> TransferItems =>
        Items ?? Files.Select(path => new RemoteFilePasteItem(path, RemoteFilePasteItemKind.File)).ToArray();
}

internal readonly record struct RemoteFilePasteResult(
    int SentFiles,
    int FailedFiles,
    int SkippedDirectories,
    int SkippedMissing,
    bool Truncated,
    int ArchivedDirectories = 0,
    string? FailureMessage = null);

internal readonly record struct RemoteFileDropPasteResult(
    RemoteFilePasteResult TransferResult,
    bool RemotePasteRequested);

internal readonly record struct RemoteFileTransferSourceSnapshot(
    long Length,
    DateTime LastWriteTimeUtc);

internal static class RemoteFileTransfer
{
    private const string SourceChangedMessage = "文件在传输过程中发生变化，请重新发送。";

    public static RemoteFilePastePlan CreatePastePlan(
        IEnumerable<string> paths,
        int maxFiles,
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists,
        bool includeDirectories = false)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(directoryExists);

        int limit = Math.Max(0, maxFiles);
        var files = new List<string>(limit);
        var items = new List<RemoteFilePasteItem>(limit);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int skippedDirectories = 0;
        int skippedMissing = 0;
        bool truncated = false;

        foreach (string? rawPath in paths)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }

            string path = rawPath.Trim();
            if (!seen.Add(path))
            {
                continue;
            }

            if (directoryExists(path))
            {
                if (includeDirectories)
                {
                    if (files.Count >= limit)
                    {
                        truncated = true;
                        continue;
                    }

                    files.Add(path);
                    items.Add(new RemoteFilePasteItem(path, RemoteFilePasteItemKind.Directory));
                }
                else
                {
                    skippedDirectories++;
                }

                continue;
            }

            if (!fileExists(path))
            {
                skippedMissing++;
                continue;
            }

            if (files.Count >= limit)
            {
                truncated = true;
                continue;
            }

            files.Add(path);
            items.Add(new RemoteFilePasteItem(path, RemoteFilePasteItemKind.File));
        }

        return new RemoteFilePastePlan(files, skippedDirectories, skippedMissing, truncated, items);
    }

    public static bool IsRecoverableTransferException(Exception ex)
    {
        return ex is InvalidDataException
            or IOException or UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException
            or InvalidOperationException or NotSupportedException or ObjectDisposedException or ArgumentException
            or System.Security.SecurityException
            or System.ComponentModel.Win32Exception
            or System.Runtime.InteropServices.ExternalException;
    }

    public static RemoteFileTransferSourceSnapshot CaptureSourceSnapshot(string path, FileStream? stream = null)
    {
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("文件不存在。", path);
        }

        long length = stream?.Length ?? fileInfo.Length;
        return new RemoteFileTransferSourceSnapshot(length, fileInfo.LastWriteTimeUtc);
    }

    public static void EnsureSourceUnchanged(string path, RemoteFileTransferSourceSnapshot snapshot)
    {
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("文件在传输过程中被删除。", path);
        }

        if (fileInfo.Length != snapshot.Length || fileInfo.LastWriteTimeUtc != snapshot.LastWriteTimeUtc)
        {
            throw new IOException(SourceChangedMessage);
        }
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        int unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{bytes} {units[unitIndex]}" : $"{value:F1} {units[unitIndex]}";
    }

    public static string CreateTemporaryDirectoryArchive(
        string directoryPath,
        CancellationToken cancellationToken = default)
    {
        return CreateTemporaryDirectoryArchive(
            directoryPath,
            RemoteMessageCodec.MaxFileTransferBytes,
            Path.GetTempPath(),
            cancellationToken);
    }

    internal static string CreateTemporaryDirectoryArchive(
        string directoryPath,
        long maxArchiveBytes,
        string temporaryDirectory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string archivePath = Path.Combine(
            temporaryDirectory,
            $"RemoteDesk-folder-{Guid.NewGuid():N}.zip");
        try
        {
            CreateDirectoryArchive(
                directoryPath,
                archivePath,
                maxArchiveBytes,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return archivePath;
        }
        catch
        {
            TryDeleteTemporaryFile(archivePath);
            throw;
        }
    }

    private static void CreateDirectoryArchive(
        string directoryPath,
        string archivePath,
        long maxArchiveBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (maxArchiveBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxArchiveBytes));
        }

        string root = Path.GetFullPath(directoryPath);
        var rootDirectory = new DirectoryInfo(root);
        if (!rootDirectory.Exists)
        {
            throw new DirectoryNotFoundException("文件夹不存在。");
        }

        rootDirectory.Refresh();
        if (rootDirectory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("无法打包符号链接或重解析点文件夹。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
        cancellationToken.ThrowIfCancellationRequested();
        string normalizedArchivePath = Path.GetFullPath(archivePath);
        using var output = new FileStream(
            normalizedArchivePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024);
        using var limitedOutput = new LimitedArchiveWriteStream(
            output,
            maxArchiveBytes,
            cancellationToken);
        using var archive = new ZipArchive(limitedOutput, ZipArchiveMode.Create);

        string parent = rootDirectory.Parent?.FullName ?? rootDirectory.FullName;
        var pendingDirectories = new Stack<DirectoryInfo>();
        pendingDirectories.Push(rootDirectory);
        byte[] copyBuffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (pendingDirectories.TryPop(out DirectoryInfo? directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool hasIncludedEntry = false;
                foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    entry.Refresh();
                    cancellationToken.ThrowIfCancellationRequested();
                    FileAttributes attributes = entry.Attributes;
                    if (ShouldExcludeArchiveEntry(entry.FullName, attributes, normalizedArchivePath))
                    {
                        continue;
                    }

                    hasIncludedEntry = true;
                    if (attributes.HasFlag(FileAttributes.Directory))
                    {
                        pendingDirectories.Push(new DirectoryInfo(entry.FullName));
                        continue;
                    }

                    CreateFileArchiveEntry(
                        archive,
                        entry,
                        ToArchiveEntryName(parent, entry.FullName),
                        copyBuffer,
                        cancellationToken);
                }

                if (!hasIncludedEntry)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    archive.CreateEntry(ToArchiveEntryName(parent, directory.FullName) + "/");
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(copyBuffer);
        }
    }

    private static void CreateFileArchiveEntry(
        ZipArchive archive,
        FileSystemInfo source,
        string entryName,
        byte[] copyBuffer,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ZipArchiveEntry archiveEntry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        // DOS timestamps in ZIP have a narrower range than NTFS. Match the
        // Linux sender: clamp archive metadata, never mutate the source file.
        DateTime modified = source.LastWriteTime;
        archiveEntry.LastWriteTime = modified.Year < 1980
            ? new DateTime(1980, 1, 1, 0, 0, 0, modified.Kind)
            : modified.Year > 2107
                ? new DateTime(2107, 12, 31, 23, 59, 58, modified.Kind)
                : modified;
        cancellationToken.ThrowIfCancellationRequested();

        using var input = new FileStream(
            source.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            copyBuffer.Length,
            FileOptions.SequentialScan);
        using Stream destination = archiveEntry.Open();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int bytesRead = input.Read(copyBuffer, 0, copyBuffer.Length);
            cancellationToken.ThrowIfCancellationRequested();
            if (bytesRead == 0)
            {
                break;
            }

            destination.Write(copyBuffer, 0, bytesRead);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    internal static bool ShouldExcludeArchiveEntry(
        string entryPath,
        FileAttributes attributes,
        string archivePath)
    {
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return true;
        }

        return string.Equals(
            Path.GetFullPath(entryPath),
            Path.GetFullPath(archivePath),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string ToArchiveEntryName(string basePath, string path)
    {
        string relative = Path.GetRelativePath(basePath, path);
        if (relative == "." || string.IsNullOrWhiteSpace(relative))
        {
            relative = "folder";
        }

        return relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }

    public static string CreateDirectoryArchiveFileName(string directoryPath)
    {
        string trimmedPath = Path.TrimEndingDirectorySeparator(directoryPath.Trim());
        string name = Path.GetFileName(trimmedPath);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "folder";
        }

        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        name = name.Trim().TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "folder";
        }

        const int maxBaseLength = 160;
        if (name.Length > maxBaseLength)
        {
            name = name[..maxBaseLength];
        }

        return name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? name
            : $"{name}.zip";
    }

    public static string GetTransferDisplayName(string path)
    {
        string trimmedPath = path.Trim();
        if (trimmedPath.Length > 0)
        {
            trimmedPath = Path.TrimEndingDirectorySeparator(trimmedPath);
        }

        string name = Path.GetFileName(trimmedPath);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    public static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class LimitedArchiveWriteStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _maxBytes;
        private readonly CancellationToken _cancellationToken;
        private long _bytesWritten;

        public LimitedArchiveWriteStream(
            Stream inner,
            long maxBytes,
            CancellationToken cancellationToken)
        {
            _inner = inner;
            _maxBytes = maxBytes;
            _cancellationToken = cancellationToken;
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _bytesWritten;

        public override long Position
        {
            get => _bytesWritten;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            _cancellationToken.ThrowIfCancellationRequested();
            _inner.Flush();
            _cancellationToken.ThrowIfCancellationRequested();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCanWrite(count);
            _inner.Write(buffer, offset, count);
            _bytesWritten += count;
            _cancellationToken.ThrowIfCancellationRequested();
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCanWrite(buffer.Length);
            _inner.Write(buffer);
            _bytesWritten += buffer.Length;
            _cancellationToken.ThrowIfCancellationRequested();
        }

        public override void WriteByte(byte value)
        {
            EnsureCanWrite(1);
            _inner.WriteByte(value);
            _bytesWritten++;
            _cancellationToken.ThrowIfCancellationRequested();
        }

        private void EnsureCanWrite(int count)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (count < 0 || _bytesWritten > _maxBytes - count)
            {
                throw new IOException($"压缩后的文件夹超过传输上限：{FormatBytes(_maxBytes)}。");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
