namespace RemoteDesk;

internal sealed partial class RemoteViewerWindow
{
    private static RemoteViewerWindow? _clipboardForegroundOwner;
    private System.Windows.Forms.Timer? _clipboardAutoTimer;
    private ClipboardAutoSyncCoordinator? _clipboardAuto;
    private CancellationTokenSource? _clipboardAutoLifetime;
    private long _clipboardAutoGeneration = -1;
    private long _clipboardDeactivateUntil;
    private long _clipboardAutoRetryAfter;
    private Task<bool> _clipboardAutoTask = Task.FromResult(true);
    private Task _clipboardPointerTask = Task.CompletedTask;
    private int _clipboardPointerPending;
    // Private-window-station probes cannot become the interactive desktop's foreground window.
    internal bool ClipboardForegroundForEntityTests { get; set; }

    private bool IsClipboardForeground => ClipboardForegroundForEntityTests ||
        (IsHandleCreated && GetForegroundWindow() == Handle);

    private bool CanUseAutomaticClipboard => !_isClosing && !IsDisposed &&
        _clipboardTextEnabled && _client.IsConnected && _client.SupportsClipboardSnapshots;

    private bool IsManualClipboardBusy => _clipboardPasteInProgress ||
        _clipboardPullTask is { IsCompleted: false } || _client.IsClipboardRequestPending ||
        _dragFileTransferInProgress || _remoteFilePullInProgress ||
        _client.IsRemoteClipboardFileRequestPending || _remoteDragOutStage != RemoteDragOutStage.None;

    private void InitializeAutomaticClipboard()
    {
        _client.LocalClipboardTextApplied += OnLocalClipboardTextApplied;
        _clipboardAutoTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _clipboardAutoTimer.Tick += (_, _) =>
        {
            if (!CanUseAutomaticClipboard)
            {
                ResetAutomaticClipboard();
                return;
            }
            bool foreground = IsClipboardForeground;
            if (foreground) Interlocked.Exchange(ref _clipboardForegroundOwner, this);
            // Stop starting reads shortly after departure. A bounded in-flight
            // copy may still finish after switching back to a local application;
            // sequence + active-viewer ownership checks protect newer user data.
            if ((!foreground && Environment.TickCount64 > _clipboardDeactivateUntil) ||
                !ReferenceEquals(Volatile.Read(ref _clipboardForegroundOwner), this) ||
                Environment.TickCount64 < _clipboardAutoRetryAfter || IsManualClipboardBusy ||
                !_clipboardAutoTask.IsCompleted) return;
            _clipboardAutoTask = RunAutomaticClipboardAsync(false, foreground);
        };
        _clipboardAutoTimer.Start();
    }

    protected override void OnActivated(EventArgs args)
    {
        base.OnActivated(args);
        Interlocked.Exchange(ref _clipboardForegroundOwner, this);
    }

    private void OnLocalClipboardTextApplied(long generation, uint sequence, string text)
    {
        if (generation == Volatile.Read(ref _clipboardAutoGeneration))
            Volatile.Read(ref _clipboardAuto)?.ObserveAppliedText(sequence, text);
    }

    private void ResetAutomaticClipboard()
    {
        _clipboardAutoLifetime?.Cancel();
        _clipboardAutoLifetime?.Dispose();
        _clipboardAutoLifetime = null;
        _clipboardAuto = null;
        _clipboardAutoGeneration = -1;
    }

    private void DisposeAutomaticClipboard()
    {
        _clipboardAutoTimer?.Stop();
        _clipboardAutoTimer?.Dispose();
        _clipboardAutoTimer = null;
        _client.LocalClipboardTextApplied -= OnLocalClipboardTextApplied;
        Interlocked.CompareExchange(ref _clipboardForegroundOwner, null, this);
        ResetAutomaticClipboard();
    }

