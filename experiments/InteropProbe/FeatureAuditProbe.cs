using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemoteDesk;

// Actual product client against an explicitly owned Linux/Xvfb host. Never
// inject desktop input, read the local clipboard, or install/update anything.
internal static class FeatureAuditProbe
{
    internal static async Task<int> RunAsync(JsonElement config, string output)
    {
        var checks = new List<object>();
        string? failure = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var token = deadline.Token;
        string incoming = Path.Combine(output, "incoming");
        Directory.CreateDirectory(incoming);
        int isolatedClipboardCalls = 0;
        using var client = new RemoteViewerClient(() => incoming, _ => {
            Interlocked.Increment(ref isolatedClipboardCalls);
            return Task.CompletedTask;
        });
        var returnedBytes = Encoding.UTF8.GetBytes("RemoteDesk owned return fixture 中文😀\n");
        client.ConfirmRemoteClipboardFileTransfer = (items, _) => items.Count == 1 &&
            items[0].TransferName == "return-fixture.txt" && items[0].SizeBytes == returnedBytes.Length;
        using var log = new StreamWriter(Path.Combine(output, "client.log")) { AutoFlush = true };
        object logLock = new();
        client.Log += text => { lock (logLock) log.WriteLine(text); };
        client.FileTransferStatusReceived += (ok, text) => { lock (logLock) log.WriteLine($"FILE {ok}: {text}"); };
        int frames = 0, jpeg = 0, h264 = 0, targetChanges = 0;
        CaptureTargetInfo[] targets = [];
        RemoteDeviceDescriptor? descriptor = null;
        client.FrameReceived += frame => {
            Interlocked.Increment(ref frames);
            if (frame.Encoding == RemoteFrameEncoding.H264AnnexB) Interlocked.Increment(ref h264);
            else Interlocked.Increment(ref jpeg);
        };
        client.DeviceInfoReceived += info => descriptor = info;
        client.CaptureTargetsReceived += value => targets = value.ToArray();
        client.CaptureTargetChanged += _ => Interlocked.Increment(ref targetChanges);
        void Check(string name, bool passed, object? details = null)
        {
            checks.Add(new { name, passed, details });
            Program.Save(Path.Combine(output, "features.json"), new { complete = false, checks });
            Console.WriteLine((passed ? "PASS " : "FAIL ") + name);
            if (!passed) throw new InvalidOperationException(name);
        }
        async Task Until(Func<bool> condition)
        {
            var watch = Stopwatch.StartNew();
            while (!condition() && watch.Elapsed.TotalSeconds < 35) await Task.Delay(100, token);
            if (!condition()) throw new TimeoutException("Expected peer state did not arrive");
        }
        RelayConnectionOptions? relay = Program.RelayOptions(config);
        Task Connect(RemoteViewerClient target, string password) => relay is null
            ? target.ConnectAsync(config.GetProperty("host").GetString()!, config.GetProperty("port").GetInt32(),
                password, ViewerVideoMode.StableJpeg, token)
            : target.ConnectViaRelayAsync(relay, password, ViewerVideoMode.StableJpeg, token);
        string password = config.GetProperty("password").GetString()!;
        try
        {
            bool rejected = false;
            try { await Connect(client, password + "-invalid"); }
            catch (UnauthorizedAccessException) { rejected = true; }
            Check("Wrong password rejected without opening a session", rejected && !client.IsConnected);
            await Connect(client, password);
            await Until(() => descriptor != null && jpeg >= 5 && targets.Length > 0);
            Check("Actual Linux capability negotiation and continuous JPEG frames", descriptor!.Platform == "Linux",
                new { capabilities = descriptor.Capabilities.ToString(), frames, jpeg, targets });
            int before = targetChanges, beforeFrames = frames;
            await client.SelectCaptureTargetAsync(targets[0].Id);
            await Until(() => targetChanges > before && frames > beforeFrames + 3);
            Check("Capture target selection acknowledged and video continues", true);
            Check("Send known Chinese/emoji clipboard text to owned Xvfb only",
                await client.SendClipboardTextToRemoteAsync("RemoteDesk clipboard 中文😀"));
            var bytes = Enumerable.Range(0, 128 * 1024 + 31).Select(i => (byte)(i % 239)).ToArray();
            var upload = Path.Combine(output, "upload-中文.bin");
            await File.WriteAllBytesAsync(upload, bytes, token);
            await client.SendFileToRemoteAsync(upload).WaitAsync(token);
            await client.SendFileToRemoteAsync(upload).WaitAsync(token);
            var empty = Path.Combine(output, "empty.txt");
            await File.WriteAllBytesAsync(empty, [], token);
            await client.SendFileToRemoteAsync(empty).WaitAsync(token);
            var directory = Path.Combine(output, "directory-fixture");
            Directory.CreateDirectory(Path.Combine(directory, "nested"));
            string archivedSource = Path.Combine(directory, "nested", "中文.txt");
            await File.WriteAllTextAsync(archivedSource, "nested 中文😀", new UTF8Encoding(false), token);
            File.SetLastWriteTimeUtc(archivedSource, DateTime.UnixEpoch);
            await client.SendFileToRemoteAsync(directory).WaitAsync(token);
            Check("Pre-1980 directory file transfers without changing the source timestamp",
                File.GetLastWriteTimeUtc(archivedSource) == DateTime.UnixEpoch);
            Check("Files, duplicate name, empty file and directory sent (remote hashes checked separately)", true,
                new { bytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(bytes)) });
            var returned = await client.RequestRemoteClipboardFilesForDragOutAsync(token);
            Check("Gesture-confirmed returned file verified using isolated clipboard callback",
                returned.Success && returned.LocalPaths.Count == 1 && isolatedClipboardCalls == 1 &&
                (await File.ReadAllBytesAsync(returned.LocalPaths[0], token)).AsSpan().SequenceEqual(returnedBytes),
                new { returned.Outcome, isolatedClipboardCalls, actualWindowsClipboardTouched = false });
            await client.UpdateViewerVideoCodecsAsync(RemoteVideoCodecs.Jpeg | RemoteVideoCodecs.H264AnnexB);
            await client.RequestVideoKeyFrameAsync();
            await Until(() => h264 >= 5);
            Check("JPEG to H264 negotiation and keyframe request", true, new { h264 });
            int beforeJpeg = jpeg;
            await client.UpdateViewerVideoCodecsAsync(RemoteVideoCodecs.Jpeg);
            await Until(() => jpeg >= beforeJpeg + 5);
            Check("H264 to JPEG returns live frames", true);
            using (var replacement = new RemoteViewerClient(() => incoming, _ => Task.CompletedTask))
            {
                int replacementFrames = 0;
                replacement.FrameReceived += _ => Interlocked.Increment(ref replacementFrames);
                await Connect(replacement, password);
                await Until(() => !client.IsConnected && replacementFrames >= 5);
                Check("New viewer replaces old viewer and receives frames", true);
                await replacement.DisconnectAsync();
            }
            await client.DisconnectAsync();
            for (int i = 0; i < 2; i++)
            {
                int receivedBefore = frames;
                await Connect(client, password);
                await Until(() => frames >= receivedBefore + 5);
                await client.DisconnectAsync();
            }
            Check("Same client reconnects twice with fresh frames", true);
        }
        catch (Exception error) { failure = error.GetType().Name + ": " + error.Message; }
        finally
        {
            await client.DisconnectAsync();
            Program.Save(Path.Combine(output, "features.json"), new {
                complete = failure == null, failure, checks, frames, jpeg, h264,
                scope = "Actual Windows product client to owned physical Linux Xvfb; no Windows capture/input or local clipboard mutation",
                route = relay is null ? "direct LAN" : "native public relay TLS/TCP; no LAN fallback"
            });
        }
        return failure == null ? 0 : 1;
    }
}
