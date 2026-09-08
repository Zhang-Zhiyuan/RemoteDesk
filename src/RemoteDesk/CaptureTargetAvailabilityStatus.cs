using System.Text;

namespace RemoteDesk;

internal readonly record struct CaptureTargetAvailabilityStatusData(
    bool IsAvailable,
    CaptureTargetInfo Target,
    int TargetGeneration,
    string DisplayMessage);

/// <summary>
/// Carries capture-target availability over the legacy ClipboardStatus
/// control kind. Older peers still show the human-readable Chinese suffix;
/// newer Windows viewers recognize the exact, non-localized machine prefix
/// and surface it as capture state rather than clipboard state.
/// </summary>
internal static class CaptureTargetAvailabilityStatusCodec
{
    internal const string MachinePrefix =
        "RemoteDesk.CaptureTargetStatus/v1|";
    internal const string TrailerSeparator = "\n";

    private const string AvailableState = "available";
    private const string UnavailableState = "unavailable";
    private const int MaxTargetFieldCharacters = 512;
    private static readonly UTF8Encoding StrictUtf8 =
        new(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

    public static CaptureTargetAvailabilityStatusData Create(
        bool isAvailable,
        CaptureTargetInfo target,
        int targetGeneration = 0)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (string.IsNullOrWhiteSpace(target.Id) ||
            string.IsNullOrWhiteSpace(target.DisplayName))
        {
            throw new ArgumentException(
                "The capture target id and display name are required.",
                nameof(target));
        }

        string displayMessage = isAvailable
            ? $"捕获目标已恢复：{target.DisplayName}；正在等待新画面。"
            : $"捕获目标暂不可用：{target.DisplayName}；" +
              "画面和指针输入已暂停；请重新连接该屏幕或选择其他屏幕。";
        return new CaptureTargetAvailabilityStatusData(
            isAvailable,
            target,
            targetGeneration,
            displayMessage);
    }

    public static string Encode(
        CaptureTargetAvailabilityStatusData status)
    {
        if (string.IsNullOrWhiteSpace(status.Target.Id) ||
            string.IsNullOrWhiteSpace(status.Target.DisplayName) ||
            string.IsNullOrWhiteSpace(status.DisplayMessage))
        {
            throw new ArgumentException(
                "The capture-target status is incomplete.",
                nameof(status));
        }

        return status.DisplayMessage.Trim() +
            TrailerSeparator + MachinePrefix +
            (status.IsAvailable
                ? AvailableState
                : UnavailableState) +
            "|" + EncodeField(status.Target.Id) +
            "|" + EncodeField(status.Target.DisplayName) +
            "|" + status.TargetGeneration.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
    }

    public static bool TryParse(
        string? message,
        out CaptureTargetAvailabilityStatusData status)
    {
        status = default;
        if (string.IsNullOrEmpty(message))
        {
            return false;
        }

        string trailerMarker =
            TrailerSeparator + MachinePrefix;
        int trailerIndex = message.LastIndexOf(
            trailerMarker,
            StringComparison.Ordinal);
        if (trailerIndex <= 0 ||
            message.IndexOf(
                trailerMarker,
                StringComparison.Ordinal) != trailerIndex)
        {
            return false;
        }

        string displayMessage =
            message[..trailerIndex];
        string[] fields = message[
                (trailerIndex + trailerMarker.Length)..]
            .Split('|', StringSplitOptions.None);
        if (fields.Length != 4 ||
            (fields[0] != AvailableState &&
                fields[0] != UnavailableState) ||
            !TryDecodeField(fields[1], out string targetId) ||
            !TryDecodeField(fields[2], out string displayName) ||
            !int.TryParse(
                fields[3],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out int targetGeneration) ||
            targetGeneration < 0 ||
            string.IsNullOrWhiteSpace(targetId) ||
            string.IsNullOrWhiteSpace(displayName) ||
            string.IsNullOrWhiteSpace(displayMessage))
        {
            return false;
        }

        status = new CaptureTargetAvailabilityStatusData(
            fields[0] == AvailableState,
            new CaptureTargetInfo(targetId, displayName),
            targetGeneration,
            displayMessage);
        return true;
    }

    private static string EncodeField(string value) =>
        Convert.ToBase64String(
            Encoding.UTF8.GetBytes(value));

    private static bool TryDecodeField(
        string value,
        out string decoded)
    {
        decoded = string.Empty;
        if (value.Length >
            MaxTargetFieldCharacters * 4)
        {
            return false;
        }

        try
        {
            decoded = StrictUtf8.GetString(
                Convert.FromBase64String(value));
            return decoded.Length <=
                    MaxTargetFieldCharacters &&
                !decoded.Any(char.IsControl) &&
                string.Equals(
                    EncodeField(decoded),
                    value,
                    StringComparison.Ordinal);
        }
        catch (Exception ex) when (
            ex is FormatException or DecoderFallbackException)
        {
            return false;
        }
    }
}
