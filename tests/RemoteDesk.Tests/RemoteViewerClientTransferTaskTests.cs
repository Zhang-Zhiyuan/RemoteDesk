using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class RemoteViewerClientTransferTaskTests
{
    private const RemoteDeviceCapabilities Modern = RemoteDeviceCapabilities.InputControl |
        RemoteDeviceCapabilities.ClipboardText | RemoteDeviceCapabilities.FileReceive |
        RemoteDeviceCapabilities.FileSend | RemoteDeviceCapabilities.FileTransferPreview |
        RemoteDeviceCapabilities.FileTransferCancel | RemoteDeviceCapabilities.FileTransferReceipt |
        RemoteDeviceCapabilities.FileDropPaste;

    [Fact]
    public async Task UploadCancellationStopsBatchAndKeepsEncryptedInputSessionUsable()
    {
        await using var peer = await Peer.StartAsync(Modern);
        string first = peer.CreateFile("first.bin", 1024 * 1024);
        string second = peer.CreateFile("never-started.bin", 3);
        bool cancelAccepted = false;
        peer.Client.FileTransferStatusReceived += (_, _) =>
        {
            RemoteFileTransferTaskSnapshot? snapshot = peer.Client.FileTransferTasks.FirstOrDefault();
            if (snapshot is null) return;
            if (snapshot.BytesTransferred > 0 && snapshot.CanCancel && !cancelAccepted)
                cancelAccepted = peer.Client.CancelFileTransferAsync(snapshot.TaskId, snapshot.ConnectionGeneration).GetAwaiter().GetResult();
        };
        RemoteFilePasteResult result = await peer.Client.SendFilesToRemoteAsync([first, second], 2).WaitAsync(peer.Token);
        Assert.True(cancelAccepted);
        Assert.True(result.Cancelled);
        Assert.Equal(0, result.SentFiles);
        Assert.Equal(2, result.FailedFiles);
        await peer.WaitControlAsync(RemoteControlKind.FileTransferCancel);
        Assert.Single(peer.Controls, item => item.Kind == RemoteControlKind.FileTransferStart);
        Assert.DoesNotContain(peer.Controls, item => item.Kind == RemoteControlKind.FileTransferComplete);
        RemoteFileTransferTaskSnapshot snapshot = Assert.Single(peer.Client.FileTransferTasks);
        Assert.Equal(RemoteFileTransferTaskState.Cancelled, snapshot.State);
        Assert.InRange(snapshot.BytesTransferred, 1, 1024 * 1024 - 1);
        Assert.Equal(1024 * 1024 + 3, snapshot.TotalBytes);
        await peer.AssertInputStillWorksAsync();
    }

    [Fact]
    public async Task QueuedCancellationIsIndependentAndWrongGenerationCannotCancelActiveTask()
    {
        await using var peer = await Peer.StartAsync(Modern);
        string file = peer.CreateFile("saved.txt", 8);
        Task active = peer.Client.SendFileToRemoteAsync(file);
        RemoteControlMessage firstComplete = await peer.WaitControlAsync(RemoteControlKind.FileTransferComplete);
        Task queued = peer.Client.SendFileToRemoteAsync(file);
        await peer.WaitUntilAsync(() => peer.Client.FileTransferTasks.Count == 2);
        var first = peer.Client.FileTransferTasks[0];
        var second = peer.Client.FileTransferTasks[1];
        Assert.Equal(RemoteFileTransferTaskState.Queued, second.State);
        Assert.False(await peer.Client.CancelFileTransferAsync(first.TaskId, first.ConnectionGeneration + 1));
        Assert.True(await peer.Client.CancelFileTransferAsync(second.TaskId, second.ConnectionGeneration));
        await Assert.ThrowsAsync<RemoteFileTransferCancelledException>(() => queued);
        Assert.False(active.IsCompleted);
        await peer.SendAsync(RemoteMessageCodec.EncodeFileTransferReceipt(firstComplete.TransferId!, true, @"已保存：C:\receive\saved.txt"));
        await active.WaitAsync(peer.Token);
        Assert.Equal(RemoteFileTransferTaskState.Completed, peer.Client.FileTransferTasks[0].State);
        Assert.Contains(@"C:\receive\saved.txt", peer.Client.FileTransferTasks[0].Destination);
        Assert.Equal(1, peer.Client.FileTransferTasks[0].CompletedItems);
        Assert.False(await peer.Client.CancelFileTransferAsync(first.TaskId, first.ConnectionGeneration));
        Assert.Single(peer.Controls, item => item.Kind == RemoteControlKind.FileTransferStart);
        await peer.AssertInputStillWorksAsync();
    }

    [Fact]
    public async Task LegacyActiveTransferExplainsCancellationLimitAndKeepsUnconfirmedReceiptWording()
    {
        await using var peer = await Peer.StartAsync(Modern & ~RemoteDeviceCapabilities.FileTransferCancel & ~RemoteDeviceCapabilities.FileTransferReceipt);
        bool? cancellation = null;
        RemoteFileTransferTaskSnapshot? activeSnapshot = null;
        peer.Client.FileTransferTaskChanged += snapshot =>
        {
            if (snapshot.State != RemoteFileTransferTaskState.Transferring || activeSnapshot is not null) return;
            activeSnapshot = snapshot;
            cancellation = peer.Client.CancelFileTransferAsync(snapshot.TaskId, snapshot.ConnectionGeneration).GetAwaiter().GetResult();
        };
        await peer.Client.SendFileToRemoteAsync(peer.CreateFile("legacy.txt", 32)).WaitAsync(peer.Token);
        Assert.NotNull(activeSnapshot);
        Assert.False(activeSnapshot.CanCancel);
        Assert.Contains("旧版", activeSnapshot.CancellationUnavailableReason);
        Assert.False(cancellation);
        var completed = Assert.Single(peer.Client.FileTransferTasks);
        Assert.Equal(RemoteFileTransferTaskState.Completed, completed.State);
        Assert.Contains("未返回保存确认", completed.Status);
        Assert.Null(completed.Destination);
        await peer.AssertInputStillWorksAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PausedConfirmedFileDropStillSavesButNeverPastes(bool pauseAfterBegin)
    {
        await using var peer = await Peer.StartAsync(Modern);
        if (!pauseAfterBegin) peer.Client.SetLocalInputPaused(true);
        else peer.Client.FileTransferTaskChanged += snapshot =>
        {
            if (snapshot.BytesTransferred > 0)
            {
                peer.Client.SetLocalInputPaused(true);
                peer.Client.SetLocalInputPaused(false); // Resuming must not restore the old paste intent.
            }
        };
        Task<RemoteFileDropPasteResult> send = peer.Client.SendFilesToRemoteDropPasteAsync([peer.CreateFile("drop.txt", 24)], 1);
        RemoteControlMessage complete = await peer.WaitControlAsync(RemoteControlKind.FileTransferComplete);
        await peer.SendAsync(RemoteMessageCodec.EncodeFileTransferReceipt(complete.TransferId!, true, "已保存 drop.txt"));
        var result = await send.WaitAsync(peer.Token);
        Assert.Equal(1, result.TransferResult.SentFiles);
        Assert.False(result.RemotePasteRequested);
        if (pauseAfterBegin) await peer.WaitControlAsync(RemoteControlKind.FileDropPasteCancel);
        Assert.DoesNotContain(peer.Controls, item => item.Kind == RemoteControlKind.FileDropPasteCommit);
        if (!pauseAfterBegin) Assert.DoesNotContain(peer.Controls, item => item.Kind == RemoteControlKind.FileDropPasteBegin);
        Assert.True(peer.Client.IsConnected);
    }

    [Fact]
    public async Task PausingReleasesWireHeldInputsPreservesQueuedUpsAndBlocksTextChordAndClipboard()
    {
        await using var peer = await Peer.StartAsync(Modern);
        var down = RemoteInputCommand.KeyDown(0xA3, 0x1D, RemoteKeyboardFlags.HasScanCode | RemoteKeyboardFlags.Extended);
        await peer.Client.SendInputAsync(down);
        await peer.Client.SendInputAsync(RemoteInputCommand.MouseDown(RemoteMouseButton.Left, 20, 30));
        await peer.Client.FlushInputAsync(peer.Token);
        await peer.WaitUntilAsync(() => peer.Inputs.Count == 2);
        SemaphoreSlim gate = peer.ClientWriteGate;
        await gate.WaitAsync(peer.Token);
        try
        {
            await peer.Client.SendInputAsync(down with { Kind = RemoteInputKind.KeyUp });
            await peer.Client.SendInputAsync(RemoteInputCommand.KeyDown(65));
            Task<bool> staleClipboard = peer.Client.SendClipboardTextToRemoteAsync("must not be written");
            peer.Client.SetLocalInputPaused(true);
            Assert.Equal(0, peer.Client.SendTextInput("禁止输入").SentCodePoints);
            Assert.False(peer.Client.TryQueueKeyboardChord([RemoteInputCommand.KeyDown(66), RemoteInputCommand.KeyUp(66)], peer.Client.InputConnectionGeneration));
            Assert.False(await peer.Client.SendClipboardTextToRemoteAsync("blocked"));
            await peer.Client.SendInputAsync(RemoteInputCommand.MouseWheel(120, 20, 30));
            // A queued write/chord from before pause must stay stale even after resume.
            peer.Client.SetLocalInputPaused(false);
            gate.Release();
            gate = null!;
            Assert.False(await staleClipboard.WaitAsync(peer.Token));
            await peer.Client.FlushInputAsync(peer.Token);
            await peer.WaitUntilAsync(() => peer.Inputs.Any(input => input.Kind == RemoteInputKind.MouseUp));
            Assert.Contains(down with { Kind = RemoteInputKind.KeyUp }, peer.Inputs);
            Assert.DoesNotContain(peer.Inputs, input => input.Data == 65 && input.Kind == RemoteInputKind.KeyDown);
            Assert.DoesNotContain(peer.Inputs, input => input.Kind is RemoteInputKind.TextInput or RemoteInputKind.MouseWheel);
            Assert.DoesNotContain(peer.Controls, control => control.Kind == RemoteControlKind.ClipboardSetText);
        }
        finally { gate?.Release(); }
        await peer.AssertInputStillWorksAsync();
    }

    [Theory]
    [InlineData((int)RemoteMouseButton.Left)]
    [InlineData((int)RemoteMouseButton.Right)]
    [InlineData((int)RemoteMouseButton.Middle)]
    public async Task PausingAfterDraggingReleasesAtLatestPointerPositionInsteadOfJumpingBack(int buttonValue)
    {
        await using var peer = await Peer.StartAsync(Modern);
        var button = (RemoteMouseButton)buttonValue;
        await peer.Client.SendInputAsync(RemoteInputCommand.MouseDown(button, 20, 30));
        await peer.Client.SendInputAsync(RemoteInputCommand.MouseMove(80, 90));
        await peer.Client.FlushInputAsync(peer.Token);
        await peer.WaitUntilAsync(() => peer.Inputs.Count == 2);
        peer.Client.SetLocalInputPaused(true);
        await peer.Client.FlushInputAsync(peer.Token);
        await peer.WaitUntilAsync(() => peer.Inputs.Any(input => input.Kind == RemoteInputKind.MouseUp));
        RemoteInputCommand released = Assert.Single(peer.Inputs, input => input.Kind == RemoteInputKind.MouseUp);
        Assert.Equal(RemoteInputCommand.MouseUp(button, 80, 90), released);
        Assert.True(peer.Client.IsConnected);
    }

    [Fact]
    public async Task CancelPullBeforeWriterAdmissionDoesNotSendOrRetainAnUnneededDrainBarrier()
    {
        await using var peer = await Peer.StartAsync(Modern);
        SemaphoreSlim gate = peer.ClientWriteGate;
        await gate.WaitAsync(peer.Token);
        Task<bool> request;
        Task<bool> cancel;
        try
        {
            request = peer.Client.RequestRemoteClipboardFilesAsync();
            var task = Assert.Single(peer.Client.FileTransferTasks);
            cancel = peer.Client.CancelFileTransferAsync(task.TaskId, task.ConnectionGeneration);
        }
        finally { gate.Release(); }
        await request.WaitAsync(peer.Token);
        Assert.True(await cancel.WaitAsync(peer.Token));
        await peer.AssertInputStillWorksAsync();
        Assert.DoesNotContain(peer.Controls, control => control.Kind is RemoteControlKind.FileTransferRequestClipboardFiles
            or RemoteControlKind.FileTransferRejectClipboardFiles);
        Assert.False(peer.Client.IsRemoteClipboardFileRequestPending);
        Assert.Equal(RemoteFileTransferTaskState.Cancelled, Assert.Single(peer.Client.FileTransferTasks).State);
    }

    [Fact]
    public async Task CancelDragPullBeforeWriterAdmissionReleasesSlotWithoutAnyWireRequest()
    {
        await using var peer = await Peer.StartAsync(Modern);
        using var cancel = new CancellationTokenSource();
        SemaphoreSlim gate = peer.ClientWriteGate;
        await gate.WaitAsync(peer.Token);
        Task<ReturnedClipboardFileBatchResult> request;
        try
        {
            request = peer.Client.RequestRemoteClipboardFilesForDragOutAsync(cancel.Token);
            Assert.Single(peer.Client.FileTransferTasks);
            cancel.Cancel();
        }
        finally { gate.Release(); }
        Assert.Equal(ReturnedClipboardFileBatchOutcome.Cancelled, (await request.WaitAsync(peer.Token)).Outcome);
        await peer.AssertInputStillWorksAsync();
        Assert.DoesNotContain(peer.Controls, control => control.Kind is RemoteControlKind.FileTransferRequestClipboardFiles
            or RemoteControlKind.FileTransferRejectClipboardFiles);
        Assert.False(peer.Client.IsRemoteClipboardFileRequestPending);
        Assert.Equal(RemoteFileTransferTaskState.Cancelled, Assert.Single(peer.Client.FileTransferTasks).State);
        Assert.True(await peer.Client.RequestRemoteClipboardFilesAsync());
        await peer.WaitControlAsync(RemoteControlKind.FileTransferRequestClipboardFiles);
    }

    [Fact]
    public async Task PausingSharedClientReleasesBothViewerOwnersAtLatestGlobalPosition()
    {
        await using var peer = await Peer.StartAsync(Modern);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                // Real viewer controls, never shown, no clipboard or desktop input.
                using var first = new RemoteViewerWindow(peer.Client, "Owned test 1", true, false, false, false, false, false);
                using var second = new RemoteViewerWindow(peer.Client, "Owned test 2", true, false, false, false, false, false);
                void Complete(Task task)
                {
                    // WinForms installs a synchronization context as controls are
                    // constructed. Pump async flush continuations even under the
                    // full suite's heavier load; never block the UI thread on them.
                    while (!task.IsCompleted && !peer.Token.IsCancellationRequested)
                    {
                        Application.DoEvents();
                        Thread.Sleep(1);
                    }
                    peer.Token.ThrowIfCancellationRequested();
                    task.GetAwaiter().GetResult();
                }
                RemoteInputOwnershipTracker Owner(RemoteViewerWindow viewer) =>
                    (RemoteInputOwnershipTracker)typeof(RemoteViewerWindow).GetField("_remoteInputOwnership",
                        BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewer)!;
                Assert.True(Owner(first).TryQueueMouseDown(RemoteMouseButton.Right, new(20, 30), peer.Client.TryQueueOwnedInput));
                Assert.True(Owner(second).TryQueueMouseDown(RemoteMouseButton.Middle, new(40, 50), peer.Client.TryQueueOwnedInput));
                Complete(peer.Client.SendInputAsync(RemoteInputCommand.MouseMove(80, 90)));
                Complete(peer.Client.FlushInputAsync(peer.Token));
                Complete(peer.WaitUntilAsync(() => peer.Inputs.Count == 3));
                peer.Client.SetLocalInputPaused(true);
                Assert.True(first.LocalInputPausedForTests);
                Assert.True(second.LocalInputPausedForTests);
                Assert.Equal(0, Owner(first).PressedMouseButtonCount);
                Assert.Equal(0, Owner(second).PressedMouseButtonCount);
                Complete(peer.Client.FlushInputAsync(peer.Token));
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(25)));
        Assert.Null(failure);
        await peer.WaitUntilAsync(() => peer.Inputs.Count(input => input.Kind == RemoteInputKind.MouseUp) >= 2);
        foreach (var button in new[] { RemoteMouseButton.Right, RemoteMouseButton.Middle })
            Assert.Equal(RemoteInputCommand.MouseUp(button, 80, 90),
                peer.Inputs.First(input => input.Kind == RemoteInputKind.MouseUp && input.Button == button));
        Assert.True(peer.Client.IsConnected);
    }

    [Fact]
    public async Task PullTaskShowsPreviewAndBytesAndCancellationDrainsWithoutClosingDesktop()
    {
        await using var peer = await Peer.StartAsync(Modern);
        int confirmations = 0;
        peer.Client.ConfirmRemoteClipboardFileTransfer = (_, _) => { confirmations++; return true; };
        Assert.True(await peer.Client.RequestRemoteClipboardFilesAsync());
        await peer.WaitControlAsync(RemoteControlKind.FileTransferRequestClipboardFiles);
        var item = new FileTransferConfirmationItem("文件", "/remote/data.bin", "data.bin", 100, "ignored");
        var lateItem = new FileTransferConfirmationItem("文件", "/remote/late.bin", "late.bin", 1, "ignored");
        await peer.SendAsync(RemoteMessageCodec.EncodeFileTransferClipboardFilesPreview([item, lateItem], null));
        await peer.WaitControlAsync(RemoteControlKind.FileTransferConfirmClipboardFiles);
        Assert.Equal(1, confirmations);
        await peer.SendAsync(RemoteMessageCodec.EncodeFileTransferStart("pull-task", "data.bin", 100));
        await peer.SendAsync(RemoteMessageCodec.EncodeFileTransferChunk("pull-task", 0, new byte[10]));
        await peer.WaitUntilAsync(() => peer.Client.FileTransferTasks.Single().BytesTransferred == 10);
        var task = Assert.Single(peer.Client.FileTransferTasks);
        Assert.Equal(RemoteFileTransferDirection.Download, task.Direction);
        Assert.Equal(101, task.TotalBytes);
        Assert.Equal(2, task.TotalItems);
        Assert.True(await peer.Client.CancelFileTransferAsync(task.TaskId, task.ConnectionGeneration));
        await peer.WaitControlAsync(RemoteControlKind.FileTransferRejectClipboardFiles);
        Assert.True(peer.Client.IsRemoteClipboardFileRequestPending);
        // A peer may already have buffered the next approved item when it sees Reject.
        // It must not reopen a local receiver during this exact cancelled batch's drain.
        await peer.SendAsync(RemoteMessageCodec.EncodeFileTransferStart("late-file", "late.bin", 1));
        await peer.SendAsync(RemoteMessageCodec.EncodeFileTransferChunk("late-file", 0, new byte[] { 7 }));
        await peer.SendAsync(RemoteMessageCodec.EncodeFileTransferComplete("late-file"));
        await peer.WaitUntilAsync(() => peer.Controls.Any(control => control.Kind == RemoteControlKind.FileTransferReceipt &&
            control.TransferId == "late-file" && !control.Success));
        Assert.True(await peer.Client.RequestRemoteClipboardFilesAsync()); // Existing API treats an already pending pull as handled.
        await peer.AssertInputStillWorksAsync();
        Assert.Single(peer.Controls, control => control.Kind == RemoteControlKind.FileTransferRequestClipboardFiles);
        // The request slot is detached before the finished task and its public
        // completion notification are published. Observing Pending=false alone
        // races that publication; wait for the batch's actual completion event.
        var completed = new TaskCompletionSource<ReturnedClipboardFileBatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.Client.RemoteClipboardFileBatchCompleted += result => completed.TrySetResult(result);
        await peer.SendAsync(RemoteMessageCodec.EncodeFileTransferStatus(false, "远端文件回传已取消"));
        await completed.Task.WaitAsync(peer.Token);
        Assert.False(peer.Client.IsRemoteClipboardFileRequestPending);
        Assert.Equal(RemoteFileTransferTaskState.Cancelled, peer.Client.FileTransferTasks.Single().State);
        Assert.False(File.Exists(Path.Combine(peer.DirectoryPath, "data.bin")));
        Assert.False(File.Exists(Path.Combine(peer.DirectoryPath, "late.bin")));
        Assert.Empty(Directory.GetFiles(peer.DirectoryPath, "*.part", SearchOption.AllDirectories));
        Assert.Equal(0, peer.ClipboardCommitCount);
        Assert.True(peer.Client.IsConnected);
    }

    [Fact]
    public async Task PullWithoutCancelCapabilityIsVisibleButNotCancellable()
    {
        await using var peer = await Peer.StartAsync(Modern & ~RemoteDeviceCapabilities.FileTransferCancel);
        Assert.True(await peer.Client.RequestRemoteClipboardFilesAsync());
        var task = Assert.Single(peer.Client.FileTransferTasks);
        Assert.False(task.CanCancel);
        Assert.NotNull(task.CancellationUnavailableReason);
        Assert.False(await peer.Client.CancelFileTransferAsync(task.TaskId, task.ConnectionGeneration));
        await peer.SendAsync(RemoteMessageCodec.EncodeFileTransferStatus(false, "远端剪贴板没有可回传文件"));
        await peer.WaitUntilAsync(() => peer.Client.FileTransferTasks.Single().IsTerminal);
        Assert.True(peer.Client.IsConnected);
    }

    [Fact]
    public async Task InputPausePreferenceSurvivesDisconnectAndGenerationReset()
    {
        await using var peer = await Peer.StartAsync(Modern);
        peer.Client.SetLocalInputPaused(true);
        long generation = peer.Client.InputConnectionGeneration;
        await peer.ReconnectAsync(Modern);
        Assert.True(peer.Client.IsLocalInputPaused);
        Assert.NotEqual(generation, peer.Client.InputConnectionGeneration);
        Assert.Equal(0, peer.Client.SendTextInput("blocked").SentCodePoints);
        Assert.True(peer.Client.IsConnected);
        peer.Client.SetLocalInputPaused(false);
        await peer.AssertInputStillWorksAsync();
    }

    [Fact]
    public async Task MissingPullCancelAcknowledgementKeepsDesktopAndQuarantinesOldBatch()
    {
        await using var peer = await Peer.StartAsync(Modern);
        Assert.True(await peer.Client.RequestRemoteClipboardFilesAsync());
        var task = Assert.Single(peer.Client.FileTransferTasks);
        Assert.True(await peer.Client.CancelFileTransferAsync(task.TaskId, task.ConnectionGeneration));
        await peer.WaitControlAsync(RemoteControlKind.FileTransferRejectClipboardFiles);
        object request = typeof(RemoteViewerClient).GetField("_returnedClipboardFileRequest",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(peer.Client)!;
        typeof(RemoteViewerClient).GetMethod("HandleReturnedClipboardFileRequestIdleTimeout",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(peer.Client, [request]);
        Assert.Equal(RemoteFileTransferTaskState.Cancelled, peer.Client.FileTransferTasks.Single().State);
        Assert.Contains("桌面连接保留", peer.Client.FileTransferTasks.Single().Status);
        Assert.True(peer.Client.IsRemoteClipboardFileRequestPending);
        await peer.Client.RequestRemoteClipboardFilesAsync();
        await peer.AssertInputStillWorksAsync();
        Assert.Single(peer.Controls, control => control.Kind == RemoteControlKind.FileTransferRequestClipboardFiles);
        await peer.SendAsync(RemoteMessageCodec.EncodeFileTransferStatus(false, "远端文件回传已取消"));
        await peer.WaitUntilAsync(() => !peer.Client.IsRemoteClipboardFileRequestPending);
        Assert.True(peer.Client.IsConnected);
    }

    [Fact]
    public async Task SuccessfulPullRetainsConfirmationSaveReceiptAndActualDestination()
    {
        await using var peer = await Peer.StartAsync(Modern);
        int confirmations = 0;
        peer.Client.ConfirmRemoteClipboardFileTransfer = (_, _) => { confirmations++; return true; };
        Assert.True(await peer.Client.RequestRemoteClipboardFilesAsync());
        await peer.WaitControlAsync(RemoteControlKind.FileTransferRequestClipboardFiles);
        await peer.SendAsync(RemoteMessageCodec.EncodeFileTransferClipboardFilesPreview(
            [new("文件", "/remote/saved.bin", "saved.bin", 4, "unused")], null));
        await peer.WaitControlAsync(RemoteControlKind.FileTransferConfirmClipboardFiles);
        await peer.SendAsync(RemoteMessageCodec.EncodeFileTransferStart("successful-pull", "saved.bin", 4));
        await peer.SendAsync(RemoteMessageCodec.EncodeFileTransferChunk("successful-pull", 0, new byte[] { 1, 2, 3, 4 }));
        await peer.SendAsync(RemoteMessageCodec.EncodeFileTransferComplete("successful-pull"));
        RemoteControlMessage receipt = await peer.WaitControlAsync(RemoteControlKind.FileTransferReceipt);
        Assert.True(receipt.Success);
        await peer.SendAsync(RemoteMessageCodec.EncodeFileTransferStatus(true, "远端文件回传完成：1 个文件。"));
        await peer.WaitUntilAsync(() => peer.Client.FileTransferTasks.Single().IsTerminal);
        var result = peer.Client.FileTransferTasks.Single();
        Assert.Equal(1, confirmations);
        Assert.Equal(RemoteFileTransferTaskState.Completed, result.State);
        Assert.Equal(4, result.BytesTransferred);
        Assert.Equal(4, result.TotalBytes);
        Assert.Equal(1, result.CompletedItems);
        Assert.Equal(Path.Combine(peer.DirectoryPath, "saved.bin"), result.Destination);
        Assert.Equal(1, peer.ClipboardCommitCount);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(result.Destination!));
        await peer.AssertInputStillWorksAsync();
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(8192, false)]
    [InlineData(0, true)]
    [InlineData(8192, true)]
    public async Task FolderPullUsesActualZipLengthOnlyWhenWholeBatchHasOneItem(long sourceSize, bool multiItem)
    {
        await using var peer = await Peer.StartAsync(Modern);
        long? previewTotal = -1;
        peer.Client.ConfirmRemoteClipboardFileTransfer = (_, _) =>
        {
            previewTotal = peer.Client.FileTransferTasks.Single().TotalBytes;
            return true;
        };
        using var zipData = new MemoryStream();
        using (var archive = new ZipArchive(zipData, ZipArchiveMode.Create, leaveOpen: true))
        {
            using Stream entry = archive.CreateEntry("inside.txt").Open();
            entry.Write(new byte[] { 1, 2, 3, 4 });
        }
        byte[] zipBytes = zipData.ToArray();
        var items = new List<FileTransferConfirmationItem>
        {
            new("文件夹", "/remote/folder", "folder.zip", sourceSize, "unused")
        };
        if (multiItem) items.Add(new("文件", "/remote/other.txt", "other.txt", 3, "unused"));
        Assert.True(await peer.Client.RequestRemoteClipboardFilesAsync());
        await peer.WaitControlAsync(RemoteControlKind.FileTransferRequestClipboardFiles);
        await peer.SendAsync(RemoteMessageCodec.EncodeFileTransferClipboardFilesPreview(items, null));
        await peer.WaitControlAsync(RemoteControlKind.FileTransferConfirmClipboardFiles);
        Assert.Null(previewTotal); // Directory source bytes (including zero) are not ZIP wire bytes.

        await peer.SendAsync(RemoteMessageCodec.EncodeFileTransferStart("folder-pull", "folder.zip", zipBytes.Length));
        await peer.WaitUntilAsync(() => peer.Client.FileTransferTasks.Single().State == RemoteFileTransferTaskState.Transferring);
        Assert.Equal(multiItem ? null : (long?)zipBytes.Length, peer.Client.FileTransferTasks.Single().TotalBytes);
        await peer.SendAsync(RemoteMessageCodec.EncodeFileTransferChunk("folder-pull", 0, zipBytes));
        await peer.SendAsync(RemoteMessageCodec.EncodeFileTransferComplete("folder-pull"));
        Assert.True((await peer.WaitControlAsync(RemoteControlKind.FileTransferReceipt)).Success);
        if (multiItem)
        {
            await peer.SendAsync(RemoteMessageCodec.EncodeFileTransferStart("other-pull", "other.txt", 3));
            await peer.SendAsync(RemoteMessageCodec.EncodeFileTransferChunk("other-pull", 0, new byte[] { 5, 6, 7 }));
            await peer.SendAsync(RemoteMessageCodec.EncodeFileTransferComplete("other-pull"));
            await peer.WaitUntilAsync(() => peer.Controls.Any(control => control.Kind == RemoteControlKind.FileTransferReceipt &&
                control.TransferId == "other-pull" && control.Success));
        }
        await peer.SendAsync(RemoteMessageCodec.EncodeFileTransferStatus(true, "远端文件回传完成"));
        await peer.WaitUntilAsync(() => peer.Client.FileTransferTasks.Single().IsTerminal);
        var snapshot = peer.Client.FileTransferTasks.Single();
        Assert.Equal(RemoteFileTransferTaskState.Completed, snapshot.State);
        Assert.Equal(zipBytes.Length + (multiItem ? 3 : 0), snapshot.BytesTransferred);
        Assert.Equal(multiItem ? null : (long?)zipBytes.Length, snapshot.TotalBytes);
        Assert.Equal(zipBytes, File.ReadAllBytes(Path.Combine(peer.DirectoryPath, "folder.zip")));
        await peer.AssertInputStillWorksAsync();
    }

    [Fact]
    public async Task CancelAwaitingReceiptWarnsAboutPossiblySavedFileAndCannotAffectNextConnection()
    {
        await using var peer = await Peer.StartAsync(Modern);
        Task send = peer.Client.SendFileToRemoteAsync(peer.CreateFile("waiting.txt", 4));
        await peer.WaitControlAsync(RemoteControlKind.FileTransferComplete);
        var task = Assert.Single(peer.Client.FileTransferTasks);
        Assert.Equal(RemoteFileTransferTaskState.AwaitingReceipt, task.State);
        Assert.True(await peer.Client.CancelFileTransferAsync(task.TaskId, task.ConnectionGeneration));
        await Assert.ThrowsAsync<RemoteFileTransferCancelledException>(() => send);
        Assert.Contains("核对接收目录", peer.Client.FileTransferTasks.Single().Status);
        await peer.WaitControlAsync(RemoteControlKind.FileTransferCancel);
        await peer.ReconnectAsync(Modern);
        Assert.False(await peer.Client.CancelFileTransferAsync(task.TaskId, task.ConnectionGeneration));
        await peer.AssertInputStillWorksAsync();
    }

    [Fact]
    public async Task CancellationWhileCompleteIsWaitingForWriterCannotCommitTheFile()
    {
        await using var peer = await Peer.StartAsync(Modern);
        SemaphoreSlim gate = peer.ClientWriteGate;
        var held = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.Client.FileTransferStatusReceived += (_, status) =>
        {
            if (status.EndsWith("100%", StringComparison.Ordinal)) held.TrySetResult(gate.Wait(0));
        };
        Task send = peer.Client.SendFileToRemoteAsync(peer.CreateFile("cancel-before-commit.txt", 32));
        Assert.True(await held.Task.WaitAsync(peer.Token));
        try
        {
            var task = Assert.Single(peer.Client.FileTransferTasks);
            Assert.True(await peer.Client.CancelFileTransferAsync(task.TaskId, task.ConnectionGeneration));
        }
        finally { gate.Release(); }
        await Assert.ThrowsAsync<RemoteFileTransferCancelledException>(() => send);
        await peer.WaitControlAsync(RemoteControlKind.FileTransferCancel);
        await peer.AssertInputStillWorksAsync();
        Assert.DoesNotContain(peer.Controls, control => control.Kind == RemoteControlKind.FileTransferComplete);
    }

    [Fact]
    public async Task BatchHistoryKeepsAllSaveReceiptsAfterResultDialogIsGone()
    {
        await using var peer = await Peer.StartAsync(Modern);
        var send = peer.Client.SendFilesToRemoteAsync([peer.CreateFile("first.txt", 3), peer.CreateFile("second.txt", 4)], 2);
        RemoteControlMessage first = await peer.WaitControlAsync(RemoteControlKind.FileTransferComplete);
        await peer.SendAsync(RemoteMessageCodec.EncodeFileTransferReceipt(first.TransferId!, true, @"已保存：C:\receive\first.txt"));
        await peer.WaitUntilAsync(() => peer.Controls.Count(control => control.Kind == RemoteControlKind.FileTransferComplete) == 2);
        RemoteControlMessage second = peer.Controls.Last(control => control.Kind == RemoteControlKind.FileTransferComplete);
        await peer.SendAsync(RemoteMessageCodec.EncodeFileTransferReceipt(second.TransferId!, true, @"已保存：C:\receive\second.txt"));
        var result = await send.WaitAsync(peer.Token);
        Assert.Equal(2, result.SentFiles);
        var snapshot = Assert.Single(peer.Client.FileTransferTasks);
        Assert.Equal(7, snapshot.BytesTransferred);
        Assert.Equal(7, snapshot.TotalBytes);
        Assert.Equal(2, snapshot.CompletedItems);
        Assert.Contains(@"C:\receive\first.txt", snapshot.Status);
        Assert.Contains(@"C:\receive\second.txt", snapshot.Status);
        Assert.Contains(@"C:\receive\first.txt", snapshot.Destination);
        Assert.Contains(@"C:\receive\second.txt", snapshot.Destination);
    }

    private sealed class Peer : IAsyncDisposable
    {
        private readonly CancellationTokenSource _lifetime = new(TimeSpan.FromSeconds(20));
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly SemaphoreSlim _hostWriteGate = new(1, 1);
        private readonly TaskCompletionSource<bool> _deviceReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task _host = Task.CompletedTask;
        private NetworkStream? _stream;
        private SecureSession? _session;
        private int _clipboardCommitCount;
        public RemoteViewerClient Client { get; }
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "RemoteDeskTransferTasks-" + Guid.NewGuid().ToString("N"));
        public ConcurrentQueue<RemoteControlMessage> Controls { get; } = new();
        public ConcurrentQueue<RemoteInputCommand> Inputs { get; } = new();
        public int ClipboardCommitCount => _clipboardCommitCount;
        public CancellationToken Token => _lifetime.Token;
        public SemaphoreSlim ClientWriteGate => (SemaphoreSlim)typeof(RemoteViewerClient)
            .GetField("_writeLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Client)!;

        private Peer()
        {
            Directory.CreateDirectory(DirectoryPath);
            Client = new RemoteViewerClient(true, () => DirectoryPath,
                _ => { Interlocked.Increment(ref _clipboardCommitCount); return Task.CompletedTask; });
            Client.DeviceInfoReceived += _ => _deviceReady.TrySetResult(true);
        }

        public static async Task<Peer> StartAsync(RemoteDeviceCapabilities capabilities)
        {
            var peer = new Peer();
            peer._listener.Start();
            peer._host = peer.RunHostAsync(capabilities);
            await peer.Client.ConnectAsync("127.0.0.1", ((IPEndPoint)peer._listener.LocalEndpoint).Port,
                "isolated-task-tests", ViewerVideoMode.StableJpeg, peer.Token);
            await peer._deviceReady.Task.WaitAsync(peer.Token);
            return peer;
        }

        public async Task ReconnectAsync(RemoteDeviceCapabilities capabilities)
        {
            await Client.DisconnectAsync();
            await _host.WaitAsync(Token);
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnDevice(RemoteDeviceDescriptor _) => ready.TrySetResult(true);
            Client.DeviceInfoReceived += OnDevice;
            try
            {
                _host = RunHostAsync(capabilities);
                await Client.ConnectAsync("127.0.0.1", ((IPEndPoint)_listener.LocalEndpoint).Port,
                    "isolated-task-tests", ViewerVideoMode.StableJpeg, Token);
                await ready.Task.WaitAsync(Token);
            }
            finally { Client.DeviceInfoReceived -= OnDevice; }
        }

        private async Task RunHostAsync(RemoteDeviceCapabilities capabilities)
        {
            try
            {
                using TcpClient socket = await _listener.AcceptTcpClientAsync(Token);
                await using NetworkStream stream = socket.GetStream();
                var auth = await Protocol.AuthenticateServerDetailedAsync(stream, "isolated-task-tests", Token);
                Assert.True(auth.IsAuthenticated);
                using SecureSession session = auth.Session!;
                _stream = stream; _session = session;
                await SendAsync(RemoteMessageCodec.EncodeDeviceInfo(new RemoteDeviceDescriptor("task-test", RemoteDevicePlatforms.Windows, capabilities)));
                while (!Token.IsCancellationRequested)
                {
                    ProtocolMessage message = await Protocol.ReadMessageAsync(stream, session, Token);
                    if (message.Type == MessageType.Input) Inputs.Enqueue(RemoteMessageCodec.DecodeInput(message.PayloadSpan));
                    else if (message.Type == MessageType.Control)
                        Controls.Enqueue(RemoteMessageCodec.DecodeControl(message.PayloadMemory));
                    else if (message.Type == MessageType.Ping)
                        await Protocol.WriteMessageAsync(stream, MessageType.Pong, ReadOnlyMemory<byte>.Empty, session, _hostWriteGate, Token);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException) { }
        }

        public Task SendAsync(byte[] payload) => Protocol.WriteMessageAsync(_stream!, MessageType.Control,
            payload, _session!, _hostWriteGate, Token);

        public string CreateFile(string name, int size)
        {
            string path = Path.Combine(DirectoryPath, name);
            File.WriteAllBytes(path, new byte[size]);
            return path;
        }

        public async Task WaitUntilAsync(Func<bool> condition)
        {
            while (!condition())
            {
                if (_host.IsFaulted) await _host;
                await Task.Delay(10, Token);
            }
        }

        public async Task<RemoteControlMessage> WaitControlAsync(RemoteControlKind kind)
        {
            await WaitUntilAsync(() => Controls.Any(item => item.Kind == kind));
            return Controls.First(item => item.Kind == kind);
        }

        public async Task AssertInputStillWorksAsync()
        {
            var command = RemoteInputCommand.MouseMove(321, 654);
            await Client.SendInputAsync(command);
            await Client.FlushInputAsync(Token);
            await WaitUntilAsync(() => Inputs.Contains(command));
            Assert.True(Client.IsConnected);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisconnectAsync();
            _lifetime.Cancel();
            _listener.Stop();
            await _host;
            Client.Dispose();
            _hostWriteGate.Dispose();
            _lifetime.Dispose();
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
