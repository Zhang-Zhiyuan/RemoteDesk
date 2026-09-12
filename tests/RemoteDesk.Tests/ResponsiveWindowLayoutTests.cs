using System.Drawing;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class ResponsiveWindowLayoutTests
{
    [Fact]
    public void DialogContentRetainsInputsAndActionsWhenViewportIsSmaller()
    {
        using var form = new Form();
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label { AutoSize = true, Text = string.Join("\n", Enumerable.Repeat("Dialog explanation", 12)) });
        var input = new TextBox { Dock = DockStyle.Top, Text = "Keep draft" };
        var accept = new Button { Text = "Confirm", AutoSize = true };
        table.Controls.Add(input);
        table.Controls.Add(accept);
        form.Controls.Add(table);
        ResponsiveWindowLayout.ConfigureDialog(form, new Size(480, 360), new Size(320, 220));
        var scroll = Assert.IsType<Panel>(Assert.Single(form.Controls.Cast<Control>()));
        Assert.True(scroll.AutoScroll);
        Assert.Equal(FormBorderStyle.Sizable, form.FormBorderStyle);
        Assert.Equal(AutoScaleMode.Dpi, form.AutoScaleMode);
        Assert.All(table.RowStyles.Cast<RowStyle>(), row => Assert.Equal(SizeType.AutoSize, row.SizeType));
        foreach (Size viewport in new[] { new Size(260, 160), new Size(700, 480), new Size(320, 220) })
        {
            form.ClientSize = viewport;
            form.PerformLayout();
            scroll.PerformLayout();
            table.PerformLayout();
            Assert.True(table.ClientRectangle.Contains(input.Bounds));
            Assert.True(table.ClientRectangle.Contains(accept.Bounds));
            Assert.Equal("Keep draft", input.Text);
        }
    }

    [Theory]
    [InlineData(15, 96, 15)]
    [InlineData(15, 120, 19)]
    [InlineData(15, 144, 23)]
    [InlineData(15, 192, 30)]
    [InlineData(15, 0, 15)]
    public void ScaleLogicalUsesCurrentMonitorDpi(int logicalPixels, int dpi, int expected)
    {
        Assert.Equal(expected, ResponsiveWindowLayout.ScaleLogical(logicalPixels, dpi));
    }

    [Theory]
    [InlineData(1099, 1100, 96, true)]
    [InlineData(1100, 1100, 96, false)]
    [InlineData(1649, 1100, 144, true)]
    [InlineData(1650, 1100, 144, false)]
    [InlineData(0, 1100, 192, false)]
    public void BreakpointsAreComparedInLogicalPixels(
        int availablePhysicalPixels,
        int logicalBreakpoint,
        int dpi,
        bool expected)
    {
        Assert.Equal(
            expected,
            ResponsiveWindowLayout.IsBelowLogicalWidth(
                availablePhysicalPixels,
                logicalBreakpoint,
                dpi));
    }

    [Fact]
    public void CalculateUsesPreferredLogicalSizeWhenItFits()
    {
        ResponsiveWindowMetrics metrics = ResponsiveWindowLayout.Calculate(
            new Rectangle(0, 0, 1920, 1040),
            96,
            new Size(1120, 720),
            new Size(640, 440));

        Assert.Equal(new Size(640, 440), metrics.MinimumSize);
        Assert.Equal(new Size(1120, 720), metrics.PreferredSize);
        Assert.Equal(new Point(400, 160), metrics.CenteredLocation);
    }

    [Fact]
    public void CalculateCapsHighDpiWindowToSmallWorkingArea()
    {
        ResponsiveWindowMetrics metrics = ResponsiveWindowLayout.Calculate(
            new Rectangle(0, 0, 1366, 728),
            192,
            new Size(1120, 720),
            new Size(640, 440));

        Assert.Equal(new Size(948, 489), metrics.MinimumSize);
        Assert.Equal(new Size(1318, 680), metrics.PreferredSize);
        Assert.Equal(new Point(24, 24), metrics.CenteredLocation);
    }

    [Fact]
    public void CalculateFitsCommonLowResolutionWorkingArea()
    {
        ResponsiveWindowMetrics metrics = ResponsiveWindowLayout.Calculate(
            new Rectangle(0, 0, 800, 600),
            96,
            new Size(1120, 720),
            new Size(640, 440));

        Assert.Equal(new Size(558, 414), metrics.MinimumSize);
        Assert.Equal(new Size(776, 576), metrics.PreferredSize);
        Assert.Equal(new Point(12, 12), metrics.CenteredLocation);
    }

    [Fact]
    public void CalculateScalesPreferredWindowOnHighDpi4kDisplay()
    {
        ResponsiveWindowMetrics metrics = ResponsiveWindowLayout.Calculate(
            new Rectangle(0, 0, 3840, 2160),
            192,
            new Size(1120, 720),
            new Size(640, 440));

        Assert.Equal(new Size(1280, 880), metrics.MinimumSize);
        Assert.Equal(new Size(2240, 1440), metrics.PreferredSize);
        Assert.Equal(new Point(800, 360), metrics.CenteredLocation);
    }

    [Fact]
    public void CalculateMaintainsBoundsInvariantsAcrossResolutionAndDpiMatrix()
    {
        Rectangle[] workingAreas =
        [
            new Rectangle(0, 0, 320, 240),
            new Rectangle(0, 0, 1024, 600),
            new Rectangle(-1920, 0, 1920, 1040),
            new Rectangle(0, 0, 3840, 2120)
        ];
        int[] dpis = [96, 120, 144, 192, 288];

        foreach (Rectangle workingArea in workingAreas)
        {
            foreach (int dpi in dpis)
            {
                ResponsiveWindowMetrics metrics = ResponsiveWindowLayout.Calculate(
                    workingArea,
                    dpi,
                    new Size(1120, 720),
                    new Size(640, 440));

                Assert.InRange(metrics.MinimumSize.Width, 1, metrics.PreferredSize.Width);
                Assert.InRange(metrics.MinimumSize.Height, 1, metrics.PreferredSize.Height);
                Assert.InRange(metrics.PreferredSize.Width, 1, workingArea.Width);
                Assert.InRange(metrics.PreferredSize.Height, 1, workingArea.Height);
                var bounds = new Rectangle(metrics.CenteredLocation, metrics.PreferredSize);
                Assert.True(workingArea.Contains(bounds), $"{workingArea} did not contain {bounds} at {dpi} DPI.");
            }
        }
    }

    [Fact]
    public void CenterAndClampSupportsDisplaysWithNegativeOrigins()
    {
        Rectangle workingArea = new(-1280, 0, 1280, 1024);

        Point location = ResponsiveWindowLayout.CenterAndClamp(
            workingArea,
            workingArea,
            new Size(800, 600));

        Assert.Equal(new Point(-1040, 212), location);
    }

    [Fact]
    public void CenterAndClampKeepsOwnerCenteredWindowInsideWorkingArea()
    {
        Point location = ResponsiveWindowLayout.CenterAndClamp(
            new Rectangle(1700, 900, 500, 400),
            new Rectangle(0, 0, 1920, 1040),
            new Size(960, 560));

        Assert.Equal(new Point(960, 480), location);
    }

    [Fact]
    public void ClampBoundsLimitsLargeOffscreenWindowWithoutRemovingDpiMargin()
    {
        Rectangle bounds = ResponsiveWindowLayout.ClampBoundsToWorkingArea(
            new Rectangle(1800, 1000, 2200, 1400),
            new Rectangle(0, 0, 1366, 728),
            192,
            new Size(948, 489));

        Assert.Equal(new Rectangle(24, 24, 1318, 680), bounds);
    }

    [Fact]
    public void ClampBoundsPreservesUsableWindowSizeAndLocation()
    {
        Rectangle bounds = ResponsiveWindowLayout.ClampBoundsToWorkingArea(
            new Rectangle(100, 100, 800, 500),
            new Rectangle(0, 0, 1920, 1040),
            96,
            new Size(640, 440));

        Assert.Equal(new Rectangle(100, 100, 800, 500), bounds);
    }

    [Fact]
    public void ClampBoundsPreservesWindowsSnapEdgesWhenMarginIsDisabled()
    {
        Rectangle bounds = ResponsiveWindowLayout.ClampBoundsToWorkingArea(
            new Rectangle(0, 0, 960, 1040),
            new Rectangle(0, 0, 1920, 1040),
            144,
            new Size(640, 440),
            insetWorkingArea: false);

        Assert.Equal(new Rectangle(0, 0, 960, 1040), bounds);
    }
}
