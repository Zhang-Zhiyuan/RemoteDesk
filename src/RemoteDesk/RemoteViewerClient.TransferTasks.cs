namespace RemoteDesk;

internal sealed partial class RemoteViewerClient
{
    private readonly object _fileTransferTasksLock = new();
    private readonly List<FileTransferTaskContext> _fileTransferTasks = [];
    private const int FileTransferTaskHistoryLimit = 64;

    public event Action<RemoteFileTransferTaskSnapshot>? FileTransferTaskChanged;

    public IReadOnlyList<RemoteFileTransferTaskSnapshot> FileTransferTasks
    {
        get
        {
            lock (_fileTransferTasksLock)
                return _fileTransferTasks.Select(GetCurrentFileTransferSnapshotLocked).ToArray();
        }
    }

    public async Task<bool> CancelFileTransferAsync(Guid taskId, long connectionGeneration)
    {
        FileTransferTaskContext? task;
        RemoteFileTransferTaskSnapshot snapshot;
        lock (_fileTransferTasksLock)
        {
            task = _fileTransferTasks.FirstOrDefault(item => item.Snapshot.TaskId == taskId &&
                item.Snapshot.ConnectionGeneration == connectionGeneration);
            if (task is null || !task.Snapshot.CanCancel || task.Snapshot.IsTerminal ||
                connectionGeneration != Interlocked.Read(ref _inputConnectionGeneration) ||
                !ReferenceEquals(task.OwnerConnection, _cancellationTokenSource) ||
                task.OwnerConnection?.IsCancellationRequested != false)
                return false;

            snapshot = task.Snapshot = GetCurrentFileTransferSnapshotLocked(task) with
            {
                State = RemoteFileTransferTaskState.Cancelling, CanCancel = false,
                Status = "正在取消；已确认保存的文件不会删除。"
            };
            task.Cancellation.Cancel();
        }
        NotifyFileTransferTaskChanged(snapshot);

        if (task.DownloadRequest is { } request)
        {
            // The legacy pull protocol has no batch id. Retain this exact request as a
            // drain barrier until its terminal acknowledgement, without closing the desktop.
            request.PreserveConnectionOnCancellation = true;
            CancelReturnedClipboardFileWait(request);
            await TryRejectReturnedClipboardFilePreviewAsync(request).ConfigureAwait(false);
        }
        return true;
    }

    private FileTransferTaskContext CreateFileTransferTask(
        RemoteFileTransferDirection direction, string displayName, long generation,
        CancellationTokenSource? owner, int totalItems, long? totalBytes,
        bool cancelSupported, ReturnedClipboardFileRequestContext? request = null)
    {
        var snapshot = new RemoteFileTransferTaskSnapshot(Guid.NewGuid(), generation, direction,
            direction == RemoteFileTransferDirection.Upload ? RemoteFileTransferTaskState.Queued : RemoteFileTransferTaskState.AwaitingConfirmation,
            displayName, null, 0, totalBytes, 0, totalItems,
            direction == RemoteFileTransferDirection.Upload || cancelSupported,
            direction == RemoteFileTransferDirection.Upload ? "等待发送" : "等待远端文件清单与确认",
            cancelSupported ? null : "旧版远端不支持安全取消已开始的传输；请等待完成。");
        var task = new FileTransferTaskContext(snapshot, owner, cancelSupported, request);
        lock (_fileTransferTasksLock)
        {
            while (_fileTransferTasks.Count >= FileTransferTaskHistoryLimit)
            {
                int oldest = _fileTransferTasks.FindIndex(item => item.Snapshot.IsTerminal);
                if (oldest < 0) break; // Never evict an active task.
                _fileTransferTasks.RemoveAt(oldest);
            }
            _fileTransferTasks.Add(task);
            if (request is not null) request.TransferTask = task;
        }
        NotifyFileTransferTaskChanged(snapshot);
        return task;
    }

    private void UpdateFileTransferTask(FileTransferTaskContext? task,
        Func<RemoteFileTransferTaskSnapshot, RemoteFileTransferTaskSnapshot> update)
    {
        if (task is null) return;
        RemoteFileTransferTaskSnapshot snapshot;
        lock (_fileTransferTasksLock)
        {
            if (task.Snapshot.IsTerminal) return;
            snapshot = update(GetCurrentFileTransferSnapshotLocked(task));
            if (task.Cancellation.IsCancellationRequested && !snapshot.IsTerminal)
                snapshot = snapshot with { State = RemoteFileTransferTaskState.Cancelling, CanCancel = false };
            task.Snapshot = snapshot;
            task.LastNotification = Environment.TickCount64;
        }
        NotifyFileTransferTaskChanged(snapshot);
    }

