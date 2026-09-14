using System.Diagnostics;

namespace RemoteDesk;

internal sealed partial class RemoteViewerWindow
{
    private Button _nativeDetailButton = null!;
    private System.Windows.Forms.Timer _nativeDetailTimer = null!;
    private bool _nativeDetailWanted, _nativeRequestPending, _nativeStopPending, _nativeStopInFlight;
    private long _nativeObservedInputRevision, _nativeIdleSince;
    private Rectangle _nativeRequestedViewport;
    private NativeDetailOffer? _nativeRequestedOffer;

    private void InitializeNativeDetailControls()
    {
        _nativeDetailButton = CreateStatusActionButton("原生补清：关");
        _nativeDetailButton.AccessibleName = "原生文字补清，关闭";
        _nativeDetailButton.Visible = _client.NativeOffer is { Available: true };
        _nativeDetailButton.Click += async (_, _) => await ToggleNativeDetailsAsync();
        _fileTransferActionsPanel.Controls.Add(_nativeDetailButton);
        _client.NativeDetailOfferChanged += OnNativeOfferChanged;
        _nativeDetailTimer = new System.Windows.Forms.Timer { Interval = 150 };
        _nativeDetailTimer.Tick += async (_, _) => await RefreshNativeRequestAsync();
        _nativeDetailTimer.Start();
    }

    private void OnNativeOfferChanged(NativeDetailOffer? offer) => OnUi(() =>
    {
        if (_isClosing || IsDisposed || offer != _client.NativeOffer) return;
        _nativeDetailButton.Visible = offer is { Available: true } || _nativeDetailWanted;
        _nativeDetailButton.Enabled = offer is { Available: true } || _nativeDetailWanted;
        if (_nativeRequestedOffer != offer)
        {
            _client.InvalidateNativeDetails(); _nativeRequestedOffer = null;
            _nativeIdleSince = Environment.TickCount64;
        }
        UpdateNativeDetailButton();
    });

    internal async Task ToggleNativeDetailsAsync()
    {
        if (_isClosing || IsDisposed) return;
        _nativeDetailWanted = !_nativeDetailWanted;
        UpdateNativeDetailButton();
        if (!_nativeDetailWanted)
        {
            // Disable the local overlay synchronously before an ordered stop
            // request can encounter a slow network or driver teardown.
            _client.InvalidateNativeDetails();
            ScheduleNativeDetailPreparation();
            _nativeStopPending = true;
            await SendNativeStopAsync();
            if (!_nativeDetailWanted && !_isClosing && !IsDisposed)
                SetStatus("原生补清已关闭，使用普通远控画面。", MutedTextColor);
        }
        else
        {
            _nativeStopPending = false;
            _nativeRequestedOffer = null; _nativeIdleSince = Environment.TickCount64;
            SetStatus("原生补清已开启：停下操作后补充鼠标附近的原始像素；拥塞或硬件不支持时保持普通画面。", MutedTextColor);
            await RefreshNativeRequestAsync();
        }
        if (!_isClosing && !IsDisposed) _pictureBox.Focus();
    }

    private void UpdateNativeDetailButton()
    {
        bool available = _client.NativeOffer is { Available: true };
        _nativeDetailButton.Visible = available || _nativeDetailWanted;
        _nativeDetailButton.Enabled = available || _nativeDetailWanted;
        _nativeDetailButton.Text = !_nativeDetailWanted ? "原生补清：关" : available ? "原生补清：开" : "原生补清：暂停";
        _nativeDetailButton.AccessibleName = _nativeDetailWanted ? "原生文字补清，开启" : "原生文字补清，关闭";
        _toolTip?.SetToolTip(_nativeDetailButton,
            "保留流畅 H.264，空闲时补充鼠标附近的原始像素，不猜测文字。\n" +
            "需要新版 Windows 被控端和 GPU 显示。默认关闭，可随时恢复普通画面。\n" +
            "无带宽余量、正在输入或窗口太小时不补清；当前版本仅增强最多 1024×1024 的区域。\n" +
            "开启后使用可靠视频传输；已有 UDP 鼠标通道继续保留。");
    }

    private async Task RefreshNativeRequestAsync()
    {
        if (!_nativeDetailWanted && _nativeStopPending && !_isClosing && !IsDisposed)
        {
            await SendNativeStopAsync();
            return;
        }
        if (_isClosing || IsDisposed || !_nativeDetailWanted || _nativeRequestPending ||
            !_client.IsConnected || _client.NativeOffer is not { Available: true } offer) return;
        long revision = _client.NativeInputRevision, now = Environment.TickCount64;
        if (revision != _nativeObservedInputRevision)
        {
            _nativeObservedInputRevision = revision; _nativeIdleSince = now; return;
        }
        if (now - _nativeIdleSince < 350) return;
        Size encoded = GetRemoteImageSize();
        if (encoded.Width <= 0 || encoded.Height <= 0) return;
        Point pointer = _pictureBox.PointToClient(Cursor.Position);
        Rectangle displayed = GetZoomedImageRectangle(encoded);
        if (displayed.Width <= 0 || displayed.Height <= 0) return;
        // A local pointer outside the picture should not move the requested
        // region to a toolbar or oscillate the request after every tick.
        if (!displayed.Contains(pointer)) pointer = new(displayed.Left + displayed.Width / 2, displayed.Top + displayed.Height / 2);
        Point nativePoint = new(
            Math.Clamp((int)((long)(pointer.X - displayed.X) * offer.NativeSize.Width / displayed.Width), 0, offer.NativeSize.Width - 1),
            Math.Clamp((int)((long)(pointer.Y - displayed.Y) * offer.NativeSize.Height / displayed.Height), 0, offer.NativeSize.Height - 1));
        Rectangle viewport = CalculateNativeDetailViewport(offer.NativeSize, nativePoint);
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
            if (!_nativeDetailWanted) _nativeStopPending = !stopped;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException)
        { /* Retry on the idle timer without blocking input or closing the viewer. */ }
        finally { _nativeStopInFlight = false; }
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
