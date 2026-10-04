using Xunit;

namespace RemoteDesk.Tests;

public sealed class MainFormClippingTests
{
    [Theory]
    [InlineData(9.25f)]
    [InlineData(18f)]
    [InlineData(24f)]
    public void EndpointChooserKeepsFullIdentitiesAndLargeFontActionsAccessible(float points)
    {
        using var font = new Font("Microsoft YaHei UI", points);
        var first = new DiscoveredHost(new string('W', 80), "2001:db8:1234:5678:abcd:ef12:3456:7890", 56565,
            "screen", true, false, RemoteDevicePlatforms.Windows, RemoteDeviceCapabilities.None);
        var second = first with { Port = 56566 };
        using Form dialog = MainForm.CreateDeviceEndpointDialog([first, second], font, out ListBox choices);
        dialog.ClientSize = new Size(340, 240);
        dialog.PerformLayout();
        var accept = Assert.IsAssignableFrom<Button>(dialog.AcceptButton);
        int textHeight = TextRenderer.MeasureText(accept.Text, accept.Font, Size.Empty, TextFormatFlags.SingleLine).Height;
        Assert.True(accept.Height >= textHeight + accept.Padding.Vertical,
            $"Action height {accept.Height} clips text height {textHeight} at {points} pt.");
        Assert.True(choices.HorizontalScrollbar, "Long device names and IPv6 endpoints need horizontal scrolling inside the list.");
        Assert.Same(first, choices.SelectedItem);
        choices.SelectedIndex = 1;
        Assert.Same(second, choices.SelectedItem);
        Assert.Equal(DialogResult.Cancel, Assert.IsAssignableFrom<Button>(dialog.CancelButton).DialogResult);
    }

    [Theory]
    [InlineData(96, 9.25f)]
    [InlineData(96, 14f)]
    [InlineData(96, 18f)]
    [InlineData(144, 9.25f)]
    [InlineData(144, 14f)]
    [InlineData(144, 18f)]
    [InlineData(192, 18f)]
    [InlineData(288, 24f)]
    public void BothDeviceRowLinesFitTheActualFontWithoutOverlapping(int dpi, float points)
    {
        using var font = new Font("Microsoft YaHei UI", points);
        var metrics = MainForm.CalculateDiscoveredHostRowMetrics(font, dpi);
        Assert.True(metrics.PrimaryHeight >= font.Height);
        Assert.True(metrics.SecondaryHeight >= font.Height);
        Assert.True(metrics.SecondaryOffset > metrics.PrimaryHeight);
        Assert.True(metrics.ItemHeight >= metrics.VerticalInset * 2 + metrics.SecondaryOffset + metrics.SecondaryHeight);
        Assert.True(metrics.ItemHeight >= ResponsiveWindowLayout.ScaleLogical(52, dpi));
    }

    [Fact]
    public void LongStatusHasAnExplicitScrollableExtentThatShrinksWhenTextClears()
    {
        using var panel = new Panel { Size = new Size(400, 120), Padding = new Padding(12) };
        using var label = new Label { Text = string.Join("\n", Enumerable.Repeat("这是一段测试诊断状态信息", 30)) };
        panel.Controls.Add(label);
        MainForm.ConfigureScrollableStatusLabel(panel, label);
        panel.PerformLayout();
        Assert.True(panel.AutoScroll);
        Assert.Equal(DockStyle.Top, label.Dock);
        Assert.True(label.AutoSize);
        Assert.Equal(0, panel.AutoScrollMinSize.Width);
        Assert.True(panel.AutoScrollMinSize.Height > panel.ClientSize.Height);
        Assert.True(panel.AutoScrollMinSize.Height >= label.PreferredSize.Height + panel.Padding.Vertical);
        label.Text = "已连接";
        panel.PerformLayout();
        Assert.True(panel.AutoScrollMinSize.Height < panel.ClientSize.Height);
    }

    [Fact]
    public void StatusWidthAndHeightFollowFontAndViewportChanges()
    {
        using var panel = new Panel { Size = new Size(640, 160), Padding = new Padding(12) };
        using var label = new Label { Text = string.Concat(Enumerable.Repeat("远程诊断信息，不能被裁掉。", 15)) };
        panel.Controls.Add(label);
        MainForm.ConfigureScrollableStatusLabel(panel, label);
        int wideHeight = panel.AutoScrollMinSize.Height;
        panel.Width = 300;
        panel.PerformLayout();
        Assert.True(panel.AutoScrollMinSize.Height > wideHeight);
        Assert.Equal(panel.ClientSize.Width - panel.Padding.Horizontal, label.MaximumSize.Width);
        int smallFontHeight = panel.AutoScrollMinSize.Height;
        using var large = new Font("Microsoft YaHei UI", 18);
        label.Font = large;
        panel.PerformLayout();
        Assert.True(panel.AutoScrollMinSize.Height > smallFontHeight);
        Assert.Equal(0, panel.AutoScrollMinSize.Width);
    }

