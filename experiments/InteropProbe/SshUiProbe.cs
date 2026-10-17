using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using RemoteDesk;

internal static class SshUiProbe
{
    internal static int Run()
    {
        using var config = JsonDocument.Parse(Console.ReadLine()!);
        string output = Path.GetFullPath(config.RootElement.GetProperty("output").GetString()!);
        Directory.CreateDirectory(output);
        nint station = CreateWindowStation(null, 0, 0x000F037F, 0);
        if (station == 0 || !SetProcessWindowStation(station)) throw new Win32Exception();
        nint desktop = CreateDesktop("Default", null, 0, 0, 0x000F01FF, 0);
        if (desktop == 0) throw new Win32Exception();
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                if (!SetThreadDesktop(desktop)) throw new Win32Exception();
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                Application.EnableVisualStyles();
                if (config.RootElement.TryGetProperty("sessionToolsOnly", out JsonElement sessionToolsOnly) && sessionToolsOnly.GetBoolean())
                    SessionToolsInteractionProbe.Verify(output);
                else if (config.RootElement.TryGetProperty("fileHeaderOnly", out JsonElement fileHeaderOnly) && fileHeaderOnly.GetBoolean())
                    FileTransferHeaderUiProbe.Verify(output, config.RootElement.TryGetProperty("replayLegacyHeaders", out var legacyHeaders) && legacyHeaders.GetBoolean());
                else if (config.RootElement.TryGetProperty("aliasOnly", out JsonElement aliasOnly) && aliasOnly.GetBoolean()) VerifyAliasUi(output);
                else Verify(output, !(config.RootElement.TryGetProperty("dialogLayoutOnly", out var layoutOnly) && layoutOnly.GetBoolean()));
            }
            catch (Exception error) { failure = error; }
        });
        worker.SetApartmentState(ApartmentState.STA); worker.Start(); worker.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        if (config.RootElement.TryGetProperty("fixturePath", out JsonElement fixturePath))
            VerifyRealSshRelayAsync(output, fixturePath.GetString()!).GetAwaiter().GetResult();
        return 0;
    }

    private static void VerifyAliasUi(string output)
    {
        var cases = new List<object>(); var failures = new List<string>();
        foreach (bool manager in new[] { false, true })
        foreach (float points in new[] { 9f, 14f, 20f })
        {
            using var font = new Font("Microsoft YaHei UI", points);
            using var dialog = manager ? MainForm.CreateSshAliasManagerDialog([
                new SshCommandProfile(Guid.NewGuid(), "5090", "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", new string('B', 64), new string('A', 64), "fixture", null)], font, out _)
                : MainForm.CreateSshAliasDialog("自有隔离别名布局设备", font, "synthetic-device-key", null, "5090", out _, out _, out _, out _);
            dialog.Show(); SyntheticDpi.Pump();
            foreach (int dpi in new[] { 96, 144, 192, 96 })
            {
                object transition = SyntheticDpi.Apply(dialog, dpi);
                foreach (int width in new[] { 360, 720 })
                {
                    dialog.MinimumSize = Size.Empty;
                    dialog.ClientSize = new Size(ResponsiveWindowLayout.ScaleLogical(width, dpi), ResponsiveWindowLayout.ScaleLogical(manager ? 240 : 380, dpi));
                    SyntheticDpi.Pump();
                    string tag = $"alias-{(manager ? "manager" : "dialog")}-font{points}-dpi{dpi}-width{width}-{cases.Count}";
                    var issues = new List<string>();
                    var scroll = (ScrollableControl)dialog.Controls[0];
                    foreach (Control control in SyntheticDpi.Descendants(dialog).Where(control => control is TextBox or Button or ComboBox))
                    {
                        scroll.ScrollControlIntoView(control); control.Select(); SyntheticDpi.Pump();
                        Check(control, scroll, issues);
                    }
                    var selectedAliases = SyntheticDpi.Descendants(dialog).OfType<ComboBox>().Select(combo => new { combo.SelectedIndex, combo.Text,
                        nativeText = WindowText(combo.Handle), itemText = combo.SelectedItem?.ToString() }).ToArray();
                    foreach (var choice in selectedAliases)
                        if (choice.SelectedIndex != 0 || !choice.Text.Contains("5090") || !choice.nativeText.Contains("5090")) issues.Add("Alias selection was lost or native text differs from expected caption.");
                    cases.Add(new { tag, transition, dialog.Bounds, issues, selectedAliases }); failures.AddRange(issues.Select(issue => tag + ": " + issue));
                    Save(dialog, tag);
                }
            }
            dialog.Close(); SyntheticDpi.Pump();
        }
        string previousIdentity = RemoteDeviceIdentity.LocalId;
        try
        {
            foreach (float points in new[] { 9f, 14f })
            {
                var settings = new RemoteDeskSettings { Relay = new RelaySettings { ServerAddress = "owned.invalid", RelayPort = 56567,
                    TlsCertificateSha256 = new string('A', 64), ProtectedAccessToken = AppSettingsService.ProtectSecret(new string('t', 32)),
                    DeviceId = "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee" } };
                using var main = new MainForm(settings, Path.Combine(output, "preview-log"));
                main.Show(); SyntheticDpi.Pump();
                using var font = new Font("Microsoft YaHei UI", points); main.Font = font; SyntheticDpi.Pump();
                var button = Get<Button>(main, "_configureSshAliasButton");
                Control parent = button; while (parent is not TabPage) parent = parent.Parent!;
                var page = (TabPage)parent; Get<TabControl>(main, "_tabs").SelectedTab = page; SyntheticDpi.Pump();
                var list = Get<ListView>(main, "_relayDevicesList"); _ = list.Handle;
                main.ApplyRelayDeviceList([new RelayOnlineDevice("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", "Owned SSH fixture", "Linux", null, false, 0)
                    { SupportsSsh = true }], 0);
                list.Items[0].Selected = true;
                typeof(MainForm).GetMethod("UpdateRelayActionState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(main, null);
                foreach (int dpi in new[] { 96, 144, 192 })
                {
                    object transition = SyntheticDpi.Apply(main, dpi);
                    foreach (int width in new[] { 640, 1120 })
                    {
                        main.MinimumSize = Size.Empty;
                        main.ClientSize = new Size(ResponsiveWindowLayout.ScaleLogical(width, dpi), ResponsiveWindowLayout.ScaleLogical(640, dpi));
                        SyntheticDpi.Pump();
                        string tag = $"alias-main-font{points}-dpi{dpi}-width{width}-{cases.Count}";
                        var issues = new List<string>();
                        foreach (Button action in new[] { button, Get<Button>(main, "_sshTerminalButton"), Get<Button>(main, "_sshLoginSettingsButton"), Get<Button>(main, "_closeSshTerminalsButton"), Get<Button>(main, "_manageSshAliasesButton") })
                        { page.ScrollControlIntoView(action); SyntheticDpi.Pump(); Check(action, page, issues); }
                        if (!button.Enabled) issues.Add("Configured SSH-capable device alias button is disabled.");
                        page.ScrollControlIntoView(button); SyntheticDpi.Pump();
                        cases.Add(new { tag, transition, main.Bounds, issues }); failures.AddRange(issues.Select(issue => tag + ": " + issue));
                        Save(main, tag);
                    }
                }
                main.Close(); SyntheticDpi.Pump();
            }
        }
        finally { RemoteDeviceIdentity.LocalId = previousIdentity; }
        VerifyLongAliasDetails(output, cases, failures);
        Program.Save(Path.Combine(output, "ssh-alias-ui.json"), new { passed = failures.Count == 0, cases, failures,
            scope = "Real WinForms on private window station; synthetic 96/144/192 DPI with managed/native trace, fonts 9/14/20, narrow/wide widths. Only owned preview settings; no config installation, production input, network or keys." });
        if (failures.Count != 0) throw new InvalidOperationException($"SSH alias UI: {failures.Count} findings; see ssh-alias-ui.json");

        void Save(Form form, string tag)
        { using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(output, tag + ".png")); }
        static T Get<T>(MainForm form, string field) => (T)typeof(MainForm).GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(form)!;
        static void Check(Control control, ScrollableControl viewport, List<string> issues)
        {
            Rectangle bounds = control.RectangleToScreen(control.ClientRectangle);
            if (!control.Visible || control.Width <= 0 || control.Height <= 0 || !viewport.RectangleToScreen(viewport.ClientRectangle).Contains(bounds))
                issues.Add(control.GetType().Name + " is unreachable: " + control.Bounds);
            for (Control? parent = control.Parent; parent is not null && parent != viewport; parent = parent.Parent)
                if (!parent.RectangleToScreen(parent.ClientRectangle).Contains(bounds)) issues.Add(control.GetType().Name + " clipped by " + parent.GetType().Name);
            if (control is Button && TextRenderer.MeasureText(control.Text, control.Font).Height > control.Height)
                issues.Add("Button text clipped: " + control.Text);
        }
        static string WindowText(nint handle) { var buffer = new StringBuilder(512); GetWindowText(handle, buffer, buffer.Capacity); return buffer.ToString(); }
    }

    private static void VerifyLongAliasDetails(string output, List<object> cases, List<string> failures)
    {
        string prefix = new string('a', 63), user = new string('u', 128);
        foreach (float points in new[] { 9f, 20f })
        {
            using var font = new Font("Microsoft YaHei UI", points);
            SshCommandProfile[] profiles = [new(Guid.NewGuid(), prefix + "b", "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", new string('B', 64), new string('A', 64), user, null),
                new(Guid.NewGuid(), prefix + "c", "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb", new string('B', 64), new string('A', 64), user, null)];
            using var dialog = MainForm.CreateSshAliasManagerDialog(profiles, font, out ComboBox aliases);
            dialog.Show(); SyntheticDpi.Pump();
            foreach (int dpi in new[] { 96, 120, 144, 168, 192, 240, 96 })
            {
                object transition = SyntheticDpi.Apply(dialog, dpi);
                foreach (int width in new[] { 360, 720 })
                {
                    dialog.MinimumSize = Size.Empty;
                    dialog.ClientSize = new Size(ResponsiveWindowLayout.ScaleLogical(width, dpi), ResponsiveWindowLayout.ScaleLogical(380, dpi));
                    SyntheticDpi.Pump();
                    string tag = $"long-alias-font{points}-dpi{dpi}-width{width}-{cases.Count}";
                    var issues = new List<string>();
                    int captionPixels = TextRenderer.MeasureText(aliases.GetItemText(aliases.Items[0]), aliases.Font).Width;
                    var detail = SyntheticDpi.Descendants(dialog).OfType<TextBox>().FirstOrDefault(control => control.Name == "sshAliasDetails");
                    if (captionPixels > aliases.Width && detail is null) issues.Add("Long aliases with distinct suffixes are visually truncated with no full selection details.");
                    for (int index = 0; index < profiles.Length; index++)
                    {
                        aliases.SelectedIndex = index; SyntheticDpi.Pump();
                        if (detail is not null)
                        {
                            if (!detail.ReadOnly || !detail.Multiline || !detail.WordWrap || !detail.Text.Contains(profiles[index].Alias) ||
                                !detail.Text.Contains(profiles[index].Username) || !detail.Text.Contains(profiles[index].DeviceId))
                                issues.Add("Selected alias details are missing, stale or not safely wrapped/read-only.");
                            var scroll = (ScrollableControl)dialog.Controls[0]; scroll.ScrollControlIntoView(detail); detail.Select(); SyntheticDpi.Pump();
                            if (!scroll.RectangleToScreen(scroll.ClientRectangle).Contains(detail.RectangleToScreen(detail.ClientRectangle)))
                                issues.Add("Full alias details cannot be reached.");
                            // Scroll within the read-only field too: the bottom
                            // identity must remain inspectable at large fonts.
                            detail.SelectionStart = detail.TextLength; detail.SelectionLength = 0;
                            detail.ScrollToCaret(); SyntheticDpi.Pump();
                        }
                    }
                    GetClientRect(dialog.Handle, out NativeRect nativeClient);
                    Rectangle nativeViewport = new(dialog.PointToScreen(Point.Empty), new Size(nativeClient.Right, nativeClient.Bottom));
                    Rectangle? detailBounds = detail?.RectangleToScreen(detail.ClientRectangle);
                    if (detailBounds is Rectangle actual && !nativeViewport.Contains(actual)) issues.Add("Details outside native client viewport: " + actual + " vs " + nativeViewport);
                    using var bitmap = new Bitmap(dialog.Width, dialog.Height);
                    dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, dialog.Size)); bitmap.Save(Path.Combine(output, tag + ".png"));
                    cases.Add(new { tag, transition, captionPixels, comboWidth = aliases.Width, aliasLength = prefix.Length + 1, userLength = user.Length, detailBounds, nativeViewport, issues });
                    failures.AddRange(issues.Select(issue => tag + ": " + issue));
                }
            }
            dialog.Close(); SyntheticDpi.Pump();
        }
    }

    private static void Verify(string output, bool verifyNative = true)
    {
        var cases = new List<object>();
        var failures = new List<string>();
        foreach (float points in new[] { 9f, 14f, 20f })
        {
            using var font = new Font("Microsoft YaHei UI", points);
            using var form = MainForm.CreateSshConnectionDialog("自有 SSH 终端测试设备", font, "synthetic-device-key", out _, out _);
            form.Show(); SyntheticDpi.Pump();
            foreach (int dpi in new[] { 96, 120, 144, 168, 192, 240, 96 })
            {
                object transition = SyntheticDpi.Apply(form, dpi);
                foreach (int width in new[] { 360, 720 })
                {
                    form.MinimumSize = Size.Empty;
                    form.ClientSize = new Size(Math.Min(1280, ResponsiveWindowLayout.ScaleLogical(width, dpi)),
                        Math.Min(800, ResponsiveWindowLayout.ScaleLogical(380, dpi)));
                    SyntheticDpi.Pump();
                    var issues = new List<string>();
                    var scroll = (ScrollableControl)form.Controls[0];
                    GetClientRect(form.Handle, out NativeRect nativeClient);
                    Rectangle nativeViewport = new(form.PointToScreen(Point.Empty), new Size(nativeClient.Right, nativeClient.Bottom));
                    foreach (Control child in SyntheticDpi.Descendants(form).Where(control => control is TextBox or Button))
                    {
                        scroll.ScrollControlIntoView(child);
                        child.Select();
                        SyntheticDpi.Pump();
                        Rectangle childBounds = child.RectangleToScreen(child.ClientRectangle);
                        if (child.Width <= 0 || child.Height <= 0 || !scroll.RectangleToScreen(scroll.ClientRectangle).Contains(childBounds) || !nativeViewport.Contains(childBounds))
                            issues.Add(child.GetType().Name + " cannot be reached: " + child.Bounds + " screen=" + child.RectangleToScreen(child.ClientRectangle) +
                                " viewport=" + scroll.RectangleToScreen(scroll.ClientRectangle) + " display=" + scroll.DisplayRectangle +
                                " extent=" + scroll.AutoScrollMinSize + " focus=" + child.Focused + " active=" + form.ActiveControl?.GetType().Name);
                        if (child is Button && TextRenderer.MeasureText(child.Text, child.Font).Height > child.Height)
                            issues.Add("Button text height clipped: " + child.Text);
                    }
                    string tag = $"ssh-{points}-{dpi}-{width}-{cases.Count}";
                    cases.Add(new { tag, transition, form.Bounds, managedClient = form.ClientSize, nativeViewport, issues });
                    failures.AddRange(issues.Select(issue => tag + ": " + issue));
                    using var bitmap = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(output, tag + ".png"));
                }
            }
            form.Close(); SyntheticDpi.Pump();
        }
        Program.Save(Path.Combine(output, "ssh-ui.json"), new { passed = failures.Count == 0, cases, failures,
            scope = "Real WinForms on private station; synthetic 96/120/144/168/192/240 DPI plus fonts 9/14/20; no production input/settings/network" });
        if (failures.Count > 0) throw new InvalidOperationException($"SSH dialog: {failures.Count} layout findings.");
        if (verifyNative) VerifyRealOpenSsh(output);
    }

    private static void VerifyRealOpenSsh(string output)
    {
        if (!File.Exists(SshTerminalLauncher.ExecutablePath)) throw new FileNotFoundException("OpenSSH client required for this native fixture.");
        // -G only parses configuration: no connection, keys or known_hosts write.
        var start = SshTerminalLauncher.CreateStartInfo(SshTerminalLauncher.ExecutablePath, 49123, "owned-fixture",
            SshTerminalLauncher.HostKeyAlias(new string('A', 64), "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
            Path.Combine(output, "path with spaces", "known_hosts"));
        start.ArgumentList.Insert(0, "-G"); start.UseShellExecute = false; start.CreateNoWindow = true;
        start.RedirectStandardOutput = start.RedirectStandardError = true;
        using (var parser = Process.Start(start)!)
        {
            string parsed = parser.StandardOutput.ReadToEnd();
            string error = parser.StandardError.ReadToEnd(); parser.WaitForExit();
            bool parsedCorrectly = parser.ExitCode == 0 && parsed.Contains("stricthostkeychecking ask") &&
                parsed.Contains("hostkeyalias remotedesk-" + new string('a', 64)) &&
                parsed.Contains("path with spaces/known_hosts") && parsed.Contains("escapechar none");
            Program.Save(Path.Combine(output, "ssh-config-parse.json"), new { passed = parsedCorrectly, parser.ExitCode, error,
                safeOptions = parsed.Split('\n').Where(line => line.StartsWith("hostkeyalias ") || line.StartsWith("stricthostkeychecking ") ||
                    line.StartsWith("userknownhostsfile ") || line.StartsWith("escapechar ") || line.StartsWith("forwardagent ") || line.StartsWith("clearallforwardings ")).ToArray() });
            if (!parsedCorrectly) throw new InvalidOperationException("Real OpenSSH did not parse intended safety options.");
        }
        // Prove the actual visible console client launches and reaches its one-shot
        // bridge. The owned banner-only peer has no credentials/host key and never
        // completes SSH authentication; it cannot execute commands anywhere.
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var remote = new TcpClient();
        remote.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var server = listener.AcceptTcpClient();
        using var forwarder = new SshLocalForwarder(remote.GetStream(), remote);
        start = SshTerminalLauncher.CreateStartInfo(SshTerminalLauncher.ExecutablePath, forwarder.Port, "owned-fixture",
            SshTerminalLauncher.HostKeyAlias(new string('A', 64), "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), Path.Combine(output, "known_hosts"));
        using var terminal = new SshTerminalSession(forwarder, start);
        int pid = terminal.ProcessId;
        server.GetStream().Write(Encoding.ASCII.GetBytes("SSH-2.0-RemoteDeskOwnedProbe\r\n"));
        byte[] data = new byte[512];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int length = server.GetStream().ReadAsync(data, timeout.Token).AsTask().GetAwaiter().GetResult();
        string banner = Encoding.ASCII.GetString(data, 0, length).Split('\n')[0].Trim();
        bool realSsh = banner.StartsWith("SSH-2.0-OpenSSH_for_Windows_", StringComparison.Ordinal);
        terminal.Dispose(); terminal.Completion.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        bool exited;
        try { using var process = Process.GetProcessById(pid); exited = process.HasExited; } catch (ArgumentException) { exited = true; }
        Program.Save(Path.Combine(output, "ssh-native-console.json"), new { passed = realSsh && exited, banner, ownedProcessId = pid,
            ownedProcessExited = exited, scope = "Real ssh.exe graphical-shell console launched on private station; owned loopback banner fixture only, no credentials, input, remote commands or user terminal touched" });
        if (!realSsh || !exited) throw new InvalidOperationException("Real owned OpenSSH console lifecycle failed.");
        VerifyLiteralOpenSshPathsAsync(output).GetAwaiter().GetResult();
    }

    private static async Task VerifyLiteralOpenSshPathsAsync(string output)
    {
        string identity = Path.GetFullPath(Path.Combine(output, "identity %h %d %% literal"));
        string knownHosts = Path.Combine(output, "known_hosts %h %d %% literal");
        var generator = new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(SshTerminalLauncher.ExecutablePath)!, "ssh-keygen.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string value in new[] { "-q", "-t", "ed25519", "-N", "", "-f", identity }) generator.ArgumentList.Add(value);
        using (var process = Process.Start(generator)!)
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (process.ExitCode != 0) throw new InvalidOperationException("Owned SSH key generation failed.");
        }
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var start = SshTerminalLauncher.CreateStartInfo(SshTerminalLauncher.ExecutablePath,
            ((IPEndPoint)listener.LocalEndpoint).Port, "owned-fixture",
            SshTerminalLauncher.HostKeyAlias(new string('A', 64), "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), knownHosts, identity);
        start.ArgumentList.Insert(0, "-vvv"); start.UseShellExecute = false; start.CreateNoWindow = true;
        start.RedirectStandardError = start.RedirectStandardOutput = true;
        using var ssh = Process.Start(start)!;
        try
        {
            Task<string> stderr = ssh.StandardError.ReadToEndAsync(deadline.Token);
            using (var peer = await listener.AcceptTcpClientAsync(deadline.Token))
            {
                byte[] banner = new byte[512];
                await peer.GetStream().ReadAsync(banner, deadline.Token);
            }
            await ssh.WaitForExitAsync(deadline.Token);
            string diagnostic = await stderr;
            string Normalize(string path) => path.Replace("\\\\", "/").Replace('\\', '/');
            string[] lines = diagnostic.Split('\n').Where(line => line.Contains("identity file ") || line.Contains("expanded UserKnownHostsFile") ||
                line.Contains("Warning: Identity file") || line.Contains("percent_expand")).ToArray();
            bool literalIdentity = lines.Any(line => Normalize(line).Contains("identity file " + Normalize(identity) + " type 3"));
            bool literalHosts = lines.Any(line => Normalize(line).Contains("-> '" + Normalize(knownHosts) + "'"));
            Program.Save(Path.Combine(output, "ssh-literal-paths.json"), new { passed = literalIdentity && literalHosts, literalIdentity,
                literalHosts, diagnostic = lines, scope = "Real Windows OpenSSH pre-authentication path loading on owned loopback banner fixture, generated test identity only" });
            if (!literalIdentity || !literalHosts) throw new InvalidOperationException("OpenSSH expanded a literal percent path; see diagnostic.");
        }
        finally { if (!ssh.HasExited) { ssh.Kill(); await ssh.WaitForExitAsync(); } }
    }

    private static async Task VerifyRealSshRelayAsync(string output, string fixturePath)
    {
        using var config = JsonDocument.Parse(File.ReadAllText(fixturePath));
        JsonElement fixture = config.RootElement;
        string Read(string name) => fixture.GetProperty(name).GetString()!;
        if (!fixture.GetProperty("owned").GetBoolean() || !IPAddress.TryParse(Read("serverAddress"), out IPAddress? address) || !IPAddress.IsLoopback(address))
            throw new InvalidOperationException("Only an explicitly owned loopback SSH fixture is allowed.");
        var options = new RelayConnectionOptions(Read("serverAddress"), fixture.GetProperty("port").GetInt32(),
            Read("token"), Read("tlsCertificateSha256"), Read("deviceId"));
        string alias = SshTerminalLauncher.HostKeyAlias(options.TlsCertificateSha256, options.DeviceId);
        string knownHosts = Path.Combine(output, "fixture_known_hosts");
        string privateKey = Path.Combine(output, "fixture_client_key");
        File.Copy(Read("keyPath"), privateKey, overwrite: true);
        var keySecurity = new FileSecurity();
        SecurityIdentifier currentUser = WindowsIdentity.GetCurrent().User!;
        keySecurity.SetOwner(currentUser);
        keySecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        keySecurity.AddAccessRule(new FileSystemAccessRule(currentUser, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(privateKey).SetAccessControl(keySecurity);
        // Trust only this fixture's separately provided public key. Product
        // StrictHostKeyChecking=ask remains enabled; no global SSH files change.
        File.WriteAllText(knownHosts, alias + " " + Read("sshHostKeyPublic").Trim() + "\n", new UTF8Encoding(false));
        var checks = new List<object>();
        foreach (bool cancel in new[] { false, true })
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            using SshRelayConnection relay = await SshRelayClient.OpenAsync(options, Read("deviceKey"), deadline.Token);
            using var bridge = new SshLocalForwarder(relay.Stream, relay);
            var start = SshTerminalLauncher.CreateStartInfo(SshTerminalLauncher.ExecutablePath, bridge.Port,
                Read("sshUsername"), alias, knownHosts, privateKey);
            start.ArgumentList[start.ArgumentList.IndexOf("-t")] = "-tt";
            start.ArgumentList.Add(cancel
                ? "printf 'RDK_CANCEL_READY\\n'; read -r fixture_line"
                : "printf 'RDK_NATIVE_SSH_PTY\\n'; test -t 0 && test -t 1 && printf 'PTY_OK\\n'; stty rows 28 cols 96; stty size; printf '中文终端验证\\n'");
            start.UseShellExecute = false; start.CreateNoWindow = true;
            start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
            start.StandardOutputEncoding = start.StandardErrorEncoding = Encoding.UTF8;
            using var process = Process.Start(start)!;
            try
            {
                Task<string> stderr = process.StandardError.ReadToEndAsync(deadline.Token);
                string stdout;
                if (cancel)
                {
                    string? ready = await process.StandardOutput.ReadLineAsync(deadline.Token);
                    if (ready?.Contains("RDK_CANCEL_READY", StringComparison.Ordinal) != true)
                        throw new InvalidOperationException("Native SSH cancellation fixture did not reach authenticated PTY.");
                    bridge.Dispose();
                    stdout = ready + "\n" + await process.StandardOutput.ReadToEndAsync(deadline.Token);
                }
                else stdout = await process.StandardOutput.ReadToEndAsync(deadline.Token);
                await process.WaitForExitAsync(deadline.Token);
                await bridge.Completion.WaitAsync(deadline.Token);
                string errors = await stderr;
                bool passed = cancel ? process.HasExited : process.ExitCode == 0 && stdout.Contains("RDK_NATIVE_SSH_PTY") &&
                    stdout.Contains("PTY_OK") && stdout.Contains("28 96") && stdout.Contains("中文终端验证");
                checks.Add(new { name = cancel ? "authenticated-pty-cancel" : "authenticated-pty-unicode-resize", passed,
                    process.ExitCode, stdout, stderr = errors, ownedProcessExited = process.HasExited });
                Program.Save(Path.Combine(output, "ssh-real-relay.json"), new { passed = checks.Count == 2 && passed, checks,
                    scope = "Real system OpenSSH through product SshRelayClient + one-shot bridge to owned WSL sshd; forced remote PTY with redirected fixture capture, pinned fixture host key and temporary identity. Native graphical console launch verified separately." });
                if (!passed) throw new InvalidOperationException("Native SSH relay fixture failed; see redacted evidence.");
            }
            finally
            {
                bridge.Dispose();
                if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
            }
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowStation(string? name, uint flags, uint access, nint security);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetProcessWindowStation(nint station);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateDesktop(string name, string? device, nint devmode, uint flags, uint access, nint security);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetThreadDesktop(nint desktop);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out NativeRect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int count);
}
