using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace RemoteDesk;

// An isolated helper owns the windows and hotkey. EOF on its stdin or a
// missing heartbeat restores the screen, including after a parent/UI crash.
// Never changes monitor settings or blocks local input.
internal sealed class WindowsPrivacyScreen : IPrivacyScreenBackend
{
    internal const string Command = "--privacy-curtain";
    private static WindowsPrivacyScreen? _localBackend;
    internal static bool IsRestoreShortcut(Keys keys) => keys == (Keys.Control | Keys.Alt | Keys.Shift | Keys.P);
    internal static bool RequestLocalRestore()
    {
        var backend = Volatile.Read(ref _localBackend);
        if (backend is null) return false;
        // Do not acquire _sync here: Stop can hold it while waiting for the
        // helper to exit. This runs inside the low-level keyboard hook.
        var process = Volatile.Read(ref backend._process);
        if (process is null) return false;
        try { if (process.HasExited) return false; }
        catch (InvalidOperationException) { return false; } // Concurrent Stop/disposal.
        // A keyboard hook must not block on a child process or pipe.
        _ = Task.Run(backend.Stop);
        return true;
    }
    private Process? _process;
    private System.Threading.Timer? _heartbeat;
    private readonly object _sync = new();
    private int _heartbeatRunning;
    private string _reason = "隐私屏未开启。仅支持 Windows 10 2004 及以上的普通桌面；不覆盖锁屏或 UAC 安全桌面。";
    public bool IsActive { get { lock (_sync) return _process is { HasExited: false }; } }
    public string InactiveReason => _reason;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Stop();
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) throw new NotSupportedException("需要 Windows 10 2004 或更新系统。");
        if (!IsDefaultInputDesktop()) throw new InvalidOperationException("请先解锁 Windows 并关闭 UAC 提示；安全桌面不支持此隐私屏。");
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法找到隐私屏程序。");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(WindowsPrivacyScreen).Assembly.Location);
        start.ArgumentList.Add(Command);
        var process = new Process { StartInfo = start };
        try
        {
            process.Start();
            lock (_sync)
            {
                _process = process;
                _heartbeat = new System.Threading.Timer(state => { _ = HeartbeatAsync(process); }, null, 0, 500);
            }
            _ = process.StandardError.ReadToEndAsync();
            string? response = await process.StandardOutput.ReadLineAsync(cancellationToken)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
            if (response != "READY" || process.HasExited) throw new InvalidOperationException(response ?? "遮罩进程未能启动。");
            Volatile.Write(ref _localBackend, this);
            _reason = "隐私屏已恢复：本地快捷键、屏幕布局或桌面发生变化。需要时请重新开启。";
        }
        catch { Stop(); process.Dispose(); throw; }
    }
    private async Task HeartbeatAsync(Process owner)
    {
        if (Interlocked.CompareExchange(ref _heartbeatRunning, 1, 0) != 0) return;
        try
        {
            lock (_sync) { if (!ReferenceEquals(_process, owner)) return; }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await owner.StandardInput.WriteLineAsync("alive".AsMemory(), timeout.Token).ConfigureAwait(false);
            await owner.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            // A stalled helper must not keep a local screen covered indefinitely.
            try { if (!owner.HasExited) owner.Kill(); }
            catch (Exception failure) when (failure is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        finally { Volatile.Write(ref _heartbeatRunning, 0); }
    }
    public void Stop()
    {
        lock (_sync)
        {
            Interlocked.CompareExchange(ref _localBackend, null, this);
            _heartbeat?.Dispose(); _heartbeat = null;
            Process? process = _process; _process = null;
            if (process is null) return;
            try
            {
                try { process.StandardInput.Close(); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
                if (!process.WaitForExit(500)) process.Kill();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            finally { process.Dispose(); }
        }
    }
    public void Dispose() => Stop();
    internal static bool TryHandleCommand(string[] args)
    {
        if (!args.Contains(Command, StringComparer.Ordinal)) return false;
        // No normal UI, networking, privilege installation, settings or clipboard.
        if (args.Length != 1 || !Console.IsInputRedirected) return true;
        try
        {
            Console.InputEncoding = new UTF8Encoding(false);
            Console.OutputEncoding = new UTF8Encoding(false);
            ApplicationConfiguration.Initialize();
            using var context = new CurtainContext();
            context.Start();
            Console.WriteLine("READY"); Console.Out.Flush();
            Application.Run(context);
        }
        catch (Exception ex) { Console.WriteLine("无法开启隐私屏：" + ex.Message.Replace('\n', ' ').Replace('\r', ' ')); Console.Out.Flush(); }
        return true;
    }
    internal static bool IsDefaultInputDesktop()
    {
        nint desktop = OpenInputDesktop(0, false, 1);
        if (desktop == 0) return false;
        try
        {
            var name = new StringBuilder(256);
            return GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out _) && name.ToString() == "Default";
        }
        finally { CloseDesktop(desktop); }
    }
    private sealed class CurtainContext : ApplicationContext
    {
        private readonly List<Curtain> _windows = [];
        private System.Threading.Timer? _watchdog;
        private System.Windows.Forms.Timer? _check;
        private long _heartbeat = Environment.TickCount64;
        private long _uiHeartbeat = Environment.TickCount64;
        private string _topology = "";
        internal void Start()
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) || !IsDefaultInputDesktop() ||
                DwmIsCompositionEnabled(out bool composed) != 0 || !composed)
                throw new InvalidOperationException("当前桌面不支持排除采集的遮罩，请先解锁并关闭安全桌面。");
            Screen[] screens = Screen.AllScreens;
            if (screens.Length == 0) throw new InvalidOperationException("没有可遮挡的显示器。");
            _topology = Topology();
            foreach (Screen screen in screens)
            {
                var window = new Curtain { Bounds = screen.Bounds };
                window.RestoreRequested += ExitThread;
                _windows.Add(window);
                _ = window.Handle;
                if (!SetLayeredWindowAttributes(window.Handle, 0, 255, 2) || !SetWindowDisplayAffinity(window.Handle, 0x11))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "系统无法排除遮罩采集。");
            }
            if (!RegisterHotKey(_windows[0].Handle, 1, 0x4000 | 1 | 2 | 4, (uint)Keys.P))
                throw new InvalidOperationException("紧急恢复快捷键 Ctrl+Alt+Shift+P 被占用，请释放后重试。");
            foreach (var window in _windows) window.Show();
            _check = new System.Windows.Forms.Timer { Interval = 250 };
            _check.Tick += (_, _) =>
            {
                Interlocked.Exchange(ref _uiHeartbeat, Environment.TickCount64);
                if (!IsDefaultInputDesktop() || Topology() != _topology) ExitThread();
            };
            _check.Start();
            _watchdog = new System.Threading.Timer(_ =>
            {
                long now = Environment.TickCount64;
                if (now - Interlocked.Read(ref _heartbeat) > 5000 || now - Interlocked.Read(ref _uiHeartbeat) > 5000)
                    Environment.Exit(0); // OS removes the windows even if the UI thread is hung.
            }, null, 1000, 500);
            _ = Task.Run(() =>
            {
                try { while (Console.ReadLine() == "alive") Interlocked.Exchange(ref _heartbeat, Environment.TickCount64); }
                finally { Environment.Exit(0); }
            });
        }
        private static string Topology() => string.Join(";", Screen.AllScreens.Select(s => s.DeviceName + s.Bounds));
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _watchdog?.Dispose(); _check?.Dispose();
                if (_windows.Count > 0 && _windows[0].IsHandleCreated) UnregisterHotKey(_windows[0].Handle, 1);
                foreach (var window in _windows) window.Dispose();
            }
            base.Dispose(disposing);
        }
    }
    private sealed class Curtain : Form
    {
        internal event Action? RestoreRequested;
        internal Curtain()
        {
            FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false; BackColor = Color.Black;
            AutoScaleMode = AutoScaleMode.Dpi; DoubleBuffered = true;
        }
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x80 | 0x8 | 0x20 | 0x80000; return p; }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            TextRenderer.DrawText(e.Graphics, "RemoteDesk · 隐私屏\n正在被远程控制\n\n本地按 Ctrl + Alt + Shift + P 恢复显示",
                Font, ClientRectangle, Color.Gainsboro, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        }
        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            // Never leave a stale-sized curtain after per-monitor scale changes.
            if (Visible) RestoreRequested?.Invoke();
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312 && m.WParam == 1) { RestoreRequested?.Invoke(); return; }
            if (m.Msg == 0x0021) { m.Result = 3; return; }
            base.WndProc(ref m);
        }
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder value, int size, out int needed);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetLayeredWindowAttributes(nint window, uint key, byte alpha, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmIsCompositionEnabled(out bool enabled);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
}
