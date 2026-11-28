using System.Diagnostics;

namespace RemoteDesk;

internal enum NativeDetailMode { Automatic, On, Off }

// Consume the viewer's existing two-second presentation reports. Ignore quiet
// content, and leave the final wire budget to the sender's receiver receipts.
internal sealed class NativeDetailAutoQuality
{
    private int _healthy, _pressured;
    private long _lastReport = -1, _retryAt;

    internal bool Observe(bool active, long now, double seconds, double mbps, double fps, double pipelineMs,
        bool gpu, bool inputChanged)
    {
        if (now < _lastReport || now - _lastReport > 3500 || seconds is < 1.5 or > 3.5)
            _healthy = _pressured = 0;
        _lastReport = now;
        bool timely = seconds is >= 1.5 and <= 3.5;
        bool healthy = timely && gpu && mbps >= NativeDetailHeadroom.MinimumDelivery / 1_000_000d &&
            fps >= 8 && pipelineMs is >= 0 and <= 25;
        bool pressured = !gpu || pipelineMs >= 65 || (inputChanged && fps < 8 && mbps < 12);
        _healthy = healthy ? Math.Min(2, _healthy + 1) : 0;
        _pressured = pressured ? Math.Min(2, _pressured + 1) : 0;
        if (active && _pressured == 2) { _retryAt = now + 30_000; return false; }
        return active || (now >= _retryAt && _healthy == 2);
    }
}

internal sealed partial class RemoteViewerWindow
{
    private ToolStripMenuItem _nativeDetailMenuItem = null!;
    private NativeDetailMode _nativeDetailMode;
    private NativeDetailAutoQuality _nativeAutoQuality = new();
    private NativeDetailOffer? _nativeAutoOffer;
    private long _nativeAutoInputRevision;
    private System.Windows.Forms.Timer _nativeDetailTimer = null!;
    private bool _nativeDetailWanted, _nativeRequestPending, _nativeStopPending, _nativeStopInFlight;
    private bool _nativeViewportTooSmall;
    private long _nativeObservedInputRevision, _nativeIdleSince;
    private Rectangle _nativeRequestedViewport;
    private NativeDetailOffer? _nativeRequestedOffer;

    private void InitializeNativeDetailControls()
    {
        _nativeDetailMenuItem = new ToolStripMenuItem("静止时补清文字");
        foreach (var (mode, title) in new[] { (NativeDetailMode.Automatic, "自动（推荐）"),
                     (NativeDetailMode.On, "开启"), (NativeDetailMode.Off, "关闭") })
        {
            var item = new ToolStripMenuItem(title) { Tag = mode };
            item.Click += async (_, _) => await SetNativeDetailModeAsync(mode);
            _nativeDetailMenuItem.DropDownItems.Add(item);
        }
        _client.NativeDetailOfferChanged += OnNativeOfferChanged;
        _nativeDetailTimer = new System.Windows.Forms.Timer { Interval = 150 };
        _nativeDetailTimer.Tick += async (_, _) => await RefreshNativeRequestAsync();
    }

    private void OnNativeOfferChanged(NativeDetailOffer? offer) => OnUi(() =>
    {
        if (_isClosing || IsDisposed || offer != _client.NativeOffer) return;
        if (_nativeAutoOffer != offer)
        {
            _nativeAutoOffer = offer;
            _nativeAutoQuality = new();
            if (_nativeDetailMode == NativeDetailMode.Automatic && _nativeDetailWanted)
                _ = SetNativeDetailWantedAsync(false);
        }
        if (_nativeRequestedOffer != offer)
        {
            _client.InvalidateNativeDetails(); _nativeRequestedOffer = null;
            _nativeIdleSince = Environment.TickCount64;
        }
        UpdateNativeDetailButton();
    });

    internal Task ToggleNativeDetailsAsync() =>
        SetNativeDetailModeAsync(_nativeDetailWanted ? NativeDetailMode.Off : NativeDetailMode.On);

    private async Task SetNativeDetailModeAsync(NativeDetailMode mode)
    {
        if (_isClosing || IsDisposed || _nativeDetailMode == mode) return;
        _nativeDetailMode = mode;
        _nativeAutoQuality = new();
        await SetNativeDetailWantedAsync(mode == NativeDetailMode.On);
        if (!_isClosing && !IsDisposed) _pictureBox.Focus();
    }

