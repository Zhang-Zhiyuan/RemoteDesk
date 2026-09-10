using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace RemoteDesk;

internal sealed class WindowsSecureDesktopClient : IDisposable
{
    private static readonly Lazy<bool> SystemProcess = new(() => WindowsSecureDesktopNative.IsSystem);
    private readonly object _gate = new();
    private readonly WindowsSessionShortcutHandler _shortcuts = new();
    private readonly string _ownerSid = WindowsPersistentStartup.UserSid;
    private readonly uint _session = checked((uint)Process.GetCurrentProcess().SessionId);
    private NamedPipeClientStream? _pipe;
    private System.Threading.Timer? _heartbeat;
    private bool _disposed;

    internal static bool IsRequired => !SystemProcess.Value && !WindowsInteractiveDesktopProbe.InspectCurrent().IsAvailable;

    internal void Apply(RemoteInputCommand command, Rectangle bounds, Size size)
    {
        InputInjector.ValidateCommand(command, size);
        if (_shortcuts.TryHandle(command, ReleaseKeyCore, LockCurrentSession)) return;
        if (!Route(new("input", command, bounds.X, bounds.Y, bounds.Width, bounds.Height, size.Width, size.Height)))
            InputInjector.Apply(command, bounds, size);
        _shortcuts.Observe(command);
    }

    internal void ReleaseKey(RemoteInputCommand pressed)
    {
        if (!_shortcuts.TryConsumeRelease(pressed)) ReleaseKeyCore(pressed);
    }

    private void ReleaseKeyCore(RemoteInputCommand pressed)
    {
        if (!Route(new("release-key", pressed))) InputInjector.ReleaseKey(pressed);
    }

