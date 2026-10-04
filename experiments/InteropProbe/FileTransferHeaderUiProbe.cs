using RemoteDesk;

internal static class FileTransferHeaderUiProbe
{
    internal static void Verify(string output, bool replayLegacyHeaders = false)
    {
        var cases = new List<object>(); var failures = new List<string>();
        foreach (float? points in new float?[] { null, 9, 14, 20 })
        {
            using var dialog = new FileTransferConfirmationDialog("确认文件传输", "自有界面探针，不开始传输。",
                [new("文件", @"C:\Owned\中文.bin", "中文.bin", 1048576, @"D:\Owned\中文.bin")], "私有窗口站；不读取剪贴板或启动会话。");
            using var font = points is float size ? new Font("Microsoft YaHei UI", size) : null;
            if (font is not null) dialog.Font = font;
            var grid = SyntheticDpi.Descendants(dialog).OfType<DataGridView>().Single();
            if (replayLegacyHeaders)
                foreach (DataGridViewColumn column in grid.Columns.Cast<DataGridViewColumn>().Take(2))
                    column.HeaderCell.Style.WrapMode = DataGridViewTriState.NotSet;
            dialog.Show(); SyntheticDpi.Pump();
            foreach (int dpi in new[] { 0, dialog.DeviceDpi, 96, 120, 144, 168, 192, 240, 96 })
            {
                object transition = dpi == 0 ? new { initialShow = true, managedDpi = dialog.DeviceDpi, nativeDpi = SyntheticDpi.NativeDpi(dialog) } : SyntheticDpi.Apply(dialog, dpi);
                if (dpi != 0)
                {
                    dialog.MinimumSize = Size.Empty;
                    dialog.ClientSize = new Size(Math.Min(1400, ResponsiveWindowLayout.ScaleLogical(960, dpi)), Math.Min(800, ResponsiveWindowLayout.ScaleLogical(560, dpi)));
                }
                SyntheticDpi.Pump();
                var issues = new List<string>();
                var columns = grid.Columns.Cast<DataGridViewColumn>().Take(2).Select(column =>
                {
                    var style = column.HeaderCell.InheritedStyle;
                    using var graphics = grid.CreateGraphics();
                    int lineHeight = TextRenderer.MeasureText(graphics, column.HeaderText, style.Font, Size.Empty, TextFormatFlags.SingleLine).Height;
                    Rectangle content = column.HeaderCell.GetContentBounds(-1);
                    // Compare like-for-like aligned rectangles through the
                    // same WinForms header renderer, with a cloned single-line
                    // style. No control style/size is changed by measurement.
                    // GetContentBounds includes vertical alignment, so its
                    // height cannot be compared to a bare glyph-line height.
                    var singleLineStyle = new DataGridViewCellStyle(style) { WrapMode = DataGridViewTriState.False };
                    var measure = typeof(DataGridViewColumnHeaderCell).GetMethod("GetContentBounds",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                        null, [typeof(Graphics), typeof(DataGridViewCellStyle), typeof(int)], null)!;
                    var singleLineBounds = (Rectangle)measure.Invoke(column.HeaderCell, [graphics, singleLineStyle, -1])!;
                    int lineWidth = TextRenderer.MeasureText(graphics, column.HeaderText, style.Font, Size.Empty, TextFormatFlags.SingleLine).Width;
                    if (content.Height > singleLineBounds.Height + 2)
                        issues.Add($"'{column.HeaderText}' short header wraps: {content.Height} vs aligned single line {singleLineBounds.Height}, width {column.Width}");
                    if (lineWidth > column.Width)
                        issues.Add($"'{column.HeaderText}' header width {column.Width} < single line {lineWidth}");
                    return new { column.HeaderText, column.Width, column.MinimumWidth, style.WrapMode, fontPoints = style.Font?.SizeInPoints, content, lineHeight, lineWidth, singleLineBounds };
                }).ToArray();
                string tag = $"file-header-font{points?.ToString() ?? "default"}-dpi{dpi}-{cases.Count}";
                using var bitmap = new Bitmap(dialog.Width, dialog.Height); dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, dialog.Size));
                bitmap.Save(Path.Combine(output, tag + ".png"));
                cases.Add(new { tag, transition, formFont = dialog.Font.SizeInPoints, gridFont = grid.Font.SizeInPoints, grid.ColumnHeadersHeight, columns, issues });
                failures.AddRange(issues.Select(issue => tag + ": " + issue));
            }
            dialog.Close(); SyntheticDpi.Pump();
        }
        Program.Save(Path.Combine(output, "file-header-ui.json"), new { passed = failures.Count == 0, replayLegacyHeaders, cases, failures,
            scope = "Real confirmation controls on private station, synthetic DPI traces plus untouched native monitor DPI. No modal confirmation, file transfer or clipboard access." });
        if (failures.Count > 0) throw new InvalidOperationException($"Confirmation headers: {failures.Count} wrapped short labels.");
    }
}
