namespace RemoteDesk;

// Separate from connection status: telemetry and directory refreshes must not
// hide a persistence failure. A failed save keeps the current in-memory changes.
internal sealed class SettingsSaveNotice : TableLayoutPanel
{
    private readonly Label _message;
    private readonly Button _retry;

    public SettingsSaveNotice()
    {
        Dock = DockStyle.Top;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        ColumnCount = 2;
        RowCount = 1;
        Margin = Padding.Empty;
        Padding = new Padding(16, 8, 16, 8);
        BackColor = Color.FromArgb(254, 242, 242);
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _message = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(153, 27, 27),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 0, 12, 0)
        };
        _retry = new Button
        {
            Text = "重试保存",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Right,
            Padding = new Padding(8, 4, 8, 4),
            Margin = Padding.Empty,
            UseVisualStyleBackColor = true
        };
        _retry.Click += (_, _) => RetryRequested?.Invoke(this, EventArgs.Empty);
        Controls.Add(_message, 0, 0);
        Controls.Add(_retry, 1, 0);
        Visible = false;
    }

    public event EventHandler? RetryRequested;

    public bool HasPendingChanges { get; private set; }

    internal string MessageText => _message.Text;

    internal Button RetryButton => _retry;

    public void ApplyResult(SettingsSaveResult result)
    {
        HasPendingChanges = !result.Success;
        _message.Text = result.Success
            ? string.Empty
            : "设置未保存，当前更改仅本次运行有效。请检查磁盘空间、目录和写入权限后重试。" +
                (string.IsNullOrWhiteSpace(result.ErrorMessage) ? string.Empty : $" 原因：{result.ErrorMessage}");
        Visible = HasPendingChanges;
    }
}
