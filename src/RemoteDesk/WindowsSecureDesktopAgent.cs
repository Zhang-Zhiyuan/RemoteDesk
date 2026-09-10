using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;

namespace RemoteDesk;

// Headless SYSTEM worker in the owner's console session. No sockets, settings,
// filesystem commands, credential storage, or arbitrary desktop selection.
internal static class WindowsSecureDesktopAgent
{
    // READOBJECTS | WRITEOBJECTS | JOURNALPLAYBACK | JOURNALRECORD.
    // The last right allows querying the fixed Shift-state diagnostic; no
    // hooks, journal recording, ACL changes or desktop activation are used.
    internal const uint DesktopAccess = 0x0001 | 0x0080 | 0x0020 | 0x0010;
    internal static void Run(string ownerSid, uint session, string stopEvent, int parentId)
    {
        SetProcessDpiAwarenessContext(-4); // Headless worker does not run WinForms initialization.
        var parentIdentity = WindowsSecureDesktopNative.ProcessIdentity(checked((uint)parentId));
        if (!WindowsSecureDesktopNative.IsSystem || parentIdentity.Sid != "S-1-5-18" ||
            parentIdentity.Session != 0 || !string.Equals(parentIdentity.Image, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("桌面辅助进程来源无效。");
        using Process parent = Process.GetProcessById(parentId);
        using EventWaitHandle stop = EventWaitHandle.OpenExisting(stopEvent);
        using var cancellation = new CancellationTokenSource();
        using var watchdog = new System.Threading.Timer(_ =>
        {
            try { if (stop.WaitOne(0) || parent.HasExited) cancellation.Cancel(); }
            catch (InvalidOperationException) { cancellation.Cancel(); }
        }, null, 0, 250);
        var connections = new ConcurrentDictionary<NamedPipeServerStream, Thread>();
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                // Reserve an instance for the listener and bound privileged workers.
                if (connections.Count >= 3) { cancellation.Token.WaitHandle.WaitOne(100); continue; }
                var pipe = NamedPipeServerStreamAcl.Create(WindowsSecureDesktopProtocol.PipeName(ownerSid, session),
                    PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                    65536, 65536, WindowsSecureDesktopProtocol.PipeSecurity(), HandleInheritability.None, (PipeAccessRights)0);
                try
                {
                    pipe.WaitForConnectionAsync(cancellation.Token).GetAwaiter().GetResult();
                    var identity = WindowsSecureDesktopNative.ProcessIdentity(WindowsSecureDesktopNative.PipeClientProcessId(pipe.SafePipeHandle));
                    if (!WindowsSecureDesktopProtocol.AllowsClient(identity.Sid, identity.Elevated, identity.Session, ownerSid, session))
                    { pipe.Dispose(); continue; }
                    var thread = new Thread(() =>
                    {
                        try { Serve(pipe, ownerSid, session, cancellation.Token); }
                        finally { pipe.Dispose(); connections.TryRemove(pipe, out _); }
                    }) { IsBackground = true, Name = "RemoteDesk protected desktop IPC" };
                    connections.TryAdd(pipe, thread);
                    thread.Start();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                    InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
                {
                    pipe.Dispose();
                    if (cancellation.IsCancellationRequested) break;
                    cancellation.Token.WaitHandle.WaitOne(100);
                }
            }
        }
        finally
        {
            cancellation.Cancel();
            foreach (var connection in connections.ToArray()) connection.Value.Join(5000);
        }
    }

    private static void Serve(NamedPipeServerStream pipe, string ownerSid, uint session, CancellationToken stop)
    {
        var native = WindowsInputDesktopNativeApi.Instance;
        nint original = native.GetThreadDesktop(native.GetCurrentThreadId()).Handle;
        nint owned = 0;
        var keys = new WindowsSecureDesktopKeyState();
        var buttons = new HashSet<RemoteMouseButton>();
        long failureLoggedAt = 0;
        void ReleaseOwnedInput()
        {
            keys.ReleaseAll(InputInjector.ReleaseKey);
            foreach (var button in buttons) InputInjector.TryReleaseMouseButton(button);
            buttons.Clear();
        }
        bool AttachInputDesktop(bool releaseOnly = false)
        {
            if (WindowsSecureDesktopNative.OwnedConsoleSession(ownerSid) != session) return false;
            // SendInput additionally requires input playback access. Do not
            // grant/change any desktop ACL; request only existing SYSTEM rights.
            nint inputDesktop = OpenInputDesktop(0, false, DesktopAccess);
            if (inputDesktop == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var opened = new InputDesktopOpenResult(inputDesktop, 0);
            bool retained = false;
            try
            {
                var desktop = native.InspectUserObject(opened.Handle);
                bool secure = string.Equals(desktop.Name, "Winlogon", StringComparison.OrdinalIgnoreCase);
                bool normal = string.Equals(desktop.Name, "Default", StringComparison.OrdinalIgnoreCase);
                if (desktop.IsInput != true || (!secure && !normal)) return false;
                // A transition can release only keys owned by this connection on
                // Default; new privileged input is restricted to Winlogon.
                var result = native.SetThreadDesktop(opened.Handle);
                if (!result.Succeeded) throw new System.ComponentModel.Win32Exception(result.Error);
                nint previous = owned;
                owned = opened.Handle; retained = true;
                if (previous != 0) native.CloseDesktop(previous);
                if (!secure || releaseOnly) ReleaseOwnedInput();
                return secure;
            }
            finally { if (!retained) native.CloseDesktop(opened.Handle); }
        }
        try
        {
            while (!stop.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(stop);
                idle.CancelAfter(TimeSpan.FromSeconds(15));
                SecureDesktopRequest request = WindowsSecureDesktopProtocol.Read<SecureDesktopRequest>(pipe, idle.Token);
                WindowsSecureDesktopProtocol.Validate(request);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                SecureDesktopReply reply;
                byte[]? jpeg = null;
                try
                {
                    if (!AttachInputDesktop()) reply = new("inactive");
                    else
                    {
                        Rectangle bounds = new(request.Left, request.Top, request.Width, request.Height);
                        if (request.Operation == "capture")
                        {
                            // Default and Winlogon can have different orientation
                            // and topology. Resolve the same monitor on this desktop,
                            // and publish its actual bounds with the encoded pixels.
                            bounds = WindowsSecureDesktopDisplays.ResolveCaptureBounds(request, WindowsSecureDesktopDisplays.GetTargets());
                        }
                        else if (request.Operation == "input" &&
                            request.Command.Kind is RemoteInputKind.MouseMove or RemoteInputKind.MouseDown or RemoteInputKind.MouseUp or RemoteInputKind.MouseWheel)
                        {
                            if (!WindowsSecureDesktopDisplays.Contains(WindowsSecureDesktopDisplays.GetTargets(), bounds))
                                throw new InvalidOperationException("登录界面屏幕范围已变化，请刷新屏幕。");
                        }
                        switch (request.Operation)
                        {
                            case "capture":
                                (reply, jpeg) = Capture(bounds, request.Quality, request.Scale);
                                break;
                            case "input":
                                var command = request.Command;
                                // Record downs before injection so partial native failure
                                // is compensated when the pipe/session closes.
                                if (command.Kind == RemoteInputKind.KeyDown) keys.Press(command);
                                if (command.Kind == RemoteInputKind.MouseDown) buttons.Add(command.Button);
                                InputInjector.Apply(command, bounds, new Size(request.FrameWidth, request.FrameHeight));
                                if (command.Kind == RemoteInputKind.KeyUp) keys.ForgetReleased(command);
                                if (command.Kind == RemoteInputKind.MouseUp) buttons.Remove(command.Button);
                                reply = new("active");
                                break;
                            case "release-key":
                                keys.Release(request.Command, InputInjector.ReleaseKey);
                                reply = new("active"); break;
                            case "release-mouse":
                                if (buttons.Contains(request.Command.Button))
                                { InputInjector.ReleaseMouseButton(request.Command.Button); buttons.Remove(request.Command.Button); }
                                reply = new("active"); break;
                            case "paste":
                                InputInjector.SendPasteShortcut(); reply = new("active"); break;
                            default:
                                GetCursorPos(out NativePoint cursor);
                                reply = new("active", CursorX: cursor.X, CursorY: cursor.Y, ShiftDown: GetAsyncKeyState(0x10) < 0);
                                break;
                        }
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ExternalException or ArgumentException)
                {
                    // Do not serialize native exception messages: some contain
                    // key codes or pointer coordinates from a password entry.
                    if (Environment.TickCount64 - failureLoggedAt >= 3000)
                    {
                        failureLoggedAt = Environment.TickCount64;
                        WindowsSecureDesktopNative.ReportFailure(request.Operation, ex);
                    }
                    reply = new("error", Error: "登录界面暂不可用，请稍后重试。错误码 " + ex.HResult);
                }
                WindowsSecureDesktopProtocol.Write(pipe, reply, deadline.Token);
                if (jpeg is not null) pipe.WriteAsync(jpeg, deadline.Token).AsTask().GetAwaiter().GetResult();
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or System.Text.Json.JsonException or ArgumentException or
            UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        finally
        {
            try { AttachInputDesktop(releaseOnly: true); } catch { }
            try { ReleaseOwnedInput(); } catch { }
            if (original != 0 && native.SetThreadDesktop(original).Succeeded && owned != 0) native.CloseDesktop(owned);
        }
    }

    private static (SecureDesktopReply, byte[]) Capture(Rectangle bounds, int quality, int scale)
    {
        Size size = ScreenCaptureService.CalculateFrameSize(bounds, scale);
        WindowsSecureDesktopProtocol.ValidateFrameSize(size.Width, size.Height);
        // Bound the source allocation too, even when the requested result is scaled.
        WindowsSecureDesktopProtocol.ValidateFrameSize(bounds.Width, bounds.Height);
        long started = Stopwatch.GetTimestamp();
        using var source = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(source))
            graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
        using var scaled = size == bounds.Size ? null : new Bitmap(size.Width, size.Height, PixelFormat.Format24bppRgb);
        if (scaled is not null)
        {
            using Graphics graphics = Graphics.FromImage(scaled);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(source, new Rectangle(Point.Empty, size));
        }
        double capture = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        started = Stopwatch.GetTimestamp();
        using var output = new MemoryStream();
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
        (scaled ?? source).Save(output, ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid), parameters);
        if (output.Length > WindowsSecureDesktopProtocol.MaxJpegBytes) throw new InvalidOperationException("登录画面超过传输上限。");
        byte[] jpeg = output.ToArray();
        return (new("active", bounds.X, bounds.Y, bounds.Width, bounds.Height, size.Width, size.Height,
            capture, Stopwatch.GetElapsedTime(started).TotalMilliseconds, jpeg.Length), jpeg);
    }

    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(nint context);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
}
