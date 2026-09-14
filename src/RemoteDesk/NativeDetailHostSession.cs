using System.Diagnostics;
using RemoteDesk.NativeDetail;

namespace RemoteDesk;

// Starts/stops a single driver owner outside all socket/input callbacks.
internal sealed class NativeDetailHostSession : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Action _selectionChanged;
    private readonly Action<string> _log;
    private readonly RemoteInteractionActivity _input;
    private readonly Func<NativeCaptureProfile, NativeCaptureProfile> _adjustProfile;
    private readonly Task _manager;
    private readonly object _requestGate = new();
    private NativeDetailRequest? _request;
    private NativeCaptureProfile? _profile;
    private NativeDetailCaptureWorker? _worker;
    private long _sequence;
    private int _disposed;
    private long _retryAt;
    private NativeDetailFeedback? _receipt;
    internal void ObserveFeedback(NativeDetailFeedback feedback)
    {
        if (Link.Observe(feedback, Environment.TickCount64)) Volatile.Write(ref _receipt, feedback);
    }
    internal NativeDetailHostSession(Action selectionChanged, RemoteInteractionActivity input, Action<string> log,
        Func<NativeCaptureProfile, NativeCaptureProfile>? adjustProfile = null)
    {
        _selectionChanged = selectionChanged; _input = input; _log = log;
        _adjustProfile = adjustProfile ?? (profile => profile); _manager = Task.Run(ManageAsync);
    }
    internal NativeDetailLinkBudget Link { get; } = new();
    internal NativeDetailRequest? Request => Volatile.Read(ref _request);
    internal NativeDetailCaptureWorker? Worker => Volatile.Read(ref _worker);
    internal bool MayStart => Environment.TickCount64 >= Interlocked.Read(ref _retryAt);
    internal void Suspend(string reason)
    {
        Interlocked.Exchange(ref _retryAt, Environment.TickCount64 + 30_000);
        Worker?.Stop(); _log(reason);
        if (Volatile.Read(ref _disposed) == 0) _selectionChanged();
    }
    internal void Configure(NativeCaptureProfile? profile) => Volatile.Write(ref _profile, profile);
    internal bool Interacting => _input.LastActivityAt > 0 &&
        Stopwatch.GetElapsedTime(_input.LastActivityAt).TotalMilliseconds < 300;
    internal bool CanEnhance => !Interacting && Link.DetailBitsPerSecond(Environment.TickCount64) > 0;
    internal bool Accept(NativeDetailRequest request)
    {
        request.Context.Validate(); request.Viewport.Validate(request.Context);
        lock (_requestGate)
        {
            if (_disposed != 0 || (_request is { } prior && (request.Context.Epoch < prior.Context.Epoch ||
                (request.Context.Epoch == prior.Context.Epoch && request.Context.Request <= prior.Context.Request)))) return false;
            Volatile.Write(ref _request, request);
        }
        return true;
    }
    private async Task ManageAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                NativeCaptureProfile? profile = Volatile.Read(ref _profile);
                if (profile is not null) profile = _adjustProfile(profile);
                NativeDetailRequest? request = Request;
                bool wanted = profile is not null && request is { Enabled: true } &&
                    profile.NativeSize.Width == request.Context.Width && profile.NativeSize.Height == request.Context.Height;
                NativeDetailCaptureWorker? worker = Worker;
                if (worker is not null && (!wanted || worker.Profile != profile || worker.Completion.IsCompleted))
                {
                    bool wasReady = worker.IsReady;
                    Volatile.Write(ref _worker, null); worker.Stop();
                    if (wasReady || worker.Failure is not null) _selectionChanged();
                    await worker.Completion.ConfigureAwait(false);
                    if (worker.Failure is { } failure)
                    {
                        Interlocked.Exchange(ref _retryAt, Environment.TickCount64 + 30_000);
                        _log("原生补清硬件不可用，保留正常画面：" + failure);
                        _selectionChanged();
                    }
                    worker = null;
                }
                if (wanted && worker is null && Environment.TickCount64 >= _retryAt)
                {
                    var next = new NativeDetailCaptureWorker(profile!, Interlocked.Read(ref _sequence), () => Request,
                        () => CanEnhance, () => !Interacting, () => Volatile.Read(ref _receipt),
                        value => Interlocked.Exchange(ref _sequence, value),
                        () => { if (Volatile.Read(ref _disposed) == 0) _selectionChanged(); });
                    Volatile.Write(ref _worker, next);
                }
                await Task.Delay(25, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally
        {
            NativeDetailCaptureWorker? worker = Interlocked.Exchange(ref _worker, null);
            if (worker is not null) { worker.Stop(); await worker.Completion.ConfigureAwait(false); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel(); await _manager.ConfigureAwait(false); _stop.Dispose();
    }
}
