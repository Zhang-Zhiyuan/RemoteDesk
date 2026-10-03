using Xunit;

namespace RemoteDesk.Tests;

public sealed class UiClarityTests
{
    [Fact]
    public void DisabledViewerActionsRemainReadableWithoutBecomingClickable()
    {
        using var button = new ViewerActionButton
        {
            Text = "Unavailable", Size = new Size(180, 44), Enabled = false,
            FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(23, 32, 51),
            ForeColor = Color.FromArgb(226, 232, 240)
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(51, 65, 85);
        int clicks = 0;
        button.Click += (_, _) => clicks++;
        button.PerformClick();
        Assert.Equal(0, clicks);
        using var bitmap = new Bitmap(button.Width, button.Height);
        button.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        int readablePixels = 0;
        for (int y = 5; y < bitmap.Height - 5; y++)
            for (int x = 5; x < bitmap.Width - 5; x++)
            {
                Color pixel = bitmap.GetPixel(x, y);
                if (pixel.R > 110 && pixel.G > 110 && pixel.B > 110) readablePixels++;
            }
        Assert.True(readablePixels > 30, "Disabled labels disappeared into the dark toolbar.");
        button.Enabled = true;
        button.PerformClick();
        Assert.Equal(1, clicks);
    }

    [Fact]
    public void VideoModeLabelsFitCompactFieldsAndDescribeOnlyAvailableModes()
    {
        using var combo = new ComboBox();
        MainForm.PopulateViewerVideoModes(combo);
        Assert.Equal(new[] { "自动（推荐）", "清晰（JPEG）" }, combo.Items.Cast<object>().Select(x => x.ToString()));
        Assert.DoesNotContain("仅 H.264", MainForm.ViewerVideoModeDescription);
        Assert.Contains("回退 JPEG", MainForm.ViewerVideoModeDescription);
        Assert.Contains("带宽", MainForm.ViewerVideoModeDescription);
    }

    [Fact]
    public void ScrollablePageWidthFollowsViewportAfterLargeContentShrinks()
    {
        using var form = new Form { ClientSize = new Size(1200, 800) };
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var page = new TabPage();
        var table = new TableLayoutPanel { ColumnCount = 1, RowCount = 2, Dock = DockStyle.Fill, AutoScroll = true };
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var content = new Panel { Dock = DockStyle.Fill, MinimumSize = new Size(0, 1200) };
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 180, WrapContents = true };
        var input = new TextBox { Width = 180 };
        toolbar.Controls.Add(input);
        var port = new NumericUpDown { Width = 96 };
        toolbar.Controls.Add(port);
        for (int i = 0; i < 8; i++) toolbar.Controls.Add(new Button { Width = 110, Text = "按钮 " + i });
        table.Controls.Add(toolbar);
        table.Controls.Add(content);
        page.Controls.Add(table); tabs.TabPages.Add(page); form.Controls.Add(tabs);
        ResponsiveWindowLayout.ConfigureScrollablePage(page, table);
        form.CreateControl(); tabs.CreateControl(); page.CreateControl(); table.CreateControl();
        foreach (int width in new[] { 1200, 800, 640, 1200, 640 })
        {
            form.ClientSize = new Size(width, 600);
            form.PerformLayout(); tabs.PerformLayout(); page.PerformLayout(); table.PerformLayout();
            Assert.Equal(page.ClientSize.Width, table.Width);
            Assert.Equal(DockStyle.Top, table.Dock);
            Assert.True(page.AutoScroll);
            Assert.False(table.AutoScroll);
            Assert.False(table.AutoSize);
            Assert.Equal(180, input.Width);
            Assert.Equal(96, port.Width);
            Assert.Equal(SizeType.Percent, table.ColumnStyles[0].SizeType);
        }
    }
}
