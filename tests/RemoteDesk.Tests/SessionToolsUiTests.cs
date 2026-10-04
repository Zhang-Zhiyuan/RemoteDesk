using System.Reflection;
using System.Runtime.ExceptionServices;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class SessionToolsUiTests
{
    [Fact]
    public void ViewOnlyIntentIsSharedAcrossScreensAndSurvivesCapabilities() => OnSta(() =>
    {
        using var client = new RemoteViewerClient();
        using var first = Viewer(client, true);
        using var second = Viewer(client, true);
        client.SetLocalInputPaused(true);
        Assert.False(InputEnabled(first));
        Assert.False(InputEnabled(second));
        Assert.Equal("仅观看：开", first.InputPauseButtonForTests.Text);
        first.SetInputEnabled(false);
        first.SetInputEnabled(true);
        Assert.False(InputEnabled(first));
        Assert.True(first.LocalInputPausedForTests);
        second.SetInputEnabled(false);
        client.SetLocalInputPaused(false);
        Assert.True(InputEnabled(first));
        Assert.False(InputEnabled(second));
        Assert.Equal("仅观看：关", second.InputPauseButtonForTests.Text);
    });

    [Fact]
    public void ReopenedScreenKeepsPauseAndCannotOverrideRemoteInputPermission() => OnSta(() =>
    {
        using var client = new RemoteViewerClient();
        client.SetLocalInputPaused(true);
        using var viewer = Viewer(client, true);
        Assert.False(InputEnabled(viewer));
        viewer.SetInputEnabled(false);
        client.SetLocalInputPaused(false);
        Assert.False(InputEnabled(viewer));
        Assert.True(viewer.TransferTasksButtonForTests.Enabled);
    });

    [Fact]
    public void PausingInvalidatesDragGestureWithoutCancellingItsConfirmedTransfer() => OnSta(() =>
    {
        using var client = new RemoteViewerClient();
        using var viewer = Viewer(client, true);
        using var transferLifetime = new CancellationTokenSource();
        Set("_remoteDragOutStage", RemoteDragOutStage.Pulling);
        Set("_remoteDragOutCancellation", transferLifetime);
        Set("_remoteDragOutOperationGeneration", 41L);
        client.SetLocalInputPaused(true);
        Assert.False(transferLifetime.IsCancellationRequested);
        Assert.Null(Get("_remoteDragOutCancellation"));
        Assert.NotEqual(41L, Get("_remoteDragOutOperationGeneration"));
        client.SetLocalInputPaused(false);
        Assert.Null(Get("_remoteDragOutCancellation"));
        object? Get(string name) => typeof(RemoteViewerWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewer);
        void Set(string name, object value) => typeof(RemoteViewerWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewer, value);
    });

    [Fact]
    public void RefreshUpdatesProgressWithoutReplacingSelectedTaskOrLosingFullPaths() => OnSta(() =>
    {
        var task = Snapshot();
        RemoteFileTransferTaskSnapshot[] tasks = [task];
        using var form = Panel(() => tasks);
        form.RefreshTasks();
        var row = Assert.Single(form.TasksForTests.Items.Cast<ListViewItem>());
        row.Selected = true;
        tasks = [task with { BytesTransferred = 1024, CurrentFileName = "second.txt" }];
        form.RefreshTasks();
        Assert.Same(row, form.TasksForTests.Items[0]);
        Assert.True(row.Selected);
        Assert.Contains("1 KB", row.SubItems[2].Text);
        Assert.Contains(task.Destination!, form.DetailsForTests.Text);
        Assert.Contains("second.txt", form.DetailsForTests.Text);
    });

    [Fact]
    public void TransferColumnsFitLargeFontsWithoutResettingUserWidthOnRefresh() => OnSta(() =>
    {
        var task = Snapshot() with { BytesTransferred = 1024, Status = "等待远端实际保存回执" };
        using var form = Panel(() => [task]);
        using var largeFont = new Font("Microsoft YaHei UI", 22);
        form.Font = largeFont;
        form.RefreshTasks();
        var list = form.TasksForTests;
        var row = Assert.Single(list.Items.Cast<ListViewItem>());
        foreach (int index in new[] { 0, 2, 3 })
            Assert.True(list.Columns[index].Width >= TextRenderer.MeasureText(row.SubItems[index].Text, list.Font).Width);
        list.Columns[1].Width = 1100;
        for (int i = 0; i < 10; i++) form.RefreshTasks();
        Assert.Equal(1100, list.Columns[1].Width);
        Assert.Same(row, list.Items[0]);
    });

    [Fact]
    public void ProgressRefreshPreservesSelectedSavePath() => OnSta(() =>
    {
        var task = Snapshot();
        RemoteFileTransferTaskSnapshot[] tasks = [task];
        using var form = Panel(() => tasks);
        form.RefreshTasks();
        form.TasksForTests.Items[0].Selected = true;
        var details = form.DetailsForTests;
        _ = details.Handle;
        details.Select(details.Text.IndexOf(task.Destination!, StringComparison.Ordinal), task.Destination!.Length);
        tasks = [task with { BytesTransferred = 1024, Status = "正在发送：50%（等待实际保存回执）" }];
        form.RefreshTasks();
        Assert.Contains("50%", details.Text);
        Assert.Equal(task.Destination, details.SelectedText);
    });

    [Fact]
    public void ReceiptSelectionDoesNotCarryOverToAnotherTask() => OnSta(() =>
    {
        var first = Snapshot();
        var second = Snapshot() with { DisplayName = "另一个传输任务" };
        using var form = Panel(() => [first, second]);
        form.RefreshTasks();
        form.TasksForTests.Items[1].Selected = false;
        form.TasksForTests.Items[0].Selected = true;
        var details = form.DetailsForTests;
        _ = details.Handle;
        details.Select(details.Text.IndexOf(first.Destination!, StringComparison.Ordinal), first.Destination!.Length);
        form.TasksForTests.Items[0].Selected = false;
        form.TasksForTests.Items[1].Selected = true;
        Assert.Contains(second.DisplayName, details.Text);
        Assert.Equal("", details.SelectedText);
    });

    [Fact]
    public void IdenticalReceiptTextStillClearsSelectionWhenTaskChanges() => OnSta(() =>
    {
        var task = Snapshot();
        using var form = Panel(() => [task]);
        form.RefreshTasks();
        var row = form.TasksForTests.Items[0];
        row.Selected = true;
        var details = form.DetailsForTests;
        _ = details.Handle;
        details.Select(details.Text.IndexOf(task.Destination!, StringComparison.Ordinal), task.Destination!.Length);
        row.Tag = task with { TaskId = Guid.NewGuid() };
        typeof(FileTransferTasksWindow).GetMethod("UpdateSelectedTask", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
        Assert.Contains(task.Destination, details.Text);
        Assert.Equal("", details.SelectedText);
    });

    [Fact]
    public void ChangedDetailsDoNotSelectUnrelatedNewText() => OnSta(() =>
    {
        var task = Snapshot();
        RemoteFileTransferTaskSnapshot[] tasks = [task];
        using var form = Panel(() => tasks);
        form.RefreshTasks();
        form.TasksForTests.Items[0].Selected = true;
        var details = form.DetailsForTests;
        _ = details.Handle;
        details.Select(details.Text.IndexOf(task.CurrentFileName!, StringComparison.Ordinal), task.CurrentFileName!.Length);
        tasks = [task with { CurrentFileName = "other.bin" }];
        form.RefreshTasks();
        Assert.Equal("", details.SelectedText);
    });

    [Fact]
    public void CancellationUsesExactTaskAndConnectionAndDoesNotCancelOnClose() => OnSta(() =>
    {
        var task = Snapshot();
        var calls = new List<(Guid, long)>();
        using var form = Panel(() => [task], (id, generation) => { calls.Add((id, generation)); return Task.FromResult(true); });
        form.RefreshTasks();
        form.TasksForTests.Items[0].Selected = true;
        Complete(form.CancelSelectedTaskAsync());
        Assert.Equal((task.TaskId, task.ConnectionGeneration), Assert.Single(calls));
        form.Dispose();
        Assert.Single(calls);
    });

    [Theory]
    [InlineData((int)RemoteFileTransferTaskState.Completed, true)]
    [InlineData((int)RemoteFileTransferTaskState.Failed, true)]
    [InlineData((int)RemoteFileTransferTaskState.Cancelled, true)]
    [InlineData((int)RemoteFileTransferTaskState.Transferring, false)]
    public void TerminalOrUnsupportedTasksCannotBeCancelled(int state, bool canCancel) => OnSta(() =>
    {
        var task = Snapshot() with { State = (RemoteFileTransferTaskState)state, CanCancel = canCancel,
            CancellationUnavailableReason = "旧版远端不支持安全取消" };
        using var form = Panel(() => [task], (_, _) => throw new InvalidOperationException("must not cancel"));
        form.RefreshTasks();
        form.TasksForTests.Items[0].Selected = true;
        Assert.False(form.CancelForTests.Enabled);
        Assert.Contains("旧版远端", form.DetailsForTests.Text);
        Complete(form.CancelSelectedTaskAsync());
    });

    [Fact]
    public void ClosingDuringCancelDoesNotAccessDisposedControls() => OnSta(() =>
    {
        var task = Snapshot();
        var completion = new TaskCompletionSource<bool>();
        using var form = Panel(() => [task], (_, _) => completion.Task);
        form.RefreshTasks();
        form.TasksForTests.Items[0].Selected = true;
        Task pending = form.CancelSelectedTaskAsync();
        Assert.False(form.CancelForTests.Enabled);
        form.Dispose();
        completion.SetResult(true);
        Complete(pending);
    });

    [Fact]
    public void LateCancelNoticeCannotAppearOnAnotherSelectedTask() => OnSta(() =>
    {
        var one = Snapshot();
        var two = Snapshot();
        var completion = new TaskCompletionSource<bool>();
        using var form = Panel(() => [one, two], (_, _) => completion.Task);
        form.RefreshTasks();
        form.TasksForTests.Items[1].Selected = false;
        form.TasksForTests.Items[0].Selected = true;
        Task pending = form.CancelSelectedTaskAsync();
        form.TasksForTests.Items[0].Selected = false;
        form.TasksForTests.Items[1].Selected = true;
        completion.SetResult(true);
        Complete(pending);
        Assert.DoesNotContain("已请求取消", form.DetailsForTests.Text);
    });

    [Fact]
    public void AllBytesSentStillRequiresReceiptBeforeCompletion()
    {
        var task = Snapshot() with { BytesTransferred = 2048, State = RemoteFileTransferTaskState.AwaitingReceipt };
        Assert.False(task.IsTerminal);
        Assert.Equal("等待保存回执", FileTransferTasksWindow.FormatState(task.State));
        Assert.Contains("2 KB / 2 KB", FileTransferTasksWindow.FormatProgress(task));
    }

    private static RemoteViewerWindow Viewer(RemoteViewerClient client, bool input) =>
        new(client, "Synthetic UI test", input, true, true, true, true, false);
    private static bool InputEnabled(RemoteViewerWindow viewer) =>
        (bool)typeof(RemoteViewerWindow).GetField("_inputEnabled", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewer)!;
    private static RemoteFileTransferTaskSnapshot Snapshot() => new(Guid.NewGuid(), 7,
        RemoteFileTransferDirection.Upload, RemoteFileTransferTaskState.Transferring,
        "发送 2 项文件", "first.txt", 0, 2048, 0, 2, true, "正在发送", Destination: @"D:\接收文件夹\长文件名\first.txt");
    private static FileTransferTasksWindow Panel(Func<IReadOnlyList<RemoteFileTransferTaskSnapshot>> read,
        Func<Guid, long, Task<bool>>? cancel = null)
    {
        var form = new FileTransferTasksWindow(read, cancel ?? ((_, _) => Task.FromResult(false)));
        // Native selection behavior, but no form is shown and no real remote is used.
        _ = form.Handle;
        _ = form.TasksForTests.Handle;
        return form;
    }
    private static void Complete(Task task)
    {
        for (int i = 0; !task.IsCompleted && i < 1000; i++) { Application.DoEvents(); Thread.Sleep(1); }
        Assert.True(task.IsCompleted, "UI completion timed out");
        task.GetAwaiter().GetResult();
    }
    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Isolated UI check timed out");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
