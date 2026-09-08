using System.Collections.Concurrent;
using System.Text;

namespace RemoteDesk;

internal sealed class WindowsDiagnosticLog : IDisposable
{
    internal const long DefaultMaxLogBytes = 512 * 1024;
    private const string DiagnosticsFolderName = "Diagnostics";
    private const string CurrentLogFileName = "remotedesk-windows-current.log";
    private const string PreviousLogFileName = "remotedesk-windows-current.log.old";

    private static readonly Lazy<WindowsDiagnosticLog> DefaultLog =
        new(CreateDefaultCore, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly BlockingCollection<LogCommand> _commands =
        new(new ConcurrentQueue<LogCommand>());
    private readonly string _logDirectory;
    private readonly long _maxLogBytes;
    private readonly Thread _writerThread;
    private int _disposed;

    public WindowsDiagnosticLog(
        string logDirectory,
        long maxLogBytes = DefaultMaxLogBytes)
    {
        if (string.IsNullOrWhiteSpace(logDirectory))
        {
            throw new ArgumentException("日志目录不能为空。", nameof(logDirectory));
        }

        _logDirectory = logDirectory;
        _maxLogBytes = Math.Max(1024, maxLogBytes);
        _writerThread = new Thread(WriteLoop)
        {
            IsBackground = true,
            Name = "RemoteDesk diagnostics writer",
            Priority = ThreadPriority.BelowNormal
        };
        _writerThread.Start();
    }

    public static WindowsDiagnosticLog CreateDefault() =>
        DefaultLog.Value;

    private static WindowsDiagnosticLog CreateDefaultCore()
    {
        string appData = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
        {
            appData = AppContext.BaseDirectory;
        }

        return new WindowsDiagnosticLog(
            Path.Combine(
                appData,
                "RemoteDesk",
                DiagnosticsFolderName));
    }

    public string CurrentLogPath =>
        Path.Combine(_logDirectory, CurrentLogFileName);

    public string PreviousLogPath =>
        Path.Combine(_logDirectory, PreviousLogFileName);

    public void Append(string category, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        TryEnqueue(
            new AppendCommand(
                FormatLine(category, message)));
    }

    public void Flush()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var completion =
            new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        if (TryEnqueue(new FlushCommand(completion)))
        {
            completion.Task.GetAwaiter().GetResult();
        }
    }

    public void ExportTo(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException(
                "导出路径不能为空。",
                nameof(outputPath));
        }

        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        var completion =
            new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        if (!TryEnqueue(
                new ExportCommand(
                    outputPath,
                    completion)))
        {
            throw new ObjectDisposedException(
                nameof(WindowsDiagnosticLog));
        }

        completion.Task.GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _commands.CompleteAdding();
        _writerThread.Join();
        _commands.Dispose();
    }

    private bool TryEnqueue(LogCommand command)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        try
        {
            _commands.Add(command);
            return true;
        }
        catch (Exception ex)
            when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return false;
        }
    }

    private void WriteLoop()
    {
        var pendingLines = new List<string>(32);
        foreach (LogCommand command in
            _commands.GetConsumingEnumerable())
        {
            ProcessCommand(command, pendingLines);
            while (_commands.TryTake(out LogCommand? queued))
            {
                ProcessCommand(queued, pendingLines);
            }

            WritePendingLines(pendingLines);
        }

        WritePendingLines(pendingLines);
    }

    private void ProcessCommand(
        LogCommand command,
        List<string> pendingLines)
    {
        if (command is AppendCommand append)
        {
            pendingLines.Add(append.Line);
            return;
        }

        WritePendingLines(pendingLines);
        switch (command)
        {
            case FlushCommand flush:
                flush.Completion.TrySetResult(true);
                break;
            case ExportCommand export:
                try
                {
                    ExportCore(export.OutputPath);
                    export.Completion.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    export.Completion.TrySetException(ex);
                }

                break;
        }
    }

    private void WritePendingLines(List<string> pendingLines)
    {
        if (pendingLines.Count == 0)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_logDirectory);
            AppendLinesWithRotation(pendingLines);
        }
        catch (Exception ex) when (IsLogIoException(ex))
        {
        }
        finally
        {
            pendingLines.Clear();
        }
    }

    private void AppendLinesWithRotation(
        IReadOnlyList<string> lines)
    {
        string currentPath = CurrentLogPath;
        bool currentExists = File.Exists(currentPath);
        long currentBytes = currentExists
            ? new FileInfo(currentPath).Length
            : 0;
        var segment = new StringBuilder();

        foreach (string line in lines)
        {
            long lineBytes = Encoding.UTF8.GetByteCount(line);
            if (currentExists &&
                currentBytes + lineBytes > _maxLogBytes)
            {
                AppendSegment(currentPath, segment);
                RotateCurrentLog(currentPath);
                currentExists = false;
                currentBytes = 0;
            }

            segment.Append(line);
            currentExists = true;
            currentBytes += lineBytes;
        }

        AppendSegment(currentPath, segment);
    }

    private static void AppendSegment(
        string currentPath,
        StringBuilder segment)
    {
        if (segment.Length == 0)
        {
            return;
        }

        File.AppendAllText(
            currentPath,
            segment.ToString(),
            Encoding.UTF8);
        segment.Clear();
    }

    private void RotateCurrentLog(string currentPath)
    {
        string previousPath = PreviousLogPath;
        if (File.Exists(previousPath))
        {
            File.Delete(previousPath);
        }

        if (File.Exists(currentPath))
        {
            File.Move(currentPath, previousPath);
        }
    }

    private void ExportCore(string outputPath)
    {
        string? outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        using var output =
            new StreamWriter(
                outputPath,
                append: false,
                Encoding.UTF8);
        output.WriteLine(
            "RemoteDesk Windows diagnostics exported at " +
            $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        output.WriteLine();
        WriteLogFile(output, PreviousLogPath, "previous");
        WriteLogFile(output, CurrentLogPath, "current");
    }

    private static void WriteLogFile(
        TextWriter output,
        string path,
        string label)
    {
        output.WriteLine(
            $"===== {label} log: {Path.GetFileName(path)} =====");
        if (!File.Exists(path))
        {
            output.WriteLine("(not present)");
            output.WriteLine();
            return;
        }

        using var input =
            new StreamReader(
                path,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true);
        string? line;
        while ((line = input.ReadLine()) is not null)
        {
            output.WriteLine(line);
        }

        output.WriteLine();
    }

    private static string FormatLine(
        string category,
        string message)
    {
        string normalizedCategory =
            string.IsNullOrWhiteSpace(category)
                ? "APP"
                : NormalizeLogText(category)
                    .ToUpperInvariant();
        return
            $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] " +
            $"[{normalizedCategory}] {NormalizeLogText(message)}" +
            Environment.NewLine;
    }

    private static string NormalizeLogText(string text)
    {
        return text
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
    }

    private static bool IsLogIoException(Exception ex)
    {
        return ex is IOException or
            UnauthorizedAccessException or
            NotSupportedException or
            ArgumentException;
    }

    private abstract record LogCommand;

    private sealed record AppendCommand(string Line) : LogCommand;

    private sealed record FlushCommand(
        TaskCompletionSource<bool> Completion) : LogCommand;

    private sealed record ExportCommand(
        string OutputPath,
        TaskCompletionSource<bool> Completion) : LogCommand;
}
