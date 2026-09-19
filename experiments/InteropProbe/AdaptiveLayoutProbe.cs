using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using RemoteDesk;

// Real WinForms on a private desktop: no focus, input or clipboard access to
// the user's desktop, and no networking/services/settings are started.
internal static class AdaptiveLayoutProbe
{
    internal static int Run(bool softwarePaint = false, bool viewerClipping = false)
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
                Application.EnableVisualStyles();
                if (viewerClipping) ViewerClippingProbe.Verify(output);
                else if (softwarePaint) SoftwarePaintProbe.Verify(output);
                else Verify(output);
            }
            catch (Exception error) { failure = error; }
        });
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start(); worker.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return 0;
    }

    private static void Verify(string output)
    {
        var evidence = new List<object>();
        using (var main = new MainForm(new RemoteDeskSettings(), Path.Combine(output, "preview-log")))
        {
            main.Show(); Application.DoEvents();
            var tabs = Descendants(main).OfType<TabControl>().Single();
            foreach (int logicalWidth in new[] { 1120, 800, 640, 1120 })
            {
                main.Size = new Size(ResponsiveWindowLayout.ScaleLogical(logicalWidth, main.DeviceDpi), 900);
                for (int index = 0; index < tabs.TabPages.Count; index++)
                {
                    tabs.SelectedIndex = index;
                    Application.DoEvents();
                    TabPage page = tabs.SelectedTab!;
                    page.AutoScrollPosition = Point.Empty;
                    Application.DoEvents();
                    using var bitmap = new Bitmap(main.Width, main.Height);
                    main.DrawToBitmap(bitmap, new Rectangle(Point.Empty, main.Size));
                    bitmap.Save(Path.Combine(output, $"main-{index}-{logicalWidth}.png"));
                    var controls = Descendants(page).Where(c => c is TextBox or NumericUpDown or ComboBox or Button or CheckBox or TrackBar).ToArray();
                    evidence.Add(new { main = true, page = index, logicalWidth, controls = controls.Select(c => new {
                        kind = c.GetType().Name, caption = c is Button ? c.Text : "", c.Bounds, c.Visible,
                        onPage = c.Visible && tabs.SelectedTab!.RectangleToScreen(tabs.SelectedTab.ClientRectangle).Contains(c.RectangleToScreen(c.ClientRectangle))
                    }).ToArray() });
                    foreach (Control control in controls.Where(c => c.Visible))
                    {
                        // Disabled transfer actions must also remain visible;
                        // scroll explicitly without invoking any action.
                        page.ScrollControlIntoView(control);
                        Application.DoEvents();
                        Rectangle bounds = control.RectangleToScreen(control.ClientRectangle);
                        Rectangle viewport = page.RectangleToScreen(page.ClientRectangle);
                        if (bounds.Width < 24 || bounds.Height < 12 || !viewport.Contains(bounds))
                            throw new InvalidOperationException($"Main page {index} at {logicalWidth}: {control.GetType().Name} {control.Text} {bounds} outside {viewport}; scroll={page.AutoScrollPosition}/{page.AutoScrollMinSize}");
                        for (Control? parent = control.Parent; parent is not null && parent != page; parent = parent.Parent)
                            if (!parent.RectangleToScreen(parent.ClientRectangle).Contains(bounds))
                                throw new InvalidOperationException($"Main page {index}: {control.GetType().Name} {control.Text} clipped by {parent.GetType().Name} {parent.ClientRectangle}");
                    }
                    using var scrolled = new Bitmap(main.Width, main.Height);
                    main.DrawToBitmap(scrolled, new Rectangle(Point.Empty, main.Size));
                    scrolled.Save(Path.Combine(output, $"main-{index}-{logicalWidth}-scrolled.png"));
                    if (page.HorizontalScroll.Visible)
                        throw new InvalidOperationException($"Main page {index} at {logicalWidth}: unexpected whole-page horizontal overflow");
                    int idleLayouts = 0;
                    LayoutEventHandler counted = (_, _) => idleLayouts++;
                    page.Controls[0].Layout += counted;
                    for (int tick = 0; tick < 10; tick++) { Thread.Sleep(10); Application.DoEvents(); }
                    page.Controls[0].Layout -= counted;
                    if (idleLayouts > 2)
                        throw new InvalidOperationException($"Main page {index}: did not settle after resize ({idleLayouts} idle layouts)");
                }
            }
            main.Close(); Application.DoEvents();
        }
        var device = new RelayOnlineDevice(Guid.NewGuid().ToString(), "Layout test", "Windows", null, false, 0)
            { SharedName = "Test", OriginalMachineName = "Synthetic-PC" };
        Func<Form>[] factories = [
            () => new RelaySetupDialog(new RelaySettings(), loginOnly: true),
            () => new RelaySetupDialog(new RelaySettings()),
            () => MainForm.CreateRelayDeviceKeyDialog("Synthetic-PC", SystemFonts.MessageBoxFont!, out _),
            () => MainForm.CreateRelayNameDialog(device, SystemFonts.MessageBoxFont!, out _)
        ];
        for (int index = 0; index < factories.Length; index++)
        {
            using Form form = factories[index]();
            form.Show(); Application.DoEvents();
            var scroll = (Panel)form.Controls[0];
            Control[] controls = Descendants(scroll).Where(c => c is TextBox or NumericUpDown or Button).ToArray();
            foreach (Size viewport in new[] { new Size(700, 500), new Size(480, 300), new Size(340, 230), new Size(700, 500) })
            {
                form.MinimumSize = Size.Empty;
                form.ClientSize = viewport;
                Application.DoEvents();
                foreach (Control control in controls)
                {
                    control.Select(); // The managed selection path used by Tab.
                    bool focused = control.Focus();
                    Application.DoEvents();
                    Rectangle bounds = control.RectangleToScreen(control.ClientRectangle);
                    Rectangle visible = scroll.RectangleToScreen(scroll.ClientRectangle);
                    if (!control.Visible || bounds.Width < 24 || bounds.Height < 12 || !visible.Contains(bounds))
                        throw new InvalidOperationException($"{form.Text}: {control.GetType().Name} {bounds} outside {visible}; focus={focused}/{control.Focused}; active={form.ActiveControl?.GetType().Name}; scroll={scroll.AutoScrollPosition}/{scroll.AutoScrollMinSize}");
                }
                evidence.Add(new { dialog = index, viewport, dpi = form.DeviceDpi, controls = controls.Length, passed = true });
                if (viewport.Width == 480)
                {
                    using var bitmap = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                    bitmap.Save(Path.Combine(output, $"dialog-{index}-480.png"));
                }
            }
            form.Close(); Application.DoEvents();
        }
        VerifyFileDialogs(output, evidence);
        Program.Save(Path.Combine(output, "layout.json"), new { passed = true, scope = "private Windows desktop; real main pages and dialogs; every input/action reachable", cases = evidence });
    }

    private static void VerifyFileDialogs(string output, List<object> evidence)
    {
        FileTransferConfirmationItem[] items = [
            new("文件", @"C:\测试文档\一份具有比较长的名称的项目说明和测试资料.txt",
                "一份具有比较长的名称的项目说明和测试资料.txt", 1024,
                @"D:\RemoteDeskReceived\一份具有比较长的名称的项目说明和测试资料 (2).txt"),
            new("文件夹", @"C:\测试文档\项目资源", "项目资源.zip", 0,
                @"D:\RemoteDeskReceived\项目资源.zip")
        ];
        foreach (float fontSize in new[] { 9f, 14f })
        {
            Func<Form>[] factories = [
                () => new FileTransferConfirmationDialog("确认文件传输", "发送所选文件到远端", items,
                    "文件夹会先打包为 ZIP；接收端不会自动解压。接收位置由远端返回，重名会自动改名。"),
                () => new FileTransferResultDialog("已发送 1 项，未完成 1 项；实际保存结果如下。",
                    "已发送：一份具有比较长的名称的项目说明和测试资料.txt\r\n" + items[0].DestinationPath +
                    "\r\n\r\n未完成：项目资源.zip\r\n远端磁盘空间不足；前面已保存的文件不会被删除。")
            ];
            for (int index = 0; index < factories.Length; index++)
            {
                using Form form = factories[index]();
                using var font = new Font("Microsoft YaHei UI", fontSize);
                form.Font = font;
                form.Show(); Application.DoEvents();
                var root = (ScrollableControl)form.Controls[0];
                foreach (Size viewport in new[] { new Size(1000, 700), new Size(700, 430), new Size(520, 320), new Size(360, 240), new Size(1000, 700) })
                {
                    form.MinimumSize = Size.Empty;
                    form.ClientSize = viewport;
                    root.AutoScrollPosition = Point.Empty;
                    Application.DoEvents();
                    using var bitmap = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                    bitmap.Save(Path.Combine(output, $"file-dialog-{index}-{fontSize}-{viewport.Width}.png"));
                    foreach (Control control in Descendants(root).Where(c => c is Button or TextBox or DataGridView))
                    {
                        control.Select();
                        control.Focus();
                        Application.DoEvents();
                        Rectangle bounds = control.RectangleToScreen(control.ClientRectangle);
                        Rectangle visible = root.RectangleToScreen(root.ClientRectangle);
                        if (!control.Visible || bounds.Width < 24 || bounds.Height < 12 || !visible.Contains(bounds))
                            throw new InvalidOperationException($"{form.Text} at {viewport}, font {fontSize}: {control.GetType().Name} '{control.Text}' {bounds} outside {visible}; scroll={root.AutoScrollPosition}/{root.AutoScrollMinSize}");
                    }
                    if (root.HorizontalScroll.Visible)
                        throw new InvalidOperationException($"{form.Text} at {viewport}, font {fontSize}: unexpected whole-dialog horizontal overflow");
                    using var scrolled = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(scrolled, new Rectangle(Point.Empty, form.Size));
                    scrolled.Save(Path.Combine(output, $"file-dialog-{index}-{fontSize}-{viewport.Width}-scrolled.png"));
                    int idleLayouts = 0;
                    LayoutEventHandler counted = (_, _) => idleLayouts++;
                    root.Controls[0].Layout += counted;
                    for (int tick = 0; tick < 10; tick++) { Thread.Sleep(10); Application.DoEvents(); }
                    root.Controls[0].Layout -= counted;
                    if (idleLayouts > 2)
                        throw new InvalidOperationException($"{form.Text}: did not settle after resize ({idleLayouts} idle layouts)");
                    evidence.Add(new { fileDialog = index, viewport, fontSize, dpi = form.DeviceDpi, passed = true });
                }
                form.Close(); Application.DoEvents();
            }
        }
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
