using System.Diagnostics;
using System.Globalization;
using System.Security;

namespace RemoteDesk;

internal static class WindowsUpdateCleanup
{
    // Older updaters did not hold their rollback file open while checking the new process.
    // The timestamp is the transaction start, not the old executable's last-write time.
    internal static bool IsRetiredRollback(string name, DateTime utcNow)
    {
        string[] parts = name.Split('.');
        return parts.Length == 5 && parts[0].Equals("RemoteDesk", StringComparison.OrdinalIgnoreCase) &&
            parts[1].Equals("exe", StringComparison.OrdinalIgnoreCase) && parts[4] == "old" &&
            Guid.TryParseExact(parts[3], "N", out _) &&
            DateTime.TryParseExact(parts[2], "yyyyMMddHHmmssfff", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var started) &&
            started < utcNow.AddHours(-1);
    }

    internal static int CleanManagedInstallation()
    {
        string current = Application.ExecutablePath;
        string root = WindowsPersistentStartup.InstallRoot;
        if (!WindowsPersistentStartup.IsManagedExecutable(current, root) || !WindowsProcessElevation.IsCurrentProcessElevated())
            return 0;
        WindowsPersistentStartup.ValidateProtectedFile(current);
        string? build = RemoteDeskBuildInfo.ReadExecutableBuildStamp(current);
        if (build is null || !IsRegisteredCurrent(current)) return 0;

        int removed = 0;
        var now = DateTime.UtcNow;
        foreach (var directory in new DirectoryInfo(root).EnumerateDirectories())
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            string executable = Path.Combine(directory.FullName, "RemoteDesk.exe");
            if (!WindowsPersistentStartup.IsManagedExecutable(executable, root)) continue;
            foreach (var old in directory.EnumerateFiles("RemoteDesk.exe.*.old"))
                if (IsRetiredRollback(old.Name, now) && DeleteOlderProgram(old.FullName, build)) removed++;

            if (string.Equals(current, executable, StringComparison.OrdinalIgnoreCase) || !File.Exists(executable)) continue;
            // Re-read both registrations, including a disabled lock-screen service, before retiring a package.
            if (!IsRegisteredCurrent(current) || !WindowsSecureDesktopInstallation.CanRetirePackage(
                    WindowsPersistentStartup.UserSid, executable) || MayBeRunning(executable)) continue;
            if (!DeleteOlderProgram(executable, build)) continue;
            removed++;
            // Other files in a former installation can belong to its owner.
            if (!directory.EnumerateFileSystemInfos().Any()) directory.Delete(recursive: false);
        }
        return removed;
    }

    private static bool IsRegisteredCurrent(string current) =>
        string.Equals(WindowsPersistentStartup.GetStatus().ExecutablePath, current, StringComparison.OrdinalIgnoreCase);

    private static bool MayBeRunning(string executable)
    {
        Process[] processes = Process.GetProcessesByName("RemoteDesk");
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (process.MainModule?.FileName is not string path ||
                        string.Equals(path, executable, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (InvalidOperationException) { /* process exited */ }
                catch (System.ComponentModel.Win32Exception) { return true; }
            }
            return false;
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private static bool DeleteOlderProgram(string path, string currentBuild)
    {
        try
        {
            WindowsPersistentStartup.ValidateProtectedFile(path);
            var version = FileVersionInfo.GetVersionInfo(path);
            if (!string.Equals(version.ProductName, "RemoteDesk", StringComparison.OrdinalIgnoreCase) ||
                RemoteDeskBuildInfo.CompareBuildStamps(RemoteDeskBuildInfo.ReadExecutableBuildStamp(path), currentBuild) is not < 0)
                return false;
            File.Delete(path);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            // An executing image or active updater may still own the file. Retry at the next host startup.
            return false;
        }
    }
}
