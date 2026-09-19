using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace RemoteDesk;

internal enum SecureDesktopFailureStage
{
    RequestValidation,
    Elevation,
    Registration,
    MissingInstallation,
    DisabledInstallation,
    InstallationValidation,
    PipeConnection,
    ServerIdentity,
    Exchange,
    HelperRejected
}

internal enum SecureDesktopServerIdentityMismatch
{
    SystemAccount,
    Session,
    Executable
}

internal sealed class SecureDesktopServerIdentityException : UnauthorizedAccessException
{
    internal SecureDesktopServerIdentityMismatch Mismatch { get; }

    internal SecureDesktopServerIdentityException(SecureDesktopServerIdentityMismatch mismatch)
        : base("Desktop helper identity does not match the protected installation.") => Mismatch = mismatch;
}

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
        => Apply(command, bounds, size, null);

    internal void Apply(RemoteInputCommand command, Rectangle bounds, Size size, bool? expectedSecureDesktop)
    {
        InputInjector.ValidateCommand(command, size);
        if (_shortcuts.TryHandle(command, ReleaseKeyCore, LockCurrentSession)) return;
        if (!Route(new("input", command, bounds.X, bounds.Y, bounds.Width, bounds.Height, size.Width, size.Height), expectedSecureDesktop))
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

    private bool Route(SecureDesktopRequest request, bool? expectedSecureDesktop = null)
    {
        bool required = IsRequired;
        if (expectedSecureDesktop is { } expected && required != expected)
            throw new SecureDesktopTargetException(true);
        if (!required) { Disconnect(); return false; }
        SecureDesktopReply reply = Exchange(request, out _);
        // Inactive proves that no input was injected. Only target-independent
        // operations may fall back; old pointer coordinates are not replayed.
        if (reply.Status == "inactive")
        {
            Disconnect();
            ValidateInactiveFallback(request);
            return false;
        }
        return true;
    }

    internal static void ValidateInactiveFallback(SecureDesktopRequest request)
    {
        // No input was injected, but coordinates belong to the previous
        // desktop. Do not replay a pointer against Default after unlocking.
        if (request.Operation == "input" && request.Command.Kind is RemoteInputKind.MouseMove or
            RemoteInputKind.MouseDown or RemoteInputKind.MouseUp or RemoteInputKind.MouseWheel)
            throw new SecureDesktopTargetException(true);
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
            new Size(reply.FrameWidth, reply.FrameHeight), jpeg, reply.CaptureMilliseconds, reply.EncodeMilliseconds,
            IsSecureDesktop: true, IsSecureDesktopFallback: reply.CaptureTargetFallback);
        return true;
    }

    internal SecureDesktopReply QueryStatus() => Exchange(new("status"), out _);

    private SecureDesktopReply Exchange(SecureDesktopRequest request, out byte[]? jpeg)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            jpeg = null;
            SecureDesktopFailureStage failureStage = SecureDesktopFailureStage.RequestValidation;
            try
            {
                WindowsSecureDesktopProtocol.Validate(request);
                EnsureConnected(ref failureStage);
                failureStage = SecureDesktopFailureStage.Exchange;
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                WindowsSecureDesktopProtocol.Write(_pipe!, request, deadline.Token);
                SecureDesktopReply reply = WindowsSecureDesktopProtocol.Read<SecureDesktopReply>(_pipe!, deadline.Token);
                if (reply.Status is not ("active" or "inactive" or "error")) throw new InvalidDataException("辅助服务响应无效。");
                if (reply.Status == "error")
                {
                    failureStage = SecureDesktopFailureStage.HelperRejected;
                    // The stage is actionable without forwarding helper exception text,
                    // which can contain desktop input or other private native details.
                    throw WindowsSecureDesktopProtocol.HelperFailure(reply);
                }
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
            catch (SecureDesktopTargetException)
            {
                // Keep the fixed, non-sensitive topology classification. It
                // means this request did not inject input; the caller still
                // must not replay it against a newly selected monitor.
                DisconnectCore();
                throw;
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or System.ComponentModel.Win32Exception or
                System.Text.Json.JsonException or UnauthorizedAccessException or InvalidOperationException or
                ArgumentException or System.Security.SecurityException)
            {
                DisconnectCore();
                throw new InvalidOperationException(DescribeFailure(failureStage, ex), ex);
            }
        }
    }

    internal static string DescribeFailure(SecureDesktopFailureStage stage, Exception error)
    {
        // Only fixed labels and numeric OS error codes leave this boundary.
        // Never append error.Message, process paths/SIDs or request contents.
        string reason = stage switch
        {
            SecureDesktopFailureStage.Elevation => "当前被控端没有管理员权限；请在被控电脑上以管理员身份重新运行 RemoteDesk。",
            SecureDesktopFailureStage.Registration => "无法读取当前用户的锁屏服务信息；请在被控电脑上检查“锁屏控制”安装和系统服务权限。",
            SecureDesktopFailureStage.MissingInstallation => "未找到当前用户的有效锁屏控制安装；请在被控电脑上选择“设置锁屏控制 → 安装 / 更新锁屏控制”。",
            SecureDesktopFailureStage.DisabledInstallation => "当前用户的锁屏控制服务未启用；如需恢复，请在被控电脑上选择“设置锁屏控制 → 安装 / 更新锁屏控制”。",
            SecureDesktopFailureStage.InstallationValidation => "锁屏辅助程序的文件或权限校验未通过，已拒绝使用；请在被控电脑上重新安装 / 更新锁屏控制。",
            SecureDesktopFailureStage.PipeConnection => "锁屏控制已启用，但辅助进程尚未连接成功；请稍后重试，持续失败时检查被控电脑的锁屏服务是否正在运行。",
            SecureDesktopFailureStage.ServerIdentity when error is SecureDesktopIdentityQueryException native =>
                $"锁屏辅助进程身份信息读取失败（API {native.Query}，Win32 {native.NativeErrorCode}），已拒绝连接；请在被控电脑上检查锁屏辅助服务。",
            SecureDesktopFailureStage.ServerIdentity when error is SecureDesktopServerIdentityException mismatch =>
                DescribeIdentityMismatch(mismatch.Mismatch),
            SecureDesktopFailureStage.ServerIdentity => "锁屏辅助进程身份校验未通过，已拒绝连接；请在被控电脑上检查或重新安装锁屏控制。",
            SecureDesktopFailureStage.HelperRejected => "锁屏辅助进程已连接，但暂时无法处理当前登录桌面；请稍后重试，持续失败时检查 Windows 应用日志中的 RemoteDesk 事件。",
            SecureDesktopFailureStage.RequestValidation => "锁屏请求参数无效；请刷新远程屏幕后重试。",
            _ when error is OperationCanceledException => "锁屏辅助进程响应超时；连接已释放，请稍后重试。",
            _ when error is InvalidDataException or System.Text.Json.JsonException => "锁屏辅助进程返回了无效响应；连接已释放，请在被控电脑上检查或更新锁屏控制。",
            _ => "与锁屏辅助进程的通信中断；连接已释放，请稍后重试，持续失败时检查被控电脑上的辅助服务。"
        };
        return "锁屏控制暂不可用：" + reason;
    }

    private static string DescribeIdentityMismatch(SecureDesktopServerIdentityMismatch mismatch) => mismatch switch
    {
        SecureDesktopServerIdentityMismatch.SystemAccount => "锁屏辅助进程并非预期的 SYSTEM 身份，已拒绝连接；请在被控电脑上检查锁屏服务安装。",
        SecureDesktopServerIdentityMismatch.Session => "锁屏辅助进程不在当前用户会话，已拒绝连接；请在被控电脑上检查锁屏服务。",
        _ => "锁屏辅助进程的运行程序与已注册安装不一致，已拒绝连接；请在被控电脑上检查或更新锁屏控制。"
    };

    internal static SecureDesktopServerIdentityMismatch? FindServerIdentityMismatch(
        string? actualSid, int actualSession, string actualExecutable, uint expectedSession, string expectedExecutable)
    {
        if (actualSid != "S-1-5-18") return SecureDesktopServerIdentityMismatch.SystemAccount;
        if (actualSession != expectedSession) return SecureDesktopServerIdentityMismatch.Session;
        if (!string.Equals(actualExecutable, expectedExecutable, StringComparison.OrdinalIgnoreCase))
            return SecureDesktopServerIdentityMismatch.Executable;
        return null;
    }

    private void EnsureConnected(ref SecureDesktopFailureStage failureStage)
    {
        if (_pipe is not null) return;
        failureStage = SecureDesktopFailureStage.Elevation;
        if (!WindowsProcessElevation.IsCurrentProcessElevated()) throw new UnauthorizedAccessException();
        failureStage = SecureDesktopFailureStage.Registration;
        string? executable = WindowsSecureDesktopInstallation.InstalledExecutable(_ownerSid, requireEnabled: false);
        if (executable is null)
        {
            failureStage = SecureDesktopFailureStage.MissingInstallation;
            throw new InvalidOperationException("No owned secure desktop installation.");
        }
        if (WindowsSecureDesktopInstallation.InstalledExecutable(_ownerSid) is null)
        {
            failureStage = SecureDesktopFailureStage.DisabledInstallation;
            throw new InvalidOperationException("Owned secure desktop installation is disabled.");
        }
        failureStage = SecureDesktopFailureStage.InstallationValidation;
        WindowsPersistentStartup.ValidateProtectedFile(executable);
        failureStage = SecureDesktopFailureStage.PipeConnection;
        var pipe = new NamedPipeClientStream(".", WindowsSecureDesktopProtocol.PipeName(_ownerSid, _session),
            PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            pipe.ConnectAsync(timeout.Token).GetAwaiter().GetResult();
            failureStage = SecureDesktopFailureStage.ServerIdentity;
            var identity = WindowsSecureDesktopNative.ProcessIdentity(WindowsSecureDesktopNative.PipeServerProcessId(pipe.SafePipeHandle));
            if (FindServerIdentityMismatch(identity.Sid, identity.Session, identity.Image, _session, executable) is { } mismatch)
                throw new SecureDesktopServerIdentityException(mismatch);
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
