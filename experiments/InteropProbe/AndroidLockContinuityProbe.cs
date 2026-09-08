using System.Diagnostics;
using System.Text.Json;
using RemoteDesk;

internal static class AndroidLockContinuityProbe
{
    // No incidental workstation input is forwarded. The caller may lock the
    // owned phone during this bounded connection; credentials arrive on stdin.
    internal static async Task<int> RunAsync(JsonElement config, string output)
    {
        using var client = new RemoteViewerClient();
        using var log = new StreamWriter(Path.Combine(output, "client.log")) { AutoFlush = true };
        client.Log += line => { lock (log) log.WriteLine(line); };
        long frames = 0;
        long bytes = 0;
        var clock = Stopwatch.StartNew();
        double firstMs = -1;
        int width = 0, height = 0;
        var stamps = new List<double>();
        client.FrameReceived += frame => {
            Interlocked.Increment(ref frames);
            Interlocked.Add(ref bytes, frame.EncodedLength);
            width = frame.Width;
            height = frame.Height;
            if (firstMs < 0) firstMs = clock.Elapsed.TotalMilliseconds;
            lock (stamps) stamps.Add(clock.Elapsed.TotalMilliseconds);
            if (frame.Encoding == RemoteFrameEncoding.Jpeg && frames % 10 == 1)
                File.WriteAllBytes(Path.Combine(output, "latest.private.jpg"), frame.EncodedBuffer.AsSpan(frame.EncodedOffset, frame.EncodedLength).ToArray());
        };
        try {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await client.ConnectAsync(config.GetProperty("host").GetString()!, config.GetProperty("port").GetInt32(),
                config.GetProperty("password").GetString()!, ViewerVideoMode.Automatic, timeout.Token);
            double seconds = config.TryGetProperty("seconds", out var duration) ? Math.Clamp(duration.GetDouble(), 5, 120) : 25;
            double wakeAt = config.TryGetProperty("wakeAtSeconds", out var wake) ? wake.GetDouble() : -1;
            bool wakeSent = false;
            while (clock.Elapsed.TotalSeconds < seconds && client.IsConnected) {
                if (!wakeSent && wakeAt >= 0 && clock.Elapsed.TotalSeconds >= wakeAt) {
                    // A non-clicking mouse move wakes a locked owned phone; the
                    // host must discard it while normal unlock is in progress.
                    await client.SendInputAsync(new(RemoteInputKind.MouseMove, RemoteMouseButton.None, 0, 0, 0));
                    wakeSent = true;
                }
                Program.Save(Path.Combine(output, "progress.json"), new { connected = client.IsConnected, frames, width, height });
                await Task.Delay(500);
            }
            if (config.TryGetProperty("text", out var text)) {
                // Use only when AndroidLockProbe's synthetic editor is focused.
                client.SendTextInput(text.GetString()!);
                await Task.Delay(1500);
            }
            double[] samples;
            lock (stamps) samples = stamps.ToArray();
            bool complete = client.IsConnected && frames >= 10;
            Program.Save(Path.Combine(output, "result.json"), new { complete, wakeSent, frames, bytes, firstMs, width, height, samples });
            return complete ? 0 : 1;
        } catch (Exception ex) {
            File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString());
            return 1;
        } finally { await client.DisconnectAsync(); }
    }
}
