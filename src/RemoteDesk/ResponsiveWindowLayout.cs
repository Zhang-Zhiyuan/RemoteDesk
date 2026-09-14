namespace RemoteDesk;

internal readonly record struct ResponsiveWindowMetrics(
    Size MinimumSize,
    Size PreferredSize,
    Point CenteredLocation);

internal static class ResponsiveWindowLayout
{
    internal const int DesignDpi = 96;
    private const double MinimumWorkAreaFraction = 0.72;
    private const int WorkAreaMargin = 12;

    internal static int ScaleLogical(int logicalPixels, int dpi)
    {
        int effectiveDpi = dpi > 0 ? dpi : DesignDpi;
        return Math.Max(0, (int)Math.Round(
            logicalPixels * effectiveDpi / (double)DesignDpi,
            MidpointRounding.AwayFromZero));
    }

    internal static bool IsBelowLogicalWidth(int availablePhysicalPixels, int logicalBreakpoint, int dpi)
    {
        return availablePhysicalPixels > 0 &&
            availablePhysicalPixels < ScaleLogical(logicalBreakpoint, dpi);
    }

    // Match FlowLayoutPanel: fixed-size children use their assigned bounds;
    // only AutoSize children are measured from their content. Width includes
    // padding/margins, and an explicit FlowBreak starts the next row.
    internal static Size MeasureFlowLayout(FlowLayoutPanel panel, int availableWidth)
    {
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(availableWidth);
        int contentWidth = Math.Max(1, availableWidth - panel.Padding.Horizontal);
        int rowWidth = 0, rowHeight = 0, totalHeight = panel.Padding.Vertical, maximumWidth = 0;
        foreach (Control control in panel.Controls)
        {
            if (!control.Visible) continue;
            Size size = control.AutoSize ? control.GetPreferredSize(Size.Empty) : control.Size;
            int width = Math.Max(control.MinimumSize.Width, size.Width) + control.Margin.Horizontal;
            int height = Math.Max(control.MinimumSize.Height, size.Height) + control.Margin.Vertical;
            if (rowWidth > 0 && rowWidth + width > contentWidth)
            {
                maximumWidth = Math.Max(maximumWidth, rowWidth);
                totalHeight += rowHeight;
                rowWidth = rowHeight = 0;
            }
            rowWidth += width;
            rowHeight = Math.Max(rowHeight, height);
            if (panel.GetFlowBreak(control))
            {
                maximumWidth = Math.Max(maximumWidth, rowWidth);
                totalHeight += rowHeight;
                rowWidth = rowHeight = 0;
            }
        }
        return new Size(Math.Max(1, Math.Max(maximumWidth, rowWidth) + panel.Padding.Horizontal),
            Math.Max(1, totalHeight + rowHeight));
    }

    internal static ResponsiveWindowMetrics Calculate(
        Rectangle workingArea,
        int dpi,
        Size logicalPreferredSize,
        Size logicalMinimumSize)
    {
        Rectangle availableArea = GetAvailableWorkingArea(workingArea, dpi);
        int availableWidth = availableArea.Width;
        int availableHeight = availableArea.Height;

        int minimumWidthCap = Math.Max(1, (int)Math.Floor(availableWidth * MinimumWorkAreaFraction));
        int minimumHeightCap = Math.Max(1, (int)Math.Floor(availableHeight * MinimumWorkAreaFraction));
        int minimumWidth = Math.Min(
            ScaleLogical(logicalMinimumSize.Width, dpi),
            minimumWidthCap);
        int minimumHeight = Math.Min(
            ScaleLogical(logicalMinimumSize.Height, dpi),
            minimumHeightCap);

        int preferredWidth = Math.Clamp(
            ScaleLogical(logicalPreferredSize.Width, dpi),
            minimumWidth,
            availableWidth);
        int preferredHeight = Math.Clamp(
            ScaleLogical(logicalPreferredSize.Height, dpi),
            minimumHeight,
            availableHeight);
        var preferredSize = new Size(preferredWidth, preferredHeight);
        var centeredLocation = CenterAndClamp(workingArea, availableArea, preferredSize);

        return new ResponsiveWindowMetrics(
            new Size(minimumWidth, minimumHeight),
            preferredSize,
            centeredLocation);
    }

    internal static Point CenterAndClamp(Rectangle anchor, Rectangle workingArea, Size windowSize)
    {
        int x = anchor.Left + ((anchor.Width - windowSize.Width) / 2);
        int y = anchor.Top + ((anchor.Height - windowSize.Height) / 2);
        int maximumX = Math.Max(workingArea.Left, workingArea.Right - windowSize.Width);
        int maximumY = Math.Max(workingArea.Top, workingArea.Bottom - windowSize.Height);
        return new Point(
            Math.Clamp(x, workingArea.Left, maximumX),
            Math.Clamp(y, workingArea.Top, maximumY));
    }

