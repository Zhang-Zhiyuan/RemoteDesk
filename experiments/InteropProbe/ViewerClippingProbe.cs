using System.Reflection;
using RemoteDesk;

internal static class ViewerClippingProbe
{
    internal static void Verify(string output)
    {
        var cases = new List<object>();
        var fonts = new List<Font>();
        int failures = 0;
        using var client = new RemoteViewerClient();
        using var viewer = new RemoteViewerWindow(client, "RemoteDesk 布局测试（独立桌面）", true, true, true, true, true, false);
        var footer = Get<Panel>(viewer, "_statusFooterPanel");
        var actions = Get<FlowLayoutPanel>(viewer, "_fileTransferActionsPanel");
        var status = Get<Control>(viewer, "_statusBar");
        var picture = viewer.Controls.OfType<PictureBox>().Single();
        var overflow = Get<ViewerActionOverflow>(viewer, "_statusActionOverflow");
        viewer.Show();
        Invoke(viewer, "UninstallSystemKeyboardCapture");
        Invoke(status, "SetStatus", "剪贴板已同步；远端正在等待确认文件接收位置。", Color.LightGreen);
        Invoke(status, "SetDetails", "H.264 / D3D11 硬解 3840×2160 · 60 FPS · 中继 28 ms · 画质增强已开启", Color.LightGray);
        foreach (Button action in overflow.Buttons.Where(b => b != overflow.MoreButton)) action.Visible = true;
        Invoke(viewer, "UpdateStatusFooterLayout", new object?[] { null });
        foreach (float fontSize in new[] { 9f, 14f, 20f, 9f })
        {
            // Change at runtime after the form has been shown. This catches stale
            // preferred-height caches which a newly constructed form can hide.
            var font = new Font("Microsoft YaHei UI", fontSize);
            fonts.Add(font);
            viewer.Font = font;
            foreach (Size size in new[] { new Size(1400, 800), new Size(1000, 640), new Size(800, 480),
                new Size(640, 360), new Size(480, 320), new Size(360, 240), new Size(1000, 640) })
            {
                viewer.MinimumSize = Size.Empty;
                viewer.ClientSize = size;
                for (int i = 0; i < 5; i++) Application.DoEvents();
                var problems = new List<string>();
                if (picture.ClientSize.Height < 80)
                    problems.Add($"Remote viewport squeezed to {picture.ClientSize.Height}px");
                if (!viewer.ClientRectangle.Contains(footer.Bounds)) problems.Add($"Footer {footer.Bounds} outside client {viewer.ClientRectangle}");
                if (footer.Bounds.IntersectsWith(picture.Bounds)) problems.Add("Footer overlaps remote picture");
                if (status.Bounds.IntersectsWith(actions.Bounds)) problems.Add("Status overlaps action buttons");
                foreach (Control action in actions.Controls.Cast<Control>().Where(c => c.Visible))
                {
                    if (!actions.ClientRectangle.Contains(action.Bounds)) problems.Add($"Action '{action.Text}' {action.Bounds} clipped by {actions.ClientRectangle}");
                    if (!footer.RectangleToScreen(footer.ClientRectangle).Contains(action.RectangleToScreen(action.ClientRectangle)))
                        problems.Add($"Action '{action.Text}' outside footer");
                    Size text = TextRenderer.MeasureText(action.Text, action.Font, Size.Empty, TextFormatFlags.SingleLine);
                    if (text.Height + action.Padding.Vertical > action.ClientSize.Height)
                        problems.Add($"Action '{action.Text}' text height {text.Height} > {action.ClientSize.Height}");
                }
                bool hasDetails = (bool)status.GetType().GetProperty("HasVisibleDetails")!.GetValue(status)!;
                var bounds = RemoteViewerWindow.CalculateStatusBarLayout(status.ClientSize, status.Padding, hasDetails, status.DeviceDpi);
                int lineHeight = TextRenderer.MeasureText("同步状态 Ag中文", status.Font, Size.Empty,
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Height;
                if (bounds.StatusBounds.Height < lineHeight || (hasDetails && bounds.DetailsBounds.Height < lineHeight))
                    problems.Add($"Status text height {lineHeight} (measured {RemoteViewerWindow.MeasureStatusTextHeight(status.Font)}) > rows {bounds.StatusBounds.Height}/{bounds.DetailsBounds.Height}");
                var inline = actions.Controls.OfType<Button>().Where(b => b.Visible && b != overflow.MoreButton).ToArray();
                if (inline.Concat(overflow.OverflowButtons).Distinct().Count() != 8)
                    problems.Add("Inline + overflow actions do not cover all eight commands");
                var scale = Get<Button>(viewer, "_displayScaleButton");
                var upscale = Get<Button>(viewer, "_experimentalUpscaleButton");
                if (scale.Parent != upscale.Parent) problems.Add("Scaling controls split across menu and toolbar");
                overflow.RebuildMenu();
                if (overflow.MenuForTests.Items.Count != overflow.OverflowButtons.Count)
                    problems.Add("Overflow menu loses an available action");
                using var bitmap = new Bitmap(viewer.Width, viewer.Height);
                viewer.DrawToBitmap(bitmap, new Rectangle(Point.Empty, viewer.Size));
                string image = $"viewer-{fontSize}-{size.Width}-{cases.Count}.png";
                bitmap.Save(Path.Combine(output, image));
                int idle = 0;
                LayoutEventHandler count = (_, _) => idle++;
                footer.Layout += count;
                for (int i = 0; i < 5; i++) { Thread.Sleep(10); Application.DoEvents(); }
                footer.Layout -= count;
                if (idle > 2) problems.Add($"Layout did not settle ({idle} idle layouts)");
                if (problems.Count > 0) failures++;
                cases.Add(new { fontSize, size, dpi = viewer.DeviceDpi, image, footer = footer.Bounds,
                    viewport = picture.Bounds, actions = actions.Bounds, status = status.Bounds, hasDetails,
                    inline = inline.Select(b => b.Text).ToArray(), overflow = overflow.OverflowButtons.Select(b => b.Text).ToArray(),
                    problems, passed = problems.Count == 0 });
                Console.WriteLine($"{(problems.Count == 0 ? "PASS" : "FAIL")} viewer {fontSize}pt {size}: {string.Join("; ", problems)}");
            }
        }
        var finalFont = new Font("Microsoft YaHei UI", 20f);
        fonts.Add(finalFont);
        viewer.Font = finalFont;
        viewer.MinimumSize = Size.Empty;
        viewer.ClientSize = new Size(360, 240);
        for (int i = 0; i < 5; i++) Application.DoEvents();
        picture.Focus();
        bool menuOpened = false, pictureFocusedAtMenuOpen = false;
        overflow.MenuForTests.Opened += (_, _) =>
        {
            menuOpened = true;
            pictureFocusedAtMenuOpen = picture.ContainsFocus;
        };
        overflow.MoreButton.PerformClick();
        Application.DoEvents();
        // On a private station Windows can immediately close a popup because
        // there is no interactive foreground window. Check ownership at Opened.
        bool menuFocus = menuOpened && !pictureFocusedAtMenuOpen;
        using (var bitmap = new Bitmap(overflow.MenuForTests.Width, overflow.MenuForTests.Height))
        {
            overflow.MenuForTests.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.Combine(output, "viewer-more-menu.png"));
        }
        var scaleButton = Get<Button>(viewer, "_displayScaleButton");
        bool previousScale = viewer.AllowDisplayUpscalingForEntityTests;
        var scaleItem = overflow.MenuForTests.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == scaleButton.Text);
        scaleItem.PerformClick();
        overflow.MenuForTests.Close();
        for (int i = 0; i < 5; i++) Application.DoEvents();
        bool menuAction = viewer.AllowDisplayUpscalingForEntityTests != previousScale;
        cases.Add(new { scenario = "More menu keeps focus local and invokes the real scaling command", menuFocus, menuAction,
            menuOpened, pictureFocusedAtMenuOpen,
            pictureFocus = picture.ContainsFocus, moreFocused = overflow.MoreButton.Focused,
            active = viewer.ActiveControl?.GetType().Name, passed = menuFocus && menuAction });
        if (!menuFocus || !menuAction) failures++;
        int fullScreenLayouts = 0;
        LayoutEventHandler fullScreenCounter = (_, _) => fullScreenLayouts++;
        footer.Layout += fullScreenCounter;
        viewer.ToggleFullScreen();
        for (int i = 0; i < 5; i++) Application.DoEvents();
        fullScreenLayouts = 0;
        for (int i = 0; i < 10; i++) { Thread.Sleep(10); Application.DoEvents(); }
        bool fullScreenStable = !footer.Visible && fullScreenLayouts <= 2 && picture.Height == viewer.ClientSize.Height;
        viewer.ToggleFullScreen();
        Invoke(viewer, "UninstallSystemKeyboardCapture");
        for (int i = 0; i < 5; i++) Application.DoEvents();
        bool restoredFooter = footer.Visible && viewer.ClientRectangle.Contains(footer.Bounds) && picture.Height >= 80;
        footer.Layout -= fullScreenCounter;
        cases.Add(new { scenario = "Fullscreen hides toolbar without a reparent loop and restores it", passed = fullScreenStable && restoredFooter });
        if (!fullScreenStable || !restoredFooter) failures++;
        foreach (int height in new[] { 600, 240, 600 })
        {
            viewer.MinimumSize = Size.Empty;
            viewer.ClientSize = new Size(360, height);
            for (int i = 0; i < 5; i++) Application.DoEvents();
            bool passed = picture.Height >= 80 && viewer.ClientRectangle.Contains(footer.Bounds);
            cases.Add(new { scenario = "Height-only resize", height, passed });
            if (!passed) failures++;
        }
        viewer.Close();
        viewer.Dispose();
        foreach (Font font in fonts) font.Dispose();
        Application.DoEvents();
        Program.Save(Path.Combine(output, "viewer-layout.json"), new { complete = true, failures, cases,
            scope = "Actual WinForms on a private station, runtime fonts/resizes; no settings, real input or network sessions" });
        if (failures > 0) throw new InvalidOperationException($"Viewer layout: {failures} cases clipped; see viewer-layout.json");
    }

    private static T Get<T>(object instance, string field) => (T)instance.GetType().GetField(field,
        BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static object? Invoke(object instance, string method, params object?[] args) => instance.GetType().GetMethod(method,
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(instance, args);
}
