using System.Diagnostics;
using System.IO.Compression;
using System.Security;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class RemoteFileTransferTests
{
    [Fact]
    public void EnsureSourceUnchangedAllowsUnchangedFile()
    {
        using var temp = TemporaryDirectory.Create();
        string filePath = Path.Combine(temp.Path, "stable.txt");
        File.WriteAllText(filePath, "stable");

        RemoteFileTransferSourceSnapshot snapshot = RemoteFileTransfer.CaptureSourceSnapshot(filePath);

        RemoteFileTransfer.EnsureSourceUnchanged(filePath, snapshot);
    }

    [Fact]
    public void EnsureSourceUnchangedRejectsChangedLength()
    {
        using var temp = TemporaryDirectory.Create();
        string filePath = Path.Combine(temp.Path, "changed-length.txt");
        File.WriteAllText(filePath, "before");
        RemoteFileTransferSourceSnapshot snapshot = RemoteFileTransfer.CaptureSourceSnapshot(filePath);

        File.AppendAllText(filePath, "-after");

        IOException ex = Assert.Throws<IOException>(() =>
            RemoteFileTransfer.EnsureSourceUnchanged(filePath, snapshot));
        Assert.Contains("发生变化", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureSourceUnchangedRejectsChangedTimestamp()
    {
        using var temp = TemporaryDirectory.Create();
        string filePath = Path.Combine(temp.Path, "changed-time.txt");
        File.WriteAllText(filePath, "same length");
        RemoteFileTransferSourceSnapshot snapshot = RemoteFileTransfer.CaptureSourceSnapshot(filePath);

        File.SetLastWriteTimeUtc(filePath, snapshot.LastWriteTimeUtc.AddSeconds(5));

        IOException ex = Assert.Throws<IOException>(() =>
            RemoteFileTransfer.EnsureSourceUnchanged(filePath, snapshot));
        Assert.Contains("发生变化", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateTemporaryDirectoryArchiveIncludesBaseDirectory()
    {
        using var temp = TemporaryDirectory.Create();
        string sourceDirectory = Path.Combine(temp.Path, "folder");
        string emptyDirectory = Path.Combine(sourceDirectory, "empty");
        Directory.CreateDirectory(emptyDirectory);
        File.WriteAllText(Path.Combine(sourceDirectory, "file.txt"), "hello");

        string archivePath = RemoteFileTransfer.CreateTemporaryDirectoryArchive(
            sourceDirectory,
            maxArchiveBytes: 16 * 1024,
            temp.Path);

        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        string[] entryNames = archive.Entries.Select(entry => entry.FullName).ToArray();
        Assert.Contains("folder/file.txt", entryNames);
        Assert.Contains("folder/empty/", entryNames);
    }

    [Theory]
    [InlineData(1970)]
    [InlineData(2150)]
    [InlineData(2024)]
    public void DirectoryArchiveClampsOnlyUnsupportedZipTimestampsAndPreservesSource(int year)
    {
        using var temp = TemporaryDirectory.Create();
        string sourceDirectory = Path.Combine(temp.Path, "folder");
        Directory.CreateDirectory(sourceDirectory);
        string filePath = Path.Combine(sourceDirectory, "payload.txt");
        byte[] payload = System.Text.Encoding.UTF8.GetBytes("archive payload 中文😀");
        File.WriteAllBytes(filePath, payload);
        var sourceTime = new DateTime(year, 4, 10, 12, 34, 56, DateTimeKind.Local);
        File.SetLastWriteTime(filePath, sourceTime);
        DateTime originalUtc = File.GetLastWriteTimeUtc(filePath);

        string archivePath = RemoteFileTransfer.CreateTemporaryDirectoryArchive(
            sourceDirectory, maxArchiveBytes: 16 * 1024, temp.Path);

        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        ZipArchiveEntry entry = Assert.Single(archive.Entries);
        DateTime expected = year < 1980 ? new DateTime(1980, 1, 1) :
            year > 2107 ? new DateTime(2107, 12, 31, 23, 59, 58) : sourceTime;
        Assert.Equal(expected, entry.LastWriteTime.DateTime);
        using var restored = new MemoryStream();
        using (Stream content = entry.Open()) content.CopyTo(restored);
        Assert.Equal(payload, restored.ToArray());
        Assert.Equal(payload, File.ReadAllBytes(filePath));
        Assert.Equal(originalUtc, File.GetLastWriteTimeUtc(filePath));
    }

    [Fact]
    public void CreateTemporaryDirectoryArchiveExcludesOutputInsideSourceTree()
    {
        using var temp = TemporaryDirectory.Create();
        string payloadPath = Path.Combine(temp.Path, "payload.txt");
        File.WriteAllText(payloadPath, "hello");

        string archivePath = RemoteFileTransfer.CreateTemporaryDirectoryArchive(
            temp.Path,
            maxArchiveBytes: 16 * 1024,
            temp.Path);

        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        string[] entryNames = archive.Entries.Select(entry => entry.FullName).ToArray();
        Assert.Contains(
            $"{Path.GetFileName(temp.Path)}/payload.txt",
            entryNames);
        Assert.DoesNotContain(
            entryNames,
            entry => entry.Contains("RemoteDesk-folder-", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ArchiveTraversalExcludesReparsePointsAndItsOwnOutput()
    {
        using var temp = TemporaryDirectory.Create();
        string archivePath = Path.Combine(temp.Path, "output.zip");

        Assert.True(RemoteFileTransfer.ShouldExcludeArchiveEntry(
            Path.Combine(temp.Path, "linked-directory"),
            FileAttributes.Directory | FileAttributes.ReparsePoint,
            archivePath));
        Assert.True(RemoteFileTransfer.ShouldExcludeArchiveEntry(
            archivePath.ToUpperInvariant(),
            FileAttributes.Archive,
            archivePath));
        Assert.False(RemoteFileTransfer.ShouldExcludeArchiveEntry(
            Path.Combine(temp.Path, "ordinary.txt"),
            FileAttributes.Archive,
            archivePath));
    }

    [Fact]
    public void CreateTemporaryDirectoryArchiveDeletesPartialArchiveWhenLimitExceeded()
    {
        using var temp = TemporaryDirectory.Create();
        string sourceDirectory = Path.Combine(temp.Path, "large-folder");
        Directory.CreateDirectory(sourceDirectory);
        byte[] bytes = new byte[4096];
        new Random(1234).NextBytes(bytes);
        File.WriteAllBytes(Path.Combine(sourceDirectory, "large.bin"), bytes);

        IOException ex = Assert.Throws<IOException>(() =>
            RemoteFileTransfer.CreateTemporaryDirectoryArchive(
                sourceDirectory,
                maxArchiveBytes: 256,
                temp.Path));

        Assert.Contains("超过传输上限", ex.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(temp.Path, "RemoteDesk-folder-*.zip"));
    }

    [Fact]
    public async Task CreateTemporaryDirectoryArchiveCancellationStopsPromptlyAndDeletesPartialArchive()
    {
        using var temp = TemporaryDirectory.Create();
        string sourceDirectory = Path.Combine(temp.Path, "large-folder");
        Directory.CreateDirectory(sourceDirectory);
        string sourcePath = Path.Combine(sourceDirectory, "large.bin");
        WriteDeterministicFile(sourcePath, 64 * 1024 * 1024);

        using var cancellation = new CancellationTokenSource();
        Task<string> archiveTask = Task.Run(() =>
            RemoteFileTransfer.CreateTemporaryDirectoryArchive(
                sourceDirectory,
                maxArchiveBytes: 128L * 1024 * 1024,
                temp.Path,
                cancellation.Token));

        Assert.True(
            SpinWait.SpinUntil(
                () => Directory.GetFiles(temp.Path, "RemoteDesk-folder-*.zip")
                    .Any(path => new FileInfo(path).Length >= 64 * 1024),
                TimeSpan.FromSeconds(10)),
            "打包未进入文件写入阶段。");

        var stopwatch = Stopwatch.StartNew();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await archiveTask);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"取消耗时 {stopwatch.Elapsed}。");
        Assert.Empty(Directory.GetFiles(temp.Path, "RemoteDesk-folder-*.zip"));
    }

    [Fact]
    public void CreateTemporaryDirectoryArchivePreCanceledLeavesNoArchive()
    {
        using var temp = TemporaryDirectory.Create();
        string sourceDirectory = Path.Combine(temp.Path, "folder");
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(Path.Combine(sourceDirectory, "payload.txt"), "payload");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            RemoteFileTransfer.CreateTemporaryDirectoryArchive(
                sourceDirectory,
                maxArchiveBytes: 16 * 1024,
                temp.Path,
                cancellation.Token));

        Assert.Empty(Directory.GetFiles(temp.Path, "RemoteDesk-folder-*.zip"));
    }

    [Theory]
    [InlineData(typeof(InvalidDataException))]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(SecurityException))]
    [InlineData(typeof(System.ComponentModel.Win32Exception))]
    [InlineData(typeof(System.Runtime.InteropServices.ExternalException))]
    public void RecoverableTransferExceptionsIncludePathAndSecurityFailures(Type exceptionType)
    {
        Exception exception = (Exception)Activator.CreateInstance(exceptionType, "transfer failed")!;

        Assert.True(RemoteFileTransfer.IsRecoverableTransferException(exception));
    }

    private static void WriteDeterministicFile(string path, int length)
    {
        byte[] buffer = new byte[1024 * 1024];
        var random = new Random(1234);
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        int remaining = length;
        while (remaining > 0)
        {
            random.NextBytes(buffer);
            int count = Math.Min(buffer.Length, remaining);
            output.Write(buffer, 0, count);
            remaining -= count;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"RemoteDesk.RemoteFileTransferTests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
