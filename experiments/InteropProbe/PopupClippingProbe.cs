using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using RemoteDesk;

// Real product dialogs on an isolated window station; synthetic addresses and
// paths only. Never confirms a transfer, provisions a server or reads clipboard.
internal static class PopupClippingProbe
{
    internal static int Run()
    {
        using var config = JsonDocument.Parse(Console.ReadLine()!);
        string output = Path.GetFullPath(config.RootElement.GetProperty("output").GetString()!);
        bool focusDiagnostic = config.RootElement.TryGetProperty("focusDiagnostic", out var diagnostic) && diagnostic.GetBoolean();
        Directory.CreateDirectory(output);
        nint station = CreateWindowStation(null, 0, 0x000F037F, 0);
        if (station == 0 || !SetProcessWindowStation(station)) throw new Win32Exception();
        nint desktop = CreateDesktop("Default", null, 0, 0, 0x000F01FF, 0);
        if (desktop == 0) throw new Win32Exception();
        Exception? failure = null;
        bool passed = false;
        var worker = new Thread(() =>
        {
            try
            {
                if (!SetThreadDesktop(desktop)) throw new Win32Exception();
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                Application.EnableVisualStyles();
                passed = focusDiagnostic ? VerifySharedNameFocus(output) : Verify(output);
            }
            catch (Exception error) { failure = error; }
        });
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        worker.Join();
        if (failure is not null) throw failure;
        return passed ? 0 : 1;
    }