    private static RemoteFileTransferTaskSnapshot GetCurrentFileTransferSnapshotLocked(FileTransferTaskContext task)
    {
        if (task.PendingProgressBytes != 0)
        {
            task.Snapshot = task.Snapshot with { BytesTransferred = task.Snapshot.BytesTransferred + task.PendingProgressBytes };
            task.PendingProgressBytes = 0;
        }
        return task.Snapshot;
    }

    private void RecordFileTransferBytes(FileTransferTaskContext? task, int bytes)
    {
        if (task is null) return;
        RemoteFileTransferTaskSnapshot? notification = null;
        lock (_fileTransferTasksLock)
        {
            if (task.Snapshot.IsTerminal) return;
            // No per-chunk strings, closures, or record allocations. Polling publishes the
            // latest exact count; optional notifications are bounded to about seven per second.
            task.PendingProgressBytes += bytes;
            long now = Environment.TickCount64;
            if (now - task.LastNotification >= 150)
            {
                notification = GetCurrentFileTransferSnapshotLocked(task);
                task.LastNotification = now;
            }
        }
        if (notification is not null) NotifyFileTransferTaskChanged(notification);
    }

    private void RecordFileTransferDestination(FileTransferTaskContext? task, string? message)
    {
        if (task is null || string.IsNullOrWhiteSpace(message)) return;
        UpdateFileTransferTask(task, value =>
        {
            if (task.ConfirmedDestinations.Count < 32) task.ConfirmedDestinations.Add(message);
            return value with { Destination = string.Join(Environment.NewLine, task.ConfirmedDestinations) };
        });
    }

    private void NotifyFileTransferTaskChanged(RemoteFileTransferTaskSnapshot snapshot)
    {
        try { FileTransferTaskChanged?.Invoke(snapshot); }
        catch (Exception ex) { Log?.Invoke($"文件任务状态通知失败：{ex.Message}"); }
    }

    private void FinishFileTransferTask(FileTransferTaskContext? task, RemoteFileTransferTaskState state,
        string status, string? destination = null)
    {
        UpdateFileTransferTask(task, value => value with
        {
            State = state, Status = status, CanCancel = false,
            Destination = destination ?? value.Destination
        });
    }

