using Xunit;

namespace RemoteDesk.Tests;

public sealed class ViewerActionOverflowTests
{
    [Fact]
    public void NarrowToolbarKeepsAllCommandsAndScalePairTogether()
    {
        using var panel = new FlowLayoutPanel { Width = 300 };
        Button[] buttons = Enumerable.Range(0, 8).Select(i => new Button {
            Text = "action" + i, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(90, 30), Margin = Padding.Empty }).ToArray();
        panel.Controls.AddRange(buttons);
        var more = new Button { Text = "More", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        using var layout = new ViewerActionOverflow(panel, more,
            buttons.Select(b => (b, (Action)(() => { }))).ToArray(),
            [buttons[4..6], [buttons[7]], [buttons[3]], buttons[..2], [buttons[2]], [buttons[6]]], () => { });
        foreach (int width in new[] { 300, 1200, 180, 300, 1200 })
        {
            panel.Width = width;
            layout.Apply(width, panel.Font);
            var inline = panel.Controls.OfType<Button>().Where(b => b != more && b.Visible).ToArray();
            Assert.Equal(8, inline.Concat(layout.OverflowButtons).Distinct().Count());
            Assert.Same(buttons[4].Parent, buttons[5].Parent);
            layout.RebuildMenu();
            Assert.Equal(layout.OverflowButtons.Count, layout.MenuForTests.Items.Count);
            Assert.Equal(width < 1200, more.Visible);
        }
    }

    [Fact]
    public void OverflowFollowsAvailabilityAndInvokesSharedActionExactlyOnce()
    {
        using var panel = new FlowLayoutPanel { Width = 140 };
        var command = new Button { Text = "Original", MinimumSize = new Size(200, 30), AutoSize = true };
        panel.Controls.Add(command);
        var more = new Button { Text = "More", AutoSize = true };
        int invoked = 0;
        using var layout = new ViewerActionOverflow(panel, more, [(command, () => invoked++)], [[command]], () => { });
        layout.Apply(140, panel.Font);
        Assert.Single(layout.OverflowButtons);
        command.Text = "Updated toggle state";
        command.Enabled = false;
        layout.RebuildMenu();
        var item = Assert.IsType<ToolStripMenuItem>(Assert.Single(layout.MenuForTests.Items.Cast<ToolStripItem>()));
        Assert.Equal(command.Text, item.Text);
        Assert.False(item.Enabled);
        command.Enabled = true;
        layout.RebuildMenu();
        layout.MenuForTests.Items[0].PerformClick();
        Assert.Equal(1, invoked);
        command.Visible = false;
        layout.Apply(140, panel.Font);
        Assert.Empty(layout.OverflowButtons);
        layout.RebuildMenu();
        Assert.Empty(layout.MenuForTests.Items);
        layout.Apply(1200, panel.Font);
        Assert.False(command.Visible);
        command.Visible = true;
        layout.Apply(1200, panel.Font);
        Assert.Same(panel, command.Parent);
    }

    [Theory]
    [InlineData(96, 40)]
    [InlineData(144, 52)]
    [InlineData(192, 70)]
    public void StatusRowsRespectActualTextHeight(int dpi, int lineHeight)
    {
        int height = RemoteViewerWindow.CalculateStatusBarPreferredHeight(320, Padding.Empty, true, dpi, lineHeight);
        var (status, details) = RemoteViewerWindow.CalculateStatusBarLayout(new Size(320, height), Padding.Empty, true, dpi);
        Assert.True(status.Height >= lineHeight && details.Height >= lineHeight);
    }

    [Fact]
    public void HiddenToolbarDoesNotReparentOrReviveRevokedActions()
    {
        using var parent = new Panel();
        var panel = new FlowLayoutPanel { Width = 230 };
        parent.Controls.Add(panel);
        var first = new Button { Text = "first", MinimumSize = new Size(90, 30), AutoSize = true };
        var second = new Button { Text = "second", MinimumSize = new Size(250, 30), AutoSize = true };
        var restored = new Button { Text = "restored", Visible = false, AutoSize = true };
        panel.Controls.AddRange([first, second, restored]);
        var more = new Button { Text = "More", AutoSize = true };
        using var layout = new ViewerActionOverflow(panel, more,
            [(first, () => { }), (second, () => { }), (restored, () => { })], [[first], [second], [restored]], () => { });
        layout.Apply(230, panel.Font);
        Assert.Same(panel, first.Parent);
        Assert.Contains(second, layout.OverflowButtons);
        parent.Visible = false;
        Control? oldFirstParent = first.Parent, oldSecondParent = second.Parent;
        first.Visible = false;
        second.Visible = false;
        restored.Visible = true;
        for (int i = 0; i < 3; i++) layout.Apply(1200, panel.Font);
        Assert.Same(oldFirstParent, first.Parent);
        Assert.Same(oldSecondParent, second.Parent);
        parent.Visible = true;
        foreach (int width in new[] { 1200, 230 })
        {
            layout.Apply(width, panel.Font);
            Assert.False(first.Visible);
            Assert.False(second.Visible);
            Assert.True(restored.Visible);
            Assert.Same(panel, restored.Parent);
        }
    }
}
