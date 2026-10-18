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

    [Fact]
    public void ViewerFailureSummaryOnlyIncludesAllowlistedVideoErrors()
    {
        using var temp = TemporaryDirectory.Create();
        using var log = new WindowsDiagnosticLog(temp.Path);
        log.Append("VIEWER", "MF/D3D11 硬解/直显失败：E_ACCESSDENIED 0x80070005");
        log.Append("CLIPBOARD", "sensitive clipboard sample");
        log.Append("VIEWER", "文件保存到 private-path");
        log.Append("HOST", "[VIEWER] MF/D3D11 硬解/直显失败：not-a-viewer-error");
        log.Append("VIEWER", "硬解暂不可用，软件解码组件尚未就绪；1 秒后重试 H.264");
        log.Flush();

        string summary = log.ReadRecentViewerDecodeFailures();
        Assert.Equal(2, summary.Split('\n').Length);
        Assert.Contains("0x80070005", summary);
        Assert.Contains("软件解码组件尚未就绪", summary);
        Assert.DoesNotContain("sensitive", summary);
        Assert.DoesNotContain("private-path", summary);
        Assert.DoesNotContain("not-a-viewer-error", summary);
        Assert.StartsWith("[", summary);
    }

    [Fact]
    public void ViewerFailureSummaryIsBoundedAndSurvivesLogRotation()
    {
        using var temp = TemporaryDirectory.Create();
        using var log = new WindowsDiagnosticLog(temp.Path, maxLogBytes: 1024);
        log.Append("VIEWER", "MF/D3D11 硬解/直显失败：previous-" + new string('x', 1200));
        log.Append("VIEWER", "H.264 解码失步 current");
        log.Flush();
        string summary = log.ReadRecentViewerDecodeFailures();
        Assert.Contains("previous-", summary);
        Assert.Contains("current", summary);
        Assert.All(summary.Split('\n'), line => Assert.True(line.Length <= 192));
    }

    [Fact]
    public void ViewerFailureSummaryOnlyKeepsLastSixErrorsAndToleratesMissingLogs()
    {
        using var temp = TemporaryDirectory.Create();
        using var log = new WindowsDiagnosticLog(temp.Path);
        Assert.Empty(log.ReadRecentViewerDecodeFailures());
        for (int i = 0; i < 20; i++) log.Append("VIEWER", $"H.264 解码失步 error-{i:D2}");
        log.Flush();
        string summary = log.ReadRecentViewerDecodeFailures();
        Assert.Equal(6, summary.Split('\n').Length);
        Assert.DoesNotContain("error-13", summary);
        Assert.Contains("error-14", summary);
        Assert.Contains("error-19", summary);
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
