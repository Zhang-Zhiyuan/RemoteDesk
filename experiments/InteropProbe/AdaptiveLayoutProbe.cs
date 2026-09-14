using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using RemoteDesk;

// Real WinForms on a private desktop: no focus, input or clipboard access to
// the user's desktop, and no networking/services/settings are started.
internal static class AdaptiveLayoutProbe
{
    internal static int Run(bool softwarePaint = false)
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
                if (softwarePaint) SoftwarePaintProbe.Verify(output);
                else Verify(output);
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
        Program.Save(Path.Combine(output, "layout.json"), new { passed = true, scope = "private Windows desktop; real main pages and dialogs; every input/action reachable", cases = evidence });
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