    internal static Rectangle ClampBoundsToWorkingArea(
        Rectangle bounds,
        Rectangle workingArea,
        int dpi,
        Size minimumSize,
        bool insetWorkingArea = true)
    {
        Rectangle availableArea = insetWorkingArea
            ? GetAvailableWorkingArea(workingArea, dpi)
            : new Rectangle(
                workingArea.Left,
                workingArea.Top,
                Math.Max(1, workingArea.Width),
                Math.Max(1, workingArea.Height));
        int width = Math.Clamp(bounds.Width, Math.Min(minimumSize.Width, availableArea.Width), availableArea.Width);
        int height = Math.Clamp(bounds.Height, Math.Min(minimumSize.Height, availableArea.Height), availableArea.Height);
        int maximumX = Math.Max(availableArea.Left, availableArea.Right - width);
        int maximumY = Math.Max(availableArea.Top, availableArea.Bottom - height);
        int x = Math.Clamp(bounds.Left, availableArea.Left, maximumX);
        int y = Math.Clamp(bounds.Top, availableArea.Top, maximumY);
        return new Rectangle(x, y, width, height);
    }

    internal static void ApplyTo(
        Form form,
        Size logicalPreferredSize,
        Size logicalMinimumSize,
        bool applyPreferredBounds,
        Rectangle? suggestedBounds = null)
    {
        Screen targetScreen;
        if (applyPreferredBounds && form.Owner is { IsDisposed: false, Visible: true } owner)
        {
            targetScreen = Screen.FromControl(owner);
        }
        else if (suggestedBounds is { Width: > 0, Height: > 0 } proposedBounds)
        {
            targetScreen = Screen.FromRectangle(proposedBounds);
        }
        else
        {
            targetScreen = Screen.FromControl(form);
        }

        Rectangle workingArea = targetScreen.WorkingArea;
        ResponsiveWindowMetrics metrics = Calculate(
            workingArea,
            form.DeviceDpi,
            logicalPreferredSize,
            logicalMinimumSize);
        form.MinimumSize = metrics.MinimumSize;

        if (!applyPreferredBounds)
        {
            if (form.WindowState == FormWindowState.Normal)
            {
                form.Bounds = ClampBoundsToWorkingArea(
                    suggestedBounds ?? form.Bounds,
                    workingArea,
                    form.DeviceDpi,
                    metrics.MinimumSize,
                    insetWorkingArea: false);
            }

            return;
        }

        Rectangle anchor = form.Owner is { IsDisposed: false } currentOwner && currentOwner.Visible
            ? currentOwner.Bounds
            : workingArea;
        Point location = CenterAndClamp(
            anchor,
            GetAvailableWorkingArea(workingArea, form.DeviceDpi),
            metrics.PreferredSize);
        form.StartPosition = FormStartPosition.Manual;
        form.Bounds = new Rectangle(location, metrics.PreferredSize);
    }

    internal static void ApplyMinimumSizeTo(Form form, Size logicalMinimumSize)
    {
        Rectangle workingArea = Screen.FromControl(form).WorkingArea;
        ResponsiveWindowMetrics metrics = Calculate(
            workingArea,
            form.DeviceDpi,
            logicalMinimumSize,
            logicalMinimumSize);
        form.MinimumSize = metrics.MinimumSize;
    }

