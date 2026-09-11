using Xunit;

namespace RemoteDesk.Tests;

public sealed class FileTransferConfirmationTests
{
    [Fact]
    public void CreateItemsReportsFileAndDirectoryTransferDetails()
    {
        using var temp = TemporaryDirectory.Create();
        string filePath = Path.Combine(temp.Path, "one.txt");
        File.WriteAllBytes(filePath, [1, 2, 3]);
        string directoryPath = Path.Combine(temp.Path, "folder");
        Directory.CreateDirectory(directoryPath);
        File.WriteAllBytes(Path.Combine(directoryPath, "two.bin"), [4, 5, 6, 7]);

        RemoteFilePastePlan plan = RemoteFileTransfer.CreatePastePlan(
            [filePath, directoryPath],
            maxFiles: 8,
            File.Exists,
            Directory.Exists,
            includeDirectories: true);

        IReadOnlyList<FileTransferConfirmationItem> items = FileTransferConfirmation.CreateItems(
            plan,
            (_item, transferName) => FileTransferConfirmation.FormatRemoteReceiveDestination(transferName));

        Assert.Collection(
            items,
            item =>
            {
                Assert.Equal("文件", item.Kind);
                Assert.Equal(filePath, item.SourcePath);
                Assert.Equal("one.txt", item.TransferName);
                Assert.Equal(3, item.SizeBytes);
                Assert.Contains("接收目录/one.txt", item.DestinationPath, StringComparison.Ordinal);
            },
            item =>
            {
                Assert.Equal("文件夹", item.Kind);
                Assert.Equal(directoryPath, item.SourcePath);
                Assert.Equal("folder.zip", item.TransferName);
                Assert.Equal(4, item.SizeBytes);
                Assert.Contains("接收目录/folder.zip", item.DestinationPath, StringComparison.Ordinal);
            });
    }

    [Fact]
    public void BuildPlanNoteMentionsDirectorySkippedItemsAndTruncation()
    {
        var plan = new RemoteFilePastePlan(
            Files: [@"C:\one.txt"],
            SkippedDirectories: 1,
            SkippedMissing: 2,
            Truncated: true,
            Items: [new RemoteFilePasteItem(@"C:\folder", RemoteFilePasteItemKind.Directory)]);

        string note = FileTransferConfirmation.BuildPlanNote(plan);

        Assert.Contains("文件夹会先打包为 zip", note, StringComparison.Ordinal);
        Assert.Contains("已跳过 2 个不存在或不可访问", note, StringComparison.Ordinal);
        Assert.Contains("已跳过 1 个文件夹", note, StringComparison.Ordinal);
        Assert.Contains("超过上限", note, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteDirectoryPreviewCanDeferExpensiveSizeTraversal()
    {
        var plan = new RemoteFilePastePlan(
            Files: [@"C:\folder"],
            SkippedDirectories: 0,
            SkippedMissing: 0,
            Truncated: false,
            Items: [new RemoteFilePasteItem(@"C:\folder", RemoteFilePasteItemKind.Directory)]);

        FileTransferConfirmationItem item = Assert.Single(
            FileTransferConfirmation.CreateItems(
                plan,
                (_item, transferName) => transferName,
                calculateDirectorySizes: false));
        string note = FileTransferConfirmation.BuildPlanNote(
            plan,
            directorySizesIncluded: false);

        Assert.Equal("文件夹", item.Kind);
        Assert.Equal("folder.zip", item.TransferName);
        Assert.Equal(0, item.SizeBytes);
        Assert.Contains("确认后打包", note, StringComparison.Ordinal);
        Assert.Contains("当前合计仅包含普通文件", note, StringComparison.Ordinal);
    }

    [Fact]
    public void DirectoryPreviewSizeSkipsReparsePointsAndBreaksCycles()
    {
        const string root = @"C:\root";
        const string linked = @"C:\root\linked";
        const string regularFile = @"C:\root\regular.bin";
        const string linkedFile = @"C:\root\linked-file.bin";

        long size = FileTransferConfirmation.CalculateDirectorySize(
            root,
            directory => directory == root ? [regularFile, linkedFile] : Array.Empty<string>(),
            directory => directory == root ? [root, linked] : Array.Empty<string>(),
            path => path is linked or linkedFile
                ? FileAttributes.ReparsePoint
                : path == regularFile
                    ? FileAttributes.Archive
                    : FileAttributes.Directory,
            file => file == regularFile ? 17 : 10_000);

        Assert.Equal(17, size);
    }

    [Fact]
    public void DirectoryPreviewSizeContainsLazyEnumerationFailures()
    {
        static IEnumerable<string> ThrowDuringEnumeration()
        {
            yield return @"C:\root\first.bin";
            throw new IOException("directory changed");
        }

        long size = FileTransferConfirmation.CalculateDirectorySize(
            @"C:\root",
            _ => ThrowDuringEnumeration(),
            _ => Array.Empty<string>(),
            path => path.EndsWith(".bin", StringComparison.Ordinal)
                ? FileAttributes.Archive
                : FileAttributes.Directory,
            _ => 5);

        Assert.Equal(5, size);
    }

    [Fact]
    public void DirectoryPreviewSizeSaturatesAboveTransferLimitWithoutOverflow()
    {
        long size = FileTransferConfirmation.CalculateDirectorySize(
            @"C:\root",
            _ => [@"C:\root\huge.bin", @"C:\root\extra.bin"],
            _ => Array.Empty<string>(),
            path => path.EndsWith(".bin", StringComparison.Ordinal)
                ? FileAttributes.Archive
                : FileAttributes.Directory,
            file => file.EndsWith("huge.bin", StringComparison.Ordinal)
                ? RemoteMessageCodec.MaxFileTransferBytes
                : long.MaxValue);

        Assert.Equal(RemoteMessageCodec.MaxFileTransferBytes + 1, size);
    }

    [Theory]
    [InlineData(0, 16L * 1024 * 1024)]
    [InlineData(100L * 1024 * 1024, 116L * 1024 * 1024)]
    [InlineData(800L * 1024 * 1024, 864L * 1024 * 1024)]
    [InlineData(1024L * 1024 * 1024, 1024L * 1024 * 1024)]
    public void DirectoryArchiveSizeAllowanceIsBounded(long sourceSize, long expectedMaximum)
    {
        Assert.Equal(
            expectedMaximum,
            FileTransferConfirmation.GetMaximumDirectoryArchiveSize(sourceSize));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1024L * 1024 * 1024 + 1)]
    public void DirectoryArchiveSizeAllowanceRejectsInvalidPreviewSizes(long sourceSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FileTransferConfirmation.GetMaximumDirectoryArchiveSize(sourceSize));
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
                $"RemoteDesk.FileTransferConfirmationTests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
