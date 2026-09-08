namespace RemoteDesk;

internal static class FatalExitGuard
{
    internal static readonly TimeSpan ProcessExitDelay = TimeSpan.FromSeconds(2);

    private static int _exitStarted;

    public static void Install()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, args) => RequestFatalExit(args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            RequestFatalExit(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            args.SetObserved();
            RequestFatalExit(args.Exception);
        };
    }

    private static void RequestFatalExit(Exception? exception)
    {
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