    private void ObserveAutoNativeDetail(long sampledAt, double elapsed, double mbps, double fps, double pipelineMs, bool gpu)
    {
        if (_isClosing || IsDisposed || _nativeDetailMode != NativeDetailMode.Automatic ||
            _client.NativeOffer is not { Extended: true, Available: true } || !_client.IsConnected ||
            Environment.TickCount64 - sampledAt > 1000) return;
        long revision = _client.NativeInputRevision;
        bool wanted = _nativeAutoQuality.Observe(_nativeDetailWanted, sampledAt, elapsed, mbps, fps, pipelineMs,
            gpu, _nativeAutoInputRevision != revision);
        _nativeAutoInputRevision = revision;
        if (wanted != _nativeDetailWanted) _ = SetNativeDetailWantedAsync(wanted);
    }

    private async Task SetNativeDetailWantedAsync(bool wanted)
    {
        if (_isClosing || IsDisposed) return;
        if (_nativeDetailWanted == wanted) { UpdateNativeDetailButton(); return; }
        _nativeDetailWanted = wanted;
        _nativeViewportTooSmall = false;
        UpdateNativeDetailButton();
        if (!_nativeDetailWanted)
        {
            // Disable the local overlay synchronously before an ordered stop
            // request can encounter a slow network or driver teardown.
            _client.InvalidateNativeDetails();
            ScheduleNativeDetailPreparation();
            _nativeStopPending = true;
            await SendNativeStopAsync();
            if (_nativeDetailMode != NativeDetailMode.Automatic && !_nativeDetailWanted && !_isClosing && !IsDisposed)
                SetStatus("原生补清已关闭，使用普通远控画面。", MutedTextColor);
        }
        else
        {
            _nativeDetailTimer.Start();
            _nativeStopPending = false;
            _nativeRequestedOffer = null; _nativeIdleSince = Environment.TickCount64;
            if (_nativeDetailMode != NativeDetailMode.Automatic)
                SetStatus(_client.NativeOffer is { Extended: true }
                    ? "文字补清已开启。停下操作后，可见区域会逐步变清。"
                    : "文字补清已开启。此版本的被控端会补清鼠标附近。", MutedTextColor);
            await RefreshNativeRequestAsync();
        }
    }

    private void UpdateNativeDetailButton()
    {
        bool available = _client.NativeOffer is { Available: true };
        _nativeDetailMenuItem.Available = available || _nativeDetailWanted;
        _nativeDetailMenuItem.Checked = _nativeDetailWanted;
        foreach (ToolStripMenuItem item in _nativeDetailMenuItem.DropDownItems)
            item.Checked = item.Tag is NativeDetailMode mode && mode == _nativeDetailMode;
        bool paused = _nativeDetailWanted && (!available || _nativeViewportTooSmall);
        _nativeDetailMenuItem.Text = paused ? "静止时补清文字（已暂停）" : "静止时补清文字";
        _nativeDetailMenuItem.ToolTipText = _nativeViewportTooSmall ? "放大窗口后会自动恢复。"
            : _nativeDetailMode == NativeDetailMode.Automatic && _client.NativeOffer is { Extended: true }
                ? "带宽充足且显示流畅时，停下操作就会补清文字。"
            : _nativeDetailMode == NativeDetailMode.Automatic ? "此被控端支持手动补清，选择“开启”可补清鼠标附近。"
            : _client.NativeOffer is { Extended: true } ? "停下操作后补清可见区域。操作和视频优先。"
            : "此版本的被控端使用可靠视频补清鼠标附近。";
    }

