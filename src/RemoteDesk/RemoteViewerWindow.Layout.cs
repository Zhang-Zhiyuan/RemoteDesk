namespace RemoteDesk;

internal sealed partial class RemoteViewerWindow
{
    private ViewerActionOverflow? _statusActionOverflow;
    private bool _statusFooterRefreshQueued;
    private bool _statusFooterMetricsDirty;

    private void InitializeStatusActionOverflow()
    {
        var more = CreateStatusActionButton("更多 ▴");
        more.AccessibleName = "更多远控操作";
        more.ContextMenuStrip = _statusMenu;
        _toolTip.SetToolTip(more, "窗口较窄，其他操作在这里；右键可查看状态与放大算法设置。");
        _statusActionOverflow = new ViewerActionOverflow(_fileTransferActionsPanel, more,
            [(_pullRemoteFilesButton, async () => await PullRemoteClipboardFilesAsync()),
             (_openReceivedFilesButton, OpenReceivedFilesDirectory),
             (_remoteInputMethodButton, async () => await SwitchRemoteInputMethodAsync()),
             (_switchCaptureTargetButton, async () => await SwitchCaptureTargetAsync()),
             (_displayScaleButton, ToggleDisplayScaleMode),
             (_experimentalUpscaleButton, ToggleExperimentalUpscaling),
             (_nativeDetailButton, async () => await ToggleNativeDetailsAsync()),
             (_fullScreenButton, ToggleFullScreen)],
            [[_displayScaleButton, _experimentalUpscaleButton], [_fullScreenButton], [_switchCaptureTargetButton],
             [_pullRemoteFilesButton, _openReceivedFilesButton], [_remoteInputMethodButton], [_nativeDetailButton]],
            ReleaseAllRemoteInputs);
        FontChanged += (_, _) => QueueStatusFooterLayout(refreshMetrics: true);
        _statusBar.FontChanged += (_, _) => QueueStatusFooterLayout(refreshMetrics: true);
        ClientSizeChanged += (_, _) => QueueStatusFooterLayout();
        Shown += (_, _) => QueueStatusFooterLayout(refreshMetrics: true);
    }

    private void QueueStatusFooterLayout(bool refreshMetrics = false)
    {
        _statusFooterMetricsDirty |= refreshMetrics;
        if (_statusFooterRefreshQueued || !IsHandleCreated || _isClosing || IsDisposed) return;
        _statusFooterRefreshQueued = true;
        try
        {
            BeginInvoke((Action)(() =>
            {
                _statusFooterRefreshQueued = false;
                if (_isClosing || IsDisposed) return;
                bool metrics = _statusFooterMetricsDirty;
                _statusFooterMetricsDirty = false;
                // WinForms has now finished docking/scaling. Changing a Bottom
                // child's height inside its parent's docking pass leaves its old Y.
                if (metrics) ApplyDpiMetrics(DeviceDpi);
                else UpdateStatusFooterLayout();
                PerformLayout();
            }));
        }
        catch (InvalidOperationException) { _statusFooterRefreshQueued = false; }
    }

    internal static int MeasureStatusTextHeight(Font font) => TextRenderer.MeasureText("Ag中文", font,
        Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Height;
}