    [Fact]
    public void DialogActionsFitNarrowWidthsAndReleaseTheLimitAfterWidening()
    {
        using var font = new Font("Microsoft YaHei UI", 18);
        using var actions = new FlowLayoutPanel { Width = 250, Font = font };
        using var save = new Button { Text = "保存并同步", AutoSize = true };
        using var cancel = new Button { Text = "取消", AutoSize = true };
        actions.Controls.Add(save);
        actions.Controls.Add(cancel);
        MainForm.ConfigureWrappingDialogActions(actions);
        actions.PerformLayout();
        Assert.Equal(AutoSizeMode.GrowAndShrink, save.AutoSizeMode);
        Assert.Equal(250 - save.Margin.Horizontal, save.MaximumSize.Width);
        Assert.True(save.Width <= save.MaximumSize.Width);
        actions.Width = 600;
        actions.PerformLayout();
        Assert.Equal(600 - save.Margin.Horizontal, save.MaximumSize.Width);
        Assert.Equal(AutoSizeMode.GrowAndShrink, actions.AutoSizeMode);
        Assert.True(actions.WrapContents);
    }

    [Fact]
    public void AddressDialogUsesScrollableContentAndUnambiguousIpv6Endpoints()
    {
        var device = new RelayOnlineDevice(Guid.NewGuid().ToString(), "Synthetic", "Linux", null, false, 0)
        { DirectAddresses = ["192.0.2.1", "2001:db8::1234"], DirectPort = 56565 };
        using Form dialog = MainForm.CreateRelayAddressesDialog(device, Guid.NewGuid().ToString(), SystemFonts.MessageBoxFont!, out ListBox addresses);
        var scroll = Assert.IsType<Panel>(Assert.Single(dialog.Controls.Cast<Control>()));
        Assert.True(scroll.AutoScroll);
        Assert.Equal("192.0.2.1:56565", addresses.Items[0]);
        Assert.Equal("[2001:db8::1234]:56565", addresses.Items[1]);
        Assert.True(addresses.HorizontalScrollbar);
        Assert.True(addresses.MinimumSize.Height >= 100);
        Assert.True(Assert.IsType<Button>(dialog.AcceptButton).Enabled);
        Assert.Equal(DialogResult.Cancel, Assert.IsType<Button>(dialog.CancelButton).DialogResult);
    }

    [Fact]
    public void AddressDialogStillPreventsSelectingTheLocalMachine()
    {
        string identity = Guid.NewGuid().ToString();
        var device = new RelayOnlineDevice(identity, "Local synthetic", "Windows", null, false, 0)
        { DirectAddresses = ["192.0.2.1"], DirectPort = 56565 };
        using Form dialog = MainForm.CreateRelayAddressesDialog(device, identity, SystemFonts.MessageBoxFont!, out _);
        Assert.False(Assert.IsType<Button>(dialog.AcceptButton).Enabled);
    }

    [Fact]
    public void AddDeviceFormKeepsFieldsAndValidationInOneScrollableColumn()
    {
        using Form dialog = MainForm.CreateAddDeviceDialog(SystemFonts.MessageBoxFont!, out TextBox[] fields, out Label error, out Button save);
        var scroll = Assert.IsType<Panel>(Assert.Single(dialog.Controls.Cast<Control>()));
        var content = Assert.IsType<TableLayoutPanel>(Assert.Single(scroll.Controls.Cast<Control>()));
        Assert.Equal(1, content.ColumnCount);
        Assert.Equal(4, fields.Length);
        Assert.All(fields, input =>
        {
            Assert.Equal(DockStyle.Top, input.Dock);
            Assert.False(string.IsNullOrWhiteSpace(input.AccessibleName));
        });
        Assert.True(fields[2].UseSystemPasswordChar);
        Assert.Equal(AppSettingsService.MaxSavedDeviceRemarkLength, fields[3].MaxLength);
        Assert.Equal(Size.Empty, error.MaximumSize);
        Assert.True(error.AutoSize);
        Assert.Same(save, dialog.AcceptButton);
        Assert.Equal(DialogResult.Cancel, Assert.IsType<Button>(dialog.CancelButton).DialogResult);
    }
}