    private async Task RefreshNativeRequestAsync()
    {
        if (_isClosing || IsDisposed) return;
        if (_nativeStopPending)
        {
            await SendNativeStopAsync();
            return;
        }
        if (!_nativeDetailWanted || _nativeRequestPending || _nativeStopInFlight ||
            !_client.IsConnected || _client.NativeOffer is not { Available: true } offer) return;
        Size encoded = GetRemoteImageSize();
        if (encoded.Width <= 0 || encoded.Height <= 0) return;
        Rectangle displayed = GetZoomedImageRectangle(encoded);
        bool tooSmall = !CanDisplayNativeDetails(offer.NativeSize, displayed.Size);
        if (_nativeViewportTooSmall != tooSmall)
        {
            _nativeViewportTooSmall = tooSmall;
            UpdateNativeDetailButton();
            _nativeIdleSince = Environment.TickCount64;
            if (tooSmall && (_nativeRequestedOffer is not null || _client.NativeDetails.IsEnabled))
            {
                _nativeRequestedOffer = null;
                _client.InvalidateNativeDetails();
                ScheduleNativeDetailPreparation();
                _nativeStopPending = true;
                await SendNativeStopAsync();
            }
        }
        if (tooSmall) return;
        long revision = _client.NativeInputRevision, now = Environment.TickCount64;
        if (revision != _nativeObservedInputRevision)
        {
            _nativeObservedInputRevision = revision; _nativeIdleSince = now; return;
        }
        if (now - _nativeIdleSince < 350) return;
        Rectangle viewport = VisibleNativeArea(offer.NativeSize, displayed, _pictureBox.ClientRectangle);
        if (viewport.IsEmpty) return;
        if (!offer.Extended)
        {
            Point pointer = _pictureBox.PointToClient(Cursor.Position);
            if (!displayed.Contains(pointer)) pointer = new(displayed.Left + displayed.Width / 2, displayed.Top + displayed.Height / 2);
            Point focus = new((int)((long)(pointer.X - displayed.X) * offer.NativeSize.Width / displayed.Width),
                              (int)((long)(pointer.Y - displayed.Y) * offer.NativeSize.Height / displayed.Height));
            viewport = CalculateNativeDetailViewport(offer.NativeSize, focus);
        }
        if (_client.NativeDetails.IsEnabled && _nativeRequestedOffer == offer && _nativeRequestedViewport == viewport) return;
        _nativeRequestPending = true;
        try
        {
            if (await _client.RequestNativeDetailsAsync(offer.NativeSize, viewport) && _nativeDetailWanted)
            { _nativeRequestedViewport = viewport; _nativeRequestedOffer = offer; }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException)
        {
            if (!_isClosing) SetStatus("原生补清暂未启用，正常远控不受影响：" + ex.Message, MutedTextColor);
        }
        finally { _nativeRequestPending = false; }
    }

    private async Task SendNativeStopAsync()
    {
        if (_nativeStopInFlight) return;
        _nativeStopInFlight = true;
        try
        {
            bool stopped = !_client.IsConnected || await _client.StopNativeDetailsAsync();
            if (!_nativeDetailWanted || _nativeViewportTooSmall) _nativeStopPending = !stopped;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException)
        { /* Retry on the idle timer without blocking input or closing the viewer. */ }
        finally
        {
            _nativeStopInFlight = false;
            if (!_nativeDetailWanted && !_nativeStopPending && !_isClosing && !IsDisposed) _nativeDetailTimer.Stop();
        }
    }

    internal static bool CanDisplayNativeDetails(Size native, Size displayed) =>
        native.Width > 0 && native.Height > 0 &&
        native.Width <= (long)displayed.Width * D3D11NativeDetailCompositor.MaxNativePixelsPerDisplayPixel &&
        native.Height <= (long)displayed.Height * D3D11NativeDetailCompositor.MaxNativePixelsPerDisplayPixel;

    internal static Rectangle VisibleNativeArea(Size native, Rectangle displayed, Rectangle client)
    {
        var visible = Rectangle.Intersect(displayed, client);
        if (visible.Width <= 0 || visible.Height <= 0 || native.Width <= 0 || native.Height <= 0) return Rectangle.Empty;
        return Rectangle.FromLTRB((int)((long)(visible.Left - displayed.Left) * native.Width / displayed.Width),
            (int)((long)(visible.Top - displayed.Top) * native.Height / displayed.Height),
            (int)(((long)(visible.Right - displayed.Left) * native.Width + displayed.Width - 1) / displayed.Width),
            (int)(((long)(visible.Bottom - displayed.Top) * native.Height + displayed.Height - 1) / displayed.Height));
    }

    internal static Rectangle CalculateNativeDetailViewport(Size size, Point focus)
    {
        if (size.Width <= 0 || size.Height <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        const int edge = 128, span = 8 * edge;
        int x = Math.Clamp((Math.Clamp(focus.X, 0, size.Width - 1) / edge - 4) * edge,
            0, Math.Max(0, ((size.Width - 1) / edge - 7) * edge));
        int y = Math.Clamp((Math.Clamp(focus.Y, 0, size.Height - 1) / edge - 4) * edge,
            0, Math.Max(0, ((size.Height - 1) / edge - 7) * edge));
        return new(x, y, Math.Min(span, size.Width - x), Math.Min(span, size.Height - y));
    }

    private void DisposeNativeDetailControls()
    {
        _nativeDetailTimer?.Stop(); _nativeDetailTimer?.Dispose();
        _client.NativeDetailOfferChanged -= OnNativeOfferChanged;
        _client.InvalidateNativeDetails();
    }
}
