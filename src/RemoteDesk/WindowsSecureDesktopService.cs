using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RemoteDesk;

internal static class WindowsSecureDesktopService
{
    internal const string AgentArgument = "--secure-desktop-agent";
    private static readonly ManualResetEventSlim Stop = new(false);
    private static nint _statusHandle;
    private static string _ownerSid = "";
    private static readonly ServiceMainCallback MainCallback = ServiceMain;
    private static readonly ServiceControlCallback ControlCallback = HandleControl;

    internal static bool TryHandleCommand(string[] args)
    {
        if (args.Length == 0 || args[0] is not (WindowsSecureDesktopInstallation.ServiceArgument or AgentArgument)) return false;
        try
        {
            if (!WindowsSecureDesktopNative.IsSystem) throw new UnauthorizedAccessException("仅系统服务可以启动桌面辅助模块。");
            if (args[0] == AgentArgument)
            {
                if (args.Length != 5 || !uint.TryParse(args[2], out uint session) ||
                    session != Process.GetCurrentProcess().SessionId || !int.TryParse(args[4], out int parent) ||
                    !args[3].StartsWith(@"Global\RemoteDesk-DesktopStop-", StringComparison.Ordinal))
                    throw new ArgumentException("桌面辅助进程参数无效。");
                WindowsSecureDesktopAgent.Run(WindowsSecureDesktopInstallation.ValidateSid(args[1]), session, args[3], parent);
            }
            else
            {
                if (args.Length != 2) throw new ArgumentException("系统服务参数无效。");
                _ownerSid = WindowsSecureDesktopInstallation.ValidateSid(args[1]);
                var table = new[] {
                    new ServiceEntry { Name = WindowsSecureDesktopInstallation.ServiceName(_ownerSid), Main = MainCallback },
                    new ServiceEntry()
                };
                WindowsSecureDesktopNative.Check(StartServiceCtrlDispatcher(table));
            }
        }
        catch (Exception ex)
        {
            // Never log input commands, credentials, pixels, or user settings.
            Console.Error.WriteLine("RemoteDesk desktop helper: " + ex.GetType().Name + " " + ex.HResult);
            WindowsSecureDesktopNative.ReportFailure(args[0] == AgentArgument ? "agent" : "service-entry", ex);
            Environment.ExitCode = 1;
        }
        return true;
    }

    private static void ServiceMain(uint count, nint arguments)
    {
        uint exitCode = 0;
        _statusHandle = RegisterServiceCtrlHandlerEx(WindowsSecureDesktopInstallation.ServiceName(_ownerSid), ControlCallback, 0);
        if (_statusHandle == 0) return;
        Publish(2, 0, 15000);
        Process? worker = null;
        EventWaitHandle? workerStop = null;
        uint? workerSession = null;
        uint? observedSession = null;
        long workerStartedAt = 0, restartAt = 0;
        int rapidFailures = 0;
        try
        {
            WindowsSecureDesktopNative.EnablePrivilege("SeTcbPrivilege");
            WindowsSecureDesktopNative.EnablePrivilege("SeAssignPrimaryTokenPrivilege");
            WindowsSecureDesktopNative.EnablePrivilege("SeIncreaseQuotaPrivilege");
            Publish(4);
            string executable = Environment.ProcessPath ?? throw new IOException("系统辅助程序路径不可用。");
            while (!Stop.Wait(500))
            {
                uint? session = WindowsSecureDesktopNative.OwnedConsoleSession(_ownerSid);
                if (observedSession != session)
                {
                    observedSession = session;
                    rapidFailures = 0; restartAt = 0;
                }
                if (worker is not null && (worker.HasExited || workerSession != session))
                {
                    if (worker.HasExited && workerSession == session)
                    {
                        rapidFailures = Stopwatch.GetElapsedTime(workerStartedAt) < TimeSpan.FromSeconds(30)
                            ? Math.Min(rapidFailures + 1, 5) : 0;
                        restartAt = Environment.TickCount64 + Math.Min(30000, 1000L << rapidFailures);
                    }
                    StopWorker(worker, workerStop!);
                    worker = null; workerStop = null; workerSession = null;
                }
                if (worker is null && session.HasValue && Environment.TickCount64 >= restartAt)
                {
                    string name = @"Global\RemoteDesk-DesktopStop-" + Guid.NewGuid().ToString("N");
                    workerStop = new EventWaitHandle(false, EventResetMode.ManualReset, name, out bool created);
                    if (!created) throw new IOException("辅助进程停止事件发生冲突。");
                    worker = WindowsSecureDesktopNative.StartAgent(executable, _ownerSid, session.Value, name);
                    workerSession = session;
                    workerStartedAt = Stopwatch.GetTimestamp();
                }
            }
        }
        catch (Exception ex)
        {
            WindowsSecureDesktopNative.ReportFailure("service", ex);
            exitCode = ex is System.ComponentModel.Win32Exception native ? (uint)native.NativeErrorCode : 1064;
        }
        finally
        {
            if (worker is not null) StopWorker(worker, workerStop!);
            else workerStop?.Dispose();
            Publish(1, exitCode);
        }
    }

    private static void StopWorker(Process worker, EventWaitHandle stop)
    {
        using (worker)
        using (stop)
        {
            stop.Set();
            try { if (!worker.WaitForExit(7000)) worker.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }
    }

    private static uint HandleControl(uint control, uint eventType, nint eventData, nint context)
    {
        if (control is 1 or 5) { Publish(3, 0, 10000); Stop.Set(); }
        else if (control == 4) Publish(Stop.IsSet ? 3u : 4u);
        return 0;
    }

    private static void Publish(uint state, uint error = 0, uint wait = 0)
    {
        var status = new ServiceStatus { Type = 0x10, State = state, Accepted = state == 4 ? 5u : 0u,
            Win32ExitCode = error, Checkpoint = state is 2 or 3 ? 1u : 0u, WaitHint = wait };
        SetServiceStatus(_statusHandle, ref status);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ServiceStatus { public uint Type, State, Accepted, Win32ExitCode, SpecificExitCode, Checkpoint, WaitHint; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceEntry { public string? Name; public ServiceMainCallback? Main; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ServiceMainCallback(uint count, nint arguments);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint ServiceControlCallback(uint control, uint eventType, nint eventData, nint context);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool StartServiceCtrlDispatcher([In] ServiceEntry[] table);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint RegisterServiceCtrlHandlerEx(string name, ServiceControlCallback handler, nint context);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetServiceStatus(nint handle, ref ServiceStatus status);
}