    private static bool Verify(string output)
    {
        var evidence = new List<object>();
        var failures = new List<string>();
        string longName = string.Concat(Enumerable.Repeat("很长的中文项目文档说明_", 10)) + "最终.txt";
        string source = @"C:\Synthetic\项目资料\" + longName;
        string target = @"D:\RemoteDeskReceived\2026\测试传输后的实际保存位置\" + longName;
        FileTransferConfirmationItem[] items = [
            new("文件", source, longName, 1024, target),
            new("文件夹", @"C:\Synthetic\项目资源", "项目资源.zip", 0, @"D:\RemoteDeskReceived\项目资源.zip")
        ];
        (string Name, Func<Form> Create)[] factories = [
            ("relay-login", () => new RelaySetupDialog(new RelaySettings {
                ServerAddress = "synthetic-very-long-private-relay-server-name.example.invalid", AdminUsername = "synthetic-admin-account-name"
            }, loginOnly: true)),
            ("relay-deploy", () => new RelaySetupDialog(new RelaySettings {
                ServerAddress = "synthetic-very-long-private-relay-server-name.example.invalid", AdminUsername = "synthetic-admin-account-name"
            })),
            ("file-confirm", () => new FileTransferConfirmationDialog("确认文件传输", "发送所选文件到远端", items,
                "文件夹会先打包为 ZIP，接收端不会自动解压。接收位置由远端返回；若重名会自动改名，保存完成后显示实际位置。")),
            ("file-result", () => new FileTransferResultDialog("已发送 1 项，未完成 1 项；实际保存结果如下。",
                "已发送：" + longName + "\r\n" + target + "\r\n\r\n未完成：项目资源.zip\r\n远端磁盘空间不足；前面已保存的文件不会被删除。"))
        ];
        foreach (float fontSize in new[] { 9.25f, 14f })
        foreach (var factory in factories)
        {
            using var form = factory.Create();
            using var font = new Font("Microsoft YaHei UI", fontSize);
            form.Font = font;
            form.Show();
            Pump();
            var scroll = (ScrollableControl)form.Controls[0];
            Size[] sizes = [new(1000, 700), new(700, 430), new(520, 320), new(360, 240), new(320, 230), new(1000, 700)];
            for (int index = 0; index < sizes.Length; index++)
            {
                Size size = sizes[index];
                form.MinimumSize = Size.Empty;
                form.ClientSize = size;
                scroll.AutoScrollPosition = Point.Empty;
                Pump();
                string fileStem = $"{factory.Name}-{fontSize}-{index}-{size.Width}";
                Screenshot(form, Path.Combine(output, fileStem + ".png"));
                var issues = new List<string>();
                var scrollDiagnostics = new List<object>();
                Control[] descendants = Descendants(scroll).Where(control => control.Visible).ToArray();
                foreach (Control control in descendants.Where(control => control is Button or CheckBox or TextBox or NumericUpDown or DataGridView))
                {
                    control.Select();
                    control.Focus();
                    Pump();
                    Rectangle bounds = control.RectangleToScreen(control.ClientRectangle);
                    Rectangle viewport = scroll.RectangleToScreen(scroll.ClientRectangle);
                    if (!viewport.Contains(bounds) || bounds.Width < 12 || bounds.Height < 10)
                    {
                        issues.Add($"{ControlName(control)} cannot be fully reached: {bounds} in {viewport}");
                        object before = Geometry(scroll, control);
                        scroll.ScrollControlIntoView(control);
                        Pump();
                        object explicitScroll = Geometry(scroll, control);
                        Rectangle remaining = scroll.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
                        Point oldPosition = scroll.AutoScrollPosition;
                        scroll.AutoScrollPosition = new Point(-oldPosition.X,
                            Math.Max(0, -oldPosition.Y + Math.Max(0, remaining.Bottom - scroll.ClientSize.Height)));
                        Pump();
                        scrollDiagnostics.Add(new { control = ControlName(control), before, explicitScroll,
                            directPosition = Geometry(scroll, control) });
                    }
                    for (Control? parent = control.Parent; parent is not null && parent != scroll; parent = parent.Parent)
                        if (!parent.RectangleToScreen(parent.ClientRectangle).Contains(bounds))
                            issues.Add($"{ControlName(control)} clipped by {parent.GetType().Name}: {control.Bounds} parent={parent.ClientRectangle}");
                    if (control is Button or CheckBox)
                    {
                        Size preferred = control.GetPreferredSize(Size.Empty);
                        if (control.Width + 2 < preferred.Width || control.Height + 2 < preferred.Height)
                            issues.Add($"{ControlName(control)} caption clipped: actual={control.Size} preferred={preferred}");
                    }
                }
                foreach (Label label in descendants.OfType<Label>())
                {
                    Size preferred = label.GetPreferredSize(new Size(Math.Max(1, label.Width), 0));
                    if (label.Height + 2 < preferred.Height)
                        issues.Add($"Label '{label.Text}' text clipped: actual={label.Size} preferred={preferred}");
                }
                Size actualExtent = scroll.DisplayRectangle.Size;
                Size requiredExtent = scroll.AutoScrollMinSize;
                if (actualExtent.Width < requiredExtent.Width || actualExtent.Height < requiredExtent.Height)
                    issues.Add($"Stale actual scroll range: display={actualExtent}, required={requiredExtent}");
                Screenshot(form, Path.Combine(output, fileStem + "-scrolled.png"));
                // Check layout settles after both resize and normal Tab/Focus scrolling.
                int idleLayouts = 0;
                LayoutEventHandler counted = (_, _) => idleLayouts++;
                scroll.Controls[0].Layout += counted;
                for (int poll = 0; poll < 8; poll++) { Thread.Sleep(10); Application.DoEvents(); }
                scroll.Controls[0].Layout -= counted;
                if (idleLayouts > 2) issues.Add($"Layout did not settle ({idleLayouts} idle events)");
                evidence.Add(new {
                    dialog = factory.Name, fontSize, dpi = form.DeviceDpi, client = form.ClientSize,
                    scroll = scroll.AutoScrollMinSize, actualDisplay = scroll.DisplayRectangle,
                    horizontalScroll = scroll.HorizontalScroll.Visible,
                    passed = issues.Count == 0, issues, scrollDiagnostics,
                    controls = descendants.Where(control => control is Label or Button or CheckBox or TextBox or NumericUpDown or DataGridView)
                        .Select(control => new { kind = control.GetType().Name, caption = ControlName(control), control.Bounds,
                            preferred = control.GetPreferredSize(new Size(Math.Max(1, control.Width), 0)) }).ToArray()
                });
                failures.AddRange(issues.Select(issue => $"{factory.Name} / {fontSize}pt / {size}: {issue}"));
            }
            form.Close();
            Pump();
        }
        Program.Save(Path.Combine(output, "popup-layout.json"), new {
            passed = failures.Count == 0,
            scope = "Private Windows window station; actual product dialogs, 9.25/14pt, synthetic data; no network or clipboard",
            failures, cases = evidence
        });
        return failures.Count == 0;
    }

