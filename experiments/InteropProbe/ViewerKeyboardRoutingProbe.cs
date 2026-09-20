using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using RemoteDesk;

// Called only by KeyboardModifierProbe on its private window station. Messages
// below target the owned viewer surface; no physical injection or real IME input.
internal static class ViewerKeyboardRoutingProbe
{
    internal sealed record Check(string Name, bool Passed, object Details);

    internal static async Task<IReadOnlyList<Check>> RunAsync(RemoteViewerWindow window,
        RemoteViewerClient client, ConcurrentQueue<RemoteInputCommand> received, CancellationToken token)
    {
        var checks = new List<Check>();
        var picture = window.Controls.OfType<PictureBox>().Single();
        var ownership = (RemoteInputOwnershipTracker)Field(window, "_remoteInputOwnership")!;
        bool Reserved(int message, Keys keys) => (bool)Invoke(window, "TryHandleReservedKeyboardMessage", message, keys)!;
        void Key(bool down, Keys keys) => Invoke(window, down ? "PictureBox_KeyDown" : "PictureBox_KeyUp",
            picture, new KeyEventArgs(keys));
        async Task Drain()
        {
            await client.FlushInputAsync(token);
            await Task.Delay(75, token);
        }
        async Task Reset()
        {
            SendMessage(picture.Handle, 0x010E, 0, 0);
            Invoke(window, "ReleaseAllRemoteInputs");
            await Drain();
            picture.Focus();
        }
        void CheckResult(string name, bool passed, object detail) => checks.Add(new(name, passed, detail));

        int start = received.Count;
        bool first = Reserved(0x100, Keys.F11);
        bool entered = Field(window, "_fullScreenRestoreState") is not null;
        bool repeat = Reserved(0x100, Keys.F11);
        bool stayed = Field(window, "_fullScreenRestoreState") is not null;
        bool release = Reserved(0x101, Keys.F11);
        CheckResult("held F11 toggles fullscreen only once", first && entered && repeat && stayed && release,
            new { first, entered, repeat, stayed, release });
        if (Field(window, "_fullScreenRestoreState") is not null)
        { Reserved(0x100, Keys.F11); Reserved(0x101, Keys.F11); }
        await Reset();

        start = received.Count;
        // No file capability is enabled on this fixture; the command stops at
        // the product's capability guard, never opens a file dialog or pulls data.
        bool pull = Reserved(0x100, Keys.Control | Keys.Shift | Keys.R);
        bool pullRepeat = Reserved(0x100, Keys.R); // LL route after owned modifiers were released.
        if (!pullRepeat)
        {
            var command = RemoteInputCommand.KeyDown((int)Keys.R, 0x13, RemoteKeyboardFlags.HasScanCode);
            Invoke(window, "TryForwardKeyboardCommand", command, Keys.R);
        }
        bool pullUp = Reserved(0x101, Keys.R);
        await Drain();
        int leaked = received.Count - start;
        CheckResult("reserved file shortcut repeat cannot leak an unpaired R", pull && pullRepeat && pullUp && leaked == 0,
            new { pull, pullRepeat, pullUp, leaked, held = ownership.PressedKeyCount });
        await Reset();

        window.SetRemotePlatform(RemoteDevicePlatforms.Android);
        picture.Focus();
        start = received.Count;
        Key(true, Keys.ControlKey | Keys.Control);
        SendMessage(picture.Handle, 0x010D, 0, 0);
        Key(false, Keys.ControlKey);
        await Drain();
        var events = received.ToArray().Skip(start).ToArray();
        CheckResult("IME candidate mode still releases an already forwarded Ctrl",
            ownership.PressedKeyCount == 0 && events.Length == 2 && events[^1].Kind == RemoteInputKind.KeyUp,
            new { count = events.Length, held = ownership.PressedKeyCount });
        await Reset();

        start = received.Count;
        Key(true, Keys.ControlKey | Keys.Control);
        Key(true, Keys.C | Keys.Control);
        Key(false, Keys.ControlKey);
        Key(false, Keys.C);
        await Drain();
        events = received.ToArray().Skip(start).ToArray();
        CheckResult("releasing Ctrl before C does not lose the owned C release",
            ownership.PressedKeyCount == 0 && events.Length == 4 && events[^1].Data == (int)Keys.C &&
            events[^1].Kind == RemoteInputKind.KeyUp,
            new { count = events.Length, held = ownership.PressedKeyCount });
        await Reset();

        foreach (Keys chord in new[] { Keys.Home | Keys.Shift, Keys.Tab | Keys.Shift,
            Keys.C | Keys.Control | Keys.Shift, Keys.Enter | Keys.Control, Keys.Enter | Keys.Alt })
        {
            start = received.Count;
            Key(true, chord);
            Key(false, chord & Keys.KeyCode);
            await Drain();
            events = received.ToArray().Skip(start).ToArray();
            CheckResult("Android unsupported chord cannot become a bare action: " + chord,
                events.Length == 0 && ownership.PressedKeyCount == 0,
                new { count = events.Length, held = ownership.PressedKeyCount });
            await Reset();
        }

        start = received.Count;
        Key(true, Keys.A | Keys.Shift);
        Invoke(window, "PictureBox_KeyPress", picture, new KeyPressEventArgs('A'));
        Key(false, Keys.A);
        Key(true, Keys.Enter);
        Key(false, Keys.Enter);
        await Drain();
        events = received.ToArray().Skip(start).ToArray();
        CheckResult("Android committed uppercase and bare newline remain supported",
            events.Length == 2 && events[0] == RemoteInputCommand.TextInput('A') &&
            events[1] == RemoteInputCommand.TextInput('\n') && ownership.PressedKeyCount == 0,
            new { count = events.Length, held = ownership.PressedKeyCount });
        await Reset();
        window.SetRemotePlatform(RemoteDevicePlatforms.Windows);

        start = received.Count;
        var rightShift = RemoteInputCommand.KeyDown((int)Keys.RShiftKey, 0x36, RemoteKeyboardFlags.HasScanCode);
        Invoke(window, "TryForwardKeyboardCommand", rightShift, Keys.RShiftKey);
        await Drain();
        bool pending;
        int fillers = 0;
        // Hold the fixture client's queue lock while exhausting both its
        // normal budget and release reserve. The real peer only records these
        // synthetic packets; it never injects them into an OS input target.
        lock (Field(client, "_inputLock")!)
        {
            while (client.TryQueueOwnedInput(RemoteInputCommand.TextInput('f')).Accepted) fillers++;
            while (client.TryQueueOwnedInput(RemoteInputCommand.KeyUp((int)Keys.F24)).Accepted) fillers++;
            Invoke(window, "TryForwardKeyboardCommand", rightShift with { Kind = RemoteInputKind.KeyUp }, Keys.RShiftKey);
            pending = ownership.PressedKeyCount == 1;
        }
        await Drain();
        for (int attempt = 0; ownership.PressedKeyCount > 0 && attempt < 100; attempt++)
            await Task.Delay(10, token);
        await Drain();
        events = received.ToArray().Skip(start).Where(item => item.Data == (int)Keys.RShiftKey).ToArray();
        CheckResult("queue recovery retries the rejected Shift release without another user event",
            fillers == RemoteViewerClient.MaxQueuedInputs + RemoteInputQueue.ReleaseReserveCapacity && pending &&
            ownership.PressedKeyCount == 0 && events.Length == 2 && events[1].Kind == RemoteInputKind.KeyUp,
            new { fillers, pendingObserved = pending, held = ownership.PressedKeyCount, count = events.Length });
        await Reset();

        start = received.Count;
        bool downQueued = ownership.TryQueueMouseDown(RemoteMouseButton.Left, new Point(10, 20),
            client.InputConnectionGeneration, client.TryQueueOwnedInput, client.TryQueueOwnedInput);
        await Drain();
        lock (Field(client, "_inputLock")!)
        {
            while (client.TryQueueOwnedInput(RemoteInputCommand.TextInput('f')).Accepted) { }
            while (client.TryQueueOwnedInput(RemoteInputCommand.KeyUp((int)Keys.F24)).Accepted) { }
            Invoke(window, "QueueOwnedRemoteMouseUp", RemoteMouseButton.Left, new Point(30, 40));
            pending = ownership.PressedMouseButtonCount == 1;
            ownership.UpdatePressedMousePosition(new Point(90, 100), client.InputConnectionGeneration);
        }
        await Drain();
        for (int attempt = 0; ownership.PressedMouseButtonCount > 0 && attempt < 100; attempt++)
            await Task.Delay(10, token);
        await Drain();
        events = received.ToArray().Skip(start).Where(item => item.Kind is RemoteInputKind.MouseDown or RemoteInputKind.MouseUp).ToArray();
        CheckResult("queue recovery releases the mouse at its original attempted up position",
            downQueued && pending && ownership.PressedMouseButtonCount == 0 && events.Length == 2 &&
            events[1] == RemoteInputCommand.MouseUp(RemoteMouseButton.Left, 30, 40),
            new { downQueued, pendingObserved = pending, held = ownership.PressedMouseButtonCount,
                count = events.Length, releaseX = events.LastOrDefault().X, releaseY = events.LastOrDefault().Y });
        await Reset();

        // Deterministically hold only this fixture's retry scheduling while a
        // failed up is pending. Unlike a full queue, the free client queue can
        // expose a wrongly admitted move/wheel. Synthetic dimensions make wheel
        // coordinate mapping valid; no desktop/frame capture is involved.
        long previousImageSize = (long)Field(window, "_remoteImageSizePacked")!;
        int previousTargetAvailable = (int)Field(window, "_captureTargetAvailable")!;
        try
        {
            SetField(window, "_remoteImageSizePacked", ((long)640 << 32) | 480L);
            SetField(window, "_captureTargetAvailable", 1);
            var wheelArgs = new MouseEventArgs(MouseButtons.None, 0,
                picture.ClientSize.Width / 2, picture.ClientSize.Height / 2, 120);
            start = received.Count;
            downQueued = ownership.TryQueueMouseDown(RemoteMouseButton.Left, new Point(10, 20),
                client.InputConnectionGeneration, client.TryQueueOwnedInput, client.TryQueueOwnedInput);
            await Drain();
            bool rejected;
            int overtaking;
            SetField(window, "_inputReleaseRetryInProgress", true);
            try
            {
                rejected = !ownership.TryQueueMouseUp(RemoteMouseButton.Left, new Point(30, 40),
                    client.InputConnectionGeneration, (_, _) => false);
                int pointerStart = received.Count;
                Invoke(window, "TrySendRemoteMouseMove", new Point(90, 100));
                Invoke(window, "PictureBox_MouseWheel", picture, wheelArgs);
                await Drain();
                overtaking = received.ToArray().Skip(pointerStart).Count(item =>
                    item.Kind is RemoteInputKind.MouseMove or RemoteInputKind.MouseWheel or RemoteInputKind.PinchZoom);
                pending = ownership.HasPendingReleases(client.InputConnectionGeneration);
            }
            finally
            {
                SetField(window, "_inputReleaseRetryInProgress", false);
                Invoke(window, "SchedulePendingInputReleaseRetry");
            }
            for (int attempt = 0; ownership.HasPendingReleases(client.InputConnectionGeneration) && attempt < 100; attempt++)
                await Task.Delay(10, token);
            await Drain();
            events = received.ToArray().Skip(start).ToArray();
            int afterReleaseStart = received.Count;
            Invoke(window, "TrySendRemoteMouseMove", new Point(90, 100));
            Invoke(window, "PictureBox_MouseWheel", picture, wheelArgs);
            await Drain();
            var afterRelease = received.ToArray().Skip(afterReleaseStart).ToArray();
            CheckResult("pending release drops new motion and wheel; fresh input resumes after up",
                downQueued && rejected && pending && overtaking == 0 && events.Length == 2 &&
                events[1] == RemoteInputCommand.MouseUp(RemoteMouseButton.Left, 30, 40) &&
                afterRelease.Count(item => item.Kind == RemoteInputKind.MouseMove) == 1 &&
                afterRelease.Count(item => item.Kind == RemoteInputKind.MouseWheel) == 1,
                new { downQueued, rejected, pendingObserved = pending, overtaking,
                    countBeforeFreshInput = events.Length,
                    freshMoveCount = afterRelease.Count(item => item.Kind == RemoteInputKind.MouseMove),
                    freshWheelCount = afterRelease.Count(item => item.Kind == RemoteInputKind.MouseWheel),
                    scope = "Synthetic failed admission and held retry scheduling; real viewer/client queue and TCP" });
        }
        finally
        {
            SetField(window, "_remoteImageSizePacked", previousImageSize);
            SetField(window, "_captureTargetAvailable", previousTargetAvailable);
        }
        await Reset();

        // Exercise the real client's temporary reject-drain input barrier and
        // existing event subscription, without starting a file request, showing
        // a dialog, accessing a clipboard, or manufacturing a new connection.
        start = received.Count;
        long inputGeneration = client.InputConnectionGeneration;
        Invoke(window, "TryForwardKeyboardCommand", rightShift, Keys.RShiftKey);
        await Drain();
        bool barrierPending;
        bool retryStoppedAtBarrier;
        int previousSuppression = (int)Field(client, "_suppressInputUntilReturnedClipboardRequestDrained")!;
        int previousFilePending = (int)Field(client, "_returnedClipboardFileRequestPending")!;
        try
        {
            SetField(client, "_returnedClipboardFileRequestPending", 1);
            SetField(client, "_suppressInputUntilReturnedClipboardRequestDrained", 1);
            Invoke(window, "TryForwardKeyboardCommand", rightShift with { Kind = RemoteInputKind.KeyUp }, Keys.RShiftKey);
            await Task.Delay(25, token);
            barrierPending = ownership.HasPendingReleases(inputGeneration);
            retryStoppedAtBarrier = !(bool)Field(window, "_inputReleaseRetryInProgress")!;
            SetField(client, "_suppressInputUntilReturnedClipboardRequestDrained", 0);
            SetField(client, "_returnedClipboardFileRequestPending", 0);
            Invoke(client, "NotifyRemoteClipboardFileRequestPendingChanged", false);
            for (int attempt = 0; ownership.HasPendingReleases(inputGeneration) && attempt < 100; attempt++)
                await Task.Delay(10, token);
            await Drain();
            events = received.ToArray().Skip(start).ToArray();
            CheckResult("file reject-drain completion resumes a release on the same connection",
                barrierPending && retryStoppedAtBarrier && client.InputConnectionGeneration == inputGeneration &&
                !ownership.HasPendingReleases(inputGeneration) && ownership.PressedKeyCount == 0 &&
                events.Length == 2 && events[1] == (rightShift with { Kind = RemoteInputKind.KeyUp }),
                new { barrierPending, retryStoppedAtBarrier, count = events.Length,
                    held = ownership.PressedKeyCount,
                    sameConnection = client.InputConnectionGeneration == inputGeneration,
                    scope = "Simulated file barrier state; real client admission, completion event, viewer retry and TCP" });
        }
        finally
        {
            SetField(client, "_suppressInputUntilReturnedClipboardRequestDrained", previousSuppression);
            SetField(client, "_returnedClipboardFileRequestPending", previousFilePending);
        }
        await Reset();
        // Directly exercise the product's drag cancellation routing with an
        // owned mouse/button, without an OLE drag or file/clipboard request.
        start = received.Count;
        var rightControl = RemoteInputCommand.KeyDown((int)Keys.RControlKey, 0x1D,
            RemoteKeyboardFlags.HasScanCode | RemoteKeyboardFlags.Extended);
        Invoke(window, "TryForwardKeyboardCommand", rightControl, Keys.RControlKey | Keys.Control);
        downQueued = ownership.TryQueueMouseDown(RemoteMouseButton.Left, new Point(10, 20),
            client.InputConnectionGeneration, client.TryQueueOwnedInput, client.TryQueueOwnedInput);
        await Drain();
        bool cancelKeyboard = (bool)Invoke(window, "QueueRemoteDragOutCancel", new Point(30, 40))!;
        await Drain();
        events = received.ToArray().Skip(start).ToArray();
        CheckResult("modified drag cancellation releases only its mouse, never Ctrl or Ctrl+Escape",
            downQueued && !cancelKeyboard && ownership.PressedKeyCount == 1 && ownership.PressedMouseButtonCount == 0 &&
            events.SequenceEqual(new[] { rightControl, RemoteInputCommand.MouseDown(RemoteMouseButton.Left, 10, 20),
                RemoteInputCommand.MouseUp(RemoteMouseButton.Left, 30, 40) }),
            new { downQueued, cancelKeyboard, heldKeys = ownership.PressedKeyCount,
                heldMouse = ownership.PressedMouseButtonCount, count = events.Length });
        await Reset();

        start = received.Count;
        downQueued = ownership.TryQueueMouseDown(RemoteMouseButton.Left, new Point(10, 20),
            client.InputConnectionGeneration, client.TryQueueOwnedInput, client.TryQueueOwnedInput);
        await Drain();
        cancelKeyboard = (bool)Invoke(window, "QueueRemoteDragOutCancel", new Point(30, 40))!;
        await Drain();
        events = received.ToArray().Skip(start).ToArray();
        CheckResult("unmodified drag cancellation keeps balanced Escape and owned mouse release",
            downQueued && cancelKeyboard && ownership.PressedKeyCount == 0 && ownership.PressedMouseButtonCount == 0 &&
            events.SequenceEqual(new[] { RemoteInputCommand.MouseDown(RemoteMouseButton.Left, 10, 20),
                RemoteInputCommand.KeyDown((int)Keys.Escape), RemoteInputCommand.KeyUp((int)Keys.Escape),
                RemoteInputCommand.MouseUp(RemoteMouseButton.Left, 30, 40) }),
            new { downQueued, cancelKeyboard, heldKeys = ownership.PressedKeyCount,
                heldMouse = ownership.PressedMouseButtonCount, count = events.Length });
        await Reset();
        return checks;
    }

    private static object? Field(object instance, string name) => instance.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance);
    private static void SetField(object instance, string name, object value) => instance.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
    private static object? Invoke(object instance, string method, params object[] args) => instance.GetType()
        .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessage(nint window, int message, nint wparam, nint lparam);
}
