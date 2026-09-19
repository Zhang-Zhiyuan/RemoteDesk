namespace RemoteDesk;

internal sealed class FileTransferResultDialog : Form
{
    internal FileTransferResultDialog(string heading, string details)
    {
        Text = "文件传输结果";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 3 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(new Label { AutoSize = true, Dock = DockStyle.Fill, Text = heading, Padding = new Padding(0, 0, 0, 10) }, 0, 0);
        var resultDetails = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill,
            MinimumSize = new Size(0, 120), AccessibleName = "实际保存位置和传输结果",
            ScrollBars = ScrollBars.Both, WordWrap = false, Text = details };
        root.Controls.Add(resultDetails, 0, 1);
        var close = new Button { Text = "知道了", AutoSize = true, MinimumSize = new Size(90, 32), Padding = new Padding(8, 4, 8, 4), Anchor = AnchorStyles.Right, DialogResult = DialogResult.OK, Margin = new Padding(0, 10, 0, 0) };
        root.Controls.Add(close, 0, 2);
        FileTransferDialogLayout.Attach(this, root, resultDetails);
        AcceptButton = CancelButton = close;
        ActiveControl = close;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ResponsiveWindowLayout.ApplyTo(this, new Size(900, 500), new Size(480, 280), applyPreferredBounds: true);
    }

    internal static string FormatDetails(RemoteFilePasteResult result, string destinationNote, bool remotePasteRequested)
    {
        var lines = new List<string> { destinationNote, "" };
        if (result.Results is not null)
            foreach (var item in result.Results)
                lines.Add($"{(item.Success ? "已发送" : "未完成")}：{item.Name}\r\n{item.Details}\r\n");
        else if (!string.IsNullOrWhiteSpace(result.FailureMessage)) lines.Add(result.FailureMessage);
        if (result.SkippedMissing > 0) lines.Add($"跳过 {result.SkippedMissing} 个不可访问项目。");
        if (result.Truncated) lines.Add("选择数量超过本次上限，未列入的项目没有发送。");
        if (result.ArchivedDirectories > 0) lines.Add("文件夹已打包为 ZIP，接收端不会自动解压。");
        if (remotePasteRequested) lines.Add("以上回执确认的是接收副本的位置。已另外请求远端当前窗口粘贴；无法确认该窗口是否接受或其最终路径，请以远端窗口为准。");
        return string.Join("\r\n", lines);
    }

    internal static void ShowResults(IWin32Window owner, RemoteFilePasteResult result, string destinationNote, bool remotePasteRequested = false)
    {
        using var dialog = new FileTransferResultDialog($"已发送 {result.SentFiles} 项，未完成 {result.FailedFiles} 项；实际保存结果如下。",
            FormatDetails(result, destinationNote, remotePasteRequested));
        dialog.ShowDialog(owner);
    }
}
