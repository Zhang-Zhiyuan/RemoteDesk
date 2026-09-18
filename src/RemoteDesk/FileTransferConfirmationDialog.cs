namespace RemoteDesk;

internal sealed record FileTransferConfirmationItem(
    string Kind,
    string SourcePath,
    string TransferName,
    long SizeBytes,
    string DestinationPath);

internal sealed record FileTransferConfirmationPreview(
    RemoteFilePastePlan Plan,
    IReadOnlyList<FileTransferConfirmationItem> Items,
    string Note);

internal static class FileTransferConfirmation
{
    private const string RenameSuffix = "（如重名会自动改名）";
    private const long MinimumDirectoryArchiveOverheadBytes = 16L * 1024 * 1024;
    private const long MaximumDirectoryArchiveOverheadBytes = 64L * 1024 * 1024;

    public static IReadOnlyList<FileTransferConfirmationItem> CreateItems(
        RemoteFilePastePlan plan,
        Func<RemoteFilePasteItem, string, string> destinationFormatter,
        bool calculateDirectorySizes = true)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(destinationFormatter);

        var items = new List<FileTransferConfirmationItem>();
        foreach (RemoteFilePasteItem item in plan.TransferItems)
        {
            string transferName = GetTransferName(item);
            long sizeBytes = GetSourceSize(item, calculateDirectorySizes);
            items.Add(new FileTransferConfirmationItem(
                item.Kind == RemoteFilePasteItemKind.Directory ? "文件夹" : "文件",
                item.Path,
                transferName,
                sizeBytes,
                destinationFormatter(item, transferName)));
        }

