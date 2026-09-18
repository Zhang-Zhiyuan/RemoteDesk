using System.Security.Cryptography;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class FileTransferReceiverTests
{
    [Fact]
    public async Task WriteChunkAsyncWritesOnlyDecodedPayloadSlice()
    {
        using var temp = TemporaryDirectory.Create();
        var receiver = new FileTransferReceiver(_ => { }, () => temp.Path);
        const string transferId = "transfer-memory-slice";
        byte[] expectedBytes = [6, 2, 6, 4, 3];
        byte[] chunkPayload = RemoteMessageCodec.EncodeFileTransferChunk(
            transferId,
            0,
            expectedBytes);
        const int prefixLength = 17;
        byte[] backing = Enumerable.Repeat((byte)0xee, prefixLength + chunkPayload.Length + 19).ToArray();
        chunkPayload.CopyTo(backing, prefixLength);

        receiver.Start(Decode(RemoteMessageCodec.EncodeFileTransferStart(
            transferId,
            "slice.bin",
            expectedBytes.Length)));
        await receiver.WriteChunkAsync(
            RemoteMessageCodec.DecodeControl(backing.AsMemory(prefixLength, chunkPayload.Length)),
            CancellationToken.None);
        await receiver.CompleteAsync(
            Decode(RemoteMessageCodec.EncodeFileTransferComplete(transferId)),
            CancellationToken.None);

        Assert.Equal(expectedBytes, await File.ReadAllBytesAsync(Path.Combine(temp.Path, "slice.bin")));
        AssertNoOwnedTemporaryFiles(temp.Path);
    }

    [Fact]
    public async Task WriteChunkAsyncReportsReceiveProgressAtTenPercentBuckets()
    {
        using var temp = TemporaryDirectory.Create();
        var receiver = new FileTransferReceiver(_ => { }, () => temp.Path);
        const string transferId = "transfer-progress";
        const int fileLength = 1_000;
        byte[] chunk = new byte[50];
        var progressMessages = new List<string>();

        receiver.Start(Decode(RemoteMessageCodec.EncodeFileTransferStart(
            transferId,
            "progress.bin",
            fileLength)));

        for (int offset = 0; offset < fileLength; offset += chunk.Length)
        {
            string? progress = await receiver.WriteChunkAsync(
                Decode(RemoteMessageCodec.EncodeFileTransferChunk(transferId, offset, chunk)),
                CancellationToken.None);
            if (progress is not null)
            {
                progressMessages.Add(progress);
            }
        }

        await receiver.CompleteAsync(
            Decode(RemoteMessageCodec.EncodeFileTransferComplete(transferId)),
            CancellationToken.None);

        Assert.Equal(9, progressMessages.Count);
        for (int percent = 10; percent < 100; percent += 10)
        {
            Assert.Contains($" {percent}% ", progressMessages[(percent / 10) - 1], StringComparison.Ordinal);
        }

        Assert.DoesNotContain(progressMessages, message => message.Contains(" 100% ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompleteAsyncUsesUniqueNameWhenRequestedFileNameIsDirectory()
    {
        using var temp = TemporaryDirectory.Create();
        string collidingDirectory = Path.Combine(temp.Path, "report.txt");
        Directory.CreateDirectory(collidingDirectory);
        byte[] fileBytes = [1, 2, 3, 4, 5];
        var receiver = new FileTransferReceiver(_ => { }, () => temp.Path);
        const string transferId = "transfer-directory-collision";

        receiver.Start(Decode(RemoteMessageCodec.EncodeFileTransferStart(
            transferId,
            @"..\report.txt",
            fileBytes.Length)));
        await receiver.WriteChunkAsync(
            Decode(RemoteMessageCodec.EncodeFileTransferChunk(transferId, 0, fileBytes)),
            CancellationToken.None);

        string message = await receiver.CompleteAsync(
            Decode(RemoteMessageCodec.EncodeFileTransferComplete(transferId)),
            CancellationToken.None);

        string savedPath = Path.Combine(temp.Path, "report (1).txt");
        Assert.True(Directory.Exists(collidingDirectory));
        Assert.True(File.Exists(savedPath));
        Assert.Equal(savedPath, receiver.LastCompletedFilePath);
        Assert.Equal(fileBytes, await File.ReadAllBytesAsync(savedPath));
        AssertNoOwnedTemporaryFiles(temp.Path);
        Assert.Contains(savedPath, message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteAsyncUsesNewUniqueNameWhenDestinationAppearsBeforeMove()
    {
        using var temp = TemporaryDirectory.Create();
        var receiver = new FileTransferReceiver(_ => { }, () => temp.Path);
        const string transferId = "transfer-late-collision";
        byte[] fileBytes = [8, 6, 7, 5];
        string collidingPath = Path.Combine(temp.Path, "late.txt");

        receiver.Start(Decode(RemoteMessageCodec.EncodeFileTransferStart(
            transferId,
            "late.txt",
            fileBytes.Length)));
        await receiver.WriteChunkAsync(
            Decode(RemoteMessageCodec.EncodeFileTransferChunk(transferId, 0, fileBytes)),
            CancellationToken.None);
        await File.WriteAllTextAsync(collidingPath, "existing");

        string message = await receiver.CompleteAsync(
            Decode(RemoteMessageCodec.EncodeFileTransferComplete(transferId)),
            CancellationToken.None);

        string savedPath = Path.Combine(temp.Path, "late (1).txt");
        Assert.Equal("existing", await File.ReadAllTextAsync(collidingPath));
        Assert.True(File.Exists(savedPath));
        Assert.Equal(fileBytes, await File.ReadAllBytesAsync(savedPath));
        AssertNoOwnedTemporaryFiles(temp.Path);
        Assert.Contains(savedPath, message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteAsyncSanitizesReservedFileNameWithTrailingBaseSpace()
    {
        using var temp = TemporaryDirectory.Create();
        var receiver = new FileTransferReceiver(_ => { }, () => temp.Path);
        const string transferId = "transfer-reserved-name";
        byte[] fileBytes = [11, 22, 33];

        receiver.Start(Decode(RemoteMessageCodec.EncodeFileTransferStart(
            transferId,
            @"..\CON .txt",
            fileBytes.Length)));
        await receiver.WriteChunkAsync(
            Decode(RemoteMessageCodec.EncodeFileTransferChunk(transferId, 0, fileBytes)),
            CancellationToken.None);

        await receiver.CompleteAsync(
            Decode(RemoteMessageCodec.EncodeFileTransferComplete(transferId)),
            CancellationToken.None);

        string savedPath = Path.Combine(temp.Path, "_CON.txt");
        Assert.True(File.Exists(savedPath));
        Assert.Equal(fileBytes, await File.ReadAllBytesAsync(savedPath));
    }

    [Fact]
    public async Task CompleteAsyncSavesEmptyFile()
    {
        using var temp = TemporaryDirectory.Create();
        var receiver = new FileTransferReceiver(_ => { }, () => temp.Path);
        const string transferId = "transfer-empty-file";

        receiver.Start(Decode(RemoteMessageCodec.EncodeFileTransferStart(
            transferId,
            "empty.txt",
            fileLength: 0)));

        string message = await receiver.CompleteAsync(
            Decode(RemoteMessageCodec.EncodeFileTransferComplete(transferId)),
            CancellationToken.None);

        string savedPath = Path.Combine(temp.Path, "empty.txt");
        Assert.True(File.Exists(savedPath));
        Assert.Equal(0, new FileInfo(savedPath).Length);
        AssertNoOwnedTemporaryFiles(temp.Path);
        Assert.Contains(savedPath, message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteAsyncReportsVerifiedChecksumWhenChecksumMatches()
    {
        using var temp = TemporaryDirectory.Create();
        var receiver = new FileTransferReceiver(_ => { }, () => temp.Path);
        const string transferId = "transfer-with-checksum";
        byte[] fileBytes = [9, 8, 7, 6, 5, 4, 3];
        string checksumHex = Convert.ToHexString(SHA256.HashData(fileBytes)).ToLowerInvariant();

        receiver.Start(Decode(RemoteMessageCodec.EncodeFileTransferStart(
            transferId,
            "checked.bin",
            fileBytes.Length)));
        await receiver.WriteChunkAsync(
            Decode(RemoteMessageCodec.EncodeFileTransferChunk(transferId, 0, fileBytes)),
            CancellationToken.None);
        receiver.SetExpectedChecksum(Decode(RemoteMessageCodec.EncodeFileTransferChecksum(transferId, checksumHex)));

        string message = await receiver.CompleteAsync(
            Decode(RemoteMessageCodec.EncodeFileTransferComplete(transferId)),
            CancellationToken.None);

        Assert.Contains("SHA-256 已校验", message, StringComparison.Ordinal);
        Assert.Equal(fileBytes, await File.ReadAllBytesAsync(Path.Combine(temp.Path, "checked.bin")));
    }

    [Fact]
    public async Task CompleteAsyncRejectsMismatchedChecksum()
    {
        using var temp = TemporaryDirectory.Create();
        var receiver = new FileTransferReceiver(_ => { }, () => temp.Path);
        const string transferId = "transfer-bad-checksum";
        byte[] fileBytes = [1, 3, 5, 7, 9];
        string wrongChecksum = new('0', 64);

        receiver.Start(Decode(RemoteMessageCodec.EncodeFileTransferStart(
            transferId,
            "bad.bin",
            fileBytes.Length)));
        await receiver.WriteChunkAsync(
            Decode(RemoteMessageCodec.EncodeFileTransferChunk(transferId, 0, fileBytes)),
            CancellationToken.None);
        receiver.SetExpectedChecksum(Decode(RemoteMessageCodec.EncodeFileTransferChecksum(transferId, wrongChecksum)));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            receiver.CompleteAsync(
                Decode(RemoteMessageCodec.EncodeFileTransferComplete(transferId)),
                CancellationToken.None));

        Assert.Throws<InvalidDataException>(() =>
            receiver.Cancel(Decode(RemoteMessageCodec.EncodeFileTransferCancel(
                transferId,
                "late cancel"))));
        Assert.False(File.Exists(Path.Combine(temp.Path, "bad.bin")));
        AssertNoOwnedTemporaryFiles(temp.Path);
    }

    [Fact]
    public async Task CompleteAsyncRejectsMissingChecksumWhenRequired()
    {
        using var temp = TemporaryDirectory.Create();
        var receiver = new FileTransferReceiver(_ => { }, () => temp.Path)
        {
            RequireChecksum = true
        };
        const string transferId = "transfer-missing-checksum";
        byte[] fileBytes = [2, 4, 6, 8];

        receiver.Start(Decode(RemoteMessageCodec.EncodeFileTransferStart(
            transferId,
            "missing-checksum.bin",
            fileBytes.Length)));
        await receiver.WriteChunkAsync(
            Decode(RemoteMessageCodec.EncodeFileTransferChunk(transferId, 0, fileBytes)),
            CancellationToken.None);

        InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            receiver.CompleteAsync(
                Decode(RemoteMessageCodec.EncodeFileTransferComplete(transferId)),
                CancellationToken.None));

        Assert.Throws<InvalidDataException>(() =>
            receiver.Cancel(Decode(RemoteMessageCodec.EncodeFileTransferCancel(
                transferId,
                "late cancel"))));
        Assert.Contains("未发送文件 SHA-256 校验值", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(temp.Path, "missing-checksum.bin")));
        AssertNoOwnedTemporaryFiles(temp.Path);
    }

    [Fact]
    public async Task CompleteAsyncRejectsIncompleteTransferAndDeletesTemporaryFile()
    {
        using var temp = TemporaryDirectory.Create();
        var receiver = new FileTransferReceiver(_ => { }, () => temp.Path);
        const string transferId = "transfer-incomplete";
        byte[] partialBytes = [1, 2, 3];

        receiver.Start(Decode(RemoteMessageCodec.EncodeFileTransferStart(
            transferId,
            "incomplete.bin",
            fileLength: partialBytes.Length + 1)));
        await receiver.WriteChunkAsync(
            Decode(RemoteMessageCodec.EncodeFileTransferChunk(transferId, 0, partialBytes)),
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            receiver.CompleteAsync(
                Decode(RemoteMessageCodec.EncodeFileTransferComplete(transferId)),
                CancellationToken.None));

        Assert.Throws<InvalidDataException>(() =>
            receiver.Cancel(Decode(RemoteMessageCodec.EncodeFileTransferCancel(
                transferId,
                "late cancel"))));
        Assert.False(File.Exists(Path.Combine(temp.Path, "incomplete.bin")));
        AssertNoOwnedTemporaryFiles(temp.Path);
    }

    [Fact]
    public async Task WriteChunkAsyncRejectsOversizedChunkAndDeletesTemporaryFile()
    {
        using var temp = TemporaryDirectory.Create();
        var receiver = new FileTransferReceiver(_ => { }, () => temp.Path);
        const string transferId = "transfer-oversized-chunk";
        byte[] oversizedBytes = [1, 2, 3, 4, 5];

        receiver.Start(Decode(RemoteMessageCodec.EncodeFileTransferStart(
            transferId,
            "oversized.bin",
            fileLength: oversizedBytes.Length - 1)));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            receiver.WriteChunkAsync(
                Decode(RemoteMessageCodec.EncodeFileTransferChunk(transferId, 0, oversizedBytes)),
                CancellationToken.None));

        Assert.Throws<InvalidDataException>(() =>
            receiver.Cancel(Decode(RemoteMessageCodec.EncodeFileTransferCancel(
                transferId,
                "late cancel"))));
        Assert.False(File.Exists(Path.Combine(temp.Path, "oversized.bin")));
        AssertNoOwnedTemporaryFiles(temp.Path);
    }

    [Fact]
    public async Task CancelDeletesTemporaryTransferFile()
    {
        using var temp = TemporaryDirectory.Create();
        var receiver = new FileTransferReceiver(_ => { }, () => temp.Path);
        const string transferId = "transfer-cancel";
        byte[] fileBytes = [10, 20, 30, 40];

        receiver.Start(Decode(RemoteMessageCodec.EncodeFileTransferStart(
            transferId,
            "cancel-me.bin",
            fileBytes.Length)));
        await receiver.WriteChunkAsync(
            Decode(RemoteMessageCodec.EncodeFileTransferChunk(transferId, 0, fileBytes)),
            CancellationToken.None);

        string message = receiver.Cancel(Decode(RemoteMessageCodec.EncodeFileTransferCancel(
            transferId,
            "sender failed")));

        Assert.Contains("sender failed", message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(temp.Path, "cancel-me.bin")));
        AssertNoOwnedTemporaryFiles(temp.Path);
    }

    [Fact]
    public async Task FailedReplacementStartKeepsActiveTransferIntact()
    {
        using var temp = TemporaryDirectory.Create();
        int directoryRequests = 0;
        var receiver = new FileTransferReceiver(
            _ => { },
            () => Interlocked.Increment(ref directoryRequests) == 1 ? temp.Path : string.Empty);
        const string activeTransferId = "transfer-active";
        byte[] firstChunk = [1, 2];
        byte[] secondChunk = [3, 4];

        receiver.Start(Decode(RemoteMessageCodec.EncodeFileTransferStart(
            activeTransferId,
            "active.bin",
            firstChunk.Length + secondChunk.Length)));
        await receiver.WriteChunkAsync(
            Decode(RemoteMessageCodec.EncodeFileTransferChunk(activeTransferId, 0, firstChunk)),
            CancellationToken.None);

        Assert.Throws<InvalidOperationException>(() =>
            receiver.Start(Decode(RemoteMessageCodec.EncodeFileTransferStart(
                "replacement",
                "replacement.bin",
                fileLength: 1))));

        await receiver.WriteChunkAsync(
            Decode(RemoteMessageCodec.EncodeFileTransferChunk(
                activeTransferId,
                firstChunk.Length,
                secondChunk)),
            CancellationToken.None);
        await receiver.CompleteAsync(
            Decode(RemoteMessageCodec.EncodeFileTransferComplete(activeTransferId)),
            CancellationToken.None);

        Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(Path.Combine(temp.Path, "active.bin")));
        Assert.False(File.Exists(Path.Combine(temp.Path, "replacement.bin")));
    }

    [Fact]
    public async Task ConcurrentReceiversReservePrivateTemporaryFilesAndCommitUniqueNames()
    {
        using var temp = TemporaryDirectory.Create();
        using var firstReceiver = new FileTransferReceiver(_ => { }, () => temp.Path);
        using var secondReceiver = new FileTransferReceiver(_ => { }, () => temp.Path);
        byte[] firstBytes = [1, 2, 3];
        byte[] secondBytes = [4, 5, 6];

        firstReceiver.Start(Decode(RemoteMessageCodec.EncodeFileTransferStart(
            "concurrent-first",
            "shared.bin",
            firstBytes.Length)));
        secondReceiver.Start(Decode(RemoteMessageCodec.EncodeFileTransferStart(
            "concurrent-second",
            "shared.bin",
            secondBytes.Length)));

        Assert.Equal(2, GetOwnedTemporaryFiles(temp.Path).Length);
        await firstReceiver.WriteChunkAsync(
            Decode(RemoteMessageCodec.EncodeFileTransferChunk("concurrent-first", 0, firstBytes)),
            CancellationToken.None);
        await secondReceiver.WriteChunkAsync(
            Decode(RemoteMessageCodec.EncodeFileTransferChunk("concurrent-second", 0, secondBytes)),
            CancellationToken.None);

        await Task.WhenAll(
            firstReceiver.CompleteAsync(
                Decode(RemoteMessageCodec.EncodeFileTransferComplete("concurrent-first")),
                CancellationToken.None),
            secondReceiver.CompleteAsync(
                Decode(RemoteMessageCodec.EncodeFileTransferComplete("concurrent-second")),
                CancellationToken.None));

        string[] savedFiles = Directory.GetFiles(temp.Path, "shared*.bin");
        Assert.Equal(2, savedFiles.Length);
        byte[][] savedContents = await Task.WhenAll(savedFiles.Select(path => File.ReadAllBytesAsync(path)));
        Assert.Contains(savedContents, bytes => bytes.SequenceEqual(firstBytes));
        Assert.Contains(savedContents, bytes => bytes.SequenceEqual(secondBytes));
        AssertNoOwnedTemporaryFiles(temp.Path);
    }

    [Fact]
    public async Task CommitClipboardFilePasteBatchKeepsCompletedFilesWhenClipboardWriteFails()
    {
        using var temp = TemporaryDirectory.Create();
        int clipboardWrites = 0;
        int pasteShortcuts = 0;
        string[]? retriedFiles = null;
        var receiver = new FileTransferReceiver(
            _ => { },
            () => temp.Path,
            setFileDropListAsync: paths =>
            {
                clipboardWrites++;
                string[] pathArray = paths.ToArray();
                if (clipboardWrites == 1)
                {
                    throw new InvalidOperationException("clipboard busy");
                }

                retriedFiles = pathArray;
                return Task.CompletedTask;
            },
            sendPasteShortcut: () => pasteShortcuts++);
        const string transferId = "transfer-drop-paste";
        byte[] fileBytes = [9, 8, 7, 6];

        receiver.BeginClipboardFilePasteBatch();
        receiver.Start(Decode(RemoteMessageCodec.EncodeFileTransferStart(
            transferId,
            "drop.txt",
            fileBytes.Length)));
        await receiver.WriteChunkAsync(
            Decode(RemoteMessageCodec.EncodeFileTransferChunk(transferId, 0, fileBytes)),
            CancellationToken.None);
        await receiver.CompleteAsync(
            Decode(RemoteMessageCodec.EncodeFileTransferComplete(transferId)),
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            receiver.CommitClipboardFilePasteBatchAsync());
        string retryMessage = await receiver.CommitClipboardFilePasteBatchAsync();

        Assert.Equal(2, clipboardWrites);
        Assert.Equal(1, pasteShortcuts);
        string savedPath = Assert.Single(retriedFiles!);
        Assert.Equal(Path.Combine(temp.Path, "drop.txt"), savedPath);
        Assert.Contains("触发当前位置粘贴", retryMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommitClipboardFilePasteBatchSkipsMissingTemporaryFiles()
    {
        using var temp = TemporaryDirectory.Create();
        string[]? clipboardFiles = null;
        int pasteShortcuts = 0;
        var receiver = new FileTransferReceiver(
            _ => { },
            () => temp.Path,
            setFileDropListAsync: paths =>
            {
                clipboardFiles = paths.ToArray();
                return Task.CompletedTask;
            },
            sendPasteShortcut: () => pasteShortcuts++);

        receiver.BeginClipboardFilePasteBatch();
        await ReceiveSmallFileAsync(receiver, "drop-existing", "existing.txt", [1]);
        await ReceiveSmallFileAsync(receiver, "drop-missing", "missing.txt", [2]);
        File.Delete(Path.Combine(temp.Path, "missing.txt"));

        string message = await receiver.CommitClipboardFilePasteBatchAsync();

        Assert.NotNull(clipboardFiles);
        Assert.Equal([Path.Combine(temp.Path, "existing.txt")], clipboardFiles);
        Assert.Equal(1, pasteShortcuts);
        Assert.Contains("跳过 1 个已不存在", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommitClipboardFilePasteBatchDoesNotPasteWhenAllTemporaryFilesAreMissing()
    {
        using var temp = TemporaryDirectory.Create();
        int clipboardWrites = 0;
        int pasteShortcuts = 0;
        var receiver = new FileTransferReceiver(
            _ => { },
            () => temp.Path,
            setFileDropListAsync: paths =>
            {
                clipboardWrites++;
                return Task.CompletedTask;
            },
            sendPasteShortcut: () => pasteShortcuts++);

        receiver.BeginClipboardFilePasteBatch();
        await ReceiveSmallFileAsync(receiver, "drop-missing-all", "gone.txt", [3]);
        File.Delete(Path.Combine(temp.Path, "gone.txt"));

        string message = await receiver.CommitClipboardFilePasteBatchAsync();

        Assert.Equal(0, clipboardWrites);
        Assert.Equal(0, pasteShortcuts);
        Assert.Contains("暂存文件已不存在", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommitReturnedClipboardFileBatchWritesAllCompletedFilesToClipboard()
    {
        using var temp = TemporaryDirectory.Create();
        string[]? clipboardFiles = null;
        var receiver = new FileTransferReceiver(
            _ => { },
            () => temp.Path,
            setFileDropListAsync: paths =>
            {
                clipboardFiles = paths.ToArray();
                return Task.CompletedTask;
            });

        receiver.BeginReturnedClipboardFileBatch();
        await ReceiveSmallFileAsync(receiver, "return-1", "first.txt", [1, 2, 3]);
        await ReceiveSmallFileAsync(receiver, "return-2", "second.txt", [4, 5]);

        string message = await receiver.CommitReturnedClipboardFileBatchAsync();

        Assert.Equal(
            new[]
            {
                Path.Combine(temp.Path, "first.txt"),
                Path.Combine(temp.Path, "second.txt")
            },
            clipboardFiles);
        Assert.Contains("放入本机剪贴板", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommitReturnedClipboardFileBatchReturnsImmutableActualLocalPaths()
    {
        using var temp = TemporaryDirectory.Create();
        var receiver = new FileTransferReceiver(
            _ => { },
            () => temp.Path,
            setFileDropListAsync: _ => Task.CompletedTask);

        receiver.BeginReturnedClipboardFileBatch();
        await ReceiveSmallFileAsync(receiver, "return-paths", "actual.txt", [1, 2, 3]);

        ReturnedClipboardFileCommitResult result =
            await receiver.CommitReturnedClipboardFileBatchWithResultAsync();

        string savedPath = Assert.Single(result.LocalPaths);
        Assert.Equal(Path.Combine(temp.Path, "actual.txt"), savedPath);
        Assert.True(File.Exists(savedPath));
        Assert.True(result.ClipboardUpdated);
        IList<string> readOnlyPaths = Assert.IsAssignableFrom<IList<string>>(result.LocalPaths);
        Assert.True(readOnlyPaths.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => readOnlyPaths[0] = "changed.txt");
    }

    [Fact]
    public async Task DragOutCommitReturnsLocalPathsWhenClipboardWriteIsBusy()
    {
        using var temp = TemporaryDirectory.Create();
        var receiver = new FileTransferReceiver(
            _ => { },
            () => temp.Path,
            setFileDropListAsync: _ => throw new InvalidOperationException("clipboard busy"));

        receiver.BeginReturnedClipboardFileBatch();
        await ReceiveSmallFileAsync(receiver, "return-drag-paths", "drag.txt", [4, 5, 6]);

        ReturnedClipboardFileCommitResult result =
            await receiver.CommitReturnedClipboardFileBatchWithResultAsync(
                allowClipboardFailure: true);

        Assert.False(result.ClipboardUpdated);
        Assert.Equal([Path.Combine(temp.Path, "drag.txt")], result.LocalPaths);
        Assert.Contains("仍可直接拖出", result.Message, StringComparison.Ordinal);
        Assert.False(receiver.HasCompletedReturnedClipboardFiles);
    }

    [Fact]
    public async Task CommitReturnedClipboardFileBatchKeepsCompletedFilesWhenClipboardWriteFails()
    {
        using var temp = TemporaryDirectory.Create();
        int clipboardWrites = 0;
        string[]? retriedFiles = null;
        var receiver = new FileTransferReceiver(
            _ => { },
            () => temp.Path,
            setFileDropListAsync: paths =>
            {
                clipboardWrites++;
                string[] pathArray = paths.ToArray();
                if (clipboardWrites == 1)
                {
                    throw new InvalidOperationException("clipboard busy");
                }

                retriedFiles = pathArray;
                return Task.CompletedTask;
            });

        receiver.BeginReturnedClipboardFileBatch();
        await ReceiveSmallFileAsync(receiver, "return-retry", "retry.txt", [6, 7, 8]);

        IReadOnlyList<string>? savedDespiteClipboardFailure = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            receiver.CommitReturnedClipboardFileBatchWithResultAsync(
                onClipboardFailure: paths => savedDespiteClipboardFailure = paths));
        Assert.Equal(Path.Combine(temp.Path, "retry.txt"), Assert.Single(savedDespiteClipboardFailure!));
        string retryMessage = await receiver.CommitReturnedClipboardFileBatchAsync();

        Assert.Equal(2, clipboardWrites);
        string savedPath = Assert.Single(retriedFiles!);
        Assert.Equal(Path.Combine(temp.Path, "retry.txt"), savedPath);
        Assert.Contains("放入本机剪贴板", retryMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PartiallyCollectedReturnBatchIsNotExposedAsClipboardRetry()
    {
        using var temp = TemporaryDirectory.Create();
        var receiver = new FileTransferReceiver(
            _ => { },
            () => temp.Path,
            setFileDropListAsync: _ => Task.CompletedTask);

        receiver.BeginReturnedClipboardFileBatch();
        await ReceiveSmallFileAsync(receiver, "partial-return", "partial.txt", [1, 2]);

        Assert.False(receiver.HasPendingReturnedClipboardFileCommit);
        Assert.Equal(1, receiver.CompletedReturnedClipboardFileCount);
        Assert.True(receiver.CancelReturnedClipboardFileBatchUnlessCommitRetryPending());
        Assert.Equal(0, receiver.CompletedReturnedClipboardFileCount);
    }

    [Fact]
    public async Task DisconnectCleanupPreservesOnlyExplicitClipboardRetry()
    {
        using var temp = TemporaryDirectory.Create();
        int clipboardWrites = 0;
        var receiver = new FileTransferReceiver(
            _ => { },
            () => temp.Path,
            setFileDropListAsync: _ =>
            {
                clipboardWrites++;
                return clipboardWrites == 1
                    ? Task.FromException(new InvalidOperationException("clipboard busy"))
                    : Task.CompletedTask;
            });

        receiver.BeginReturnedClipboardFileBatch();
        await ReceiveSmallFileAsync(receiver, "retry-return", "retry-preserved.txt", [3, 4]);
        await Assert.ThrowsAsync<InvalidOperationException>(
            receiver.CommitReturnedClipboardFileBatchAsync);

        Assert.True(receiver.HasPendingReturnedClipboardFileCommit);
        Assert.False(receiver.CancelReturnedClipboardFileBatchUnlessCommitRetryPending());
        Assert.Equal(1, receiver.CompletedReturnedClipboardFileCount);

        ReturnedClipboardFileCommitResult retry =
            await receiver.CommitReturnedClipboardFileBatchWithResultAsync();
        Assert.True(retry.ClipboardUpdated);
        Assert.Single(retry.LocalPaths);
        Assert.False(receiver.HasPendingReturnedClipboardFileCommit);
    }

    [Fact]
    public async Task OlderClipboardCommitCannotClearFilesFromNewerBatch()
    {
        using var temp = TemporaryDirectory.Create();
        var firstClipboardWriteStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstClipboardWrite = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int clipboardWrites = 0;
        var receiver = new FileTransferReceiver(
            _ => { },
            () => temp.Path,
            setFileDropListAsync: async _ =>
            {
                if (Interlocked.Increment(ref clipboardWrites) == 1)
                {
                    firstClipboardWriteStarted.TrySetResult(true);
                    await releaseFirstClipboardWrite.Task;
                }
            });

        receiver.BeginReturnedClipboardFileBatch();
        await ReceiveSmallFileAsync(receiver, "old-return", "old.txt", [1]);
        Task<ReturnedClipboardFileCommitResult> oldCommit =
            receiver.CommitReturnedClipboardFileBatchWithResultAsync();
        await firstClipboardWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        receiver.BeginReturnedClipboardFileBatch();
        await ReceiveSmallFileAsync(receiver, "new-return", "new.txt", [2]);
        releaseFirstClipboardWrite.TrySetResult(true);
        await oldCommit;

        Assert.Equal(1, receiver.CompletedReturnedClipboardFileCount);
        ReturnedClipboardFileCommitResult newCommit =
            await receiver.CommitReturnedClipboardFileBatchWithResultAsync();
        Assert.Equal([Path.Combine(temp.Path, "new.txt")], newCommit.LocalPaths);
    }

    [Fact]
    public async Task CommitReturnedClipboardFileBatchSkipsMissingTemporaryFiles()
    {
        using var temp = TemporaryDirectory.Create();
        string[]? clipboardFiles = null;
        var receiver = new FileTransferReceiver(
            _ => { },
            () => temp.Path,
            setFileDropListAsync: paths =>
            {
                clipboardFiles = paths.ToArray();
                return Task.CompletedTask;
            });

        receiver.BeginReturnedClipboardFileBatch();
        await ReceiveSmallFileAsync(receiver, "return-existing", "existing.txt", [1]);
        await ReceiveSmallFileAsync(receiver, "return-missing", "missing.txt", [2]);
        File.Delete(Path.Combine(temp.Path, "missing.txt"));

        string message = await receiver.CommitReturnedClipboardFileBatchAsync();

        Assert.NotNull(clipboardFiles);
        Assert.Equal([Path.Combine(temp.Path, "existing.txt")], clipboardFiles);
        Assert.Contains("跳过 1 个已不存在", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommitReturnedClipboardFileBatchDoesNotWriteClipboardWhenAllTemporaryFilesAreMissing()
    {
        using var temp = TemporaryDirectory.Create();
        int clipboardWrites = 0;
        var receiver = new FileTransferReceiver(
            _ => { },
            () => temp.Path,
            setFileDropListAsync: paths =>
            {
                clipboardWrites++;
                return Task.CompletedTask;
            });

        receiver.BeginReturnedClipboardFileBatch();
        await ReceiveSmallFileAsync(receiver, "return-missing-all", "gone.txt", [3]);
        File.Delete(Path.Combine(temp.Path, "gone.txt"));

        string message = await receiver.CommitReturnedClipboardFileBatchAsync();

        Assert.Equal(0, clipboardWrites);
        Assert.Contains("暂存文件已不存在", message, StringComparison.Ordinal);
    }

    [Fact]
    public void CleanupStaleTemporaryFilesDeletesOnlyOldTransferFiles()
    {
        using var temp = TemporaryDirectory.Create();
        string staleTransfer = Path.Combine(temp.Path, FileTransferReceiver.CreateTemporaryFileName());
        string freshTransfer = Path.Combine(temp.Path, FileTransferReceiver.CreateTemporaryFileName());
        string foreignTransfer = Path.Combine(temp.Path, "old.txt.rdtransfer");
        string normalFile = Path.Combine(temp.Path, "normal.txt");
        File.WriteAllText(staleTransfer, "old");
        File.WriteAllText(freshTransfer, "fresh");
        File.WriteAllText(foreignTransfer, "foreign");
        File.WriteAllText(normalFile, "normal");

        var now = new DateTimeOffset(2026, 6, 9, 8, 0, 0, TimeSpan.Zero);
        File.SetLastWriteTimeUtc(staleTransfer, now.AddDays(-2).UtcDateTime);
        File.SetLastWriteTimeUtc(freshTransfer, now.AddMinutes(-10).UtcDateTime);
        File.SetLastWriteTimeUtc(foreignTransfer, now.AddDays(-2).UtcDateTime);
        File.SetLastWriteTimeUtc(normalFile, now.AddDays(-3).UtcDateTime);

        int deleted = FileTransferReceiver.CleanupStaleTemporaryFiles(temp.Path, now);

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(staleTransfer));
        Assert.True(File.Exists(freshTransfer));
        Assert.True(File.Exists(foreignTransfer));
        Assert.True(File.Exists(normalFile));
    }

    [Fact]
    public void StartRejectsTransferWhenDiskSafetyReserveWouldBeConsumed()
    {
        using var temp = TemporaryDirectory.Create();
        using var receiver = new FileTransferReceiver(
            _ => { },
            () => temp.Path,
            availableFreeSpaceProvider: _ =>
                FileTransferReceiver.MinimumFreeSpaceReserveBytes);

        IOException error = Assert.Throws<IOException>(() =>
            receiver.Start(Decode(RemoteMessageCodec.EncodeFileTransferStart(
                "low-space",
                "blocked.bin",
                1))));

        Assert.Contains("空间不足", error.Message, StringComparison.Ordinal);
        AssertNoOwnedTemporaryFiles(temp.Path);
    }

    [Fact]
    public void StartRejectsDeclaredBytesBeyondPerSessionQuota()
    {
        using var temp = TemporaryDirectory.Create();
        using var receiver = new FileTransferReceiver(
            _ => { },
            () => temp.Path,
            availableFreeSpaceProvider: _ => long.MaxValue);

        receiver.Start(Decode(RemoteMessageCodec.EncodeFileTransferStart(
            "quota-one",
            "one.bin",
            RemoteMessageCodec.MaxFileTransferBytes)));
        receiver.Start(Decode(RemoteMessageCodec.EncodeFileTransferStart(
            "quota-two",
            "two.bin",
            RemoteMessageCodec.MaxFileTransferBytes)));

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            receiver.Start(Decode(RemoteMessageCodec.EncodeFileTransferStart(
                "quota-three",
                "three.bin",
                1))));

        Assert.Contains("累计", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StaleTemporaryFileCleanupIsThrottledPerReceiverSession()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Assert.True(FileTransferReceiver.ShouldCleanupStaleTemporaryFiles(
            DateTimeOffset.MinValue,
            now));
        Assert.False(FileTransferReceiver.ShouldCleanupStaleTemporaryFiles(
            now.AddMinutes(-59),
            now));
        Assert.True(FileTransferReceiver.ShouldCleanupStaleTemporaryFiles(
            now.AddHours(-1),
            now));
        Assert.True(FileTransferReceiver.ShouldCleanupStaleTemporaryFiles(
            now.AddMinutes(1),
            now));
    }

    [Theory]
    [InlineData("report.txt.rdtransfer")]
    [InlineData(".RemoteDesk-transfer-not-a-guid.rdtransfer")]
    [InlineData(".RemoteDesk-transfer-00000000000000000000000000000000.rdtransfer.extra")]
    public void TemporaryFileOwnershipRejectsForeignNames(string fileName)
    {
        Assert.False(FileTransferReceiver.IsOwnedTemporaryFileName(fileName));
    }

    private static RemoteControlMessage Decode(byte[] payload)
    {
        return RemoteMessageCodec.DecodeControl(payload);
    }

    private static string[] GetOwnedTemporaryFiles(string directory)
    {
        return Directory.GetFiles(directory)
            .Where(path => FileTransferReceiver.IsOwnedTemporaryFileName(Path.GetFileName(path)))
            .ToArray();
    }

    private static void AssertNoOwnedTemporaryFiles(string directory)
    {
        Assert.Empty(GetOwnedTemporaryFiles(directory));
    }

    private static async Task ReceiveSmallFileAsync(
        FileTransferReceiver receiver,
        string transferId,
        string fileName,
        byte[] fileBytes)
    {
        receiver.Start(Decode(RemoteMessageCodec.EncodeFileTransferStart(
            transferId,
            fileName,
            fileBytes.Length)));
        await receiver.WriteChunkAsync(
            Decode(RemoteMessageCodec.EncodeFileTransferChunk(transferId, 0, fileBytes)),
            CancellationToken.None);
        await receiver.CompleteAsync(
            Decode(RemoteMessageCodec.EncodeFileTransferComplete(transferId)),
            CancellationToken.None);
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
                $"RemoteDesk.FileTransferReceiverTests.{Guid.NewGuid():N}");
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
