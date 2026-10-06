using Xunit;

namespace RemoteDesk.Tests;

public sealed class FileTransferDialogLayoutTests
{
    [Fact]
    public void AutosizedMetadataHeadersRemainSingleLineWhilePathHeadersCanWrap()
    {
        using var dialog = new FileTransferConfirmationDialog("确认", "发送",
            [new("文件", @"C:\Owned\one.bin", "one.bin", 1048576, @"D:\Owned\one.bin")], null);
        var scroll = Assert.IsType<Panel>(Assert.Single(dialog.Controls.Cast<Control>()));
        var content = Assert.IsType<TableLayoutPanel>(Assert.Single(scroll.Controls.Cast<Control>()));
        var grid = Assert.Single(content.Controls.OfType<DataGridView>());
        Assert.All(grid.Columns.Cast<DataGridViewColumn>().Take(2), column =>
        {
            Assert.Equal(DataGridViewAutoSizeColumnMode.DisplayedCells, column.AutoSizeMode);
            Assert.Equal(DataGridViewTriState.False, column.HeaderCell.InheritedStyle.WrapMode);
        });
        Assert.All(grid.Columns.Cast<DataGridViewColumn>().Skip(2), column =>
            Assert.Equal(DataGridViewTriState.True, column.HeaderCell.InheritedStyle.WrapMode));
    }

    [Fact]
    public void ConfirmationConstrainsLongPathsAndKeepsActionsInScrollableContent()
    {
        using var dialog = new FileTransferConfirmationDialog("确认文件传输", "发送文件",
            [new FileTransferConfirmationItem("文件", @"C:\很长的路径\测试.txt", "测试.txt", 10,
                @"D:\接收目录\测试 (2).txt")], "确认后开始");
        var scroll = Assert.IsType<Panel>(Assert.Single(dialog.Controls.Cast<Control>()));
        var content = Assert.IsType<TableLayoutPanel>(Assert.Single(scroll.Controls.Cast<Control>()));
        Assert.True(scroll.AutoScroll);
        Assert.False(content.AutoScroll);
        Assert.Equal(DockStyle.Top, content.Dock);
        Assert.Equal(SizeType.Percent, Assert.Single(content.ColumnStyles.Cast<ColumnStyle>()).SizeType);
        Assert.Equal(100, content.ColumnStyles[0].Width);
        var grid = Assert.Single(content.Controls.OfType<DataGridView>());
        Assert.True(grid.ReadOnly);
        Assert.Equal(DataGridViewColumnHeadersHeightSizeMode.AutoSize, grid.ColumnHeadersHeightSizeMode);
        Assert.True(grid.MinimumSize.Height >= 120);
        Assert.All(grid.Columns.Cast<DataGridViewColumn>().Skip(2), column => Assert.True(column.MinimumWidth >= 140));
        var details = Assert.Single(content.Controls.OfType<TextBox>());
        Assert.True(details.ReadOnly);
        Assert.True(details.Multiline);
        Assert.Equal("所选文件的完整路径", details.AccessibleName);
        Assert.Equal(DialogResult.OK, Assert.IsType<Button>(dialog.AcceptButton).DialogResult);
        Assert.Equal(DialogResult.Cancel, Assert.IsType<Button>(dialog.CancelButton).DialogResult);
    }

    [Fact]
    public void ResultsPreserveScrollableSelectableReceiptAndDismissAction()
    {
        const string result = "已发送：测试.txt\r\nD:\\接收目录\\测试 (2).txt\r\n未完成：第二个文件";
        using var dialog = new FileTransferResultDialog("完成 1 项，失败 1 项", result);
        var scroll = Assert.IsType<Panel>(Assert.Single(dialog.Controls.Cast<Control>()));
        var content = Assert.IsType<TableLayoutPanel>(Assert.Single(scroll.Controls.Cast<Control>()));
        Assert.True(scroll.AutoScroll);
        Assert.False(content.AutoScroll);
        Assert.Equal(SizeType.Percent, Assert.Single(content.ColumnStyles.Cast<ColumnStyle>()).SizeType);
        var details = Assert.Single(content.Controls.OfType<TextBox>());
        Assert.Equal(result, details.Text);
        Assert.True(details.ReadOnly);
        Assert.True(details.Multiline);
        Assert.Equal(ScrollBars.Vertical, details.ScrollBars);
        Assert.True(details.WordWrap);
        Assert.True(details.MinimumSize.Height >= 120);
        Assert.Equal("实际保存位置和传输结果", details.AccessibleName);
        Assert.Same(dialog.AcceptButton, dialog.CancelButton);
    }

    [Fact]
    public void LongReceiptPathsWrapWithoutChangingSelectableContents()
    {
        string path = @"D:\RemoteDeskReceived\" +
            string.Join("\\", Enumerable.Repeat("很长的目录名称-without-spaces", 20)) + @"\报告 (2).txt";
        string receipt = "已发送：报告.txt\r\n文件已保存到被控端：" + path + "（SHA-256 已校验）";
        using var dialog = new FileTransferResultDialog("已发送 1 项", receipt);
        var scroll = Assert.IsType<Panel>(Assert.Single(dialog.Controls.Cast<Control>()));
        var content = Assert.IsType<TableLayoutPanel>(Assert.Single(scroll.Controls.Cast<Control>()));
        var details = Assert.Single(content.Controls.OfType<TextBox>());
        details.SelectAll();

        Assert.True(details.WordWrap);
        Assert.Equal(ScrollBars.Vertical, details.ScrollBars);
        Assert.Equal(receipt, details.Text);
        Assert.Equal(receipt, details.SelectedText);
        Assert.Contains(path, details.SelectedText, StringComparison.Ordinal);
    }
}
