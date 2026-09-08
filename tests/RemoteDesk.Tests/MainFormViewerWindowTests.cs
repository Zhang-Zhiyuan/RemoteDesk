using System.Windows.Forms;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class MainFormViewerWindowTests
{
    [Fact]
    public void BusySessionRejectionDoesNotTriggerRemoteStartRetry()
    {
        Assert.False(
            MainForm.IsConnectionFailureForRemoteStartRetry(
                new RemoteSessionRejectedException(
                    "被控端正由另一台查看端占用。")));
        Assert.True(
            MainForm.IsConnectionFailureForRemoteStartRetry(
                new TimeoutException("连接超时。")));
        Assert.True(
            MainForm.IsConnectionFailureForRemoteStartRetry(
                new IOException("连接被重置。")));
    }

    [Fact]
    public void ShouldDisconnectViewerAfterWindowClosedForManualCloseWhileConnected()
    {
        Assert.True(MainForm.ShouldDisconnectViewerAfterWindowClosed(
            closedFromDisconnect: false,
            isClosing: false,
            isDisposed: false,
            viewerConnected: true));
    }

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    [InlineData(false, false, false, false)]
    public void ShouldNotDisconnectViewerAfterWindowClosedForPassiveOrInactiveClose(
        bool closedFromDisconnect,
        bool isClosing,
        bool isDisposed,
        bool viewerConnected)
    {
        Assert.False(MainForm.ShouldDisconnectViewerAfterWindowClosed(
            closedFromDisconnect,
            isClosing,
            isDisposed,
            viewerConnected));
    }

    [Theory]
    [InlineData(Keys.Enter, true)]
    [InlineData(Keys.Control | Keys.Enter, false)]
    [InlineData(Keys.Shift | Keys.Enter, false)]
    [InlineData(Keys.F5, false)]
    public void IsPlainEnterOnlyAcceptsUnmodifiedEnter(Keys keys, bool expected)
    {
        Assert.Equal(expected, MainForm.IsPlainEnter(new KeyEventArgs(keys)));
    }

    [Theory]
    [InlineData(Keys.Control | Keys.V, true)]
    [InlineData(Keys.Shift | Keys.Insert, true)]
    [InlineData(Keys.Control | Keys.C, false)]
    [InlineData(Keys.Control | Keys.Shift | Keys.V, false)]
    [InlineData(Keys.V, false)]
    public void MainWindowFilePasteShortcutRecognizesOnlyPaste(Keys keys, bool expected)
    {
        Assert.Equal(expected, MainForm.IsMainWindowFilePasteShortcut(new KeyEventArgs(keys)));
    }

    [Fact]
    public void IsTextEntryControlRecognizesNestedInputs()
    {
        using var panel = new Panel();
        using var textBox = new TextBox();
        using var comboBox = new ComboBox();
        using var button = new Button();

        panel.Controls.Add(textBox);
        panel.Controls.Add(comboBox);
        panel.Controls.Add(button);

        Assert.True(MainForm.IsTextEntryControl(textBox));
        Assert.True(MainForm.IsTextEntryControl(comboBox));
        Assert.False(MainForm.IsTextEntryControl(button));
        Assert.False(MainForm.IsTextEntryControl(panel));
    }

    [Theory]
    [InlineData(
        "连接中断：远端关闭。",
        "连接中断：远端关闭；点击“连接”重试。")]
    [InlineData(
        "连接超时，已断开。",
        "连接超时，已断开；点击“连接”重试。")]
    [InlineData(
        "输入发送中断：连接被重置。",
        "输入发送中断：连接被重置；点击“连接”重试。")]
    [InlineData(
        "已加密连接 | 远端版本 2026-07-26 00:35:29",
        "连接已断开；点击“连接”可重新连接。")]
    [InlineData(
        "",
        "连接已断开；点击“连接”可重新连接。")]
    [InlineData(
        "连接已断开。",
        "连接已断开；点击“连接”可重新连接。")]
    public void
        ResolveDisconnectedViewerStatusPreservesActionableFailure(
            string currentStatus,
            string expected)
    {
        Assert.Equal(
            expected,
            MainForm
                .ResolveDisconnectedViewerStatus(
                    currentStatus));
    }

    [Fact]
    public void
        ResolveDisconnectedViewerStatusExplainsRemoteUpdateRestart()
    {
        Assert.Equal(
            "远程更新包已发送，等待被控端校验并重启：RemoteDesk.exe；远端正在重启，请稍后点击“连接”验证版本。",
            MainForm
                .ResolveDisconnectedViewerStatus(
                    "远程更新包已发送，等待被控端校验并重启：RemoteDesk.exe"));
    }

    [Theory]
    [InlineData(1_000, 46_000, true)]
    [InlineData(46_000, 46_000, true)]
    [InlineData(46_001, 46_000, false)]
    [InlineData(1_000, 0, false)]
    public void
        RemoteUpdateRestartExpectationHasBoundedLifetime(
            long now,
            long deadline,
            bool expected)
    {
        Assert.Equal(
            expected,
            MainForm
                .IsRemoteUpdateRestartExpected(
                    now,
                    deadline));
    }

    [Theory]
    [InlineData(
        "192.0.2.249",
        56565,
        "正在连接 192.0.2.249:56565（加密握手）...")]
    [InlineData(
        "  remote-host  ",
        443,
        "正在连接 remote-host:443（加密握手）...")]
    [InlineData(
        "",
        56565,
        "正在连接 远程设备（加密握手）...")]
    public void
        FormatViewerConnectingStatusNamesCurrentTarget(
            string host,
            int port,
            string expected)
    {
        Assert.Equal(
            expected,
            MainForm.FormatViewerConnectingStatus(
                host,
                port));
    }

    [Fact]
    public void WrappedViewerToolbarSectionShrinksToItsReflowedHeight()
    {
        int wideHeight = MeasureWrappedViewerToolbarSection(720);
        int narrowHeight = MeasureWrappedViewerToolbarSection(360);

        Assert.True(wideHeight > 0);
        Assert.True(narrowHeight > wideHeight);
    }

    [Fact]
    public void ViewerToolbarKeepsEachLabelWithItsInputWhenWrapping()
    {
        using var input = new TextBox
        {
            Width = 180
        };
        using FlowLayoutPanel field =
            MainForm.CreateNonWrappingToolbarField(
                "口令",
                input);

        Assert.False(field.WrapContents);
        Assert.Equal(2, field.Controls.Count);
        Assert.Equal("口令", field.Controls[0].Text);
        Assert.Same(input, field.Controls[1]);
        Assert.True(
            field.GetPreferredSize(Size.Empty).Width >=
                field.Controls[0].PreferredSize.Width +
                input.Width);
    }

    private static int MeasureWrappedViewerToolbarSection(
        int availableWidth)
    {
        using var shell = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2
        };
        shell.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(
            new RowStyle(SizeType.AutoSize));
        shell.RowStyles.Add(
            new RowStyle(SizeType.AutoSize));

        var header = new Label
        {
            AutoSize = true,
            Text = "连接",
            Padding = new Padding(12, 9, 12, 8),
            Margin = new Padding(1, 1, 1, 0)
        };
        var content = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 1
        };
        var toolbar = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Margin = new Padding(0)
        };
        for (int index = 0; index < 8; index++)
        {
            toolbar.Controls.Add(new Button
            {
                Text = $"Action {index}",
                Size = new Size(120, 36)
            });
        }

        shell.Controls.Add(header, 0, 0);
        shell.Controls.Add(content, 0, 1);
        content.Controls.Add(toolbar, 0, 0);

        int height = MainForm.ConstrainWrappedToolbarSection(
            toolbar,
            content,
            availableWidth);

        Assert.Equal(
            new Size(availableWidth, 0),
            toolbar.MaximumSize);
        Assert.Equal(height, shell.MaximumSize.Height);
        Assert.Equal(height, shell.Height);
        Assert.InRange(
            content.MaximumSize.Height,
            1,
            height - 1);
        return height;
    }
}
