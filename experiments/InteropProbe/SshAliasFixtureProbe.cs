using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using RemoteDesk;

// Uses an explicitly owned, ephemeral relay + real sshd. No production settings,
// SSH config, accounts or authorized_keys are changed. Credentials enter via stdin.
internal static class SshAliasFixtureProbe
{
    internal static async Task<int> RunAsync(JsonElement config, string output)
    {
        if (!config.GetProperty("owned").GetBoolean() ||
            !IPAddress.TryParse(config.GetProperty("serverAddress").GetString(), out var address) || !IPAddress.IsLoopback(address))
            throw new InvalidOperationException("Only an owned loopback SSH fixture is permitted.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = deadline.Token;
        string root = Path.Combine(output, "config-root"), profiles = Path.Combine(root, "ssh");
        Directory.CreateDirectory(profiles);
        string userConfig = Path.Combine(root, "config"), managed = Path.Combine(profiles, "terminal_config"),
            known = Path.Combine(profiles, "known_hosts"), key = Path.Combine(root, "fixture-key");
        File.Copy(config.GetProperty("keyPath").GetString()!, key, overwrite: false);
        PrivateFile(key);
        var options = new RelayConnectionOptions(config.GetProperty("serverAddress").GetString()!, config.GetProperty("port").GetInt32(),
            config.GetProperty("token").GetString()!, config.GetProperty("tlsCertificateSha256").GetString()!, config.GetProperty("deviceId").GetString()!).Validate();
        var profile = new SshCommandProfile(Guid.NewGuid(), "5090", options.DeviceId,
            SshCommandProfiles.ComputeRelayScope(options), options.TlsCertificateSha256, config.GetProperty("sshUsername").GetString()!, key).Validate();
        string helper = Path.GetFullPath(config.GetProperty("helperPath").GetString()!);
        var checks = new List<string>();
        try
        {
            SshCommandProfiles.Save(profile, profiles);
            SaveSettings(config.GetProperty("deviceKey").GetString()!);
            SshAliasConfig.Install(profile, helper, userConfig, managed, known);
            // Only this owned fixture uses an alternate settings root. Generated
            // real aliases always use the current user's normal saved settings.
            string configText = File.ReadAllText(managed).Replace(" --stdio " + profile.Id.ToString("D"),
                " --stdio " + profile.Id.ToString("D") + " --config-root \"" + root.Replace('\\', '/') + "\"", StringComparison.Ordinal);
            File.WriteAllText(managed, configText, new UTF8Encoding(false));
            File.WriteAllText(known, SshTerminalLauncher.HostKeyAlias(profile.RelayPin, profile.DeviceId) + " " +
                config.GetProperty("sshHostKeyPublic").GetString()!.Trim() + "\n", new UTF8Encoding(false));
            PrivateFile(managed); PrivateFile(known);
            var result = await Ssh(["-T", "5090", "printf '\\122\\104\\137\\101\\114\\111\\101\\123\\137\\344\\270\\255\\346\\226\\207\\n'"], null);
            Require(result.Code == 0 && result.Out.Trim() == "RD_ALIAS_中文", "native command and Unicode", result.Error);
            checks.Add("native ssh -F <owned-config> -T 5090: relay, gate, pinned host key, public-key login, Unicode");
            result = await Ssh(["-T", "5090", "cat"], "STDIN_中文_42\n");
            Require(result.Code == 0 && result.Out == "STDIN_中文_42\n", "stdin binary transport", result.Error);
            checks.Add("stdin preserved and EOF completes");
            result = await Ssh(["-T", "5090", "exit 37"], null);
            Require(result.Code == 37, "remote exit code", result.Error);
            checks.Add("native SSH preserves remote exit code 37");
            int localPort;
            using (var port = new TcpListener(IPAddress.Loopback, 0)) { port.Start(); localPort = ((IPEndPoint)port.LocalEndpoint).Port; }
            using (var forwarding = StartSsh(["-N", "-o", "ExitOnForwardFailure=yes", "-L",
                $"127.0.0.1:{localPort}:127.0.0.1:{config.GetProperty("sshPort").GetInt32()}", "5090"]))
            {
                Task<string> errors = forwarding.StandardError.ReadToEndAsync(token);
                bool forwarded = false;
                try
                {
                    for (int retry = 0; retry < 80 && !forwarding.HasExited; retry++)
                    {
                        using var socket = new TcpClient();
                        try
                        {
                            await socket.ConnectAsync(IPAddress.Loopback, localPort, token);
                            using var lineDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                            lineDeadline.CancelAfter(TimeSpan.FromSeconds(4));
                            using var reader = new StreamReader(socket.GetStream());
                            string? banner = await reader.ReadLineAsync(lineDeadline.Token);
                            forwarded = banner?.StartsWith("SSH-2.0-OpenSSH", StringComparison.Ordinal) == true;
                            if (forwarded) break;
                        }
                        catch (SocketException) { }
                        await Task.Delay(100, token);
                    }
                    Require(forwarded, "native TCP forwarding", forwarding.HasExited ? await errors : "No SSH banner received over -L");
                    checks.Add("native -N -L authenticated TCP forwarding (VS Code transport prerequisite)");
                }
                finally { if (!forwarding.HasExited) forwarding.Kill(entireProcessTree: true); await forwarding.WaitForExitAsync(token); }
            }
            // Fail closed: normal SSH cannot start after device auth is revoked.
            SaveSettings("deliberately-invalid-owned-fixture-key");
            result = await Ssh(["-T", "5090", "printf forbidden"], null);
            Require(result.Code != 0 && result.Out.Length == 0, "wrong device key fails closed", "Unexpected SSH success");
            checks.Add("wrong device key denied with no SSH stdout");
            File.WriteAllText(Path.Combine(root, "settings.json"), "{}", new UTF8Encoding(false));
            result = await Ssh(["-T", "5090", "printf forbidden"], null);
            Require(result.Code != 0 && result.Out.Length == 0, "logout fails closed", "Unexpected SSH success");
            checks.Add("logged-out settings deny new SSH with no SSH stdout");
            Program.Save(Path.Combine(output, "native-ssh-alias.json"), new { passed = true, checks,
                actualNativeOpenSsh = true, productionConfigChanged = false, installedGuiNeeded = false,
                note = "Owned TLS relay / real WSL sshd fixture; this is not live device 5090 or a VS Code UI end-to-end test." });
            Console.WriteLine($"Native SSH alias fixture: {checks.Count} checks passed.");
            return 0;
        }
        catch (Exception error)
        {
            Program.Save(Path.Combine(output, "native-ssh-alias.json"), new { passed = false, checks, error = error.Message, errorType = error.GetType().Name });
            Console.Error.WriteLine(error.Message);
            return 1;
        }
        finally
        {
            // Delete only generated credentials, not evidence or the source fixture key.
            File.Delete(key);
            File.Delete(Path.Combine(root, "settings.json"));
        }

        void SaveSettings(string deviceKey)
        {
            string Protect(string secret) => Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret),
                Encoding.UTF8.GetBytes("RemoteDesk.HostPassword.v1"), DataProtectionScope.CurrentUser));
            File.WriteAllText(Path.Combine(root, "settings.json"), JsonSerializer.Serialize(new { Relay = new {
                options.ServerAddress, RelayPort = options.Port, ProtectedAccessToken = Protect(options.AccessToken),
                options.TlsCertificateSha256, DeviceKeys = new[] { new { Scope = profile.RelayScope, profile.DeviceId, ProtectedPassword = Protect(deviceKey) } }
            } }), new UTF8Encoding(false));
        }
        Process StartSsh(string[] arguments)
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH", "ssh.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            foreach (string arg in new[] { "-F", userConfig, "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes" }.Concat(arguments)) start.ArgumentList.Add(arg);
            return Process.Start(start) ?? throw new IOException("Native SSH could not start.");
        }
        async Task<(int Code, string Out, string Error)> Ssh(string[] arguments, string? input)
        {
            using var process = StartSsh(arguments);
            try
            {
                Task<string> stdout = process.StandardOutput.ReadToEndAsync(token), stderr = process.StandardError.ReadToEndAsync(token);
                if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), token);
                process.StandardInput.Close();
                await process.WaitForExitAsync(token);
                return (process.ExitCode, await stdout, await stderr);
            }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        }
    }
    private static void Require(bool success, string check, string detail)
    { if (!success) throw new InvalidOperationException(check + ": " + detail); }
    private static void PrivateFile(string path)
    {
        var owner = WindowsIdentity.GetCurrent().User!;
        var access = new FileSecurity();
        access.SetOwner(owner); access.SetAccessRuleProtection(true, false);
        access.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(access);
    }
}
