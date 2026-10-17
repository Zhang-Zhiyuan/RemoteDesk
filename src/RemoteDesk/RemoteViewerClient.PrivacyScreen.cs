using System.Collections.Concurrent;

namespace RemoteDesk;

internal sealed partial class RemoteViewerClient
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<PrivacyScreenStatus>> _privacyRequests = new();
    internal bool SupportsPrivacyScreen => IsConnected && _remoteCapabilities.HasFlag(RemoteDeviceCapabilities.PrivacyScreen);
    internal async Task<PrivacyScreenStatus> RequestPrivacyScreenAsync(PrivacyScreenAction action, long expectedGeneration)
    {
        CancellationTokenSource owner = RequireActiveFileTransferConnection();
        void EnsureCurrent()
        {
            if (!IsCurrentConnection(owner) || InputConnectionGeneration != expectedGeneration)
                throw new IOException("连接已改变，请重新确认隐私屏状态。");
        }
        EnsureCurrent();
        if (!SupportsPrivacyScreen) throw new NotSupportedException("此被控设备或版本不支持隐私屏。");
        string id = Guid.NewGuid().ToString("N");
        var reply = new TaskCompletionSource<PrivacyScreenStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        _privacyRequests.TryAdd(id, reply);
        try
        {
            if (!await SendControlAsync(RemoteMessageCodec.EncodePrivacyScreenRequest(id, action), owner))
                throw new IOException("连接已断开，无法确认隐私屏状态。");
            PrivacyScreenStatus status = await reply.Task.WaitAsync(TimeSpan.FromSeconds(12), owner.Token);
            EnsureCurrent();
            return status;
        }
        catch (TimeoutException ex) { throw new IOException("未收到隐私屏确认，状态未知。请刷新状态；不要假定本地屏幕已遮挡。", ex); }
        finally { _privacyRequests.TryRemove(id, out _); }
    }
}
