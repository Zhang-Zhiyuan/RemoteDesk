using RemoteDesk;

// Production dialog controls, synthetic data and synthetic DPI messages on an
// isolated station. Never changes monitor settings or confirms any operation.
internal static class DialogDpiProbe
{
    internal static void Verify(string output)
    {
        var cases = new List<object>();
        var failures = new List<string>();
        FileTransferConfirmationItem[] items = [new("文件", @"C:\Synthetic\测试资料\项目说明.txt", "项目说明.txt", 1024,
            @"D:\RemoteDeskReceived\测试资料\项目说明 (2).txt")];
        var task = new RemoteFileTransferTaskSnapshot(Guid.NewGuid(), 1, RemoteFileTransferDirection.Upload,
            RemoteFileTransferTaskState.AwaitingReceipt, "发送项目文档", items[0].TransferName, 1024, 1024, 0, 1,
            true, "文件数据已发送，等待远端保存回执。", Destination: items[0].DestinationPath);
        (string Name, Func<Form> Create)[] factories = [
            ("file-confirm", () => new FileTransferConfirmationDialog("确认文件传输", "发送文件到远端", items,
                "文件夹会打包为 ZIP。若重名会自动改名；完成后显示实际保存位置。")),
            ("file-result", () => new FileTransferResultDialog("已发送 1 项，未完成 1 项。",
                "已保存：" + items[0].DestinationPath + "\r\n未完成：另一个文档.txt\r\n远端磁盘空间不足")),
            ("file-tasks", () => new FileTransferTasksWindow(() => [task], (_, _) => Task.FromResult(false))),
            ("relay-login", () => new RelaySetupDialog(new RelaySettings { ServerAddress = "relay.example.invalid" }, true)),
            ("relay-deploy", () => new RelaySetupDialog(new RelaySettings { ServerAddress = "relay.example.invalid" }))
        ];
        foreach (var factory in factories)
        {
            using Form form = factory.Create();
            form.Show();
            SyntheticDpi.Pump();
            Rectangle workArea = Screen.FromControl(form).WorkingArea;
            form.Bounds = Rectangle.Inflate(workArea, -12, -12);
            var boundsTransition = SyntheticDpi.Apply(form, 288);
            bool bounded = Screen.FromControl(form).WorkingArea.Contains(form.Bounds);
            cases.Add(new { dialog = factory.Name, boundaryTransition = boundsTransition, bounded, workArea });
            if (!bounded) failures.Add(factory.Name + ": DPI transition leaves dialog outside working area: " + form.Bounds);
            int index = 0;
            foreach (int dpi in new[] { 96, 120, 144, 168, 192, 240, 288, 144, 96 })
            {
                object transition = SyntheticDpi.Apply(form, dpi);
                foreach (Size logicalSize in new[] { new Size(860, 600), new Size(460, 320) })
                {
                    form.MinimumSize = Size.Empty;
                    // A small physical monitor at high DPI deliberately offers
                    // fewer logical pixels, exercising scrolling and wrapping.
                    form.ClientSize = new Size(Math.Min(1280, ResponsiveWindowLayout.ScaleLogical(logicalSize.Width, dpi)),
                        Math.Min(800, ResponsiveWindowLayout.ScaleLogical(logicalSize.Height, dpi)));
                    var scroll = (ScrollableControl)form.Controls[0];
                    scroll.AutoScrollPosition = Point.Empty;
                    SyntheticDpi.Pump();
                    var issues = new List<string>();
                    string stem = $"{factory.Name}-{index++}-{dpi}-{logicalSize.Width}";
                    void Capture(string suffix)
                    {
                        using var bitmap = new Bitmap(form.Width, form.Height);
                        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                        bitmap.Save(Path.Combine(output, stem + suffix + ".png"));
                    }
                    Capture("");
                    foreach (Control control in SyntheticDpi.Descendants(scroll).Where(c => c.Visible &&
                        c is Button or CheckBox or TextBox or NumericUpDown or ListView or DataGridView))
                    {
                        scroll.ScrollControlIntoView(control);
                        control.Select();
                        SyntheticDpi.Pump();
                        Rectangle bounds = control.RectangleToScreen(control.ClientRectangle);
                        Rectangle viewport = scroll.RectangleToScreen(scroll.ClientRectangle);
                        if (!viewport.Contains(bounds) || bounds.Width < 12 || bounds.Height < 10)
                            issues.Add($"{control.GetType().Name} {control.AccessibleName ?? control.Text}: not reachable {bounds} inside {viewport}");
                        for (Control? parent = control.Parent; parent is not null && parent != scroll; parent = parent.Parent)
                            if (!parent.RectangleToScreen(parent.ClientRectangle).Contains(bounds))
                                issues.Add($"{control.GetType().Name} clipped by {parent.GetType().Name}");
                        if (control is Button or CheckBox)
                        {
                            Size preferred = control.GetPreferredSize(Size.Empty);
                            if (control.Width + 2 < preferred.Width || control.Height + 2 < preferred.Height)
                                issues.Add($"{control.GetType().Name} {control.Text}: caption {control.Size} preferred {preferred}");
                        }
                    }
                    foreach (Label label in SyntheticDpi.Descendants(scroll).OfType<Label>().Where(c => c.Visible))
                    {
                        Size preferred = label.GetPreferredSize(new Size(Math.Max(1, label.Width), 0));
                        if (label.Height + 2 < preferred.Height)
                            issues.Add($"Label {label.Text}: height {label.Height} preferred {preferred.Height}");
                    }
                    foreach (ListView list in SyntheticDpi.Descendants(scroll).OfType<ListView>())
                    {
                        foreach (int columnIndex in new[] { 0, 2, 3 })
                        {
                            var column = list.Columns[columnIndex];
                            int needed = TextRenderer.MeasureText(column.Text, list.Font).Width;
                            foreach (ListViewItem row in list.Items)
                                needed = Math.Max(needed, TextRenderer.MeasureText(row.SubItems[columnIndex].Text, list.Font).Width);
                            needed += ResponsiveWindowLayout.ScaleLogical(6, dpi);
                            if (column.Width < needed) issues.Add($"List column '{column.Text}' clipped: {column.Width} needs {needed}");
                        }
                    }
                    foreach (DataGridView grid in SyntheticDpi.Descendants(scroll).OfType<DataGridView>())
                    {
                        int textHeight = TextRenderer.MeasureText("类型", grid.ColumnHeadersDefaultCellStyle.Font ?? grid.Font).Height;
                        if (grid.ColumnHeadersHeight < textHeight + 4)
                            issues.Add($"Grid header clipped: {grid.ColumnHeadersHeight} needs {textHeight + 4}");
                    }
                    if (SyntheticDpi.Descendants(form).Where(c => c.Visible).Any(c => c.DeviceDpi != dpi))
                        issues.Add("Visible descendants do not share the requested managed DPI");
                    int idleLayouts = 0;
                    LayoutEventHandler counted = (_, _) => idleLayouts++;
                    scroll.Layout += counted;
                    SyntheticDpi.Pump();
                    scroll.Layout -= counted;
                    if (idleLayouts > 3) issues.Add($"Layout did not settle: {idleLayouts}");
                    Capture("-scrolled");
                    cases.Add(new { dialog = factory.Name, dpi, nativeDpi = SyntheticDpi.NativeDpi(form),
                        transition, form.ClientSize, form.Font.SizeInPoints, idleLayouts, issues,
                        listColumns = SyntheticDpi.Descendants(form).OfType<ListView>().Select(list =>
                            list.Columns.Cast<ColumnHeader>().Select(column => new { column.Text, column.Width }).ToArray()).ToArray(),
                        gridColumns = SyntheticDpi.Descendants(form).OfType<DataGridView>().Select(grid =>
                            grid.Columns.Cast<DataGridViewColumn>().Select(column => new { column.HeaderText, column.Width, column.MinimumWidth }).ToArray()).ToArray() });
                    failures.AddRange(issues.Select(issue => stem + ": " + issue));
                    Console.WriteLine($"{factory.Name} {dpi} DPI {logicalSize.Width}: {issues.Count} findings");
                }
            }
            if (factory.Name == "file-tasks")
            {
                var list = SyntheticDpi.Descendants(form).OfType<ListView>().Single();
                list.Columns[1].Width = 900;
                SyntheticDpi.Apply(form, 240);
                int enlarged = list.Columns[1].Width;
                SyntheticDpi.Apply(form, 96);
                bool preserved = enlarged == 2250 && list.Columns[1].Width == 900;
                cases.Add(new { dialog = factory.Name, userResizedColumnRoundTrip = true, enlarged,
                    restored = list.Columns[1].Width, passed = preserved });
                if (!preserved) failures.Add("Task column manual width was lost or accumulated rounding across DPI changes");
            }
            form.Close();
            SyntheticDpi.Pump();
        }
        Program.Save(Path.Combine(output, "dialog-dpi.json"), new {
            passed = failures.Count == 0,
            scope = "Actual product dialogs on a private station. Synthetic managed DPI transitions; native monitor DPI unchanged. Not physical monitor certification.",
            failures, cases
        });
        if (failures.Count > 0) throw new InvalidOperationException($"DPI dialog audit: {failures.Count} findings; see dialog-dpi.json");
    }
}
