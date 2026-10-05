using System.Reflection;
using RemoteDesk;

// Called only inside SshUiProbe's owned private station/desktop STA.
internal static class SessionToolsInteractionProbe
{
    internal static void Verify(string output)
    {
        var cases = new List<object>();
        var failures = new List<string>();
        foreach (float points in new[] { 9f, 18f })
        foreach (int width in new[] { 440, 720 })
        {
            var original = Snapshot();
            RemoteFileTransferTaskSnapshot[] tasks = [original];
            var calls = new List<(Guid TaskId, long Generation)>();
            using var form = new FileTransferTasksWindow(() => tasks, (id, generation) =>
            { calls.Add((id, generation)); return Task.FromResult(true); });
            using var font = new Font("Microsoft YaHei UI", points);
            form.Font = font;
            form.Show(); SyntheticDpi.Pump();
            foreach (int dpi in new[] { 96, 120, 144, 192, 96 })
            {
                object transition = SyntheticDpi.Apply(form, dpi);
                form.MinimumSize = Size.Empty;
                form.ClientSize = new Size(ResponsiveWindowLayout.ScaleLogical(width, dpi), ResponsiveWindowLayout.ScaleLogical(500, dpi));
                SyntheticDpi.Pump();
                tasks = [original]; form.RefreshTasks();
                var row = form.TasksForTests.Items[0];
                row.Selected = true; row.Focused = true;
                form.CancelForTests.PerformClick(); SyntheticDpi.Pump();
                var details = form.DetailsForTests;
                // Keep the selected range visible in the fixture screenshot
                // even though the private station is not the input desktop.
                details.HideSelection = false;
                details.Focus();
                details.Select(details.Text.IndexOf(original.Destination!, StringComparison.Ordinal), original.Destination!.Length);
                details.ScrollToCaret();
                bool detailsFocusedBefore = details.Focused;
                string tag = $"tasks-font{points}-width{width}-dpi{dpi}-{cases.Count}";
                Save(form, tag + "-before");
                tasks = [original with { BytesTransferred = 1024 }, Snapshot()];
                form.RefreshTasks(); SyntheticDpi.Pump();
                var issues = new List<string>();
                Check(details.SelectedText == original.Destination, "Appending another task erased the selected receipt path.");
                Check(ReferenceEquals(form.TasksForTests.FocusedItem, row), "Appending another task lost the keyboard-focused task.");
                Check(details.Text.Contains("已请求取消", StringComparison.Ordinal), "Appending another task erased the current cancellation notice.");
                Check(details.Focused == detailsFocusedBefore, "Appending another task changed receipt control focus.");
                Check(calls.LastOrDefault() == (original.TaskId, original.ConnectionGeneration), "Cancel did not use exact task and generation.");
                string receiptSelectionAfterAppend = details.SelectedText;
                bool cancellationNoticeAfterAppend = details.Text.Contains("已请求取消", StringComparison.Ordinal);
                details.ScrollToCaret(); Save(form, tag + "-appended");
                // Task history eviction must clear the old selection even when receipt text is identical.
                tasks = [original with { TaskId = Guid.NewGuid() }];
                form.RefreshTasks(); SyntheticDpi.Pump();
                Check(details.SelectedText.Length == 0, "Removed task selection leaked to replacement.");
                Check(!details.Text.Contains("已请求取消", StringComparison.Ordinal), "Removed task cancellation notice leaked to replacement.");
                var viewport = (ScrollableControl)form.Controls[0];
                foreach (Button button in SyntheticDpi.Descendants(form).OfType<Button>())
                {
                    viewport.ScrollControlIntoView(button); SyntheticDpi.Pump();
                    Check(button.Visible && viewport.RectangleToScreen(viewport.ClientRectangle).Contains(button.RectangleToScreen(button.ClientRectangle)), "Task action is unreachable: " + button.Text);
                }
                cases.Add(new { tag, transition, detailsFocusedBefore, detailsFocusedAfter = details.Focused,
                    receiptSelectionAfterAppend, cancellationNoticeAfterAppend, issues });
                failures.AddRange(issues.Select(issue => tag + ": " + issue));
                Save(form, tag + "-after");
                void Check(bool passed, string error) { if (!passed) issues.Add(error); }
            }
            int beforeClose = calls.Count;
            form.Close(); SyntheticDpi.Pump();
            Record("Closing panel continues transfers", calls.Count == beforeClose);
        }

        // A pending cancel captures one task/generation, is not reissued, and
        // can complete after the panel closes without touching disposed UI.
        var pendingTask = Snapshot();
        var completion = new TaskCompletionSource<bool>();
        int pendingCalls = 0;
        using (var panel = new FileTransferTasksWindow(() => [pendingTask], (_, _) => { pendingCalls++; return completion.Task; }))
        {
            panel.Show(); SyntheticDpi.Pump();
            Task pending = panel.CancelSelectedTaskAsync();
            panel.CancelForTests.PerformClick();
            Record("Pending cancel disables repeat action", pendingCalls == 1 && !panel.CancelForTests.Enabled);
            panel.Close(); SyntheticDpi.Pump();
            completion.SetResult(true);
            for (int i = 0; i < 200 && !pending.IsCompleted; i++) { Application.DoEvents(); Thread.Sleep(5); }
            if (!pending.IsCompleted) throw new TimeoutException("Owned pending cancellation did not complete.");
            pending.GetAwaiter().GetResult();
            Record("Pending cancellation completes after close without duplicate requests", pending.IsCompletedSuccessfully && pendingCalls == 1);
        }

        using (var client = new RemoteViewerClient())
        using (var first = new RemoteViewerWindow(client, "Owned screen 1", true, true, true, true, true, false))
        using (var second = new RemoteViewerWindow(client, "Owned screen 2", true, true, true, true, true, false))
        {
            first.Show(); second.Show(); SyntheticDpi.Pump();
            first.InputPauseButtonForTests.PerformClick(); SyntheticDpi.Pump();
            Record("Only-watch button pauses both owned viewers", client.IsLocalInputPaused && !Input(first) && !Input(second));
            first.SetInputEnabled(false); first.SetInputEnabled(true); SyntheticDpi.Pump();
            Record("Capability refresh cannot undo only-watch", !Input(first) && !Input(second));
            first.WindowState = FormWindowState.Minimized; SyntheticDpi.Pump();
            Record("Minimizing one viewer keeps sibling visible", second.Visible && second.WindowState == FormWindowState.Normal);
            first.WindowState = FormWindowState.Normal;
            second.SetInputEnabled(false);
            first.InputPauseButtonForTests.PerformClick(); SyntheticDpi.Pump();
            Record("Resuming respects each viewer remote capability", !client.IsLocalInputPaused && Input(first) && !Input(second));
            Save(first, "only-watch-first"); Save(second, "only-watch-second");
            first.Close(); second.Close(); SyntheticDpi.Pump();
        }
        Program.Save(Path.Combine(output, "session-tools-ui.json"), new
        {
            passed = failures.Count == 0, cases, failures,
            scope = "Actual shown WinForms and native list/text HWNDs in owned private station; synthetic managed DPI transitions 96/120/144/192 with native DPI separately recorded. No sockets, real remote input, real clipboard, installed GUI or user settings."
        });
        if (failures.Count != 0) throw new InvalidOperationException($"Session tools UI: {failures.Count} findings; see session-tools-ui.json");

        void Record(string name, bool passed)
        {
            cases.Add(new { tag = name, passed });
            if (!passed) failures.Add(name);
        }
        void Save(Form form, string tag)
        {
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(Path.Combine(output, tag + ".png"));
        }
    }

    private static bool Input(RemoteViewerWindow viewer) =>
        (bool)typeof(RemoteViewerWindow).GetField("_inputEnabled", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(viewer)!;
    private static RemoteFileTransferTaskSnapshot Snapshot() => new(Guid.NewGuid(), 7,
        RemoteFileTransferDirection.Upload, RemoteFileTransferTaskState.Transferring,
        "发送：自有隔离文件批次 · 不连接生产设备", "actual-receipt.txt", 0, 2048, 0, 2, true, "正在发送（等待实际回执）",
        Destination: @"D:\接收文件夹\自有隔离任务\actual-receipt.txt");
}