        return items;
    }

    public static FileTransferConfirmationPreview CreatePreview(
        RemoteFilePastePlan plan,
        Func<RemoteFilePasteItem, string, string> destinationFormatter)
    {
        return new FileTransferConfirmationPreview(
            plan,
            CreateItems(plan, destinationFormatter),
            BuildPlanNote(plan));
    }

    public static string FormatRemoteReceiveDestination(string transferName)
    {
        return $"被控端接收目录/{transferName}{RenameSuffix}；保存完成后显示实际位置";
    }

    public static string FormatRemoteDropPasteDestination(string transferName)
    {
        return $"被控端当前位置；若目标不接受粘贴，则保留在接收目录/{transferName}{RenameSuffix}";
    }

    public static string FormatLocalReceiveDestination(string transferName)
    {
        return Path.Combine(FileTransferReceiver.GetReceiveDirectory(), transferName) + RenameSuffix;
    }

    public static bool Confirm(
        IWin32Window? owner,
        string title,
        string actionText,
        IReadOnlyList<FileTransferConfirmationItem> items,
        string? note = null)
    {
        if (items.Count == 0)
        {
            return false;
        }

        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            using var dialog = new FileTransferConfirmationDialog(title, actionText, items, note);
            dialog.TopMost = owner is null;
            return dialog.ShowDialog(owner) == DialogResult.OK;
        }

        bool confirmed = false;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var dialog = new FileTransferConfirmationDialog(title, actionText, items, note);
                dialog.TopMost = true;
                confirmed = dialog.ShowDialog() == DialogResult.OK;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw failure;
        }

        return confirmed;
    }

    public static string BuildPlanNote(RemoteFilePastePlan plan)
    {
        return BuildPlanNote(plan, directorySizesIncluded: true);
    }

    public static string BuildPlanNote(
        RemoteFilePastePlan plan,
        bool directorySizesIncluded)
    {
        var notes = new List<string>();
        if (plan.TransferItems.Any(item => item.Kind == RemoteFilePasteItemKind.Directory))
        {
            notes.Add(directorySizesIncluded
                ? "文件夹会先打包为 zip 后传输，大小按原始文件夹内容统计。"
                : "文件夹会在确认后打包为 zip，大小届时计算；当前合计仅包含普通文件。");
        }

        if (plan.SkippedMissing > 0)
        {
            notes.Add($"已跳过 {plan.SkippedMissing} 个不存在或不可访问的项目。");
        }

        if (plan.SkippedDirectories > 0)
        {
            notes.Add($"已跳过 {plan.SkippedDirectories} 个文件夹。");
        }

        if (plan.Truncated)
        {
            notes.Add("本次列表超过上限，后续项目不会传输。");
        }

        return string.Join(Environment.NewLine, notes);
    }

    internal static long GetMaximumDirectoryArchiveSize(long sourceSizeBytes)
    {
        if (sourceSizeBytes < 0 || sourceSizeBytes > RemoteMessageCodec.MaxFileTransferBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceSizeBytes));
        }

        // ZIP metadata and incompressible-data overhead make an archive slightly larger than the
        // summed source files. Keep a generous allowance while still binding an accepted folder
        // preview to a finite wire size instead of allowing an unrelated 1 GiB payload.
        long overhead = Math.Clamp(
            sourceSizeBytes / 10,
            MinimumDirectoryArchiveOverheadBytes,
            MaximumDirectoryArchiveOverheadBytes);
        return sourceSizeBytes > RemoteMessageCodec.MaxFileTransferBytes - overhead
            ? RemoteMessageCodec.MaxFileTransferBytes
            : sourceSizeBytes + overhead;
    }

    private static string GetTransferName(RemoteFilePasteItem item)
    {
        return item.Kind == RemoteFilePasteItemKind.Directory
            ? RemoteFileTransfer.CreateDirectoryArchiveFileName(item.Path)
            : Path.GetFileName(item.Path);
    }

    private static long GetSourceSize(
        RemoteFilePasteItem item,
        bool calculateDirectorySizes)
    {
        if (item.Kind == RemoteFilePasteItemKind.File)
        {
            try
            {
                return new FileInfo(item.Path).Length;
            }
            catch (Exception ex) when (IsRecoverableSizeException(ex))
            {
                return 0;
            }
        }

        if (!calculateDirectorySizes)
        {
            return 0;
        }

        return CalculateDirectorySize(
            item.Path,
            directory => Directory.EnumerateFiles(directory),
            directory => Directory.EnumerateDirectories(directory),
            File.GetAttributes,
            file => new FileInfo(file).Length);
    }

    internal static long CalculateDirectorySize(
        string path,
        Func<string, IEnumerable<string>> enumerateFiles,
        Func<string, IEnumerable<string>> enumerateDirectories,
        Func<string, FileAttributes> getAttributes,
        Func<string, long> getFileLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(enumerateFiles);
        ArgumentNullException.ThrowIfNull(enumerateDirectories);
        ArgumentNullException.ThrowIfNull(getAttributes);
        ArgumentNullException.ThrowIfNull(getFileLength);

        long total = 0;
        var pending = new Stack<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Push(path);

        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            try
            {
                string fullPath = Path.GetFullPath(directory);
                if (!visited.Add(fullPath) ||
                    getAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }
            }
            catch (Exception ex) when (IsRecoverableSizeException(ex))
            {
                continue;
            }

            bool saturated = false;
            VisitEntries(enumerateFiles, directory, file =>
            {
                try
                {
                    if (getAttributes(file).HasFlag(FileAttributes.ReparsePoint))
                    {
                        return true;
                    }

                    long length = Math.Max(0, getFileLength(file));
                    if (length > RemoteMessageCodec.MaxFileTransferBytes - total)
                    {
                        saturated = true;
                        return false;
                    }

                    total += length;
                }
                catch (Exception ex) when (IsRecoverableSizeException(ex))
                {
                }

                return true;
            });
            if (saturated)
            {
                return RemoteMessageCodec.MaxFileTransferBytes + 1;
            }

            VisitEntries(enumerateDirectories, directory, child =>
            {
                pending.Push(child);
                return true;
            });
        }

        return total;
    }

    private static void VisitEntries(
        Func<string, IEnumerable<string>> enumerate,
        string directory,
        Func<string, bool> visitor)
    {
        IEnumerable<string> entries;
        try
        {
            entries = enumerate(directory) ?? Array.Empty<string>();
        }
        catch (Exception ex) when (IsRecoverableSizeException(ex))
        {
            return;
        }

        IEnumerator<string> enumerator;
        try
        {
            enumerator = entries.GetEnumerator();
        }
        catch (Exception ex) when (IsRecoverableSizeException(ex))
        {
            return;
        }

        try
        {
            while (true)
            {
                string entry;
                try
                {
                    if (!enumerator.MoveNext())
                    {
                        return;
                    }

                    entry = enumerator.Current;
                }
                catch (Exception ex) when (IsRecoverableSizeException(ex))
                {
                    return;
                }

                if (!visitor(entry))
                {
                    return;
                }
            }
        }
        finally
        {
            try
            {
                enumerator.Dispose();
            }
            catch (Exception ex) when (IsRecoverableSizeException(ex))
            {
            }
        }
    }

    private static bool IsRecoverableSizeException(Exception ex)
    {
        return ex is IOException or
            UnauthorizedAccessException or
            FileNotFoundException or
            DirectoryNotFoundException or
            PathTooLongException or
            NotSupportedException or
            ArgumentException or
            System.Security.SecurityException;
    }
}

internal sealed class FileTransferConfirmationDialog : Form
{
    private bool _responsiveLayoutReady;

    public FileTransferConfirmationDialog(
        string title,
        string actionText,
        IReadOnlyList<FileTransferConfirmationItem> items,
        string? note)
    {
        SuspendLayout();
        Text = string.IsNullOrWhiteSpace(title) ? "确认文件传输" : title;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(ResponsiveWindowLayout.DesignDpi, ResponsiveWindowLayout.DesignDpi);
        MinimumSize = Size.Empty;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(12)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        long totalBytes = items.Sum(item => Math.Max(0, item.SizeBytes));
        bool hasDeferredDirectorySize = items.Any(item =>
            string.Equals(item.Kind, "文件夹", StringComparison.Ordinal) &&
            item.SizeBytes == 0);
        string totalText = hasDeferredDirectorySize
            ? $"普通文件合计 {RemoteFileTransfer.FormatBytes(totalBytes)}，文件夹大小确认后计算"
            : $"合计 {RemoteFileTransfer.FormatBytes(totalBytes)}";
        var summary = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Text = $"{actionText}{Environment.NewLine}共 {items.Count} 项，{totalText}。",
            Padding = new Padding(0, 0, 0, 8)
        };
        root.Controls.Add(summary, 0, 0);

