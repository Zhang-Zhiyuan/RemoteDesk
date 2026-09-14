using System.Diagnostics;
using RemoteDesk.NativeDetail;

namespace RemoteDesk;

internal sealed partial class LowLatencyVideoHostTransport
{
    private long _nativePauseGeneration;
    private NativeDetailUdpResume? _pendingNativeResume;

    internal NativeDetailUdpResume? PrepareNativeResume(DetailContext stopContext)
    {
        lock (_setupStateLock)
        {
            if (_state != 3 || _videoRouteDisabled == 0 || _offer is null ||
                !IsRouteActive || _nextFrameSequence == long.MaxValue) return null;
            return _pendingNativeResume = new(stopContext, _offer.ChannelId, _offer.Epoch,
                checked(Interlocked.Read(ref _nextFrameSequence) + 1));
        }
    }

    internal bool CompleteNativeResume(NativeDetailUdpResume acknowledgement)
    {
        lock (_setupStateLock)
        {
            if (_pendingNativeResume != acknowledgement || _state != 3 || _videoRouteDisabled == 0 ||
                !Matches(acknowledgement.ChannelId, acknowledgement.Epoch) || !IsRouteActive) return false;
            _pendingNativeResume = null;
            Volatile.Write(ref _videoRouteDisabled, 0);
            _log("原生补清已关闭，UDP 低延迟视频已恢复；旧视频帧仍被代次边界隔离。");
            return true;
        }
    }
}

internal sealed partial class LowLatencyVideoViewerTransport
{
    private readonly object _framePublicationGate;
    private long _nativeResumeMinimumFrame;
    private int _nativeResumePendingFirstFrame;

    internal bool ResumeAfterNativeStop(NativeDetailUdpResume resume)
    {
        lock (_framePublicationGate)
        lock (_socketLock)
        {
            if (!Matches(resume.ChannelId, resume.Epoch) || _state != 5 || _fatalRouteFailure != 0 ||
                resume.MinimumFrameSequence <= Interlocked.Read(ref _nativeResumeMinimumFrame) ||
                resume.MinimumFrameSequence <= Interlocked.Read(ref _highestCompleteFrameSequence) ||
                _mouseInputTask.IsCompleted) return false;
            // Old datagrams, including a completed reassembly racing this
            // control, cannot cross the minimum-frame boundary. Preserve the
            // replay window, cipher counters and existing UDP mouse worker.
            Interlocked.Exchange(ref _nativeResumeMinimumFrame, resume.MinimumFrameSequence);
            Volatile.Write(ref _nativeResumePendingFirstFrame, 1);
            Volatile.Write(ref _hasCompleteFrame, 0);
            Volatile.Write(ref _initialFrameWaitStartedAt, Stopwatch.GetTimestamp());
            Volatile.Write(ref _state, 2);
            return true;
        }
    }
}
