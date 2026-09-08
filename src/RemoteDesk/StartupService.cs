using Microsoft.Win32;

namespace RemoteDesk;

internal static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "RemoteDesk";

    public static StartupRegistrationStatus GetStatus()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            string? command = key?.GetValue(
                ValueName,
                defaultValue: null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            return EvaluateRegistration(command, Application.ExecutablePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return StartupRegistrationStatus.Disabled;
        }
    }

    public static bool IsEnabled()
    {
        return GetStatus().IsRegistered;
    }

    public static void SetEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开当前用户开机启动注册表项。");

        if (enabled)
        {
            key.SetValue(
                ValueName,
                BuildRegistrationCommand(Application.ExecutablePath),
                RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    internal static StartupRegistrationStatus EvaluateRegistration(
        string? command,
        string currentExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return StartupRegistrationStatus.Disabled;
        }

        string? targetPath = TryReadExecutablePath(command);
        bool targetsCurrentExecutable = targetPath is not null &&
            PathsEqual(targetPath, currentExecutablePath);
        return new StartupRegistrationStatus(
            IsRegistered: true,
            TargetsCurrentExecutable: targetsCurrentExecutable,
            TargetExecutablePath: targetPath);
    }

    internal static string BuildRegistrationCommand(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        return $"\"{executablePath}\" --tray";
    }

    internal static bool IsOrphanedRegistration(
        StartupRegistrationStatus status,
        Func<string, bool>? pathExists = null)
    {
        if (!status.IsRegistered || status.TargetsCurrentExecutable)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(status.TargetExecutablePath))
        {
            return true;
        }

        pathExists ??= File.Exists;
        try
        {
            return !pathExists(status.TargetExecutablePath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static string? TryReadExecutablePath(string command)
    {
        string value = Environment.ExpandEnvironmentVariables(command).TrimStart();
        if (value.Length == 0)
        {
            return null;
        }

        if (value[0] == '"')
        {
            int closingQuote = value.IndexOf('"', 1);
            return closingQuote > 1
                ? value[1..closingQuote]
                : null;
        }

        int separator = value.IndexOfAny([' ', '\t']);
        return separator < 0
            ? value
            : separator == 0
                ? null
                : value[..separator];
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            string normalizedLeft = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
            string normalizedRight = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
            return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

internal readonly record struct StartupRegistrationStatus(
    bool IsRegistered,
    bool TargetsCurrentExecutable,
    string? TargetExecutablePath)
{
    public static StartupRegistrationStatus Disabled { get; } = new(
        IsRegistered: false,
        TargetsCurrentExecutable: false,
        TargetExecutablePath: null);
}
