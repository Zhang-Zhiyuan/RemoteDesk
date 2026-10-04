using System.Diagnostics;

namespace RemoteDesk;

internal sealed class SshTerminalSession : IDisposable
{
    private readonly SshLocalForwarder _forwarder;
    private readonly Process _process;
    private int _disposed;
    internal Task Completion { get; }
    internal int ProcessId { get; }

    internal SshTerminalSession(SshLocalForwarder forwarder, ProcessStartInfo startInfo)
    {
        _forwarder = forwarder;
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
            Dispose();
            await _forwarder.Completion.ConfigureAwait(false);
            await exit.ConfigureAwait(false);
        }
        finally { Dispose(); _process.Dispose(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _forwarder.Dispose();
        try { if (!_process.HasExited) _process.Kill(); } // Only the exact ssh.exe we launched, never its terminal host.
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