    private async Task<bool> RunAutomaticClipboardAsync(bool beforeContext, bool allowLocalPush)
    {
        if (!CanUseAutomaticClipboard) return false;
        long generation = _client.InputConnectionGeneration;
        if (_clipboardAuto is null || _clipboardAutoGeneration != generation)
        {
            ResetAutomaticClipboard();
            _clipboardAutoGeneration = generation;
            _clipboardAutoLifetime = new CancellationTokenSource();
            CancellationToken token = _clipboardAutoLifetime.Token;
            bool Current() => !token.IsCancellationRequested && CanUseAutomaticClipboard &&
                _client.InputConnectionGeneration == generation &&
                ReferenceEquals(Volatile.Read(ref _clipboardForegroundOwner), this);
            _clipboardAuto = new ClipboardAutoSyncCoordinator(
                ClipboardTextService.ReadClipboardSequenceNumber,
                ClipboardTextService.GetTextAsync,
                text => Current() && IsClipboardForeground
                    ? _client.SendClipboardTextToRemoteAsync(text, string.Empty,
                        forPasteShortcut: true, expectedGeneration: generation)
                    : Task.FromResult(false),
                revision => _client.GetClipboardSnapshotAsync(revision, generation, token),
                ClipboardTextService.SetTextAndGetSequenceAsync, Current);
        }
        bool success = await _clipboardAuto.SyncAsync(beforeContext, allowLocalPush);
        if (!success) _clipboardAutoRetryAfter = Environment.TickCount64 + 2000;
        return success;
    }

    private async Task<bool> PrepareContextClipboardAsync()
    {
        await _clipboardAutoTask;
        if (!CanUseAutomaticClipboard || !IsClipboardForeground || IsManualClipboardBusy) return false;
        Interlocked.Exchange(ref _clipboardForegroundOwner, this);
        _clipboardAutoTask = RunAutomaticClipboardAsync(true, true);
        return await _clipboardAutoTask;
    }

    internal async Task<bool> SyncClipboardForEntityTestsAsync(bool beforeContext = false)
    {
        await _clipboardAutoTask;
        Interlocked.Exchange(ref _clipboardForegroundOwner, this);
        _clipboardAutoTask = RunAutomaticClipboardAsync(beforeContext, true);
        return await _clipboardAutoTask;
    }

    private void PictureBox_MouseDown(object? sender, MouseEventArgs args)
    {
        _pictureBox.Focus();
        bool prepareClipboard = args.Button == MouseButtons.Right && !_isAndroidRemote &&
            _inputEnabled && CanUseAutomaticClipboard;
        if (prepareClipboard || _clipboardPointerPending > 0)
            QueueClipboardPointer(() => PictureBox_MouseDownCore(sender, args), prepareClipboard);
        else PictureBox_MouseDownCore(sender, args);
    }

    private void PictureBox_MouseUp(object? sender, MouseEventArgs args)
    {
        if (_clipboardPointerPending > 0)
            QueueClipboardPointer(() => PictureBox_MouseUpCore(sender, args), false);
        else PictureBox_MouseUpCore(sender, args);
    }

    private void QueueClipboardPointer(Action send, bool prepareClipboard)
    {
        if (_clipboardPointerPending >= 32)
        {
            ReleaseAllRemoteInputs();
            SetStatus("剪贴板仍在同步，请稍后再点击", MutedTextColor);
            return;
        }
        long generation = _client.InputConnectionGeneration;
        long revision = _clipboardPasteRevision;
        Task previous = _clipboardPointerTask;
        _clipboardPointerPending++;
        _clipboardPointerTask = RunAsync();
        async Task RunAsync()
        {
            bool Current() => !_isClosing && !IsDisposed && _inputEnabled && _client.IsConnected &&
                _client.InputConnectionGeneration == generation && _clipboardPasteRevision == revision;
            try
            {
                await previous;
                if (!Current()) return;
                if (prepareClipboard && !await PrepareContextClipboardAsync())
                {
                    if (Current())
                        SetStatus("本机文本未同步；右键菜单仍可使用，粘贴将使用远端自身剪贴板", DangerTextColor);
                }
                if (Current()) send();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
            {
                if (Current()) ReleaseAllRemoteInputs();
            }
            finally { _clipboardPointerPending--; }
        }
    }
}
