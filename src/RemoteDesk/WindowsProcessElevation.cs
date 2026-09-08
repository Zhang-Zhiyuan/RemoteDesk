using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Security.Principal;

namespace RemoteDesk;

internal static class WindowsProcessElevation
{
    internal const string WaitForProcessArgument =
        "--wait-for-process";
    internal const int UserCancelledError = 1223;

    public static bool IsCurrentProcessElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using WindowsIdentity identity =
                WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(
                WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (
            ex is SecurityException or
                UnauthorizedAccessException or
                InvalidOperationException)
        {
            return false;
        }
    }

    public static Process StartElevatedReplacement(
        string executablePath,
        int previousProcessId)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException(
                "无法定位 RemoteDesk.exe。",
                nameof(executablePath));
        }

        string fullPath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                "找不到当前 RemoteDesk.exe。",
                fullPath);
        }

        return Process.Start(
                CreateElevatedReplacementStartInfo(
                    fullPath,
                    previousProcessId))
            ?? throw new InvalidOperationException(
                "Windows 未能启动管理员 RemoteDesk。");
    }

    internal static ProcessStartInfo
        CreateElevatedReplacementStartInfo(
            string executablePath,
            int previousProcessId)
    {
        if (previousProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(previousProcessId));
        }

        string fullPath = Path.GetFullPath(executablePath);
        var startInfo = new ProcessStartInfo
        {
            FileName = fullPath,
            WorkingDirectory =
                Path.GetDirectoryName(fullPath) ??
                Environment.CurrentDirectory,
            UseShellExecute = true,
            Verb = "runas"
        };
        startInfo.ArgumentList.Add("--tray");
        startInfo.ArgumentList.Add(
            RemoteUpdater.ResumeHostAfterUpdateArgument);
        startInfo.ArgumentList.Add(WaitForProcessArgument);
        startInfo.ArgumentList.Add(
            previousProcessId.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        return startInfo;
    }

    internal static int? TryReadPreviousProcessId(
        IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        for (int index = 0;
             index + 1 < arguments.Count;
             index++)
        {
            if (!string.Equals(
                    arguments[index],
                    WaitForProcessArgument,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return int.TryParse(
                    arguments[index + 1],
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int processId) &&
                processId > 0
                    ? processId
                    : null;
        }

        return null;
    }

    internal static bool WaitForPreviousProcess(
        int? previousProcessId,
        TimeSpan timeout)
    {
        if (previousProcessId is null)
        {
            return true;
        }

        if (previousProcessId <= 0 ||
            previousProcessId == Environment.ProcessId ||
            timeout <= TimeSpan.Zero)
        {
            return false;
        }

        try
        {
            using Process process =
                Process.GetProcessById(previousProcessId.Value);
            int timeoutMilliseconds = checked(
                (int)Math.Min(
                    timeout.TotalMilliseconds,
                    int.MaxValue));
            return process.WaitForExit(timeoutMilliseconds);
        }
        catch (ArgumentException)
        {
            // The previous process already exited before the elevated copy
            // opened its process handle.
            return true;
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or
                Win32Exception or
                NotSupportedException)
        {
            return false;
        }
    }

    internal static bool IsUserCancellation(
        Win32Exception exception) =>
        exception.NativeErrorCode == UserCancelledError;
}
