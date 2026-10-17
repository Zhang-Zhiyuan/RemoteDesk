using System.Diagnostics;

namespace RemoteDesk;

internal sealed class SshTerminalSession : IDisposable
{
    private readonly SshLocalForwarder _forwarder;
    private readonly Process _process;
    private int _disposed;
    internal Task Completion { get; }
    internal int ProcessId { get; }
    internal string? HostKeyAlias { get; }
    internal bool IsActive => Volatile.Read(ref _disposed) == 0 && !Completion.IsCompleted;
    private readonly string? _windowId;
    private readonly string? _terminalPath;

    internal SshTerminalSession(SshLocalForwarder forwarder, ProcessStartInfo startInfo, string? hostKeyAlias = null, string? windowId = null)
    {
        _forwarder = forwarder;
        HostKeyAlias = hostKeyAlias;
        _windowId = windowId;
        _terminalPath = windowId is null ? null : startInfo.FileName;
        try { _process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 Windows OpenSSH 终端。"); }
        catch { forwarder.Dispose(); throw; }
        ProcessId = _process.Id;
        Completion = ObserveAsync();
    }

    private async Task ObserveAsync()
    {
        try
        {
            Task exit = _process.WaitForExitAsync();
            await Task.WhenAny(exit, _forwarder.Completion).ConfigureAwait(false);
            if (_windowId is not null)
            {
                if (exit.IsCompleted && _process.ExitCode != 0)
                    throw new InvalidOperationException("Windows Terminal 启动失败。请检查系统终端，或使用已配置的 ssh 别名。");
                // A successful wt launcher normally exits while its window and
                // SSH remain alive. Only tunnel EOF ends this session.
                await _forwarder.Completion.ConfigureAwait(false);
                return;
            }
            Dispose();
            await _forwarder.Completion.ConfigureAwait(false);
            await exit.ConfigureAwait(false);
        }
        finally { Dispose(); _process.Dispose(); }
    }

    internal async Task<bool> RecallAsync()
    {
        if (!IsActive) return false;
        try
        {
            if (_windowId is not null)
            {
                using var recall = Process.Start(SshTerminalLauncher.CreateRecallStartInfo(_terminalPath!, _windowId));
                if (recall is null) return false;
                await recall.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                return recall.ExitCode == 0 && IsActive;
            }
            _process.Refresh();
            nint window = _process.MainWindowHandle;
            if (window == 0) return false;
            ShowWindowAsync(window, 9);
            return SetForegroundWindow(window);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException) { return false; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _forwarder.Dispose();
        try { if (_windowId is null && !_process.HasExited) _process.Kill(); } // Never kill a shared Windows Terminal process.
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint window, int command);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
}
