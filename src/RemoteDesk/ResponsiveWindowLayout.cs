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
