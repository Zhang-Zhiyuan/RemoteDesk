using System.Net;
using System.Net.Sockets;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace RemoteDesk;

internal static class WslForwardedSsh
{
    // WSL may already forward a guest listener to Windows localhost. Accept it
    // only if its SSH public host key belongs to the selected distribution.
    internal static async Task<TcpClient?> ConnectAsync(string distro, CancellationToken token)
    {
        string root = @"\\wsl.localhost\" + distro;
        try
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (string type in new[] { "ed25519", "ecdsa", "rsa" })
            {
                string? pub = ReadLines(Path.Combine(root, "etc", "ssh", $"ssh_host_{type}_key.pub")).FirstOrDefault();
                if (PublicKey(pub) is string key) keys.Add(key);
            }
            if (keys.Count == 0) return null;
            var ports = new List<int>();
            foreach (string directory in new[] { @"etc\systemd\system", @"run\systemd\system", @"run\systemd\generator" })
                foreach (string name in new[] { "ssh.socket.d", "sshd.socket.d" })
                    foreach (string file in ConfigurationFiles(Path.Combine(root, directory, name), "*.conf"))
                        foreach (var line in ReadLines(file))
                            if (line.Trim().StartsWith("ListenStream=", StringComparison.Ordinal))
                                AddPort(line.Trim().Split('=', 2)[1].Split(':')[^1], ports);
            string ssh = Path.Combine(root, "etc", "ssh");
            foreach (string file in ConfigurationFiles(Path.Combine(ssh, "sshd_config.d"), "*.conf").Prepend(Path.Combine(ssh, "sshd_config")))
                foreach (string line in ReadLines(file))
                {
                    string[] fields = line.Split('#', 2)[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length < 2) continue;
                    if (fields[0].Equals("Match", StringComparison.OrdinalIgnoreCase)) break;
                    if (fields[0].Equals("Port", StringComparison.OrdinalIgnoreCase)) AddPort(fields[1], ports);
                    if (fields[0].Equals("ListenAddress", StringComparison.OrdinalIgnoreCase) &&
                        (fields[1].Count(c => c == ':') == 1 || fields[1].Contains("]:", StringComparison.Ordinal)))
                        AddPort(fields[1].Split(':')[^1], ports);
                }
            AddPort("22", ports);
            foreach (int port in ports.Take(8))
                foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
                {
                    token.ThrowIfCancellationRequested();
                    using var shortWait = CancellationTokenSource.CreateLinkedTokenSource(token);
                    shortWait.CancelAfter(TimeSpan.FromSeconds(2));
                    try
                    {
                        if (!await MatchesGuestKeyAsync(address, port, keys, shortWait.Token).ConfigureAwait(false)) continue;
                        var client = new TcpClient(address.AddressFamily) { NoDelay = true };
                        try { await client.ConnectAsync(address, port, shortWait.Token).ConfigureAwait(false); return client; }
                        catch { client.Dispose(); throw; }
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                    catch (SocketException) { }
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    private static void AddPort(string value, List<int> ports)
    {
        if (int.TryParse(value, out int port) && port is > 0 and <= 65535 && !ports.Contains(port)) ports.Add(port);
    }

    private static async Task<bool> MatchesGuestKeyAsync(IPAddress address, int port, HashSet<string> keys, CancellationToken token)
    {
        // Stop at server identity verification, before any SSH user authentication.
        using var method = new NoneAuthenticationMethod("remotedesk");
        var info = new ConnectionInfo(address.ToString(), port, method.Username, method) { Timeout = TimeSpan.FromSeconds(2) };
        using var probe = new SshClient(info);
        bool matches = false;
        probe.HostKeyReceived += (_, e) => { matches = keys.Contains(Convert.ToBase64String(e.HostKey)); e.CanTrust = false; };
        try { await probe.ConnectAsync(token).ConfigureAwait(false); }
        catch (SshException) { }
        return matches;
    }

    private static string? PublicKey(string? line)
    {
        var parts = line?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts is { Length: >= 2 } && parts[0] is "ssh-ed25519" or "ssh-rsa" or
            "ecdsa-sha2-nistp256" or "ecdsa-sha2-nistp384" or "ecdsa-sha2-nistp521"
            ? parts[1] : null;
    }

    private static string[] ConfigurationFiles(string path, string pattern)
    {
        try { return Directory.EnumerateFiles(path, pattern).Take(32).Order(StringComparer.Ordinal).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static string[] ReadLines(string path)
    {
        try
        {
            using var input = File.OpenRead(path);
            using var reader = new StreamReader(input, Encoding.UTF8);
            char[] buffer = new char[16384];
            int read = reader.ReadBlock(buffer, 0, buffer.Length);
            return new string(buffer, 0, read).Split('\n');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}