    internal static void ConfigureScrollablePage(TabPage page, TableLayoutPanel content)
    {
        // One scroll owner: nested Fill-docked scroll panels retain their old
        // virtual width, preventing children from seeing a narrower viewport.
        page.AutoScroll = true;
        content.AutoScroll = false;
        content.Dock = DockStyle.Top;
        content.AutoSize = false;
        content.ColumnStyles.Clear();
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bool queued = false;
        bool applying = false;
        void Reflow()
        {
            queued = false;
            if (page.IsDisposed || !page.Visible || page.ClientSize.Width <= 0) return;
            applying = true;
            try
            {
                // GetPreferredSize is a measurement, not AutoSize: allowing the
                // outer table to AutoSize also shrinks fixed-width inputs to 0.
                int height = Math.Max(page.ClientSize.Height,
                    content.GetPreferredSize(new Size(page.ClientSize.Width, 0)).Height);
                if (content.Height != height) content.Height = height;
                var extent = new Size(0, height);
                if (page.AutoScrollMinSize != extent) page.AutoScrollMinSize = extent;
            }
            finally { applying = false; }
        }
        void QueueReflow()
        {
            if (queued || applying || !page.IsHandleCreated || page.IsDisposed) return;
            queued = true;
            page.BeginInvoke((Action)Reflow);
        }
        page.ClientSizeChanged += (_, _) => QueueReflow();
        page.VisibleChanged += (_, _) => QueueReflow();
        page.HandleCreated += (_, _) => QueueReflow();
        content.Layout += (_, _) => QueueReflow();
        void TrackFocus(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                child.Enter += (_, _) => page.ScrollControlIntoView(child);
                TrackFocus(child);
            }
        }
        TrackFocus(content);
        QueueReflow();
    }

    internal static void ConfigureDialog(Form form, Size logicalPreferredSize, Size logicalMinimumSize)
    {
        form.SuspendLayout();
        try
        {
            form.AutoScaleMode = AutoScaleMode.Dpi;
            form.AutoScaleDimensions = new SizeF(DesignDpi, DesignDpi);
            form.MinimumSize = Size.Empty;
            form.FormBorderStyle = FormBorderStyle.Sizable;
            if (form.Controls.Count == 1 && form.Controls[0] is TableLayoutPanel content)
            {
                // A dialog must remain usable even when its complete form is
                // taller/wider than the monitor. Let the content retain its
                // required height and scroll it; never shrink away the buttons.
                form.Controls.Remove(content);
                var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = content.BackColor };
                content.AutoScroll = false;
                content.AutoSize = true;
                content.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                content.Dock = DockStyle.Top;
                content.MinimumSize = new Size(content.ColumnCount > 1 ? 320 : 0, 0);
                if (content.ColumnCount == 1)
                {
                    content.ColumnStyles.Clear();
                    content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                }
                foreach (RowStyle row in content.RowStyles) row.SizeType = SizeType.AutoSize;
                scroll.Controls.Add(content);
                form.Controls.Add(scroll);
                // WinForms ignores horizontal overflow of a Top-docked child
                // when computing scrollbars. Include its minimum-width bounds
                // explicitly, otherwise ScrollControlIntoView cannot reach it.
                void UpdateScrollExtent() => scroll.AutoScrollMinSize = new Size(content.MinimumSize.Width, content.Height);
                content.SizeChanged += (_, _) => UpdateScrollExtent();
                UpdateScrollExtent();
                void TrackFocus(Control parent)
                {
                    foreach (Control child in parent.Controls)
                    {
                        child.Enter += (_, _) => scroll.ScrollControlIntoView(child);
                        TrackFocus(child);
                    }
                }
                TrackFocus(content);
                bool focusRefreshPending = false;
                scroll.ClientSizeChanged += (_, _) =>
                {
                    if (focusRefreshPending || !scroll.IsHandleCreated) return;
                    focusRefreshPending = true;
                    scroll.BeginInvoke((Action)(() =>
                    {
                        focusRefreshPending = false;
                        if (!scroll.IsDisposed) scroll.ScrollControlIntoView(form.ActiveControl);
                    }));
                };
            }
        }
        finally { form.ResumeLayout(performLayout: true); }

        form.Load += (_, _) => ApplyTo(form, logicalPreferredSize, logicalMinimumSize, applyPreferredBounds: true);
        form.ResizeEnd += (_, _) => ApplyTo(form, logicalPreferredSize, logicalMinimumSize, applyPreferredBounds: false);
        form.DpiChanged += (_, _) =>
        {
            // The public event precedes WinForms' automatic DPI scaling.
            // Clamp only after that pass has applied the new control sizes.
            form.BeginInvoke((Action)(() =>
            {
                if (!form.IsDisposed) ApplyTo(form, logicalPreferredSize, logicalMinimumSize, applyPreferredBounds: false);
            }));
        };
    }

    private static Rectangle GetAvailableWorkingArea(Rectangle workingArea, int dpi)
    {
        int requestedMargin = ScaleLogical(WorkAreaMargin, dpi);
        int horizontalMargin = Math.Min(requestedMargin, Math.Max(0, (workingArea.Width - 1) / 2));
        int verticalMargin = Math.Min(requestedMargin, Math.Max(0, (workingArea.Height - 1) / 2));
        return new Rectangle(
            workingArea.Left + horizontalMargin,
            workingArea.Top + verticalMargin,
            Math.Max(1, workingArea.Width - (horizontalMargin * 2)),
            Math.Max(1, workingArea.Height - (verticalMargin * 2)));
    }
}
