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
        string? currentVersion = FileVersionInfo.GetVersionInfo(current).ProductVersion;

        int removed = 0;
        var now = DateTime.UtcNow;
        foreach (var directory in new DirectoryInfo(root).EnumerateDirectories())
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            string executable = Path.Combine(directory.FullName, "RemoteDesk.exe");
            if (!WindowsPersistentStartup.IsManagedExecutable(executable, root)) continue;
            foreach (var old in directory.EnumerateFiles("RemoteDesk.exe.*.old"))
                if (IsRetiredRollback(old.Name, now) && DeleteOlderProgram(old.FullName, build, currentVersion)) removed++;

            if (string.Equals(current, executable, StringComparison.OrdinalIgnoreCase) || !File.Exists(executable)) continue;
            // Re-read both registrations, including a disabled lock-screen service, before retiring a package.
            if (!IsRegisteredCurrent(current) || !WindowsSecureDesktopInstallation.CanRetirePackage(
                    WindowsPersistentStartup.UserSid, executable) || MayBeRunning(executable)) continue;
            if (!DeleteOlderProgram(executable, build, currentVersion)) continue;
            removed++;
            foreach (string name in WindowsPersistentStartup.LicenseFileNames)
            {
                string license = Path.Combine(directory.FullName, name);
                if (!File.Exists(license)) continue;
                try
                {
                    WindowsPersistentStartup.ValidateProtectedFile(license);
                    File.Delete(license);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException) { }
            }
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

    internal static bool IsOlderLegacyVersion(string? candidate, string? current)
    {
        static Version? ReadVersion(string? productVersion)
        {
            int marker = productVersion?.IndexOf("+remotedesk.", StringComparison.OrdinalIgnoreCase) ?? -1;
            if (marker <= 0 || !Version.TryParse(productVersion![..marker], out var version)) return null;
            return new Version(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));
        }
        return ReadVersion(candidate) is Version previous && ReadVersion(current) is Version installed && previous < installed;
    }

    private static bool DeleteOlderProgram(string path, string currentBuild, string? currentVersion)
    {
        try
        {
            WindowsPersistentStartup.ValidateProtectedFile(path);
            var version = FileVersionInfo.GetVersionInfo(path);
            if (!string.Equals(version.ProductName, "RemoteDesk", StringComparison.OrdinalIgnoreCase)) return false;
            int? order = RemoteDeskBuildInfo.CompareBuildStamps(RemoteDeskBuildInfo.ReadExecutableBuildStamp(path), currentBuild);
            bool older = order.HasValue ? order.Value < 0 : IsOlderLegacyVersion(version.ProductVersion, currentVersion);
            if (!older) return false;
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
