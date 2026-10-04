namespace RemoteDesk;

internal sealed class RemoteFileTransferCancelledException : OperationCanceledException
{
    public RemoteFileTransferCancelledException() : base("文件传输已由用户取消。") { }
}

internal enum RemoteFileTransferDirection { Upload, Download }

internal enum RemoteFileTransferTaskState
{
    Queued, Preparing, AwaitingConfirmation, Transferring, AwaitingReceipt,
    Cancelling, Completed, Cancelled, Failed
}

// Immutable copies: a modeless UI may poll these without holding a transport lock.
internal sealed record RemoteFileTransferTaskSnapshot(
    Guid TaskId,
    long ConnectionGeneration,
    RemoteFileTransferDirection Direction,
    RemoteFileTransferTaskState State,
    string DisplayName,
    string? CurrentFileName,
    long BytesTransferred,
    long? TotalBytes,
    int CompletedItems,
    int TotalItems,
    bool CanCancel,
    string Status,
    string? CancellationUnavailableReason = null,
    string? Destination = null)
{
    public bool IsTerminal => State is RemoteFileTransferTaskState.Completed or
        RemoteFileTransferTaskState.Cancelled or RemoteFileTransferTaskState.Failed;
}