    private static void LockCurrentSession()
    {
        if (!WindowsInteractiveDesktopProbe.InspectCurrent().IsAvailable) return; // Already locked.
        if (!LockWorkStation())
            throw new InvalidOperationException("Windows 未接受锁屏请求。", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
    }

    internal void ReleaseMouseButton(RemoteMouseButton button)
    {
        if (!Route(new("release-mouse", new RemoteInputCommand(RemoteInputKind.MouseUp, button, 0, 0, 0))))
            InputInjector.ReleaseMouseButton(button);
    }

    internal void SendPasteShortcut()
    {
        if (!Route(new("paste"))) InputInjector.SendPasteShortcut();
    }

    private bool Route(SecureDesktopRequest request)
    {
        if (!IsRequired) { Disconnect(); return false; }
        SecureDesktopReply reply = Exchange(request, out _);
        // Inactive proves that no input was injected, so falling back after
        // an unlock race is safe. Never replay an operation after an I/O error.
        if (reply.Status == "inactive") { Disconnect(); return false; }
        return true;
    }

    internal bool TryCapture(Rectangle bounds, int quality, int scale, out ScreenCaptureResult capture, string? targetId = null)
    {
        capture = default;
        if (!IsRequired) { Disconnect(); return false; }
        SecureDesktopReply reply = Exchange(new("capture", Left: bounds.X, Top: bounds.Y,
            Width: bounds.Width, Height: bounds.Height, Quality: quality, Scale: scale, CaptureTargetId: targetId), out byte[]? jpeg);
        if (reply.Status == "inactive") { Disconnect(); return false; }
        if (jpeg is null) throw new InvalidOperationException("登录画面尚未准备好。");
        capture = new(new Rectangle(reply.Left, reply.Top, reply.Width, reply.Height),
            new Size(reply.FrameWidth, reply.FrameHeight), jpeg, reply.CaptureMilliseconds, reply.EncodeMilliseconds);
        return true;
    }

    internal SecureDesktopReply QueryStatus() => Exchange(new("status"), out _);

    private SecureDesktopReply Exchange(SecureDesktopRequest request, out byte[]? jpeg)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            jpeg = null;
            try
            {
                WindowsSecureDesktopProtocol.Validate(request);
                EnsureConnected();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                WindowsSecureDesktopProtocol.Write(_pipe!, request, deadline.Token);
                SecureDesktopReply reply = WindowsSecureDesktopProtocol.Read<SecureDesktopReply>(_pipe!, deadline.Token);
                if (reply.Status is not ("active" or "inactive" or "error")) throw new InvalidDataException("辅助服务响应无效。");
                if (reply.Status == "error") throw new InvalidOperationException(reply.Error ?? "登录界面暂不可用。");
                if (request.Operation == "capture" && reply.Status == "active")
                {
                    WindowsSecureDesktopProtocol.ValidateFrameSize(reply.FrameWidth, reply.FrameHeight);
                    WindowsSecureDesktopProtocol.ValidateGeometry(reply.Left, reply.Top, reply.Width, reply.Height);
                    Size expected = ScreenCaptureService.CalculateFrameSize(new Rectangle(reply.Left, reply.Top, reply.Width, reply.Height), request.Scale);
                    if (reply.JpegLength is < 1 or > WindowsSecureDesktopProtocol.MaxJpegBytes ||
                        reply.FrameWidth != expected.Width || reply.FrameHeight != expected.Height ||
                        !double.IsFinite(reply.CaptureMilliseconds) || !double.IsFinite(reply.EncodeMilliseconds))
                        throw new InvalidDataException("登录画面响应无效。");
                    jpeg = new byte[reply.JpegLength];
                    _pipe!.ReadExactlyAsync(jpeg, deadline.Token).AsTask().GetAwaiter().GetResult();
                }
                else if (reply.JpegLength != 0) throw new InvalidDataException("辅助服务返回了意外的数据。");
                return reply;
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or System.ComponentModel.Win32Exception or
                System.Text.Json.JsonException or UnauthorizedAccessException or InvalidOperationException)
            {
                DisconnectCore();
                throw new InvalidOperationException("锁屏控制暂不可用：请在被控端启用“锁屏控制”，并以管理员身份运行 RemoteDesk。", ex);
            }
        }
    }

    private void EnsureConnected()
    {
        if (_pipe is not null) return;
        if (!WindowsProcessElevation.IsCurrentProcessElevated()) throw new UnauthorizedAccessException();
        string executable = WindowsSecureDesktopInstallation.InstalledExecutable(_ownerSid)
            ?? throw new InvalidOperationException("未安装或未启用锁屏控制服务。");
        WindowsPersistentStartup.ValidateProtectedFile(executable);
        var pipe = new NamedPipeClientStream(".", WindowsSecureDesktopProtocol.PipeName(_ownerSid, _session),
            PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            pipe.ConnectAsync(timeout.Token).GetAwaiter().GetResult();
            var identity = WindowsSecureDesktopNative.ProcessIdentity(WindowsSecureDesktopNative.PipeServerProcessId(pipe.SafePipeHandle));
            if (identity.Sid != "S-1-5-18" || identity.Session != _session ||
                !string.Equals(identity.Image, executable, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("桌面辅助服务身份无效。");
            _pipe = pipe;
            _heartbeat ??= new System.Threading.Timer(_ => Heartbeat(), null, 3000, 3000);
        }
        catch { pipe.Dispose(); throw; }
    }

    private void Heartbeat()
    {
        // No queued timer callbacks while a slow capture is in flight.
        if (!Monitor.TryEnter(_gate)) return;
        try
        {
            if (_disposed || _pipe is null) return;
            if (!IsRequired) { DisconnectCore(); return; }
            try { QueryStatus(); } catch (InvalidOperationException) { }
        }
        finally { Monitor.Exit(_gate); }
    }

    private void Disconnect() { lock (_gate) DisconnectCore(); }
    private void DisconnectCore() { _pipe?.Dispose(); _pipe = null; }
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _heartbeat?.Dispose(); _heartbeat = null;
            DisconnectCore();
        }
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool LockWorkStation();
}
