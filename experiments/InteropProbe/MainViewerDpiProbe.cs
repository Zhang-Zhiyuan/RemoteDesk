using System.Reflection;
using RemoteDesk;

internal static class MainViewerDpiProbe
{
    internal static void Verify(string output)
    {
        var cases = new List<object>();
        var issues = new List<string>();
        string? previousIdentity = RemoteDeviceIdentity.LocalId;
        try
        {
            foreach (float points in new[] { 9.25f, 14f, 18f }) VerifyMain(output, cases, issues, points);
            foreach (float points in new[] { 9f, 14f, 20f }) VerifyViewer(output, cases, issues, points);
        }
        finally { RemoteDeviceIdentity.LocalId = previousIdentity; }
        Program.Save(Path.Combine(output, "main-viewer-dpi.json"), new { passed = issues.Count == 0, cases, issues,
            scope = "Real controls on a private Windows station; synthetic PMv2 notifications at 96/120/144/168/192/240 DPI; no global display changes, production settings, desktop input or remote session" });
        if (issues.Count > 0) throw new InvalidOperationException($"DPI audit: {issues.Count} findings; see main-viewer-dpi.json");
    }

    private static void VerifyMain(string output, List<object> cases, List<string> issues, float points)
    {
        using var main = new MainForm(new RemoteDeskSettings(), Path.Combine(output, "preview-log"));
        main.Show(); SyntheticDpi.Pump();
        using var runtimeFont = new Font("Microsoft YaHei UI", points);
        main.Font = runtimeFont; SyntheticDpi.Pump();
        var tabs = Get<TabControl>(main, "_tabs");
        foreach (int dpi in new[] { main.DeviceDpi, 96, 120, 144, 168, 192, 240, 144, 96, 240, 96 })
        {
            object transition = SyntheticDpi.Apply(main, dpi);
            foreach (int logicalWidth in new[] { 1120, 640 })
            {
                main.MinimumSize = Size.Empty;
                main.ClientSize = new Size(ResponsiveWindowLayout.ScaleLogical(logicalWidth, dpi), ResponsiveWindowLayout.ScaleLogical(640, dpi));
                for (int tab = 0; tab < tabs.TabPages.Count; tab++)
                {
                    tabs.SelectedIndex = tab; SyntheticDpi.Pump();
                    var page = tabs.SelectedTab!;
                    int before = issues.Count;
                    string tag = $"main-font{points}-dpi{dpi}-width{logicalWidth}-tab{tab}-case{cases.Count}";
                    foreach (Control control in SyntheticDpi.Descendants(page).Where(control => control.Visible &&
                        control is TextBox or ComboBox or NumericUpDown or Button or CheckBox or TrackBar))
                    {
                        page.ScrollControlIntoView(control); SyntheticDpi.Pump();
                        Rectangle bounds = control.RectangleToScreen(control.ClientRectangle);
                        if (control.Width < 24 || control.Height < 12 || !page.RectangleToScreen(page.ClientRectangle).Contains(bounds))
                            issues.Add($"{tag}: {control.GetType().Name} '{control.Text}' outside page: {bounds}");
                        for (Control? parent = control.Parent; parent != null && parent != page; parent = parent.Parent)
                            if (!parent.RectangleToScreen(parent.ClientRectangle).Contains(bounds))
                                issues.Add($"{tag}: '{control.Text}' {control.Bounds} min={control.MinimumSize} font={control.Font.SizeInPoints} clipped by {parent.GetType().Name} bounds={parent.Bounds} padding={parent.Padding}");
                    }
                    if (page.HorizontalScroll.Visible) issues.Add($"{tag}: whole-page horizontal scroll");
                    if (tabs.ItemSize.Height != ResponsiveWindowLayout.ScaleLogical(40, dpi))
                        issues.Add($"{tag}: tab item height {tabs.ItemSize.Height} != expected {ResponsiveWindowLayout.ScaleLogical(40, dpi)}");
                    if (issues.Count != before || logicalWidth == 640 && tab == 1) Save(main, Path.Combine(output, tag + ".png"));
                    cases.Add(new { tag, transition, issues = issues.Count - before, tabs.ItemSize, font = main.Font.SizeInPoints });
                }
            }
        }
        main.Close(); SyntheticDpi.Pump();
    }

