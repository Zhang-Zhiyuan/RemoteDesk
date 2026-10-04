using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemoteDesk;

// Opt-in actual client -> explicitly owned Linux/Xvfb endpoint. No Windows
// desktop input or clipboard APIs: the file clipboard commit is substituted.
internal static class SessionLifecycleProbe
{
    internal static async Task<int> RunAsync(JsonElement config, string output)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        CancellationToken token = deadline.Token;
        string targetPath = config.GetProperty("targetPath").GetString()!;
        string incoming = Path.Combine(output, "incoming");
        Directory.CreateDirectory(incoming);
        var checks = new List<object>();
        int clipboardCommits = 0, frames = 0, width = 0, height = 0;
        string? failure = null;
        using var client = new RemoteViewerClient(true, () => incoming, _ =>
        {
            Interlocked.Increment(ref clipboardCommits);
            return Task.CompletedTask;
        });
        using var log = new StreamWriter(Path.Combine(output, "client.log")) { AutoFlush = true };
        object logLock = new();
        client.Log += message => { lock (logLock) log.WriteLine(message); };
        client.FileTransferStatusReceived += (ok, message) => { lock (logLock) log.WriteLine($"FILE {ok}: {message}"); };
        client.FrameReceived += frame => { width = frame.Width; height = frame.Height; Interlocked.Increment(ref frames); };
        RemoteDeviceDescriptor? descriptor = null;
        client.DeviceInfoReceived += value => descriptor = value;
        async Task Until(Func<bool> condition)
        {
            var watch = Stopwatch.StartNew();
            while (!condition() && watch.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(20, token);
            if (!condition()) throw new TimeoutException("Expected isolated peer state did not arrive");
        }
        void Check(string name, bool passed, object? detail = null)
        {
            checks.Add(new { name, passed, detail });
            Program.Save(Path.Combine(output, "session-audit.json"), new { complete = false, checks });
            Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}");
            if (!passed) throw new InvalidOperationException(name);
        }
        Task Connect() => client.ConnectAsync(config.GetProperty("host").GetString()!, config.GetProperty("port").GetInt32(),
            config.GetProperty("password").GetString()!, ViewerVideoMode.StableJpeg, token);
        JsonElement Target()
        {
            using var document = JsonDocument.Parse(File.ReadAllText(targetPath));
            return document.RootElement.Clone();
        }
        async Task Click(string key)
        {
            var point = Target().GetProperty(key);
            int x = (int)Math.Round(point[0].GetDouble() * (width - 1));
            int y = (int)Math.Round(point[1].GetDouble() * (height - 1));
            await client.SendInputAsync(RemoteInputCommand.MouseDown(RemoteMouseButton.Left, x, y));
            await client.SendInputAsync(RemoteInputCommand.MouseUp(RemoteMouseButton.Left, x, y));
            await client.FlushInputAsync(token);
        }
        try
        {
            await Connect();
            await Until(() => descriptor != null && frames >= 5);
            Check("Real Linux endpoint negotiates capabilities and supplies live frames", descriptor!.Platform == "Linux",
                new { descriptor.Capabilities, width, height, frames });
            int beforeClicks = Target().GetProperty("clicks").GetInt32();
            await Click("button");
            await Until(() => Target().GetProperty("clicks").GetInt32() == beforeClicks + 1);
            await Click("editor");
            string marker = "audit-" + Guid.NewGuid().ToString("N")[..8];
            Check("Text reaches only the owned Xvfb editor", client.SendTextInput(marker).SentCodePoints == marker.Length);
            await Until(() => Target().GetProperty("text").GetString()!.Contains(marker));
            var baseline = Target();
            client.SetLocalInputPaused(true);
            int beforeFrames = frames;
            await Click("button");
            Check("Only-watch blocks IME, keyboard shortcut and remote clipboard writes",
                client.SendTextInput("MUST-NOT-ARRIVE").SentCodePoints == 0 &&
                !client.TryQueueKeyboardChord([RemoteInputCommand.KeyDown(65), RemoteInputCommand.KeyUp(65)], client.InputConnectionGeneration) &&
                !await client.SendClipboardTextToRemoteAsync("MUST-NOT-WRITE"));
            await Until(() => frames > beforeFrames + 5);
            Check("Only-watch preserves live video without changing owned target",
                Target().GetProperty("clicks").GetInt32() == baseline.GetProperty("clicks").GetInt32() &&
                Target().GetProperty("text").GetString() == baseline.GetProperty("text").GetString());

            byte[] bytes = Enumerable.Range(0, 128 * 1024 + 31).Select(i => (byte)(i % 239)).ToArray();
            string source = Path.Combine(output, "session-upload.bin");
            await File.WriteAllBytesAsync(source, bytes, token);
            var upload = await client.SendFilesToRemoteAsync([source], 1).WaitAsync(token);
            var savedTask = client.FileTransferTasks.Last();
            Check("Explicit upload completes while paused with actual saved receipt",
                upload.SentFiles == 1 && upload.FailedFiles == 0 && !upload.Cancelled &&
                savedTask.State == RemoteFileTransferTaskState.Completed && !string.IsNullOrWhiteSpace(savedTask.Destination),
                new { sha256 = Convert.ToHexString(SHA256.HashData(bytes)), savedTask.Destination, savedTask.Status });

            var pull = await client.RequestRemoteClipboardFilesForDragOutAsync(token).WaitAsync(token);
            var pulledTask = client.FileTransferTasks.Last();
            Check("Gesture-approved file and directory pull finishes with truthful unknown batch total",
                pull.Success && pull.LocalPaths.Count == 2 && clipboardCommits == 1 &&
                pulledTask.TotalBytes is null && pulledTask.BytesTransferred > 0,
                new { pull.Outcome, pulledTask.BytesTransferred, pulledTask.TotalBytes, pull.LocalPaths });
            string returnedFile = pull.LocalPaths.Single(path => Path.GetFileName(path) == "return-fixture.txt");
            string returnedZip = pull.LocalPaths.Single(path => path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            using (var archive = ZipFile.OpenRead(returnedZip))
            {
                var nested = archive.Entries.Single(entry => entry.FullName.EndsWith("nested.txt", StringComparison.Ordinal));
                using var reader = new StreamReader(nested.Open(), Encoding.UTF8);
                Check("Returned file bytes and folder ZIP contents verified",
                    await File.ReadAllTextAsync(returnedFile, token) == "RemoteDesk owned return fixture 中文😀\n" &&
                    await reader.ReadToEndAsync(token) == "Owned folder fixture 中文😀\n");
            }

            // A large incompressible source ensures the real peer sees a partial
            // upload before cancellation; progress polling does not block writes.
            string large = Path.Combine(output, "cancel-upload.bin");
            await File.WriteAllBytesAsync(large, RandomNumberGenerator.GetBytes(32 * 1024 * 1024), token);
            Task<RemoteFilePasteResult> sending = client.SendFilesToRemoteAsync([large], 1);
            await Until(() => client.FileTransferTasks.Last() is
                { BytesTransferred: > 0 and < 32 * 1024 * 1024, CanCancel: true, State: RemoteFileTransferTaskState.Transferring });
            var active = client.FileTransferTasks.Last();
            Check("Mid-stream cancel targets exact task and generation", await client.CancelFileTransferAsync(active.TaskId, active.ConnectionGeneration),
                new { active.BytesTransferred, active.TotalBytes });
            var cancelled = await sending.WaitAsync(token);
            Check("Upload cancellation preserves desktop and records cancelled task", cancelled.Cancelled && client.IsConnected &&
                client.FileTransferTasks.Last().State == RemoteFileTransferTaskState.Cancelled);
            var afterCancel = await client.SendFilesToRemoteAsync([source], 1).WaitAsync(token);
            Check("Next actual upload receives saved receipt after cancellation", afterCancel.SentFiles == 1 && afterCancel.FailedFiles == 0);

            Task<bool>? pullCancel = null;
            client.ConfirmRemoteClipboardFileTransfer = (_, _) =>
            {
                var pending = client.FileTransferTasks.Last();
                pullCancel = client.CancelFileTransferAsync(pending.TaskId, pending.ConnectionGeneration);
                return true; // A delayed positive dialog result cannot undo cancellation.
            };
            await client.RequestRemoteClipboardFilesAsync().WaitAsync(token);
            await Until(() => client.FileTransferTasks.Last().IsTerminal);
            Check("Cancel during real pull preflight fences late confirmation", pullCancel != null && await pullCancel &&
                client.FileTransferTasks.Last().State == RemoteFileTransferTaskState.Cancelled);
            await Until(() => !client.IsRemoteClipboardFileRequestPending);
            beforeFrames = frames;
            await Until(() => frames >= beforeFrames + 3);
            Check("Cancelled pull drains cleanly while live video continues", client.IsConnected && clipboardCommits == 1);

            long oldGeneration = client.InputConnectionGeneration;
            await client.DisconnectAsync();
            beforeFrames = frames;
            await Connect();
            await Until(() => frames >= beforeFrames + 3);
            Check("Reconnect retains only-watch and rejects old task cancellation", client.IsLocalInputPaused &&
                client.InputConnectionGeneration != oldGeneration && !await client.CancelFileTransferAsync(active.TaskId, active.ConnectionGeneration));
            client.SetLocalInputPaused(false);
            beforeClicks = Target().GetProperty("clicks").GetInt32();
            await Click("button");
            await Until(() => Target().GetProperty("clicks").GetInt32() == beforeClicks + 1);
            Check("Explicit resume restores real owned-target control", true);
        }
        catch (Exception error) { failure = error.ToString(); }
        finally
        {
            await client.DisconnectAsync();
            Program.Save(Path.Combine(output, "session-audit.json"), new { complete = failure is null, failure, checks, frames,
                clipboardCommits, tasks = client.FileTransferTasks,
                scope = "Actual Windows client -> owned Linux Xvfb; substituted Windows clipboard; no installed instance or production session" });
        }
        return failure is null ? 0 : 1;
    }
}
