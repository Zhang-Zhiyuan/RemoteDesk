using System.Collections.Concurrent;

namespace RemoteDesk;

internal sealed record FileReceiveLocation(string Directory, string Note, string Device)
{
    public bool IsKnown => !string.IsNullOrWhiteSpace(Directory);

    public string FormatFile(string name, bool pasteAtTarget = false)
    {
        string destination = IsKnown ? Join(Directory, name) : "位置未确认：远端未提供完整接收目录";
        return pasteAtTarget
            ? $"请求粘贴到远端当前窗口（无法确认该窗口的路径）；接收副本：{destination}；重名自动改名"
            : $"{destination}；重名自动改名，不覆盖已有文件";
    }

    internal static string Join(string directory, string name)
    {
        // Never interpret another OS's directory with the local Path.Combine.
        char separator = directory.Contains('\\') && !directory.StartsWith('/') ? '\\' : '/';
        return directory.TrimEnd('/', '\\') + separator + name;
    }
}

internal sealed partial class RemoteViewerClient
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RemoteControlMessage>> _fileLocationRequests = new();

    internal async Task<FileReceiveLocation> GetRemoteFileReceiveLocationAsync(long expectedGeneration)
    {
        CancellationTokenSource owner = RequireActiveFileTransferConnection();
        void EnsureCurrent()
        {
            if (!IsCurrentConnection(owner) || InputConnectionGeneration != expectedGeneration)
                throw new IOException("连接已改变，请重新确认传输文件和接收位置。");
        }
        EnsureCurrent();
        string device = _remoteDeviceInfo?.MachineName ?? "当前远端设备";
        if (!_remoteCapabilities.HasFlag(RemoteDeviceCapabilities.FileReceiveLocation))
            return new("", "远端版本不支持报告完整接收目录。位置未确认；建议更新被控端后再传输。", device);
        string id = Guid.NewGuid().ToString("N");
        var reply = new TaskCompletionSource<RemoteControlMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _fileLocationRequests.TryAdd(id, reply);
        try
        {
            if (!await SendControlAsync(RemoteMessageCodec.EncodeFileReceiveLocationRequest(id), owner))
                throw new IOException("连接已断开，无法确认接收位置。");
            RemoteControlMessage result = await reply.Task.WaitAsync(TimeSpan.FromSeconds(10), owner.Token);
            EnsureCurrent();
            if (!result.Success || string.IsNullOrWhiteSpace(result.Text))
                throw new IOException(result.StatusMessage ?? "远端未能确认接收目录，未开始传输。");
            return new(result.Text, result.StatusMessage ?? "", device);
        }
        catch (TimeoutException ex)
        {
            throw new IOException("读取远端接收位置超时，尚未发送文件，请重试。", ex);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            throw new IOException("连接已断开，尚未发送文件，请重新连接并确认接收位置。", ex);
        }
        finally { _fileLocationRequests.TryRemove(id, out _); }
    }

    internal async Task<FileTransferConfirmationPreview> PrepareFileTransferPreviewAsync(
        IReadOnlyList<string> paths, int maxFiles, long expectedGeneration, bool pasteAtTarget = false)
    {
        FileReceiveLocation location = await GetRemoteFileReceiveLocationAsync(expectedGeneration);
        return await Task.Run(() =>
        {
            RemoteFilePastePlan plan = CreateFilePastePlan(paths, maxFiles, File.Exists, Directory.Exists, includeDirectories: true);
            var preview = FileTransferConfirmation.CreatePreview(plan,
                (_, name) => location.FormatFile(name, pasteAtTarget));
            return preview with { Note = $"接收设备：{location.Device}\r\n{location.Note}\r\n{preview.Note}" };
        });
    }
}
