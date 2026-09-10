using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace RemoteDesk;

internal sealed record SecureDesktopRequest(string Operation, RemoteInputCommand Command = default,
    int Left = 0, int Top = 0, int Width = 0, int Height = 0, int FrameWidth = 0, int FrameHeight = 0,
    int Quality = 90, int Scale = 100, string? CaptureTargetId = null);
internal sealed record SecureDesktopReply(string Status, int Left = 0, int Top = 0, int Width = 0, int Height = 0,
    int FrameWidth = 0, int FrameHeight = 0, double CaptureMilliseconds = 0, double EncodeMilliseconds = 0,
    int JpegLength = 0, string? Error = null, int CursorX = 0, int CursorY = 0, bool ShiftDown = false);

internal static class WindowsSecureDesktopProtocol
{
    internal const int MaxMessageBytes = 4096;
    internal const int MaxJpegBytes = 24 * 1024 * 1024;
    internal static string PipeName(string sid, uint session) =>
        "RemoteDesk-SecureDesktop-v1-" + WindowsSecureDesktopInstallation.ValidateSid(sid) + "-" + session;

    internal static PipeSecurity PipeSecurity()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        // A local privileged IPC surface, never an SMB/network pipe endpoint.
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        foreach (WellKnownSidType sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    internal static bool AllowsClient(string? clientSid, bool elevated, int clientSession, string ownerSid, uint session) =>
        elevated && clientSid == ownerSid && clientSession == session && session != 0;

    internal static void Validate(SecureDesktopRequest request)
    {
        if (request.Operation is not ("status" or "capture" or "input" or "release-key" or "release-mouse" or "paste"))
            throw new InvalidDataException("不支持的桌面辅助操作。");
        if (request.Operation == "capture")
        {
            if (request.CaptureTargetId is { } target && (target.Length is < 1 or > 128 || target.Contains('\0')))
                throw new InvalidDataException("屏幕标识无效。");
            if (request.Quality is < 30 or > 100 || request.Scale is < 1 or > 100)
                throw new InvalidDataException("画面参数无效。");
            ValidateGeometry(request.Left, request.Top, request.Width, request.Height);
        }
        else if (request.Operation == "input")
        {
            ValidateGeometry(request.Left, request.Top, request.Width, request.Height);
            ValidateFrameSize(request.FrameWidth, request.FrameHeight);
            InputInjector.ValidateCommand(request.Command, new Size(request.FrameWidth, request.FrameHeight));
        }
        else if (request.Operation == "release-key")
        {
            if (request.Command.Kind != RemoteInputKind.KeyDown) throw new InvalidDataException("按键释放参数无效。");
            InputInjector.ValidateCommand(request.Command);
        }
        else if (request.Operation == "release-mouse" && request.Command.Button is not
            (RemoteMouseButton.Left or RemoteMouseButton.Right or RemoteMouseButton.Middle))
            throw new InvalidDataException("鼠标释放参数无效。");
    }

    internal static void ValidateGeometry(int left, int top, int width, int height)
    {
        if (left is < -32768 or > 32768 || top is < -32768 or > 32768 ||
            width is < 1 or > 32768 || height is < 1 or > 32768)
            throw new InvalidDataException("屏幕范围无效。");
    }

    internal static void ValidateFrameSize(int width, int height)
    {
        if (width is < 1 or > RemoteMessageCodec.MaxFrameDimension || height is < 1 or > RemoteMessageCodec.MaxFrameDimension ||
            (long)width * height > RemoteMessageCodec.MaxFramePixels)
            throw new InvalidDataException("登录画面过大，请选择单个屏幕。");
    }

    internal static void Write<T>(Stream stream, T value, CancellationToken cancellation)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length is < 1 or > MaxMessageBytes) throw new InvalidDataException("辅助消息过大。");
        byte[] frame = new byte[4 + bytes.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, bytes.Length);
        bytes.CopyTo(frame, 4);
        stream.WriteAsync(frame, cancellation).AsTask().GetAwaiter().GetResult();
    }

    internal static T Read<T>(Stream stream, CancellationToken cancellation)
    {
        byte[] header = new byte[4];
        stream.ReadExactlyAsync(header, cancellation).AsTask().GetAwaiter().GetResult();
        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is < 1 or > MaxMessageBytes) throw new InvalidDataException("辅助消息长度无效。");
        byte[] bytes = new byte[length];
        stream.ReadExactlyAsync(bytes, cancellation).AsTask().GetAwaiter().GetResult();
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("辅助消息为空。");
    }
}