    private async Task<T> RunOutgoingFileTransferTaskAsync<T>(RemoteFilePastePlan plan, long generation,
        Func<FileTransferTaskContext, CancellationTokenSource, Task<T>> send)
    {
        CancellationTokenSource owner = RequireActiveFileTransferConnection();
        if (InputConnectionGeneration != generation)
            throw new IOException("连接已改变，原文件确认已失效，请重新确认文件和接收位置。");
        CancellationToken ownerToken = owner.Token;
        long? totalBytes = TryGetTransferPlanBytes(plan);
        string name = plan.TransferItems.Count == 1
            ? RemoteFileTransfer.GetTransferDisplayName(plan.TransferItems[0].Path)
            : $"发送 {plan.TransferItems.Count} 项文件";
        FileTransferTaskContext task = CreateFileTransferTask(RemoteFileTransferDirection.Upload,
            name, generation, owner, plan.TransferItems.Count, totalBytes,
            _remoteCapabilities.HasFlag(RemoteDeviceCapabilities.FileTransferCancel));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ownerToken, task.Cancellation.Token);
        task.OperationToken = linked.Token;
        bool acquired = false;
        try
        {
            await _fileTransferLock.WaitAsync(linked.Token).ConfigureAwait(false);
            acquired = true;
            linked.Token.ThrowIfCancellationRequested();
            if (!IsCurrentConnection(owner) || InputConnectionGeneration != generation)
                throw new IOException("连接已改变，原文件确认已失效，请重新确认文件和接收位置。");
            T result = await send(task, owner).ConfigureAwait(false);
            RemoteFilePasteResult? batchResult = result switch
            {
                RemoteFilePasteResult paste => paste,
                RemoteFileDropPasteResult drop => drop.TransferResult,
                _ => null
            };
            bool failed = batchResult?.FailedFiles > 0;
            string summary = task.Cancellation.IsCancellationRequested
                ? "已取消；已保存的文件保留，正在保存的文件请核对接收目录。"
                : failed ? "部分文件发送失败，请查看逐项结果。" : task.Snapshot.Status;
            if (batchResult is { Results.Count: > 0 } batch)
            {
                summary += Environment.NewLine + string.Join(Environment.NewLine,
                    batch.Results!.Take(32).Select(item => $"{item.Name}：{item.Details}"));
                if (batch.Results.Count > 32) summary += Environment.NewLine + $"另有 {batch.Results.Count - 32} 项，请查看本批逐项结果。";
            }
            FinishFileTransferTask(task,
                task.Cancellation.IsCancellationRequested ? RemoteFileTransferTaskState.Cancelled :
                    failed ? RemoteFileTransferTaskState.Failed : RemoteFileTransferTaskState.Completed,
                summary);
            return result;
        }
        catch (OperationCanceledException) when (task.Cancellation.IsCancellationRequested)
        {
            FinishFileTransferTask(task, RemoteFileTransferTaskState.Cancelled,
                "已取消；已保存的文件保留，正在保存的文件请核对接收目录。");
            throw new RemoteFileTransferCancelledException();
        }
        catch (Exception ex)
        {
            FinishFileTransferTask(task, RemoteFileTransferTaskState.Failed, ex.Message);
            throw;
        }
        finally
        {
            if (acquired) ReleaseFileTransferLock();
        }
    }

    private static long? TryGetTransferPlanBytes(RemoteFilePastePlan plan)
    {
        try
        {
            if (plan.TransferItems.Any(item => item.Kind == RemoteFilePasteItemKind.Directory)) return null;
            return plan.TransferItems.Sum(item => new FileInfo(item.Path).Length);
        }
        catch (Exception ex) when (RemoteFileTransfer.IsRecoverableTransferException(ex)) { return null; }
    }

    private async Task<bool> SendFileControlAsync(ReadOnlyMemory<byte> payload,
        CancellationTokenSource owner, CancellationToken operationToken)
    {
        operationToken.ThrowIfCancellationRequested();
        bool sent = await SendControlAsync(payload, owner, operationCancellation: operationToken).ConfigureAwait(false);
        // Cancellation can discard a frame still waiting for the shared writer. It never
        // cancels a partial encrypted TCP write or spends a nonce for a discarded frame.
        if (!sent) operationToken.ThrowIfCancellationRequested();
        return sent;
    }

    private void StartTrackedOutgoingFile(FileTransferTaskContext? task, string fileName, long length)
    {
        if (task is null) return;
        lock (_fileTransferTasksLock)
        {
            task.OperationToken.ThrowIfCancellationRequested();
            task.Snapshot = task.Snapshot with
            {
                State = RemoteFileTransferTaskState.Transferring, CurrentFileName = fileName,
                TotalBytes = task.Snapshot.TotalBytes ?? (task.Snapshot.TotalItems == 1 ? length : null),
                CanCancel = task.CancelSupported, Status = $"正在发送：{fileName}"
            };
        }
        NotifyFileTransferTaskChanged(task.Snapshot);
    }

    private void TrackIncomingFileProgress(RemoteControlMessage? control, string? message,
        CancellationTokenSource owner)
    {
        if (control is null || !TryGetActiveReturnedClipboardFileRequest(out var request) ||
            !ReferenceEquals(request.OwnerConnection, owner) || request.IsRejectRequested) return;
        switch (control.Kind)
        {
            case RemoteControlKind.FileTransferStart:
                UpdateFileTransferTask(request.TransferTask, value => value with
                {
                    State = RemoteFileTransferTaskState.Transferring, CurrentFileName = control.FileName,
                    TotalBytes = value.TotalBytes ?? (value.TotalItems == 1 ? control.FileLength : null),
                    Status = message ?? $"正在接收：{control.FileName}"
                });
                break;
            case RemoteControlKind.FileTransferChunk:
                RecordFileTransferBytes(request.TransferTask, control.FileBytes.Length);
                break;
            case RemoteControlKind.FileTransferComplete:
                RecordFileTransferDestination(request.TransferTask, message);
                UpdateFileTransferTask(request.TransferTask, value => value with
                {
                    CompletedItems = value.CompletedItems + 1, Status = message ?? "文件已接收，等待批次结束"
                });
                break;
        }
    }

    private sealed class FileTransferTaskContext(
        RemoteFileTransferTaskSnapshot snapshot, CancellationTokenSource? ownerConnection,
        bool cancelSupported, ReturnedClipboardFileRequestContext? downloadRequest)
    {
        public RemoteFileTransferTaskSnapshot Snapshot = snapshot;
        public readonly CancellationTokenSource? OwnerConnection = ownerConnection;
        public readonly CancellationTokenSource Cancellation = new();
        public readonly bool CancelSupported = cancelSupported;
        public readonly ReturnedClipboardFileRequestContext? DownloadRequest = downloadRequest;
        public CancellationToken OperationToken;
        public long LastNotification;
        public long PendingProgressBytes;
        public readonly List<string> ConfirmedDestinations = [];
    }
}
