using Xunit;

namespace RemoteDesk.Tests;

public sealed class WindowsDiagnosticLogTests
{
    [Fact]
    public void AppendWritesNormalizedLogLine()
    {
        using var temp = TemporaryDirectory.Create();
        using var log = new WindowsDiagnosticLog(temp.Path);

        log.Append("viewer", "first line\r\nsecond line");
        log.Flush();

        string content = File.ReadAllText(log.CurrentLogPath);
        Assert.Contains("[VIEWER]", content, StringComparison.Ordinal);
        Assert.Contains("first line  second line", content, StringComparison.Ordinal);
    }

    [Fact]
    public void AppendRotatesCurrentLogWhenItExceedsLimit()
    {
        using var temp = TemporaryDirectory.Create();
        using var log = new WindowsDiagnosticLog(
            temp.Path,
            maxLogBytes: 1024);
        string largeMessage = new('x', 1200);

        log.Append("host", largeMessage);
        log.Append("host", "after rotation");
        log.Flush();

        Assert.True(File.Exists(log.PreviousLogPath));
        Assert.True(File.Exists(log.CurrentLogPath));
        Assert.Contains(largeMessage, File.ReadAllText(log.PreviousLogPath), StringComparison.Ordinal);
        Assert.Contains("after rotation", File.ReadAllText(log.CurrentLogPath), StringComparison.Ordinal);
    }

    [Fact]
    public void ExportToCombinesPreviousAndCurrentLogs()
    {
        using var temp = TemporaryDirectory.Create();
        using var log = new WindowsDiagnosticLog(
            temp.Path,
            maxLogBytes: 1024);
        log.Append("host", new string('a', 1200));
        log.Append("viewer", "current entry");

        string exportPath = Path.Combine(temp.Path, "export", "diagnostics.log");
        log.ExportTo(exportPath);

        string exported = File.ReadAllText(exportPath);
        Assert.Contains("previous log", exported, StringComparison.Ordinal);
        Assert.Contains("current log", exported, StringComparison.Ordinal);
        Assert.Contains("[HOST]", exported, StringComparison.Ordinal);
        Assert.Contains("[VIEWER] current entry", exported, StringComparison.Ordinal);
    }

    [Fact]
    public void DisposeFlushesQueuedEntries()
    {
        using var temp = TemporaryDirectory.Create();
        string currentPath;
        using (var log = new WindowsDiagnosticLog(temp.Path))
        {
            currentPath = log.CurrentLogPath;
            for (int index = 0; index < 100; index++)
            {
                log.Append("host", $"queued entry {index}");
            }
        }

        string content = File.ReadAllText(currentPath);
        Assert.Contains("queued entry 0", content, StringComparison.Ordinal);
        Assert.Contains("queued entry 99", content, StringComparison.Ordinal);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"RemoteDesk.WindowsDiagnosticLogTests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
