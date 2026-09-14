using Xunit;

namespace RemoteDesk.Tests;

public sealed class MainFormResponsivenessTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SectionResizesWithinOneBreakpointReuseLayoutStyles(bool hostSettings)
    {
        using var table = new TableLayoutPanel { Size = new Size(1600, 600) };
        var first = new Panel { Dock = DockStyle.Fill };
        var second = new Panel { Dock = DockStyle.Fill };
        table.Controls.Add(first);
        table.Controls.Add(second);
        Configure(hostSettings, table, first, second);
        Assert.Equal(2, table.ColumnCount);
        ColumnStyle[] columns = table.ColumnStyles.Cast<ColumnStyle>().ToArray();
        RowStyle[] rows = table.RowStyles.Cast<RowStyle>().ToArray();

        for (int step = 0; step < 50; step++)
        {
            table.Size = new Size(1601 + step, 601 + step);
            Assert.Equal(columns, table.ColumnStyles.Cast<ColumnStyle>());
            Assert.Equal(rows, table.RowStyles.Cast<RowStyle>());
            Assert.Equal(1, table.GetColumn(second));
            Assert.Equal(0, table.GetRow(second));
        }
    }

    [Theory]
    [InlineData(true, 860)]
    [InlineData(false, 760)]
    public void CrossingBreakpointStillReflowsAndThenReusesStyles(bool hostSettings, int breakpoint)
    {
        using var table = new TableLayoutPanel { Size = new Size(1600, 600) };
        var first = new Panel { Dock = DockStyle.Fill };
        var second = new Panel { Dock = DockStyle.Fill };
        table.Controls.Add(first);
        table.Controls.Add(second);
        Configure(hostSettings, table, first, second);
        int boundary = ResponsiveWindowLayout.ScaleLogical(breakpoint, table.DeviceDpi);
        int gap = ResponsiveWindowLayout.ScaleLogical(12, table.DeviceDpi);

        for (int round = 0; round < 3; round++)
        {
            table.Width = boundary - 1;
            Assert.Equal(1, table.ColumnCount);
            Assert.Equal(2, table.RowCount);
            Assert.Equal(0, table.GetColumn(second));
            Assert.Equal(1, table.GetRow(second));
            Assert.Equal(new Padding(0, 0, 0, gap), first.Margin);
            var compactStyle = table.ColumnStyles[0];
            table.Height++;
            table.Width--;
            Assert.Same(compactStyle, table.ColumnStyles[0]);
            Assert.All(table.RowStyles.Cast<RowStyle>(), row =>
                Assert.Equal(hostSettings ? SizeType.AutoSize : SizeType.Percent, row.SizeType));

            table.Width = boundary;
            Assert.Equal(2, table.ColumnCount);
            Assert.Equal(1, table.RowCount);
            Assert.Equal(1, table.GetColumn(second));
            Assert.Equal(0, table.GetRow(second));
            Assert.Equal(new Padding(0, 0, gap, 0), first.Margin);
            var wideStyle = table.ColumnStyles[0];
            table.Height++;
            table.Width++;
            Assert.Same(wideStyle, table.ColumnStyles[0]);
            Assert.NotSame(compactStyle, wideStyle);
            Assert.Equal(hostSettings ? 46f : 42f, wideStyle.Width);
        }
    }

    [Fact]
    public async Task SlowPermissionQueryDoesNotBlockCallingThread()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<MainForm.HostPermissionStatus> pending = MainForm.QueryHostPermissionStatusAsync(() =>
        {
            entered.SetResult(true);
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test did not release query.");
            return new(true, false);
        });
        try
        {
            Assert.True(await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.False(pending.IsCompleted);
        }
        finally { release.Set(); }
        Assert.Equal(new MainForm.HostPermissionStatus(true, false), await pending);
    }

    [Fact]
    public async Task PermissionQueryFailureIsObservableAndNextReadCanRecover()
    {
        await Assert.ThrowsAsync<IOException>(() => MainForm.QueryHostPermissionStatusAsync(
            () => throw new IOException("Synthetic unavailable status")));
        Assert.Equal(new MainForm.HostPermissionStatus(false, true),
            await MainForm.QueryHostPermissionStatusAsync(() => new(false, true)));
    }

    private static void Configure(bool hostSettings, TableLayoutPanel table, Control first, Control second)
    {
        if (hostSettings) MainForm.ConfigureResponsiveHostSettings(table, first, second);
        else MainForm.ConfigureResponsiveViewerWorkspace(table, first, second);
    }
}
