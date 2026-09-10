using System.Diagnostics;
using System.Text.Json;
using RemoteDesk;

internal static class WindowsSecureDesktopProbe
{
    // Explicit opt-in real lock-screen probe. Sign-in is permitted only for
    // the user's confirmed password-free account. Never guesses or enters an
    // OS credential, sends SAS, or changes the computer's locking policy.
    internal static int Run(JsonElement config, string output)
    {
        try
        {
            using var helper = new WindowsSecureDesktopClient();
            var before = helper.QueryStatus();
            if (before.Status != "active") throw new InvalidOperationException("Lock-screen helper is not active.");
            if (config.TryGetProperty("network", out var network) && network.GetBoolean())
                return RunNetworkAsync(helper, before, output,
                    config.TryGetProperty("dismissLockScreen", out var dismiss) && dismiss.GetBoolean(),
                    config.TryGetProperty("signInButton", out var signIn) && signIn.GetBoolean(),
                    config.TryGetProperty("signInMethod", out var method) ? method.GetString()! : "enter",
                    config.TryGetProperty("holdSeconds", out var hold) ? hold.GetInt32() : 0,
                    config.TryGetProperty("installedHost", out var installed) && installed.GetBoolean(),
                    config.TryGetProperty("leaveUnlocked", out var leave) && leave.GetBoolean()).GetAwaiter().GetResult();
            ScreenCaptureTarget target = ScreenCaptureService.GetDefaultTarget();
            using var capture = new ScreenCaptureService(target);
            var timings = new List<double>();
            ScreenCaptureResult frame = default;
            for (int i = 0; i < 10; i++)
            {
                var timer = Stopwatch.StartNew();
                frame = capture.CaptureJpeg(90, 100);
                timings.Add(timer.Elapsed.TotalMilliseconds);
                Thread.Sleep(100);
            }
            File.WriteAllBytes(Path.Combine(output, "login.jpg"), frame.JpegBytes.ToArray());
            if (config.TryGetProperty("clickSignIn", out var clickSignIn) && clickSignIn.GetBoolean())
            {
                // This opt-in is restricted to the inspected password-free button.
                using var signInInput = new InputInjectionDispatcher();
                int x = frame.FrameSize.Width / 2, y = (int)(frame.FrameSize.Height * 0.529);
                signInInput.Apply(new(RemoteInputKind.MouseMove, RemoteMouseButton.None, x, y, 0), frame.Bounds, frame.FrameSize);
                Thread.Sleep(150);
                signInInput.Apply(new(RemoteInputKind.MouseDown, RemoteMouseButton.Left, x, y, 0), frame.Bounds, frame.FrameSize);
                Thread.Sleep(150);
                signInInput.Apply(new(RemoteInputKind.MouseUp, RemoteMouseButton.Left, x, y, 0), frame.Bounds, frame.FrameSize);
                Thread.Sleep(1500);
                var desktop = WindowsInteractiveDesktopProbe.InspectCurrent();
                Program.Save(Path.Combine(output, "signin-result.json"), new { desktop });
                if (!desktop.IsAvailable)
                {
                    var after = capture.CaptureJpeg(90, 100);
                    File.WriteAllBytes(Path.Combine(output, "after-signin.jpg"), after.JpegBytes.ToArray());
                }
                return desktop.IsAvailable ? 0 : 1;
            }
            bool input = config.TryGetProperty("testInput", out var enabled) && enabled.GetBoolean();
            bool moved = false, shiftDown = false, shiftReleased = false;
            if (input)
            {
                using var dispatcher = new InputInjectionDispatcher();
                int x = Math.Clamp(before.CursorX - frame.Bounds.X + 25, 0, frame.FrameSize.Width - 1);
                int y = Math.Clamp(before.CursorY - frame.Bounds.Y + 25, 0, frame.FrameSize.Height - 1);
                var shift = RemoteInputCommand.KeyDown((int)Keys.ShiftKey);
                try
                {
                    dispatcher.Apply(new(RemoteInputKind.MouseMove, RemoteMouseButton.None, x, y, 0), frame.Bounds, frame.FrameSize);
                    Thread.Sleep(100);
                    var pointer = helper.QueryStatus();
                    moved = Math.Abs(pointer.CursorX - frame.Bounds.X - x) <= 1 && Math.Abs(pointer.CursorY - frame.Bounds.Y - y) <= 1;
                    if (!before.ShiftDown)
                    {
                        dispatcher.Apply(shift, frame.Bounds, frame.FrameSize);
                        Thread.Sleep(100);
                        shiftDown = helper.QueryStatus().ShiftDown;
                    }
                }
                finally
                {
                    if (!before.ShiftDown) dispatcher.ReleaseKey(shift);
                    dispatcher.Apply(new(RemoteInputKind.MouseMove, RemoteMouseButton.None,
                        Math.Clamp(before.CursorX - frame.Bounds.X, 0, frame.FrameSize.Width - 1),
                        Math.Clamp(before.CursorY - frame.Bounds.Y, 0, frame.FrameSize.Height - 1), 0), frame.Bounds, frame.FrameSize);
                }
                Thread.Sleep(100);
                shiftReleased = !helper.QueryStatus().ShiftDown;
            }
            Program.Save(Path.Combine(output, "result.json"), new
            {
                status = before.Status, frame.Bounds, frame.FrameSize, bytes = frame.JpegBytes.Length,
                frame.CaptureMilliseconds, frame.EncodeMilliseconds, roundTripMilliseconds = timings,
                inputRequested = input, moved, shiftDown, shiftReleased,
                scope = "Existing logged-in owner's lock screen only; no credential entry or unlock attempt"
            });
            return !input || moved && shiftDown && shiftReleased ? 0 : 1;
        }
        catch (Exception error)
        {
            Program.Save(Path.Combine(output, "failure.json"), new { error = error.ToString() });
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static async Task<int> RunNetworkAsync(WindowsSecureDesktopClient helper, SecureDesktopReply before, string output, bool dismissLockScreen, bool signInButton, string signInMethod, int holdSeconds, bool installedHost, bool leaveUnlocked)
    {
        if (signInMethod is not ("enter" or "numpad-enter" or "mouse")) throw new ArgumentException("Unknown sign-in test method.");
        if (holdSeconds is < 0 or > 90) throw new ArgumentOutOfRangeException(nameof(holdSeconds));
        // Normally an isolated ephemeral host. The explicit installedHost mode
        // verifies the actual deployed app; its RemoteDesk password is read via
        // the existing per-user DPAPI store, never printed or written to output.
        var reservation = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((System.Net.IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        string password = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        RemoteDeskSettings? settings = null;
        if (installedHost)
        {
            settings = new AppSettingsService().Load();
            port = settings.Host.Port;
            password = AppSettingsService.UnprotectSecret(settings.Host.ProtectedPassword);
            if (string.IsNullOrEmpty(password)) throw new InvalidOperationException("Installed host has no readable RemoteDesk connection password.");
        }
        using var host = installedHost ? null : new RemoteHostServer();
        using var client = new RemoteViewerClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds((signInButton ? 55 : 25) + holdSeconds));
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        if (host is not null) host.Log += lines.Enqueue;
        client.Log += lines.Enqueue;
        ScreenCaptureTarget target = ScreenCaptureService.GetDefaultTarget();
        if (settings?.Host.CaptureTargetId is { Length: > 0 } selected) target = ScreenCaptureService.FindTargetById(selected);
        var first = new TaskCompletionSource<RemoteFrameMetadata>(TaskCreationOptions.RunContinuationsAsynchronously);
        int count = 0;
        int h264 = 0;
        var frameSizes = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        var watch = Stopwatch.StartNew();
        double firstFrameMs = 0;
        byte[]? latestJpeg = null;
        client.FrameReceived += frame =>
        {
            if (frame.Encoding == RemoteFrameEncoding.H264AnnexB) Interlocked.Increment(ref h264);
            if (frame.Encoding != RemoteFrameEncoding.Jpeg) return;
            frameSizes.AddOrUpdate($"{frame.Width}x{frame.Height}", 1, (_, seen) => seen + 1);
            Volatile.Write(ref latestJpeg, frame.EncodedBuffer.AsSpan(frame.EncodedOffset, frame.EncodedLength).ToArray());
            if (Interlocked.Increment(ref count) == 1)
            {
                firstFrameMs = watch.Elapsed.TotalMilliseconds;
                File.WriteAllBytes(Path.Combine(output, "network-login.jpg"), frame.EncodedBuffer.AsSpan(frame.EncodedOffset, frame.EncodedLength).ToArray());
                first.TrySetResult(RemoteFrameMetadata.FromFrame(frame));
            }
        };
        bool moved = false, down = false, released = false;
        try
        {
            if (host is not null) await host.StartAsync(port, password, 30, 90, 100, target, adaptiveQuality: false);
            await client.ConnectAsync("127.0.0.1", port, password, ViewerVideoMode.Automatic, deadline.Token);
            var frame = await first.Task.WaitAsync(deadline.Token);
            await Task.Delay(1500, deadline.Token);
            if (holdSeconds > 0) await Task.Delay(TimeSpan.FromSeconds(holdSeconds), deadline.Token);
            if (dismissLockScreen)
            {
                // Keep both stages in this SAME session. Windows can return
                // an idle sign-in page to its clock/wallpaper between probes;
                // a single Enter then only reveals sign-in, not a failed login.
                int revealX = frame.Width / 2, revealY = frame.Height / 2;
                await client.SendInputAsync(new(RemoteInputKind.MouseDown, RemoteMouseButton.Left, revealX, revealY, 0));
                await client.SendInputAsync(new(RemoteInputKind.MouseUp, RemoteMouseButton.Left, revealX, revealY, 0));
                await Task.Delay(1200, deadline.Token);
                if (Volatile.Read(ref latestJpeg) is { } revealed) File.WriteAllBytes(Path.Combine(output, "after-click.jpg"), revealed);
            }
            if (signInButton)
            {
                if (WindowsInteractiveDesktopProbe.InspectCurrent().IsAvailable)
                    throw new InvalidOperationException("Desktop was already unlocked before sign-in; no keyboard input sent to a user app.");
                // Opt-in only after inspecting this exact password-free "Sign in"
                // button. Never discover, guess, fill, or submit any credential.
                int signInX = frame.Width / 2, signInY = (int)(frame.Height * 0.529);
                await client.SendInputAsync(new(RemoteInputKind.MouseMove, RemoteMouseButton.None, signInX, signInY, 0));
                if (signInMethod == "mouse")
                {
                    await client.SendInputAsync(new(RemoteInputKind.MouseDown, RemoteMouseButton.Left, signInX, signInY, 0));
                    await Task.Delay(100, deadline.Token);
                    await client.SendInputAsync(new(RemoteInputKind.MouseUp, RemoteMouseButton.Left, signInX, signInY, 0));
                }
                else
                {
                    // Physical Windows keys, including the distinct numpad Enter.
                    var flags = RemoteKeyboardFlags.HasScanCode | (signInMethod == "numpad-enter" ? RemoteKeyboardFlags.Extended : 0);
                    await client.SendInputsAsync([RemoteInputCommand.KeyDown((int)Keys.Enter, 0x1C, flags), RemoteInputCommand.KeyUp((int)Keys.Enter, 0x1C, flags)]);
                }
                var transition = Stopwatch.StartNew();
                while (!WindowsInteractiveDesktopProbe.InspectCurrent().IsAvailable && transition.Elapsed.TotalSeconds < 12)
                    await Task.Delay(100, deadline.Token);
                bool unlocked = WindowsInteractiveDesktopProbe.InspectCurrent().IsAvailable;
                double unlockMs = transition.Elapsed.TotalMilliseconds;
                if (!unlocked)
                {
                    if (Volatile.Read(ref latestJpeg) is { } failedJpeg) File.WriteAllBytes(Path.Combine(output, "after-signin.jpg"), failedJpeg);
                    throw new InvalidOperationException("Sign-in did not unlock; no additional credential attempts were made.");
                }
                transition.Restart();
                while (Volatile.Read(ref h264) < 5 && transition.Elapsed.TotalSeconds < 20)
                    await Task.Delay(100, deadline.Token);
                bool hardwareRestored = Volatile.Read(ref h264) >= 5;
                double videoRestoreMs = transition.Elapsed.TotalMilliseconds;
                if (leaveUnlocked)
                {
                    Program.Save(Path.Combine(output, "unlock-only.json"), new
                    {
                        unlocked, unlockMs, signInMethod, installedHost, dismissLockScreen,
                        hardwareRestored, h264Frames = h264, videoRestoreMs,
                        scope = "Confirmed password-free account, clock dismissal and sign-in in one authenticated connection; left on Default, no credentials entered"
                    });
                    return hardwareRestored ? 0 : 1;
                }
                await client.SendInputsAsync([
                    RemoteInputCommand.KeyDown((int)Keys.LWin), RemoteInputCommand.KeyDown((int)Keys.L),
                    RemoteInputCommand.KeyUp((int)Keys.L), RemoteInputCommand.KeyUp((int)Keys.LWin)]);
                transition.Restart();
                while (WindowsInteractiveDesktopProbe.InspectCurrent().IsAvailable && transition.Elapsed.TotalSeconds < 10)
                    await Task.Delay(100, deadline.Token);
                bool relocked = !WindowsInteractiveDesktopProbe.InspectCurrent().IsAvailable;
                int jpegAtLock = Volatile.Read(ref count);
                transition.Restart();
                while (Volatile.Read(ref count) < jpegAtLock + 4 && transition.Elapsed.TotalSeconds < 10)
                    await Task.Delay(100, deadline.Token);
                bool lockVideoRestored = Volatile.Read(ref count) >= jpegAtLock + 4;
                Program.Save(Path.Combine(output, "unlock-cycle.json"), new
                {
                    unlocked, unlockMs, signInMethod, installedHost, dismissLockScreen, hardwareRestored, h264Frames = h264, videoRestoreMs,
                    relocked, lockVideoRestored, lockVideoRestoreMs = transition.Elapsed.TotalMilliseconds,
                    scope = "Activated inspected password-free Sign in button, observed Default and H.264, sent Win+L and observed secure JPEG; no credentials entered"
                });
                return unlocked && hardwareRestored && relocked && lockVideoRestored ? 0 : 1;
            }
            int x = frame.Width / 2, y = frame.Height / 2;
            await client.SendInputAsync(RemoteInputCommand.MouseMove(x, y));
            await Task.Delay(250, deadline.Token);
            var cursor = helper.QueryStatus();
            moved = Math.Abs(cursor.CursorX - target.Bounds.X - x) <= 2 && Math.Abs(cursor.CursorY - target.Bounds.Y - y) <= 2;
            await client.SendInputAsync(RemoteInputCommand.MouseMove(
                Math.Clamp(before.CursorX - target.Bounds.X, 0, frame.Width - 1),
                Math.Clamp(before.CursorY - target.Bounds.Y, 0, frame.Height - 1)));
            if (!before.ShiftDown)
            {
                await client.SendInputAsync(RemoteInputCommand.KeyDown((int)Keys.ShiftKey));
                await Task.Delay(250, deadline.Token);
                down = helper.QueryStatus().ShiftDown;
            }
            // Deliberately disconnect with this single harmless modifier held:
            // the real host and helper must compensate without a sticky key.
            await client.DisconnectAsync();
            await Task.Delay(500, deadline.Token);
            released = !helper.QueryStatus().ShiftDown;
            Program.Save(Path.Combine(output, "network-result.json"), new
            {
                frame.Width, frame.Height, frame.Encoding, frames = count, firstFrameMs, moved, down, released, dismissLockScreen,
                initialShiftDown = before.ShiftDown, holdSeconds, frameSizes, installedHost,
                scope = "Authenticated product protocol on loopback, Winlogon JPEG, pointer and Shift, disconnect release; no credentials entered"
            });
            return count >= 5 && moved && down && released ? 0 : 1;
        }
        finally
        {
            await client.DisconnectAsync();
            if (host is not null) await host.StopAsync();
            File.WriteAllLines(Path.Combine(output, "network.log"), lines);
        }
    }
}
