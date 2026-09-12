using System.Reflection;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class WindowsFfmpegDependencyTests
{
    [Theory]
    [InlineData("FFmpeg does not expose the gfxcapture source filter.", true)]
    [InlineData("No compatible hardware encoder", false)]
    [InlineData("Access denied to the current desktop", false)]
    [InlineData(null, false)]
    public void OnlyMissingCaptureInterfaceRequestsCompanionUpgrade(string? failure, bool expected)
    {
        Assert.Equal(expected, WindowsFfmpegDependency.NeedsGraphicsCaptureUpgrade(failure));
    }

    [Fact]
    public void ExistingWingetAndManagedCompanionDoNotDependOnInheritedPath()
    {
        string root = Path.GetFullPath("test-output/profile with spaces");
        string[] paths = WindowsFfmpegDependency.CompanionPaths(root).ToArray();
        Assert.Contains(Path.Combine(root, "RemoteDesk", "Dependencies", "ffmpeg-8.1.2", "ffmpeg.exe"), paths);
        Assert.Contains(Path.Combine(root, "Microsoft", "WinGet", "Links", "ffmpeg.exe"), paths);
        Assert.All(paths, path => Assert.StartsWith(root + Path.DirectorySeparatorChar, path));
    }

    [Fact]
    public void RefreshInvalidatesOnlyTheProcessCache()
    {
        long before = FfmpegH264Decoder.AvailabilityVersion;
        FfmpegH264Decoder.RefreshAvailablePaths();
        Assert.True(FfmpegH264Decoder.AvailabilityVersion > before);
    }

    [Fact]
    public void EmbeddedInstallerRetainsPinnedHashAndCapabilityVerification()
    {
        using var stream = typeof(WindowsFfmpegDependency).Assembly.GetManifestResourceStream("RemoteDesk.InstallFfmpeg.ps1");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        string source = reader.ReadToEnd();
        Assert.Contains(WindowsFfmpegDependency.ExecutableSha256, source);
        Assert.Contains("db580001caa24ac104c8cb856cd113a87b0a443f7bdf47d8c12b1d740584a2ec", source);
        Assert.Contains("gfxcapture", source);
        Assert.Contains("h264_qsv", source);
        Assert.Contains("FFMPEG-LICENSE.txt", source);
    }

    [Fact]
    public void UnverifiedOrIncompleteCompanionIsNotAccepted()
    {
        string directory = Path.Combine(Path.GetTempPath(), "RemoteDesk-dependency-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "ffmpeg.exe"), "not a trusted executable");
            File.WriteAllText(Path.Combine(directory, "FFMPEG-LICENSE.txt"), "fixture");
            File.WriteAllText(Path.Combine(directory, "FFMPEG-README.txt"), "fixture");
            Assert.False(WindowsFfmpegDependency.HasVerifiedExecutable(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task UntrustedArchiveIsRejectedBeforeInstallationAndStagingIsCleaned()
    {
        string directory = Path.Combine(Path.GetTempPath(), "RemoteDesk-dependency-rejection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string archive = Path.Combine(directory, "invalid.zip");
            await File.WriteAllTextAsync(archive, "untrusted archive; never execute or expand");
            string destination = Path.Combine(directory, "dependency");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await Assert.ThrowsAsync<IOException>(() => WindowsFfmpegDependency.InstallAsync(
                destination, _ => { }, deadline.Token, archive));
            Assert.False(Directory.Exists(destination));
            Assert.Empty(Directory.GetDirectories(directory));
            Assert.True(File.Exists(archive)); // Caller-owned files must not be deleted.
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task CancelledInstallDoesNotCreateOrChangeDirectories()
    {
        string directory = Path.Combine(Path.GetTempPath(), "RemoteDesk-dependency-cancel-" + Guid.NewGuid().ToString("N"));
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WindowsFfmpegDependency.InstallAsync(directory, _ => { }, stop.Token));
        Assert.False(Directory.Exists(directory));
    }
}
