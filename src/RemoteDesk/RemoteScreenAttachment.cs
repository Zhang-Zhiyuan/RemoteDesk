namespace RemoteDesk;

// Ephemeral, authenticated attachment to an existing controller. Never stored
// in device settings or sent outside the encrypted session.
internal sealed record RemoteScreenAttachment(string Token, string TargetId)
{
    internal static string ValidateToken(string? token) =>
        token is { Length: 64 } && token.All(char.IsAsciiHexDigit)
            ? token.ToUpperInvariant()
            : throw new InvalidDataException("多屏会话凭据无效。");

    internal static string ValidateTarget(string? target) =>
        !string.IsNullOrWhiteSpace(target) && target.Length <= 256 &&
        !string.Equals(target, ScreenCaptureTarget.AllScreensId, StringComparison.OrdinalIgnoreCase)
            ? target
            : throw new InvalidDataException("请为新窗口选择一块独立屏幕。");
}
