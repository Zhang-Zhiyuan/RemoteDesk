using System.Reflection;
using System.Runtime.InteropServices;
using RemoteDesk;

// Only the owned viewer's confirmation dialog is operated. Explicit generated
// paths enter the product file-paste flow after clipboard reading, so the
// interactive user's clipboard is neither read nor changed.
internal static class FileClipboardUiProbe
{
    internal static Task<IReadOnlyList<object>> RunAsync(RemoteViewerClient client, string source, string received,
        nint privateDesktop = default, TimeSpan? operationTimeout = null)
    {
        // A real public relay can take longer than the loopback fixture's
        // twenty seconds to transfer one MiB. Bound the test separately from
        // the product's own transfer/receipt deadlines; never cancel an active
        // upload merely because a LAN-only fixture budget was reused for WAN.
        TimeSpan scenarioTimeout = operationTimeout ?? TimeSpan.FromSeconds(20);
        if (scenarioTimeout <= TimeSpan.Zero || scenarioTimeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        var done = new TaskCompletionSource<IReadOnlyList<object>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            FieldInfo? interactiveCache = null;
            try
            {
                if (privateDesktop != 0)
                {
                    if (!SetThreadDesktop(privateDesktop)) throw new System.ComponentModel.Win32Exception();
                    // Test harness only: WinForms disallows ShowDialog on any
                    // non-visible station even with a real private message loop.
                    // Permit that loop without making the station interactive,
                    // switching WinSta0 or changing OS permissions/clipboard.
                    if (SystemInformation.UserInteractive) throw new InvalidOperationException("Expected an isolated non-visible station.");
                    interactiveCache = typeof(SystemInformation).GetField("s_isUserInteractive", BindingFlags.Static | BindingFlags.NonPublic)
                        ?? throw new MissingFieldException("WinForms private-station probe no longer supported.");
                    interactiveCache.SetValue(null, true);
                }
                else if (GetForegroundWindow() == 0) throw new InvalidOperationException("Interactive desktop unavailable for confirmation test");
                using var window = new RemoteViewerWindow(client, "Isolated file clipboard fixture", true, true, true, false, false, false)
                    { ShowInTaskbar = false };
                window.Shown += async (_, _) =>
                {
                    try { done.TrySetResult(await CheckAsync(window, source, received, scenarioTimeout)); }
                    catch (Exception error) { done.TrySetException(error); }
                    finally { window.Close(); }
                };
                Application.Run(window);
            }
            catch (Exception error) { done.TrySetException(error); }
            finally { interactiveCache?.SetValue(null, false); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task.WaitAsync(scenarioTimeout + scenarioTimeout + TimeSpan.FromSeconds(5));
    }

    private static async Task<IReadOnlyList<object>> CheckAsync(RemoteViewerWindow window, string source, string received,
        TimeSpan operationTimeout)
    {
        Invoke(window, "UninstallSystemKeyboardCapture");
        var picture = window.Controls.OfType<PictureBox>().Single();
        var results = new List<object>();
        foreach (bool accept in new[] { false, true })
        {
            string[] before = Directory.GetFiles(received);
            bool sawConfirmation = false, transferredBeforeConfirmation = false, exactPreview = false, exactResult = false;
            using var timer = new System.Windows.Forms.Timer { Interval = 25 };
            timer.Tick += (_, _) =>
            {
                var dialog = Application.OpenForms.OfType<FileTransferConfirmationDialog>()
                    .SingleOrDefault(form => form.Owner == window);
                if (dialog is null) {
                    var completed = Application.OpenForms.OfType<FileTransferResultDialog>().SingleOrDefault(form => form.Owner == window);
                    if (completed is null) return;
                    string detail = Descendants(completed).OfType<TextBox>()
                        .Single(control => control.AccessibleName == "实际保存位置和传输结果").Text;
                    var actualFiles = Directory.GetFiles(received).Except(before).ToArray();
                    exactResult = actualFiles.Length == 1 && detail.Contains(actualFiles[0], StringComparison.Ordinal);
                    SaveDialog(completed, Path.Combine(Path.GetDirectoryName(received)!, "result-dialog.png"));
                    timer.Stop();
                    completed.AcceptButton!.PerformClick();
                    return;
                }
                sawConfirmation = true;
                transferredBeforeConfirmation |= Directory.GetFiles(received).Length != before.Length;
                var grid = Descendants(dialog).OfType<DataGridView>().Single();
                exactPreview = grid.Rows.Cast<DataGridViewRow>().Any(row => row.DataBoundItem is FileTransferConfirmationItem item &&
                    item.DestinationPath.Contains(Path.Combine(received, Path.GetFileName(source)), StringComparison.Ordinal));
                SaveDialog(dialog, Path.Combine(Path.GetDirectoryName(received)!, accept ? "confirm-send.png" : "confirm-cancel.png"));
                if (!accept) timer.Stop();
                (accept ? dialog.AcceptButton : dialog.CancelButton)!.PerformClick();
            };
            timer.Start();
            picture.Focus();
            using var deadline = new CancellationTokenSource(operationTimeout);
            await ((Task)Invoke(window, "PasteClipboardFilesToRemoteAsync", (object)new[] { source })!).WaitAsync(deadline.Token);
            string[] added = Directory.GetFiles(received).Except(before).ToArray();
            bool passed = sawConfirmation && exactPreview && !transferredBeforeConfirmation && (accept
                ? exactResult && added.Length == 1 && File.ReadAllBytes(added[0]).SequenceEqual(File.ReadAllBytes(source))
                : added.Length == 0);
            results.Add(new { name = accept ? "relay file-paste confirmation saves exact bytes" : "cancelled file-paste confirmation sends nothing",
                passed, sawConfirmation, transferredBeforeConfirmation, exactPreview, exactResult });
            if (!passed)
            {
                object bar = window.GetType().GetField("_statusBar", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                object? status = bar.GetType().GetProperty("StatusText")?.GetValue(bar);
                throw new InvalidOperationException($"File clipboard confirmation failed (accept={accept}, sawDialog={sawConfirmation}, early={transferredBeforeConfirmation}, added={added.Length}, status={status})");
            }
        }
        return results;
    }

    private static object? Invoke(object instance, string name, params object[] args) =>
        instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args);
    // Dialogs may wrap their content in a scrolling viewport. Find the owned
    // semantic control rather than assuming its layout container is a Form child.
    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child)) yield return nested;
        }
    }
    private static void SaveDialog(Form dialog, string path)
    {
        using var bitmap = new Bitmap(dialog.Width, dialog.Height);
        dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, dialog.Size));
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetThreadDesktop(nint desktop);
}
