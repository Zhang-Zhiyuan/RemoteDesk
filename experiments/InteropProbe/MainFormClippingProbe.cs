using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using RemoteDesk;

// Synthetic preview controls on a private window station; no user settings,
// real clipboard, production sessions or host startup are involved.
internal static class MainFormClippingProbe
{
    internal static int Run()
    {
        using var config = JsonDocument.Parse(Console.ReadLine()!);
        string output = Path.GetFullPath(config.RootElement.GetProperty("output").GetString()!);
        Directory.CreateDirectory(output);
        nint station = CreateWindowStation(null, 0, 0x000F037F, 0);
        if (station == 0 || !SetProcessWindowStation(station)) throw new Win32Exception();
        nint desktop = CreateDesktop("Default", null, 0, 0, 0x000F01FF, 0);
        if (desktop == 0) throw new Win32Exception();
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                if (!SetThreadDesktop(desktop)) throw new Win32Exception();
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                Application.EnableVisualStyles();
                Verify(output);
            }
            catch (Exception error) { failure = error; }
        });
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start(); worker.Join();
        if (failure is not null) throw failure;
        return 0;
    }

    private static void Verify(string output)
    {
        var cases = new List<object>();
        var issues = new List<string>();
        foreach (float points in new[] { 9.25f, 14f, 18f })
        {
            using var main = new MainForm(new RemoteDeskSettings(), Path.Combine(output, "preview-log"));
            using var font = new Font("Microsoft YaHei UI", points);
            main.Font = font;
            main.Show(); Pump();
            SetText(main, "_localIpsBox", "192.0.2.123; 2001:db8:1234:5678:abcd:ef12:3456:7890");
            SetText(main, "_hostStatusLabel", "正在监听 192.0.2.123:56565；管理员模式");
            SetText(main, "_viewerStatusLabel", string.Join("\r\n", Enumerable.Range(1, 18).Select(i => $"诊断 {i}：已连接很长的测试设备名称，网络链路正常；文件已保存到 C:\\SyntheticReceived\\测试目录\\远程传输结果.txt。")));
            SetText(main, "_relayServerSummaryLabel", "relay-with-a-very-long-synthetic-hostname.example.invalid:56567 · 已保存登录 · 本机 ID 12345678");
            SetText(main, "_relayRouteStatusLabel", "已选择测试无线网络，延迟 12 ms；候选有线网络延迟 150 ms。当前会话保持稳定线路，不会频繁切换网卡。");
            SetText(main, "_relayStatusLabel", "已连接公网中继服务器，发现 12 台在线设备；如果连接失败，请确认被控端设备密钥并刷新列表。\r\n测试长状态：所有状态文本应完整换行，设备列表仍应保持可访问。");
            var directList = Field<ListBox>(main, "_discoveredHostsList");
            var saved = new SavedRemoteDevice
            {
                MachineName = "Synthetic-测试设备-具有很长的备注和机器名称",
                Remark = "实验室工作站 · 长备注完整信息测试",
                Address = "2001:db8:1234:5678:abcd:ef12:3456:7890", Port = 56565
            };
            Type deviceItem = typeof(MainForm).GetNestedType("RemoteDeviceListItem", BindingFlags.NonPublic)!;
            directList.Items.Add(deviceItem.GetMethod("FromSaved", BindingFlags.Static | BindingFlags.Public)!.Invoke(null, new object[] { saved })!);
            var relayList = Field<ListView>(main, "_relayDevicesList");
            relayList.Items.Add(new ListViewItem(new[] { "Synthetic-长名称测试工作站", "Windows", "1.0.25", "正在监听", "刚刚", "2001:db8:1234:5678:abcd:ef12:3456:7890:56565" }));
            var tabs = Field<TabControl>(main, "_tabs");
            foreach (int logicalWidth in new[] { 1120, 800, 640, 520, 1120 })
            {
                main.MinimumSize = Size.Empty;
                main.ClientSize = new Size(ResponsiveWindowLayout.ScaleLogical(logicalWidth, main.DeviceDpi), 820);
                for (int index = 0; index < tabs.TabPages.Count; index++)
                {
                    tabs.SelectedIndex = index; Pump();
                    TabPage page = tabs.SelectedTab!;
                    page.AutoScrollPosition = Point.Empty; Pump();
                    string prefix = $"main-font{points}-width{logicalWidth}-tab{index}";
                    Save(main, Path.Combine(output, prefix + ".png"));
                    int before = issues.Count;
                    foreach (Control control in Descendants(page).Where(c => c.Visible))
                    {
                        if (control is Label label && label.Text.Length > 0)
                        {
                            int required = label.GetPreferredSize(new Size(Math.Max(1, label.Width), 0)).Height;
                            if (required > label.Height + 2)
                                issues.Add($"{prefix}: label '{label.Text[..Math.Min(48, label.Text.Length)]}' height {label.Height} < preferred {required}; dock={label.Dock}");
                        }
                        if (control is not (TextBox or ComboBox or NumericUpDown or Button or CheckBox or TrackBar)) continue;
                        page.ScrollControlIntoView(control); Pump();
                        Rectangle bounds = control.RectangleToScreen(control.ClientRectangle);
                        Rectangle viewport = page.RectangleToScreen(page.ClientRectangle);
                        if (bounds.Width < 24 || bounds.Height < 12 || !viewport.Contains(bounds))
                            issues.Add($"{prefix}: {control.GetType().Name} '{SafeCaption(control)}' {bounds} outside page {viewport}");
                        for (Control? parent = control.Parent; parent is not null && parent != page; parent = parent.Parent)
                            if (!parent.RectangleToScreen(parent.ClientRectangle).Contains(bounds))
                                issues.Add($"{prefix}: {control.GetType().Name} '{SafeCaption(control)}' clipped by {parent.GetType().Name}: {bounds} outside {parent.RectangleToScreen(parent.ClientRectangle)}");
                    }
                    if (page.HorizontalScroll.Visible) issues.Add($"{prefix}: unexpected whole-page horizontal scroll");
                    if (index == 1 && directList.ItemHeight < directList.Font.Height * 2 + ResponsiveWindowLayout.ScaleLogical(14, main.DeviceDpi))
                        issues.Add($"{prefix}: two device-name/status lines need font-aware height; item={directList.ItemHeight}, fontHeight={directList.Font.Height}");
                    Save(main, Path.Combine(output, prefix + "-scrolled.png"));
                    if (index == 1)
                    {
                        page.AutoScrollPosition = new Point(0, int.MaxValue); Pump();
                        Save(main, Path.Combine(output, prefix + "-bottom.png"));
                        var status = Field<Label>(main, "_viewerStatusLabel");
                        var statusScroll = (ScrollableControl)status.Parent!;
                        if (status.Height + statusScroll.Padding.Vertical > statusScroll.ClientSize.Height && !statusScroll.VerticalScroll.Visible)
                            issues.Add($"{prefix}: long diagnostic text extends below its viewport with no scrollbar");
                        statusScroll.AutoScrollPosition = new Point(0, int.MaxValue); Pump();
                        Save(main, Path.Combine(output, prefix + "-status-end.png"));
                        string longStatus = status.Text;
                        status.Text = "已连接"; Pump();
                        if (statusScroll.VerticalScroll.Visible)
                            issues.Add($"{prefix}: status scrollbar did not shrink after a short status update");
                        status.Text = longStatus; Pump();
                        if (!statusScroll.VerticalScroll.Visible ||
                            statusScroll.DisplayRectangle.Height + statusScroll.Padding.Vertical < statusScroll.AutoScrollMinSize.Height)
                            issues.Add($"{prefix}: dynamic long status range: bar={statusScroll.VerticalScroll.Visible}, display={statusScroll.DisplayRectangle}, extent={statusScroll.AutoScrollMinSize}, client={statusScroll.ClientSize}, padding={statusScroll.Padding}, label={status.Bounds}");
                        statusScroll.AutoScrollPosition = new Point(0, int.MaxValue); Pump();
                        Save(main, Path.Combine(output, prefix + "-status-dynamic-end.png"));
                    }
                    cases.Add(new { points, logicalWidth, tab = index, dpi = main.DeviceDpi, issues = issues.Count - before,
                        labels = Descendants(page).OfType<Label>().Where(label => label.Visible && label.Text.Length > 25).Select(label => new {
                            label.Text, label.Bounds, label.PreferredSize, parent = label.Parent!.ClientRectangle,
                            scrolling = label.Parent is ScrollableControl scroll && scroll.VerticalScroll.Visible
                        }).ToArray() });
                }
            }
            main.Close(); Pump();
        }
        VerifyDialogs(output, cases, issues);
        Program.Save(Path.Combine(output, "main-layout.json"), new { passed = issues.Count == 0, cases, issues });
        Console.WriteLine($"Main layout: {cases.Count} cases, {issues.Count} findings; {output}");
        if (issues.Count > 0)
            throw new InvalidOperationException($"Main layout verification found {issues.Count} issue(s); see {Path.Combine(output, "main-layout.json")}.");
    }

    private static void VerifyDialogs(string output, List<object> cases, List<string> issues)
    {
        var device = new RelayOnlineDevice(Guid.NewGuid().ToString(), "合成测试机器", "Windows", null, false, 0)
        {
            SharedName = "测试名称", OriginalMachineName = string.Concat(Enumerable.Repeat("Synthetic-原始机器名称很长-", 6)),
            DirectAddresses = ["192.0.2.123", "2001:db8:1234:5678:abcd:ef12:3456:7890"], DirectPort = 56565
        };
        foreach (float points in new[] { 9.25f, 14f, 18f })
        {
            using var font = new Font("Microsoft YaHei UI", points);
            Func<Form>[] factories = [
                () => MainForm.CreateRelayDeviceKeyDialog(device.MachineName, font, out _),
                () => MainForm.CreateRelayNameDialog(device, font, out _),
                () => MainForm.CreateRelayAddressesDialog(device, "other-synthetic-device", font, out _),
                () => MainForm.CreateAddDeviceDialog(font, out _, out _, out _)
            ];
            for (int index = 0; index < factories.Length; index++)
            {
                using Form form = factories[index](); form.Show(); Pump();
                foreach (Size viewport in new[] { new Size(720, 450), new Size(420, 300), new Size(340, 220), new Size(720, 450) })
                {
                    form.MinimumSize = Size.Empty; form.ClientSize = viewport; Pump();
                    string prefix = $"dialog-{index}-font{points}-width{viewport.Width}";
                    int before = issues.Count;
                    var root = (ScrollableControl)form.Controls[0];
                    root.AutoScrollPosition = Point.Empty; Pump();
                    Save(form, Path.Combine(output, prefix + ".png"));
                    foreach (Control control in Descendants(form).Where(c => c.Visible && c is TextBox or Button or ListBox))
                    {
                        // The initial text box can already own focus after
                        // Shown; reselecting it cannot synthesize a Tab/Enter
                        // event after the probe deliberately scrolled to top.
                        Control? other = Descendants(form).FirstOrDefault(c => c.Visible && c is Button && c != control);
                        other?.Select(); bool? otherFocused = other?.Focus(); Pump();
                        if (other is not null && (otherFocused != true || !other.Focused))
                            issues.Add($"{prefix}: probe could not establish a focus transition before testing '{SafeCaption(control)}'");
                        control.Select(); bool focused = control.Focus(); Pump();
                        Rectangle bounds = control.RectangleToScreen(control.ClientRectangle);
                        if (!root.RectangleToScreen(root.ClientRectangle).Contains(bounds))
                            issues.Add($"{prefix}: {control.GetType().Name} '{SafeCaption(control)}' {bounds} unreachable; viewport={root.RectangleToScreen(root.ClientRectangle)} scroll={root.AutoScrollPosition}/{root.AutoScrollMinSize}; focus={focused}/{control.Focused} active={form.ActiveControl?.GetType().Name}");
                    }
                    if (root.HorizontalScroll.Visible) issues.Add($"{prefix}: unexpected whole-dialog horizontal overflow");
                    Save(form, Path.Combine(output, prefix + "-scrolled.png"));
                    cases.Add(new { dialog = index, points, viewport, issues = issues.Count - before });
                }
                form.Close(); Pump();
            }
        }
    }

    private static void SetText(object target, string field, string text) => Field<Control>(target, field).Text = text;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static string SafeCaption(Control control) => control is Button or CheckBox ? control.Text : "input";
    private static void Pump() { for (int i = 0; i < 4; i++) Application.DoEvents(); }
    private static void Save(Form form, string path)
    {
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(path);
    }
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
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