    private static void Pump()
    {
        for (int tick = 0; tick < 4; tick++) Application.DoEvents();
    }

    private static bool VerifySharedNameFocus(string output)
    {
        using var font = new Font("Microsoft YaHei UI", 18f);
        var device = new RelayOnlineDevice(Guid.NewGuid().ToString(), "合成测试机器", "Windows", null, false, 0)
        {
            SharedName = "测试名称", OriginalMachineName = string.Concat(Enumerable.Repeat("Synthetic-原始机器名称很长-", 6))
        };
        using Form form = MainForm.CreateRelayNameDialog(device, font, out TextBox input);
        var root = (ScrollableControl)form.Controls[0];
        var events = new List<object>();
        void Record(string stage, Control? source = null) => events.Add(new {
            stage, source = source?.GetType().Name, sourceCanFocus = source?.CanFocus,
            sourceHasHandle = source?.IsHandleCreated, sourceFocused = source?.Focused, input.Focused,
            active = form.ActiveControl?.GetType().Name, root.VerticalScroll.Visible,
            root.ContainsFocus, root.CanFocus, current = Geometry(root, input)
        });
        foreach (Control child in Descendants(form))
        {
            child.Enter += (_, _) => Record("Enter", child);
            child.Leave += (_, _) => Record("Leave", child);
        }
        root.Scroll += (_, _) => Record("Scroll");
        form.Show(); Pump(); Record("shown");
        int index = 0;
        foreach (Size viewport in new[] { new Size(720, 450), new Size(420, 300), new Size(340, 220), new Size(720, 450) })
        {
            form.MinimumSize = Size.Empty; form.ClientSize = viewport; Pump(); Record($"{index}-resize");
            root.AutoScrollPosition = Point.Empty; Pump(); Record($"{index}-reset");
            Screenshot(form, Path.Combine(output, $"focus-{index}-initial.png"));
            foreach (Control control in Descendants(form).Where(c => c.Visible && c is TextBox or Button))
            {
                Control? other = Descendants(form).FirstOrDefault(c => c.Visible && c is Button && c != control);
                Record($"{index}-other-before", other);
                other?.Select(); other?.Focus(); Pump(); Record($"{index}-other", other);
                control.Select(); control.Focus(); Pump(); Record($"{index}-target", control);
            }
            root.ScrollControlIntoView(input); Pump(); Record($"{index}-explicit");
            root.AutoScrollPosition = new Point(0, int.MaxValue); Pump(); Record($"{index}-max");
            Screenshot(form, Path.Combine(output, $"focus-{index}-bottom.png"));
            index++;
        }
        Program.Save(Path.Combine(output, "popup-focus-diagnostic.json"), new { events });
        form.Close(); Pump();
        return true;
    }

    private static object Geometry(ScrollableControl scroll, Control control)
    {
        var parents = new List<object>();
        for (Control? parent = control.Parent; parent is not null; parent = parent.Parent)
        {
            parents.Add(new { kind = parent.GetType().Name, parent.Bounds, parent.ClientRectangle,
                parent.DisplayRectangle, origin = parent.PointToScreen(Point.Empty),
                rowHeights = parent is TableLayoutPanel table ? table.GetRowHeights() : null });
            if (parent == scroll) break;
        }
        return new { control.Bounds, controlClient = control.ClientRectangle, control.AutoScrollOffset,
            screenBySelf = control.RectangleToScreen(control.ClientRectangle),
            screenByParent = control.Parent?.RectangleToScreen(control.Bounds),
            scroll.AutoScrollPosition, scroll.AutoScrollMinSize, scroll.AutoScrollMargin,
            scrollClient = scroll.ClientRectangle, scroll.DisplayRectangle,
            vertical = new { scroll.VerticalScroll.Value, scroll.VerticalScroll.Maximum, scroll.VerticalScroll.LargeChange }, parents };
    }

    private static string ControlName(Control control) => control is Button or CheckBox or Label
        ? control.Text : control.AccessibleName ?? control.GetType().Name;

    private static void Screenshot(Form form, string path)
    {
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(path);
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child)) yield return nested;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowStation(string? name, uint flags, uint access, nint security);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessWindowStation(nint station);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateDesktop(string name, string? device, nint devmode, uint flags, uint access, nint security);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(nint desktop);
}
