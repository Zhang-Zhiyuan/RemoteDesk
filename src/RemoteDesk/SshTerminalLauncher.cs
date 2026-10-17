using System.Diagnostics;
using System.Globalization;

namespace RemoteDesk;

internal static class SshTerminalLauncher
{
    internal static string ExecutablePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh.exe");
    internal static string WindowsTerminalPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");

    // wt.exe is an IPC launcher, not the lifetime of the SSH process. Use a
    // dedicated, randomly named window so recalling it never creates a new SSH.
    internal static ProcessStartInfo? CreateWindowStartInfo(ProcessStartInfo ssh, string terminalPath, string windowId)
    {
        ValidateWindowId(windowId);
        // Windows Terminal interprets semicolons itself, even without a shell.
        // Preserve unusual literal key paths by falling back to native OpenSSH.
        if (ssh.ArgumentList.Prepend(ssh.FileName).Any(value => value.Contains(';') || value.Any(char.IsControl))) return null;
        var start = new ProcessStartInfo(terminalPath) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Normal };
        foreach (string value in new[] { "-w", windowId, "new-tab", "--title", "RemoteDesk SSH [" + windowId[^8..] + "]", "--suppressApplicationTitle", ssh.FileName })
            start.ArgumentList.Add(value);
        foreach (string value in ssh.ArgumentList) start.ArgumentList.Add(value);
        return start;
    }

    internal static ProcessStartInfo CreateRecallStartInfo(string terminalPath, string windowId)
    {
        ValidateWindowId(windowId);
        var start = new ProcessStartInfo(terminalPath) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Normal };
        foreach (string value in new[] { "-w", windowId, "focus-tab", "-t", "0" }) start.ArgumentList.Add(value);
        return start;
    }

    private static void ValidateWindowId(string value)
    {
        if (!value.StartsWith("remotedesk-ssh-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(value["remotedesk-ssh-".Length..], "N", out _)) throw new ArgumentException("SSH 终端窗口身份无效。");
    }

    internal static string HostKeyAlias(string certificateSha256, string deviceId)
    {
        string pin = RelayTls.NormalizeFingerprint(certificateSha256);
        if (pin.Length != 64 || !pin.All(Uri.IsHexDigit) || !Guid.TryParse(deviceId, out Guid id) || id == Guid.Empty)
            throw new ArgumentException("SSH 设备身份无效，请刷新在线列表。");
        return $"remotedesk-{pin.ToLowerInvariant()}-{id:D}:ssh";
    }

    internal static bool IsValidUsername(string? user) => user is { Length: > 0 and <= 128 } &&
        user.All(value => char.IsLetterOrDigit(value) || value is '_' or '-' or '.' or '@' or '\\');

    internal static ProcessStartInfo CreateStartInfo(string executable, int port, string username, string alias, string knownHosts, string? identityFile = null)
    {
        if (port is <= 0 or > 65535 || !IsValidUsername(username)) throw new ArgumentException("请输入有效的 SSH 用户名。");
        if (!alias.StartsWith("remotedesk-", StringComparison.Ordinal) || alias.Any(char.IsWhiteSpace) || alias.Any(char.IsControl))
            throw new ArgumentException("SSH 主机身份无效。");
        string hosts = QuoteLiteralPath(knownHosts);
        // ShellExecute means the Windows graphical shell, not cmd/PowerShell.
        // A real console ssh.exe uses the user's default terminal host and owns
        // its own PTY/password prompt. Neither SSH nor device secrets enter argv.
        var start = new ProcessStartInfo(executable) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Normal };
        foreach (string argument in new[] { "-F", "NUL", "-t", "-p", port.ToString(CultureInfo.InvariantCulture),
            "-o", "EscapeChar=none", "-o", "ForwardAgent=no", "-o", "ForwardX11=no", "-o", "ClearAllForwardings=yes",
            "-o", "HostKeyAlias=" + alias, "-o", "StrictHostKeyChecking=ask", "-o", "CheckHostIP=no",
            "-o", "UserKnownHostsFile=" + hosts, "-o", "GlobalKnownHostsFile=NUL",
            "-o", "ServerAliveInterval=30", "-o", "ServerAliveCountMax=3", "-o", "ConnectTimeout=20",
            "-l", username }) start.ArgumentList.Add(argument);
        if (!string.IsNullOrWhiteSpace(identityFile))
        {
            // -i performs an early literal file check, followed by token expansion.
            // The quoted config form expands %% once without rejecting that
            // escaped spelling first. The original user key is never copied.
            start.ArgumentList.Add("-o"); start.ArgumentList.Add("IdentityFile=" + QuoteLiteralPath(identityFile));
            start.ArgumentList.Add("-o"); start.ArgumentList.Add("IdentitiesOnly=yes");
        }
        start.ArgumentList.Add("127.0.0.1");
        return start;
    }

    private static string QuoteLiteralPath(string path)
    {
        string full = Path.GetFullPath(path).Replace('\\', '/');
        if (full.Contains('"') || full.Any(char.IsControl) || full.Contains("${", StringComparison.Ordinal))
            throw new ArgumentException("SSH 文件路径包含不支持的引号、控制字符或 ${变量} 形式，请选择普通文件路径。");
        return "\"" + full.Replace("%", "%%", StringComparison.Ordinal) + "\"";
    }
}
