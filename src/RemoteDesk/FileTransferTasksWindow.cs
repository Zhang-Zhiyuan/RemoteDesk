using System.Runtime.CompilerServices;

namespace RemoteDesk;

// A non-modal view of client-owned operations. Closing this window never
// cancels files or the desktop connection; cancellation names an exact task.
internal sealed class FileTransferTasksWindow : Form
{
    private static readonly ConditionalWeakTable<RemoteViewerClient, FileTransferTasksWindow> Windows = new();
    private readonly Func<IReadOnlyList<RemoteFileTransferTaskSnapshot>> _readTasks;
    private readonly Func<Guid, long, Task<bool>> _cancelTask;
    private readonly ListView _tasks;
    private readonly TextBox _details;
    private readonly Label _summary;
    private readonly ProgressBar _progress;
    private readonly Button _cancel;
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 250 };
    private readonly HashSet<Guid> _cancellationPending = [];
    private string? _actionNotice;
    private Guid? _actionNoticeTask;
    private Guid? _detailsTask;
    private readonly double[] _logicalColumnWidths = [70, 240, 230, 150];
    private bool _applyingColumnWidths;

    internal static void ShowFor(RemoteViewerClient client)
    {
        if (Windows.TryGetValue(client, out var existing) && !existing.IsDisposed)
        {
            if (existing.WindowState == FormWindowState.Minimized) existing.WindowState = FormWindowState.Normal;
            existing.Show();
            existing.Activate();
            return;
        }
        Windows.Remove(client);
        var window = new FileTransferTasksWindow(() => client.FileTransferTasks, client.CancelFileTransferAsync);
        Windows.Add(client, window);
        window.FormClosed += (_, _) => Windows.Remove(client);
        // Independent task window: minimizing the main/viewer window must not
        // hide the user's progress/cancel controls with it.
        window.Show();
    }

    internal FileTransferTasksWindow(Func<IReadOnlyList<RemoteFileTransferTaskSnapshot>> readTasks,
        Func<Guid, long, Task<bool>> cancelTask)
    {
        _readTasks = readTasks;
        _cancelTask = cancelTask;
        Text = "文件传输任务";
        Font = new Font("Microsoft YaHei UI", 9.25F, FontStyle.Regular, GraphicsUnit.Point);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        StartPosition = FormStartPosition.CenterScreen;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 5 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _summary = new Label { AutoSize = true, Dock = DockStyle.Top, UseMnemonic = false,
            Text = "暂无传输任务。发送或取回文件后，这里会显示进度和保存回执。", Margin = new Padding(0, 0, 0, 8) };
        _tasks = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
            MultiSelect = false, HideSelection = false, ShowItemToolTips = true,
            MinimumSize = new Size(0, 140), AccessibleName = "文件传输任务列表" };
        foreach (var column in new[] { ("方向", 70), ("文件 / 任务", 240), ("进度", 230), ("状态", 150) })
            _tasks.Columns.Add(column.Item1, column.Item2);
        _tasks.HandleCreated += (_, _) => ApplyColumnWidths();
        _tasks.DpiChangedAfterParent += (_, _) => ApplyColumnWidths();
        _tasks.FontChanged += (_, _) => ApplyColumnWidths();
        _tasks.ColumnWidthChanged += (_, e) =>
        {
            if (!_applyingColumnWidths && e.ColumnIndex >= 0 && e.ColumnIndex < _logicalColumnWidths.Length)
                _logicalColumnWidths[e.ColumnIndex] = _tasks.Columns[e.ColumnIndex].Width * 96d / _tasks.DeviceDpi;
        };
        _details = new TextBox { Dock = DockStyle.Top, Multiline = true, ReadOnly = true,
            ScrollBars = ScrollBars.Both, WordWrap = false, Height = 115,
            AccessibleName = "所选传输任务的完整文件名、实际保存位置和状态" };
        _progress = new ProgressBar { Dock = DockStyle.Top, Height = 18, Maximum = 100,
            AccessibleName = "所选任务传输进度" };
        _cancel = new WrappingActionButton { Text = "取消所选任务", AutoSize = true, Enabled = false,
            Padding = new Padding(8, 4, 8, 4) };
        var close = new WrappingActionButton { Text = "关闭面板（传输继续）", AutoSize = true,
            Padding = new Padding(8, 4, 8, 4) };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Margin = new Padding(0, 8, 0, 0) };
        actions.Controls.Add(_cancel);
        actions.Controls.Add(close);
        MainForm.ConfigureWrappingDialogActions(actions);
        root.Controls.Add(_summary, 0, 0);
        root.Controls.Add(_tasks, 0, 1);
        root.Controls.Add(_details, 0, 2);
        root.Controls.Add(_progress, 0, 3);
        root.Controls.Add(actions, 0, 4);
        FileTransferDialogLayout.Attach(this, root, _tasks);
        ResponsiveWindowLayout.TrackDpiAndResizeBounds(this, new Size(900, 560), new Size(440, 280));
        _tasks.SelectedIndexChanged += (_, _) => { _actionNotice = null; UpdateSelectedTask(); };
        _cancel.Click += async (_, _) => await CancelSelectedTaskAsync();
        close.Click += (_, _) => Close();
        CancelButton = close;
        _refreshTimer.Tick += (_, _) => { if (Visible && WindowState != FormWindowState.Minimized) RefreshTasks(); };
        Shown += (_, _) => { RefreshTasks(); _refreshTimer.Start(); };
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ResponsiveWindowLayout.ApplyTo(this, new Size(900, 560), new Size(440, 280), applyPreferredBounds: true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _refreshTimer.Dispose();
        base.Dispose(disposing);
    }

    internal ListView TasksForTests => _tasks;
    internal Button CancelForTests => _cancel;
    internal TextBox DetailsForTests => _details;

    private void ApplyColumnWidths()
    {
        if (_tasks.IsDisposed || _applyingColumnWidths) return;
        _applyingColumnWidths = true;
        try
        {
            // ListView scales its font but not its native column widths. Retain
            // user resizing in logical units rather than repeatedly multiplying
            // rounded physical widths when moving between monitors.
            string[] samples = ["发送", "文件 / 任务", "1023.99 MB / 1023.99 MB · 32/32 项", "传输结束（见回执）"];
            for (int i = 0; i < _tasks.Columns.Count; i++)
            {
                int contentWidth = TextRenderer.MeasureText(samples[i], _tasks.Font).Width +
                    ResponsiveWindowLayout.ScaleLogical(12, _tasks.DeviceDpi);
                int width = Math.Max(contentWidth, (int)Math.Round(_logicalColumnWidths[i] * _tasks.DeviceDpi / 96d));
                if (_tasks.Columns[i].Width != width) _tasks.Columns[i].Width = width;
            }
        }
        finally { _applyingColumnWidths = false; }
    }

    internal void RefreshTasks()
    {
        if (IsDisposed || Disposing) return;
        IReadOnlyList<RemoteFileTransferTaskSnapshot> tasks = _readTasks();
        var previous = _tasks.Items.Cast<ListViewItem>().ToDictionary(item => ((RemoteFileTransferTaskSnapshot)item.Tag!).TaskId);
        bool sameOrder = _tasks.Items.Count == tasks.Count;
        for (int i = 0; sameOrder && i < tasks.Count; i++)
            sameOrder = ((RemoteFileTransferTaskSnapshot)_tasks.Items[i].Tag!).TaskId == tasks[i].TaskId;
        Guid? selected = (_tasks.SelectedItems.Count > 0 ? _tasks.SelectedItems[0].Tag as RemoteFileTransferTaskSnapshot : null)?.TaskId;
        var rows = tasks.Select(task =>
        {
            var row = previous.TryGetValue(task.TaskId, out var old) ? old : new ListViewItem();
            while (row.SubItems.Count < 4) row.SubItems.Add("");
            SetCell(0, task.Direction == RemoteFileTransferDirection.Upload ? "发送" : "取回");
            SetCell(1, task.DisplayName);
            SetCell(2, FormatProgress(task));
            SetCell(3, FormatState(task.State));
            row.Tag = task;
            if (row.ToolTipText != task.Status) row.ToolTipText = task.Status;
            return row;
            void SetCell(int index, string value) { if (row.SubItems[index].Text != value) row.SubItems[index].Text = value; }
        }).ToArray();
        if (!sameOrder)
        {
            _tasks.BeginUpdate();
            try
            {
                _tasks.Items.Clear();
                _tasks.Items.AddRange(rows);
                var selection = rows.FirstOrDefault(row => ((RemoteFileTransferTaskSnapshot)row.Tag!).TaskId == selected)
                    ?? rows.LastOrDefault();
                if (selection is not null) selection.Selected = true;
            }
            finally { _tasks.EndUpdate(); }
        }
        string summary = tasks.Count == 0 ? "暂无传输任务。发送或取回文件后，这里会显示进度和保存回执。"
            : $"进行中 {tasks.Count(task => !task.IsTerminal)} 项 · 最近任务 {tasks.Count} 项。关闭面板不会中断传输；取消不会删除已经完成的文件。";
        if (_summary.Text != summary) _summary.Text = summary;
        UpdateSelectedTask();
    }

    private void UpdateSelectedTask()
    {
        var task = _tasks.SelectedItems.Count > 0 ? _tasks.SelectedItems[0].Tag as RemoteFileTransferTaskSnapshot : null;
        _cancel.Enabled = task is { CanCancel: true, IsTerminal: false } && !_cancellationPending.Contains(task.TaskId);
        string details = task is null ? "选择一项任务可查看详情和取消。" :
            $"任务：{task.DisplayName}\r\n当前文件：{task.CurrentFileName}\r\n{task.Status}" +
            (string.IsNullOrWhiteSpace(task.Destination) ? "" : $"\r\n保存位置：{task.Destination}") +
            (string.IsNullOrWhiteSpace(task.CancellationUnavailableReason) ? "" : $"\r\n取消说明：{task.CancellationUnavailableReason}");
        if (!string.IsNullOrWhiteSpace(_actionNotice) && task?.TaskId == _actionNoticeTask) details += "\r\n" + _actionNotice;
        if (_details.Text != details)
        {
            // Progress can change while the user selects a receipt path to copy.
            // Keep selections in unchanged prefix/suffix text, never remap a
            // changed filename or carry a selection over to another task.
            string previous = _details.Text;
            int start = _details.SelectionStart, length = _details.SelectionLength;
            int preservedStart = -1;
            if (task is not null && _detailsTask == task.TaskId && length > 0)
            {
                int prefix = 0, suffix = 0, common = Math.Min(previous.Length, details.Length);
                while (prefix < common && previous[prefix] == details[prefix]) prefix++;
                while (suffix < common - prefix && previous[^(suffix + 1)] == details[^(suffix + 1)]) suffix++;
                if (start + length <= prefix) preservedStart = start;
                else if (start >= previous.Length - suffix) preservedStart = start + details.Length - previous.Length;
            }
            _details.Text = details;
            if (preservedStart >= 0) _details.Select(preservedStart, length);
        }
        else if (_detailsTask != task?.TaskId)
            _details.Select(0, 0);
        _detailsTask = task?.TaskId;
        bool indeterminate = task is { IsTerminal: false, TotalBytes: null };
        ProgressBarStyle style = indeterminate ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
        if (_progress.Style != style) _progress.Style = style;
        if (_progress.MarqueeAnimationSpeed != (indeterminate ? 30 : 0))
            _progress.MarqueeAnimationSpeed = indeterminate ? 30 : 0;
        int percent = task is null ? 0 : task.TotalBytes is > 0
            ? (int)Math.Clamp((decimal)task.BytesTransferred * 100 / task.TotalBytes.Value, 0, 100)
            : task.State == RemoteFileTransferTaskState.Completed ? 100 : 0;
        if (_progress.Value != percent) _progress.Value = percent;
    }

    internal async Task CancelSelectedTaskAsync()
    {
        if (_tasks.SelectedItems.Count == 0 || _tasks.SelectedItems[0].Tag is not RemoteFileTransferTaskSnapshot task ||
            !task.CanCancel || task.IsTerminal || !_cancellationPending.Add(task.TaskId)) return;
        UpdateSelectedTask();
        try
        {
            bool requested = await _cancelTask(task.TaskId, task.ConnectionGeneration);
            _actionNotice = requested ? "已请求取消，正在等待当前任务停止。" : "任务已结束、连接已改变，或当前阶段不支持取消。";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        { _actionNotice = "取消未完成：" + ex.Message; }
        finally
        {
            _actionNoticeTask = task.TaskId;
            _cancellationPending.Remove(task.TaskId);
            if (!IsDisposed && !Disposing) RefreshTasks();
        }
    }

    internal static string FormatProgress(RemoteFileTransferTaskSnapshot task) =>
        $"{FormatBytes(task.BytesTransferred)} / {(task.TotalBytes.HasValue ? FormatBytes(task.TotalBytes.Value) : "总量待确认")} · {task.CompletedItems}/{task.TotalItems} 项";

    private static string FormatBytes(long value) => value >= 1024L * 1024 * 1024 ? $"{value / (1024d * 1024 * 1024):0.##} GB"
        : value >= 1024 * 1024 ? $"{value / (1024d * 1024):0.##} MB"
        : value >= 1024 ? $"{value / 1024d:0.##} KB" : $"{Math.Max(0, value)} B";

    internal static string FormatState(RemoteFileTransferTaskState state) => state switch
    {
        RemoteFileTransferTaskState.Queued => "等待发送",
        RemoteFileTransferTaskState.Preparing => "正在准备",
        RemoteFileTransferTaskState.AwaitingConfirmation => "等待确认",
        RemoteFileTransferTaskState.Transferring => "正在传输",
        RemoteFileTransferTaskState.AwaitingReceipt => "等待保存回执",
        RemoteFileTransferTaskState.Cancelling => "正在取消",
        RemoteFileTransferTaskState.Completed => "传输结束（见回执）",
        RemoteFileTransferTaskState.Cancelled => "已取消",
        _ => "未完成"
    };
}
