using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using RemoteDesk;

// Actual Windows Terminal, only a generated loopback SSH fixture and one uniquely
// named owned window. No typing into the user's foreground window or SSH config.
internal static class SshRecallProbe
{
    internal static int Run() { RunAsync().GetAwaiter().GetResult(); return 0; }
    private static async Task RunAsync()
    {
        if (!WindowsPrivacyScreen.IsDefaultInputDesktop())
            throw new InvalidOperationException("Unlock the test desktop before exercising native terminal windows.");
        using var request = JsonDocument.Parse(Console.ReadLine()!);
        string output = Path.GetFullPath(request.RootElement.GetProperty("output").GetString()!);
        using var metadata = JsonDocument.Parse(File.ReadAllText(request.RootElement.GetProperty("fixturePath").GetString()!));
        var fixture = metadata.RootElement;
        string Read(string name) => fixture.GetProperty(name).GetString()!;
        if (!fixture.GetProperty("owned").GetBoolean() || Read("serverAddress") != "127.0.0.1")
            throw new InvalidOperationException("Only the owned loopback fixture is permitted.");
        if (!File.Exists(SshTerminalLauncher.WindowsTerminalPath)) throw new InvalidOperationException("Windows Terminal unavailable.");
        Directory.CreateDirectory(output);
        string privateKey = Path.Combine(output, "fixture_client_key");
        File.Copy(Read("keyPath"), privateKey, overwrite: true);
        var security = new FileSecurity();
        var user = WindowsIdentity.GetCurrent().User!;
        security.SetOwner(user); security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(privateKey).SetAccessControl(security);
        var options = new RelayConnectionOptions(Read("serverAddress"), fixture.GetProperty("port").GetInt32(), Read("token"), Read("tlsCertificateSha256"), Read("deviceId"));
        string alias = SshTerminalLauncher.HostKeyAlias(options.TlsCertificateSha256, options.DeviceId);
        string hosts = Path.Combine(output, "fixture_known_hosts");
        File.WriteAllText(hosts, alias + " " + Read("sshHostKeyPublic").Trim() + "\n", new UTF8Encoding(false));
        string windowId = "remotedesk-ssh-" + Guid.NewGuid().ToString("N");
        string marker = Path.Combine(output, "pty-" + windowId + ".txt");
        if (!marker.StartsWith("C:\\", StringComparison.OrdinalIgnoreCase) || marker.Contains('\''))
            throw new InvalidOperationException("Owned WSL probe output must be an ordinary C: path.");
        string linuxMarker = "/mnt/c/" + marker[3..].Replace('\\', '/');
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var relay = await SshRelayClient.OpenAsync(options, Read("deviceKey"), deadline.Token);
        using var bridge = new SshLocalForwarder(relay.Stream, relay);
        var ssh = SshTerminalLauncher.CreateStartInfo(SshTerminalLauncher.ExecutablePath, bridge.Port, Read("sshUsername"), alias, hosts, privateKey);
        ssh.ArgumentList.Add("tty > '" + linuxMarker + "' && sleep 90");
        var start = SshTerminalLauncher.CreateWindowStartInfo(ssh, SshTerminalLauncher.WindowsTerminalPath, windowId)
            ?? throw new InvalidOperationException("Owned fixture command cannot use WT.");
        nint window = 0;
        using var terminal = new SshTerminalSession(bridge, start, alias, windowId);
        try
        {
            for (int attempt = 0; attempt < 120 && (window == 0 || !File.Exists(marker)); attempt++)
            {
                window = Find(windowId);
                if (!terminal.IsActive) { await terminal.Completion; throw new InvalidOperationException("Terminal ended before authenticating."); }
                await Task.Delay(150, deadline.Token);
            }
            if (window == 0 || !File.Exists(marker) || !File.ReadAllText(marker).StartsWith("/dev/pts/"))
                throw new InvalidOperationException("Real graphical OpenSSH did not authenticate and allocate a PTY.");
            ShowWindowAsync(window, 6);
            for (int attempt = 0; attempt < 30 && !IsIconic(window); attempt++) await Task.Delay(100, deadline.Token);
            if (!IsIconic(window)) throw new InvalidOperationException("Owned terminal did not minimize.");
            bool recalled = await terminal.RecallAsync();
            for (int attempt = 0; attempt < 20 && IsIconic(window); attempt++) await Task.Delay(100, deadline.Token);
            bool same = window == Find(windowId) && terminal.IsActive && !IsIconic(window);
            if (!recalled || !same) throw new InvalidOperationException("Recall failed to restore the same authenticated terminal window.");
            terminal.Dispose();
            await terminal.Completion.WaitAsync(deadline.Token);
            Program.Save(Path.Combine(output, "native-recall.json"), new { passed = true,
                checks = new[] { "real Windows Terminal and OpenSSH", "TLS pin and device gate", "real owned sshd authentication and PTY", "successful IPC launcher exit keeps tunnel", "minimize and recall same live window", "explicit disconnect closes tunnel" } });
            Console.WriteLine("Windows Terminal: real SSH PTY, minimize, same-window recall and disconnect passed.");
        }
        finally
        {
            terminal.Dispose();
            window = Find(windowId);
            if (window != 0) PostMessage(window, 0x0010, 0, 0); // Close only our uniquely tagged window, never shared WT processes.
            File.Delete(privateKey); // Generated fixture key, not a user's SSH identity.
        }
    }
    private static nint Find(string id)
    {
        nint found = 0;
        EnumWindows((window, _) => {
            var text = new StringBuilder(512); GetWindowText(window, text, text.Capacity);
            // OpenSSH may own an invisible console window with the same title
            // as WT. Minimize/recall must target the actual visible terminal.
            if (IsWindowVisible(window) && text.ToString().Contains("RemoteDesk SSH [" + id[^8..] + "]", StringComparison.Ordinal)) found = window;
            return true;
        }, 0);
        return found;
    }
    private delegate bool WindowCallback(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int length);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nint first, nint second);
}
