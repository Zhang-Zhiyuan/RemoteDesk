using System.Text.Json;
using RemoteDesk;

// Operator-stepped inspection of RemoteDesk's own diagnostic directory only.
// No shell commands, arbitrary text, local clipboard writes, or session takeover.
internal static class RelayDiagnosticUiProbe
{
    internal static async Task<int> RunAsync(JsonElement config, string output)
    {
        var settings = new AppSettingsService().Load();
        var saved = settings.Relay;
        if (string.IsNullOrWhiteSpace(saved.ServerAddress) || saved.ServerAddress != config.GetProperty("expectedServer").GetString() ||
            string.IsNullOrEmpty(saved.SshHostKeySha256)) throw new InvalidOperationException("Unexpected relay identity.");
        string id = Guid.Parse(config.GetProperty("deviceId").GetString()!).ToString("D");
        var options = new RelayConnectionOptions(saved.ServerAddress, saved.RelayPort,
            AppSettingsService.UnprotectSecret(saved.ProtectedAccessToken), saved.TlsCertificateSha256!, id).Validate();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var device = (await RelayTunnelClient.ListDevicesAsync(options, deadline.Token)).Single(x => x.DeviceId == id);
        if (device.Busy || device.Platform != RemoteDevicePlatforms.Windows) throw new InvalidOperationException("Windows device must be idle; no takeover.");
        string incoming = Path.Combine(output, "logs");
        Directory.CreateDirectory(incoming);
        using var client = new RemoteViewerClient(() => incoming, _ => Task.CompletedTask);
        client.ConfirmRemoteClipboardFileTransfer = (items, _) => items.Count is > 0 and <= 2 && items.All(x =>
            x.TransferName is "remotedesk-windows-current.log" or "remotedesk-windows-current.log.old" && x.SizeBytes < 1024 * 1024);
        client.FileTransferStatusReceived += (ok, message) => Console.WriteLine($"FILE {ok}: {message}");
        client.ClipboardStatusReceived += message => Console.WriteLine("STATUS " + message);
        client.DeviceInfoReceived += info => Console.WriteLine("DEVICE " + info.Platform + " " + info.Capabilities);
        client.Log += message => Console.WriteLine(message);
        object frameLock = new();
        byte[]? jpeg = null;
        int width = 0, height = 0;
        long frameSequence = 0;
        client.HostVideoDiagnosticsReceived += message =>
            Program.Save(Path.Combine(output, "video-diagnostics.json"), new { receivedAtUtc = DateTimeOffset.UtcNow, message });
        client.FrameReceived += frame =>
        {
            if (frame.Encoding != RemoteFrameEncoding.Jpeg) return;
            lock (frameLock)
            {
                jpeg = frame.EncodedBuffer.AsSpan(frame.EncodedOffset, frame.EncodedLength).ToArray();
                width = frame.Width;
                height = frame.Height;
                frameSequence++;
            }
        };
        async Task Chord(params int[] keys)
        {
            try { foreach (int key in keys) await client.SendInputAsync(RemoteInputCommand.KeyDown(key)); }
            finally { foreach (int key in keys.Reverse()) await client.SendInputAsync(RemoteInputCommand.KeyUp(key)); }
        }
        try
        {
            await client.ConnectViaRelayAsync(options, config.GetProperty("deviceKey").GetString()!, ViewerVideoMode.StableJpeg, deadline.Token);
            await Task.Delay(2500, deadline.Token);
            int previousCommand = 0;
            string commandPath = Path.Combine(output, "command.json");
            Console.WriteLine("READY: operator commands from " + commandPath);
            while (!deadline.IsCancellationRequested && client.IsConnected)
            {
                await Task.Delay(200, deadline.Token);
                if (!File.Exists(commandPath)) continue;
                JsonDocument command;
                try { command = JsonDocument.Parse(await File.ReadAllTextAsync(commandPath, deadline.Token)); }
                catch (JsonException) { continue; }
                using (command)
                {
                    var c = command.RootElement;
                    int number = c.GetProperty("id").GetInt32();
                    if (number <= previousCommand) continue;
                    previousCommand = number;
                    string operation = c.GetProperty("op").GetString()!;
                    if (operation == "exit") break;
                    long beforeSequence;
                    lock (frameLock) beforeSequence = frameSequence;
                    if (operation == "winr") await Chord(0x5b, 0x52);
                    else if (operation == "escape") await Chord(0x1b);
                    else if (operation == "video-diagnostics") await client.RequestHostVideoDiagnosticsAsync();
                    else if (operation == "diagnostics-folder")
                    {
                        client.SendTextInput("%APPDATA%\\RemoteDesk\\Diagnostics");
                        await Task.Delay(800, deadline.Token);
                        await Chord(0x0d);
                    }
                    else if (operation == "click")
                    {
                        double xf = c.GetProperty("xFraction").GetDouble(), yf = c.GetProperty("yFraction").GetDouble();
                        if (!double.IsFinite(xf) || !double.IsFinite(yf) || xf < 0 || xf >= 1 || yf < 0 || yf >= 1)
                            throw new ArgumentOutOfRangeException("Observed normalized coordinates required.");
                        int x = (int)(xf * width), y = (int)(yf * height);
                        if (x < 0 || y < 0 || x >= width || y >= height) throw new ArgumentOutOfRangeException("Observed screen coordinates required.");
                        await client.SendInputAsync(RemoteInputCommand.MouseMove(x, y));
                        await client.SendInputAsync(RemoteInputCommand.MouseDown(RemoteMouseButton.Left, x, y));
                        await client.SendInputAsync(RemoteInputCommand.MouseUp(RemoteMouseButton.Left, x, y));
                    }
                    else if (operation == "copy") await Chord(0xa2, 0x43);
                    else if (operation == "request-log-files") await client.RequestRemoteClipboardFilesAsync();
                    else if (operation != "snapshot") throw new InvalidOperationException("Unsupported diagnostic operation.");
                    await Task.Delay(1500, deadline.Token);
                    var freshDeadline = DateTimeOffset.UtcNow.AddSeconds(15);
                    while (client.IsConnected && DateTimeOffset.UtcNow < freshDeadline)
                    {
                        lock (frameLock) if (frameSequence > beforeSequence) break;
                        await Task.Delay(200, deadline.Token);
                    }
                    byte[] snapshot;
                    long observedSequence;
                    lock (frameLock)
                    {
                        snapshot = jpeg ?? throw new InvalidOperationException("No JPEG frame available.");
                        observedSequence = frameSequence;
                    }
                    string path = Path.Combine(output, $"step-{number}.jpg");
                    await File.WriteAllBytesAsync(path, snapshot, deadline.Token);
                    Program.Save(Path.Combine(output, "completed.json"), new { number, operation, path, width, height, observedSequence,
                        freshFrameReceived = observedSequence > beforeSequence });
                    Console.WriteLine($"DONE {number}: {operation}, {width}x{height}, fresh={observedSequence > beforeSequence}, {path}");
                }
            }
            return 0;
        }
        finally { await client.DisconnectAsync(); }
    }
}
