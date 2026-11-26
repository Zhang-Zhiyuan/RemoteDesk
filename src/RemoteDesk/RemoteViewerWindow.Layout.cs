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
        _toolTip.SetToolTip(more, "输入、隐私、文件和其他会话操作。");
        _statusActionOverflow = new ViewerActionOverflow(_fileTransferActionsPanel, more,
            [(_androidBackButton, async () => await SendAndroidNavigationAsync(Keys.Escape)),
             (_androidHomeButton, async () => await SendAndroidNavigationAsync(Keys.Home)),
             (_androidRecentsButton, async () => await SendAndroidNavigationAsync(Keys.F12)),
             (_inputPauseButton, ToggleLocalInputPause),
             (_privacyScreenButton, () => BeginInvoke((Action)(() => ShowFooterMenu(_privacyMenu, _privacyScreenButton)))),
             (_transferTasksButton, ShowTransferTasks),
             (_sendAccountPackageButton, async () => await SendAccountPackageAsync()),
             (_pullRemoteFilesButton, async () => await PullRemoteClipboardFilesAsync()),
             (_openReceivedFilesButton, OpenReceivedFilesDirectory),
             (_remoteInputMethodButton, async () => await SwitchRemoteInputMethodAsync()),
             (_switchCaptureTargetButton, async () => await SwitchCaptureTargetAsync()),
             (_additionalScreenButton, async () => await OpenAdditionalScreenAsync()),
             (_displayScaleButton, () => BeginInvoke((Action)ShowDisplayOptions)),
             (_fullScreenButton, ToggleFullScreen)],
            [[_displayScaleButton], [_fullScreenButton], [_switchCaptureTargetButton], [_androidBackButton]],
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
