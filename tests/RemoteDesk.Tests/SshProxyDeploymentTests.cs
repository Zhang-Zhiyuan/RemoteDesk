using System.Security.Cryptography;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class SshProxyDeploymentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RemoteDesk-SshProxy-Test-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] First = "MZ owned synthetic binary first"u8.ToArray();
    private static readonly byte[] Second = "MZ owned synthetic binary second"u8.ToArray();

    [Fact] public void InstallsExactEmbeddedBytesIntoContentAddressedPathAndReusesThem()
    {
        string path = Install(First);
        Assert.Equal(Path.Combine(_root, Convert.ToHexString(SHA256.HashData(First)).ToLowerInvariant(), "RemoteDesk.SshProxy.exe"), path);
        Assert.Equal(First, File.ReadAllBytes(path));
        Assert.Equal(path, Install(First));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact] public void DifferentVersionNeverOverwritesAnOpenOldHelper()
    {
        string old = Install(First);
        using var running = File.Open(old, FileMode.Open, FileAccess.Read, FileShare.Read);
        string next = Install(Second);
        Assert.NotEqual(old, next);
        Assert.Equal(First, File.ReadAllBytes(old));
        Assert.Equal(Second, File.ReadAllBytes(next));
    }

    [Fact] public void ModifiedCacheIsRejectedWithoutOverwritingIt()
    {
        string path = Install(First);
        byte[] tampered = (byte[])First.Clone(); tampered[^1] ^= 1;
        File.WriteAllBytes(path, tampered);
        Assert.Throws<InvalidDataException>(() => Install(First));
        Assert.Equal(tampered, File.ReadAllBytes(path));
    }

    [Fact] public void InvalidPayloadOrRelativeRootCannotProduceExecutable()
    {
        Assert.Throws<InvalidDataException>(() => Install([]));
        using var bytes = new MemoryStream(First);
        Assert.Throws<ArgumentException>(() => SshProxyDeployment.EnsureInstalled(bytes, "relative-directory"));
        Assert.False(Directory.Exists(_root));
    }

    [Fact] public void PinnedAncestorsCannotBeRenamedDuringElevatedWrite()
    {
        string child = Path.Combine(_root, "parent", "child");
        using (SshFileSafety.PinDirectories(child))
            Assert.Throws<IOException>(() => Directory.Move(Path.Combine(_root, "parent"), Path.Combine(_root, "renamed")));
        Directory.Move(Path.Combine(_root, "parent"), Path.Combine(_root, "renamed"));
        Assert.True(Directory.Exists(Path.Combine(_root, "renamed", "child")));
    }

    [Fact] public void SharedDirectorySafetyRejectsRelativePaths()
    {
        Assert.Throws<ArgumentException>(() => SshFileSafety.PinDirectories("relative-directory"));
        Assert.False(Directory.Exists(_root));
    }

    [Fact] public void DirectoryJunctionCannotRedirectAnElevatedGuiWrite()
    {
        Directory.CreateDirectory(_root);
        string other = Path.Combine(_root, "other"); Directory.CreateDirectory(other);
        string link = Path.Combine(_root, "junction");
        // Junctions require no symbolic-link privilege. Only test-owned paths.
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J");
        start.ArgumentList.Add(link); start.ArgumentList.Add(other);
        using var process = System.Diagnostics.Process.Start(start)!;
        process.WaitForExit(); Assert.Equal(0, process.ExitCode);
        try
        {
            using var bytes = new MemoryStream(First);
            Assert.Throws<IOException>(() => SshProxyDeployment.EnsureInstalled(bytes, link));
            Assert.Empty(Directory.GetFileSystemEntries(other));
        }
        finally { Directory.Delete(link); }
    }

    private string Install(byte[] bytes) { using var stream = new MemoryStream(bytes); return SshProxyDeployment.EnsureInstalled(stream, _root); }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
