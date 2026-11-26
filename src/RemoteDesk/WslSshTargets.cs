using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace RemoteDesk;

internal sealed record WslSshTarget(string Id, string Name)
{
    public string RouteId(string hostId)
    {
        byte[] digest = SHA256.HashData(Encoding.ASCII.GetBytes($"RemoteDesk/WSL/1\n{Guid.Parse(hostId):D}\n{Guid.Parse(Id):D}"));
        return new Guid(digest.AsSpan(0, 16), bigEndian: true).ToString("D");
    }
}

internal sealed class WslSshTargets
{
    private WslSshTarget[] _running = [];
    private long _lastCheck;
    internal WslSshTarget? Find(string host, string route) =>
        Volatile.Read(ref _running).FirstOrDefault(d => d.RouteId(host) == route);

    internal async Task<WslSshTarget[]> ReadAsync(CancellationToken token)
    {
        long now = Environment.TickCount64;
        if (_lastCheck != 0 && now - _lastCheck < 25_000) return Volatile.Read(ref _running);
        WslSshTarget[] result = [];
        try
        {
            using var registry = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
            if (registry is not null)
            {
                var installed = new List<WslSshTarget>();
                foreach (string sub in registry.GetSubKeyNames())
                {
                    if (!Guid.TryParse(sub, out Guid id)) continue;
                    using var distro = registry.OpenSubKey(sub);
                    if (distro?.GetValue("DistributionName") is string name && ValidName(name))
                        installed.Add(new(id.ToString("D"), name));
                }
                if (installed.Count != 0)
                {
                    var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "wsl.exe")) {
                        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                        StandardOutputEncoding = Encoding.Unicode, RedirectStandardError = true };
                    // Redirected wsl --list is UTF-16 unless the caller has opted into UTF-8.
                    start.Environment.Remove("WSL_UTF8");
                    foreach (var arg in new[] { "--list", "--running", "--quiet" }) start.ArgumentList.Add(arg);
                    using var child = Process.Start(start) ?? throw new IOException("无法列出 WSL 发行版。");
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(4));
                    try
                    {
                        Task<string> output = child.StandardOutput.ReadToEndAsync(timeout.Token);
                        Task<string> error = child.StandardError.ReadToEndAsync(timeout.Token);
                        await Task.WhenAll(output, error, child.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
                        if (child.ExitCode == 0)
                        {
                            var names = (await output).TrimStart('\uFEFF').Split(['\r','\n'], StringSplitOptions.RemoveEmptyEntries)
                                .Select(n => n.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                            result = installed.Where(d => names.Contains(d.Name)).OrderBy(d => d.Name).Take(8).ToArray();
                        }
                    }
                    finally { if (!child.HasExited) child.Kill(); }
                }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
        token.ThrowIfCancellationRequested();
        Volatile.Write(ref _running, result);
        _lastCheck = now;
        return result;
    }

    internal static bool ValidName(string name) => name.Length is > 0 and <= 80 &&
        name is not ("." or "..") && name == name.Trim() && !name.StartsWith('-') &&
        !name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase) &&
        name.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or ' ' or '(' or ')' or '+');
}
