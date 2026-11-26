using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RemoteDesk;

// Only addresses, ports and account names are remembered. Authentication is owned by OpenSSH.
internal sealed record DirectSshTarget(string Host, int? Port, string Username)
{
    internal static DirectSshTarget Create(string host, string port, string username)
    {
        host = host.Trim();
        if (host.StartsWith('[') && host.EndsWith(']') && host.Contains(':')) host = host[1..^1];
        bool valid = host.Contains(':') ? IPAddress.TryParse(host, out var ip) &&
            ip.AddressFamily == AddressFamily.InterNetworkV6 && Regex.IsMatch(host, @"\A[0-9A-Fa-f:.]+(?:%[A-Za-z0-9_.-]+)?\z")
            : Regex.IsMatch(host, @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,252}\z");
        if (!valid) throw new ArgumentException("请填写 SSH 主机的 IPv4、IPv6 地址或主机名。");
        int? number = null;
        if (port.Trim() != "")
        {
            if (!int.TryParse(port.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value is < 1 or > 65535)
                throw new ArgumentException("SSH 端口必须在 1 到 65535 之间。");
            number = value;
        }
        username = username.Trim();
        if (username.Length != 0 && (!SshTerminalLauncher.IsValidUsername(username) || username.StartsWith('-')))
            throw new ArgumentException("请填写有效的系统账号，或留空使用 SSH 配置。");
        return new(host, number, username);
    }

    internal string[] TerminalArguments()
    {
        List<string> args = ["-o", "ServerAliveInterval=30", "-o", "ServerAliveCountMax=3"];
        if (Port is int p) args.AddRange(["-p", p.ToString(CultureInfo.InvariantCulture)]);
        if (Username.Length != 0) args.AddRange(["-l", Username]);
        args.AddRange(["--", Host]);
        return args.ToArray();
    }

    internal string EditorAuthority()
    {
        var address = new Dictionary<string, object> { ["hostName"] = Host };
        if (Username.Length != 0) address.Add("user", Username);
        if (Port is int port) address.Add("port", port);
        return "ssh-remote+" + Convert.ToHexString(JsonSerializer.SerializeToUtf8Bytes(address)).ToLowerInvariant();
    }
}

internal sealed record DirectSshPreference(string Device, bool Wsl, DirectSshTarget Target);
