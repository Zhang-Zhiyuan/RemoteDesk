using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace RemoteDesk;

internal static class SshProxyDeployment
{
    internal const string ResourceName = "RemoteDesk.SshProxy.exe";
    private const string FileName = "RemoteDesk.SshProxy.exe";
    private const long MaximumBytes = 256L * 1024 * 1024;

    internal static string EnsureInstalled()
    {
        using Stream payload = typeof(SshProxyDeployment).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("当前程序未包含 SSH 命令行组件，请使用完整发布版本。");
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
            throw new InvalidOperationException("无法取得当前用户的本地应用数据目录。");
        return EnsureInstalled(payload, Path.Combine(local, "RemoteDesk", "ssh-proxy"));
    }

    // The embedded resource is trusted by the signed/published parent executable.
    // Its hash is both the expected on-disk identity and an immutable version path.
    internal static string EnsureInstalled(Stream payload, string storageRoot)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        ArgumentNullException.ThrowIfNull(payload);
        if (!payload.CanSeek || payload.Length is <= 0 or > MaximumBytes)
            throw new InvalidDataException("内置 SSH 命令行组件大小无效。");
        if (!Path.IsPathFullyQualified(storageRoot))
            throw new ArgumentException("SSH 命令行组件目录必须为绝对路径。", nameof(storageRoot));
        payload.Position = 0;
        byte[] expectedHash = SHA256.HashData(payload);
        long expectedLength = payload.Length;
        payload.Position = 0;
        string directory = Path.Combine(Path.GetFullPath(storageRoot), Convert.ToHexString(expectedHash).ToLowerInvariant());
        string target = Path.Combine(directory, FileName);
        // Reject junctions/symlinks and hold each ancestor without delete sharing
        // until installation ends. An elevated GUI must not follow user-created
        // reparse paths into another location or replace an executing old helper.
        using var pinned = PinDirectories(directory);
        if (File.Exists(target)) { VerifyFile(target, expectedHash, expectedLength); return target; }
        string temporary = Path.Combine(directory, ".ssh-proxy-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                payload.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
            VerifyFile(temporary, expectedHash, expectedLength);
            try { File.Move(temporary, target, overwrite: false); }
            catch (IOException) when (File.Exists(target))
            {
                // Another instance may have installed the same resource. Accept
                // only exactly the same bytes; never overwrite an existing file.
                VerifyFile(target, expectedHash, expectedLength);
            }
            VerifyFile(target, expectedHash, expectedLength);
            return target;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void VerifyFile(string path, byte[] expectedHash, long expectedLength)
    {
        using SafeFileHandle handle = OpenPath(path, directory: false);
        using var stream = new FileStream(handle, FileAccess.Read);
        if (stream.Length != expectedLength || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(stream), expectedHash))
            throw new InvalidDataException("本地 SSH 命令行组件校验失败；未覆盖现有文件，请检查此用户目录。");
    }

    internal static SshFileSafety.PinnedDirectories PinDirectories(string directory) => SshFileSafety.PinDirectories(directory);
    internal static SafeFileHandle OpenPath(string path, bool directory) => SshFileSafety.OpenPath(path, directory);
}
