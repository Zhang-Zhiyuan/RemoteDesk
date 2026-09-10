namespace RemoteDesk;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // SYSTEM modes must never initialize the normal UI, network host,
        // updater, clipboard or user settings.
        if (WindowsSecureDesktopService.TryHandleCommand(args)) return;
        if (WindowsSecureDesktopInstallation.TryHandleCommand(args)) return;
        long applicationStartedAt =
            System.Diagnostics.Stopwatch.GetTimestamp();
        int? previousProcessId =
            WindowsProcessElevation
                .TryReadPreviousProcessId(args);
        bool previousProcessExited =
            WindowsProcessElevation
                .WaitForPreviousProcess(
                    previousProcessId,
                    TimeSpan.FromSeconds(20));
        using IDisposable timerResolution =
            WindowsTimerResolution.TryAcquireInteractive();
        using IDisposable managedLatency =
            ManagedLatencyMode
                .TryAcquireSustainedLowLatency();
        ApplicationConfiguration.Initialize();
        if (WindowsPersistentStartup.TryHandleCommand(args)) return;
        if (!previousProcessExited)
        {
            MessageBox.Show(
                "原 RemoteDesk 未能在 20 秒内退出，" +
                "管理员副本没有启动，以免两个被控端争用端口。",
                "RemoteDesk 管理员重启",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        FatalExitGuard.Install();
        if (!WindowsProcessElevation.IsCurrentProcessElevated() && WindowsPersistentStartup.TryStartInstalled())
        {
            WindowsAppInstance.ActivateExisting();
            return;
        }
        using var instance = new WindowsAppInstance();
        if (!instance.IsOwner)
        {
            WindowsAppInstance.ActivateExisting();
            return;
        }
        bool startMinimizedToTray = args.Any(arg =>
            string.Equals(arg, "--tray", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "/tray", StringComparison.OrdinalIgnoreCase));
        bool resumeHostAfterUpdate = args.Any(arg =>
            string.Equals(
                arg,
                RemoteUpdater.ResumeHostAfterUpdateArgument,
                StringComparison.OrdinalIgnoreCase));

        Application.Run(
            new MainForm(
                startMinimizedToTray,
                resumeHostAfterUpdate,
                applicationStartedAt));
    }
}
