using System.Diagnostics;
using System.Globalization;
using System.Reflection;

namespace RemoteDesk;

internal static class RemoteDeskBuildInfo
{
    private const string BuildStampMetadataName = "RemoteDeskBuildStamp";
    private const string BuildStampFormat = "yyyyMMddHHmmss";
    private const string ExecutableBuildStampMarker = "+remotedesk.";
    private const string Unknown = "未知";

    public static string BuildStamp { get; } =
        NormalizeBuildStamp(ReadAssemblyMetadata(BuildStampMetadataName)) ?? Unknown;

    public static string? NormalizeBuildStamp(string? stamp)
    {
        if (string.IsNullOrWhiteSpace(stamp))
        {
            return null;
        }

        string trimmed = stamp.Trim();
        return trimmed.Length == BuildStampFormat.Length &&
            trimmed.All(char.IsDigit)
            ? trimmed
            : null;
    }

    public static int? CompareBuildStamps(string? left, string? right)
    {
        string? normalizedLeft = NormalizeBuildStamp(left);
        string? normalizedRight = NormalizeBuildStamp(right);
        if (normalizedLeft is null || normalizedRight is null)
        {
            return null;
        }

        return string.CompareOrdinal(normalizedLeft, normalizedRight);
    }

    public static string FormatBuildStamp(string? stamp)
    {
        string? normalized = NormalizeBuildStamp(stamp);
        if (normalized is null)
        {
            return Unknown;
        }

        return DateTime.TryParseExact(
            normalized,
            BuildStampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTime parsed)
            ? parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)
            : normalized;
    }

    public static string FormatShortBuildStamp(string? stamp)
    {
        string? normalized = NormalizeBuildStamp(stamp);
        if (normalized is null)
        {
            return Unknown;
        }

        return DateTime.TryParseExact(
            normalized,
            BuildStampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTime parsed)
            ? parsed.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.CurrentCulture)
            : normalized;
    }

    internal static string? ReadExecutableBuildStamp(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            string? productVersion = FileVersionInfo.GetVersionInfo(path).ProductVersion;
            if (string.IsNullOrWhiteSpace(productVersion))
            {
                return null;
            }

            int markerIndex = productVersion.LastIndexOf(
                ExecutableBuildStampMarker,
                StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                return null;
            }

            int stampStart = markerIndex + ExecutableBuildStampMarker.Length;
            return productVersion.Length >= stampStart + BuildStampFormat.Length
                ? NormalizeBuildStamp(productVersion.Substring(stampStart, BuildStampFormat.Length))
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadAssemblyMetadata(string key)
    {
        return Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.Ordinal))
            ?.Value;
    }
}