    private static void VerifyViewer(string output, List<object> cases, List<string> issues, float points)
    {
        using var client = new RemoteViewerClient();
        using var viewer = new RemoteViewerWindow(client, "Synthetic DPI audit", true, true, true, true, true, false);
        viewer.Show(); Invoke(viewer, "UninstallSystemKeyboardCapture"); SyntheticDpi.Pump();
        using var runtimeFont = new Font("Microsoft YaHei UI", points);
        viewer.Font = runtimeFont; SyntheticDpi.Pump();
        var overflow = Get<ViewerActionOverflow>(viewer, "_statusActionOverflow");
        var picture = viewer.Controls.OfType<PictureBox>().Single();
        var footer = Get<Panel>(viewer, "_statusFooterPanel");
        var actions = Get<FlowLayoutPanel>(viewer, "_fileTransferActionsPanel");
        foreach (Button button in overflow.Buttons.Where(button => button != overflow.MoreButton)) button.Visible = true;
        foreach (int dpi in new[] { 96, 120, 144, 168, 192, 240, 144, 96, 240, 96 })
        {
            object transition = SyntheticDpi.Apply(viewer, dpi);
            foreach (int logicalWidth in new[] { 1120, 480, 360, 1120 })
            {
                int before = issues.Count;
                string tag = $"viewer-font{points}-dpi{dpi}-width{logicalWidth}-case{cases.Count}";
                viewer.MinimumSize = Size.Empty;
                viewer.ClientSize = new Size(ResponsiveWindowLayout.ScaleLogical(logicalWidth, dpi), ResponsiveWindowLayout.ScaleLogical(500, dpi));
                SyntheticDpi.Pump();
                if (!viewer.ClientRectangle.Contains(footer.Bounds) || footer.Bounds.IntersectsWith(picture.Bounds))
                    issues.Add($"{tag}: footer outside viewport or overlapping picture");
                foreach (Button button in actions.Controls.OfType<Button>().Where(button => button.Visible))
                {
                    if (!actions.ClientRectangle.Contains(button.Bounds)) issues.Add($"{tag}: '{button.Text}' clipped");
                    if (button.DeviceDpi != dpi) issues.Add($"{tag}: '{button.Text}' stale managed DPI {button.DeviceDpi}");
                    if (TextRenderer.MeasureText(button.Text, button.Font).Height + button.Padding.Vertical > button.Height)
                        issues.Add($"{tag}: '{button.Text}' text clipped vertically");
                }
                var inline = actions.Controls.OfType<Button>().Where(button => button.Visible).ToArray();
                foreach (Button required in new[] { viewer.InputPauseButtonForTests, viewer.TransferTasksButtonForTests })
                    if (!inline.Contains(required) && !overflow.OverflowButtons.Contains(required)) issues.Add($"{tag}: '{required.Text}' unreachable");
                overflow.RebuildMenu();
                if (overflow.MenuForTests.Items.Count != overflow.OverflowButtons.Count) issues.Add($"{tag}: menu actions missing");
                var remoteSize = new Size(320, 180);
                Rectangle image = RemoteViewerWindow.CalculateDisplayedImageRectangle(picture.ClientRectangle, remoteSize, allowUpscaling: false);
                if (image.Size != remoteSize) issues.Add($"{tag}: 1:1 mode changed remote pixel size to {image.Size}");
                if (!RemoteViewerWindow.TryMapZoomedImagePoint(new Point(image.Right - 1, image.Bottom - 1), remoteSize, image, 0, out var point) || point != new Point(319, 179))
                    issues.Add($"{tag}: remote bottom-right input mapping changed with UI DPI");
                Save(viewer, Path.Combine(output, tag + ".png"));
                cases.Add(new { tag, transition, issues = issues.Count - before, picture.Bounds,
                    actions = inline.Select(button => new { button.Text, button.DeviceDpi, font = button.Font.SizeInPoints }).ToArray(),
                    overflow = overflow.OverflowButtons.Select(button => new { button.Text, button.DeviceDpi, font = button.Font.SizeInPoints }).ToArray() });
            }
        }
        viewer.Close(); SyntheticDpi.Pump();
    }

    private static T Get<T>(object instance, string field) => (T)instance.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static void Invoke(object instance, string method) => instance.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, null);
    private static void Save(Form form, string path) { using var image = new Bitmap(form.Width, form.Height); form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(path); }
}
