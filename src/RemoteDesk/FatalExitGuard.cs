namespace RemoteDesk;

internal static class FatalExitGuard
{
    internal static readonly TimeSpan ProcessExitDelay = TimeSpan.FromSeconds(2);

    private static int _exitStarted;

    public static void Install()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, args) => RequestFatalExit(args.Exception, "ui-thread");
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            RequestFatalExit(args.ExceptionObject as Exception, "runtime");
        TaskScheduler.UnobservedTaskException += (_, args) =>
            ObserveBackgroundTaskFailure(args, ReportDiagnostic);
    }

    internal static void ObserveBackgroundTaskFailure(
        UnobservedTaskExceptionEventArgs args,
        Action<string> report)
    {
        // A faulted task is not an unhandled UI/runtime exception. Collection of
        // an abandoned network task must not terminate the host and other sessions.
        args.SetObserved();
        TryReport("background-task; application kept running; ", args.Exception, report);
    }

    private static void ReportDiagnostic(string summary) =>
        WindowsDiagnosticLog.CreateDefault().Append("FAULT", summary);

    private static void TryReport(string context, Exception? exception, Action<string> report)
    {
        try { report(context + DescribeException(exception)); }
        catch { /* Failure diagnostics must not create another fatal exception. */ }
    }

    internal static string DescribeException(Exception? exception)
    {
        if (exception is null) return "unknown exception";
        var pending = new Queue<Exception>();
        pending.Enqueue(exception);
        var summaries = new List<string>();
        while (pending.Count > 0 && summaries.Count < 8)
        {
            Exception current = pending.Dequeue();
            // Only code identity is recorded: no exception message, Data,
            // file paths, clipboard text, credentials or method arguments.
            var frames = new System.Diagnostics.StackTrace(current, false).GetFrames();
            string methods = string.Join(" <- ", (frames ?? []).Take(6).Select(frame =>
            {
                var method = frame.GetMethod();
                return method is null ? "unknown method" : $"{method.DeclaringType?.FullName}.{method.Name}";
            }));
            summaries.Add($"{current.GetType().FullName} HRESULT=0x{current.HResult:X8} [{methods}]");
            if (current is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.InnerExceptions.Take(8)) pending.Enqueue(inner);
            }
            else if (current.InnerException is { } inner) pending.Enqueue(inner);
        }
        string summary = string.Join("; ", summaries);
        return summary.Length <= 4096 ? summary : summary[..4096];
    }

    private static void RequestFatalExit(Exception? exception, string source)
    {
        TryReport($"{source}; application exiting; ", exception, ReportDiagnostic);
        BeginFatalExit(
            () =>
            {
                try
                {
                    Application.Exit();
                }
                catch (Exception ex) when (IsExitCoordinationException(ex))
                {
                }
            },
            exitCode => Environment.Exit(exitCode),
            ProcessExitDelay,
            ref _exitStarted);
    }

    internal static bool BeginFatalExit(
        Action requestApplicationExit,
        Action<int> exitProcess,
        TimeSpan processExitDelay,
        ref int exitStarted)
    {
        if (Interlocked.Exchange(ref exitStarted, 1) != 0)
        {
            return false;
        }

        try
        {
            requestApplicationExit();
        }
        catch (Exception ex) when (IsExitCoordinationException(ex))
        {
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(processExitDelay).ConfigureAwait(false);
                if (!Environment.HasShutdownStarted)
                {
                    exitProcess(1);
                }
            }
            catch
            {
            }
        });

        return true;
    }

    private static bool IsExitCoordinationException(Exception ex)
    {
        return ex is InvalidOperationException or ObjectDisposedException or
            System.ComponentModel.Win32Exception;
    }
}