        var grid = new DataGridView
        {
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AutoGenerateColumns = false,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
            BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.FixedSingle,
            Dock = DockStyle.Fill,
            MinimumSize = new Size(0, 120),
            MultiSelect = false,
            ReadOnly = true,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect
        };
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "类型",
            DataPropertyName = nameof(FileTransferConfirmationItem.Kind),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.DisplayedCells
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "大小",
            Name = "Size",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.DisplayedCells
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "文件名",
            DataPropertyName = nameof(FileTransferConfirmationItem.TransferName),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 25,
            MinimumWidth = 80
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "原始位置",
            DataPropertyName = nameof(FileTransferConfirmationItem.SourcePath),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 45
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "传输后位置",
            DataPropertyName = nameof(FileTransferConfirmationItem.DestinationPath),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 55
        });
        grid.CellFormatting += (_, args) =>
        {
            if (grid.Columns[args.ColumnIndex].Name == "Size" &&
                args.RowIndex >= 0 &&
                args.RowIndex < items.Count)
            {
                FileTransferConfirmationItem item = items[args.RowIndex];
                args.Value = string.Equals(item.Kind, "文件夹", StringComparison.Ordinal) &&
                    item.SizeBytes == 0
                    ? "确认后计算"
                    : RemoteFileTransfer.FormatBytes(item.SizeBytes);
                args.FormattingApplied = true;
            }
        };
        grid.DataSource = items.ToArray();
        root.Controls.Add(grid, 0, 1);

        var selectedDetails = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
            WordWrap = true, ScrollBars = ScrollBars.Vertical, Height = 84,
            Margin = new Padding(0, 8, 0, 0), AccessibleName = "所选文件的完整路径"
        };
        void UpdateSelectedDetails()
        {
            if (grid.CurrentRow?.DataBoundItem is FileTransferConfirmationItem item)
                selectedDetails.Text = $"文件名：{item.TransferName}\r\n原始位置：{item.SourcePath}\r\n接收位置：{item.DestinationPath}";
        }
        grid.SelectionChanged += (_, _) => UpdateSelectedDetails();
        Shown += (_, _) => UpdateSelectedDetails();
        root.Controls.Add(selectedDetails, 0, 2);

        var noteLabel = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = SystemColors.GrayText,
            Padding = new Padding(0, 8, 0, 8),
            Text = string.IsNullOrWhiteSpace(note) ? "确认后才会开始实际传输。" : $"确认后才会开始实际传输。{Environment.NewLine}{note}"
        };
        root.Controls.Add(noteLabel, 0, 3);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = true
        };
        var confirmButton = new Button
        {
            AutoSize = true,
            DialogResult = DialogResult.OK,
            Text = "开始传输"
        };
        var cancelButton = new Button
        {
            AutoSize = true,
            DialogResult = DialogResult.Cancel,
            Text = "取消"
        };
        buttons.Controls.Add(confirmButton);
        buttons.Controls.Add(cancelButton);
        root.Controls.Add(buttons, 0, 4);

        AcceptButton = confirmButton;
        CancelButton = cancelButton;

        void UpdateWrappingWidths()
        {
            int availableWidth = Math.Max(1, root.ClientSize.Width - root.Padding.Horizontal);
            summary.MaximumSize = new Size(availableWidth, 0);
            noteLabel.MaximumSize = new Size(availableWidth, 0);
        }

        root.ClientSizeChanged += (_, _) => UpdateWrappingWidths();
        root.HandleCreated += (_, _) => UpdateWrappingWidths();
        UpdateWrappingWidths();
        ResumeLayout(performLayout: false);
        PerformLayout();
    }

    protected override void OnLoad(EventArgs args)
    {
        base.OnLoad(args);
        ResponsiveWindowLayout.ApplyTo(
            this,
            logicalPreferredSize: new Size(960, 560),
            logicalMinimumSize: new Size(600, 360),
            applyPreferredBounds: true);
        _responsiveLayoutReady = true;
    }

    protected override void OnDpiChanged(DpiChangedEventArgs args)
    {
        base.OnDpiChanged(args);
        ResponsiveWindowLayout.ApplyTo(
            this,
            logicalPreferredSize: new Size(960, 560),
            logicalMinimumSize: new Size(600, 360),
            applyPreferredBounds: false,
            suggestedBounds: args.SuggestedRectangle);
    }

    protected override void OnResizeEnd(EventArgs args)
    {
        base.OnResizeEnd(args);
        if (!_responsiveLayoutReady || IsDisposed)
        {
            return;
        }

        ResponsiveWindowLayout.ApplyTo(
            this,
            logicalPreferredSize: new Size(960, 560),
            logicalMinimumSize: new Size(600, 360),
            applyPreferredBounds: false);
    }
}
