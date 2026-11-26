using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RemoteDesk;

internal static class WindowsSshService
{
    internal static async Task<string> PrepareAsync()
    {
        using var resource = typeof(WindowsSshService).Assembly.GetManifestResourceStream("RemoteDesk.PrepareSsh.ps1")!;
        using var reader = new StreamReader(resource);
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(await reader.ReadToEndAsync().ConfigureAwait(false))) }) start.ArgumentList.Add(arg);
        using var child = Process.Start(start) ?? throw new IOException("无法准备 Windows OpenSSH。");
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        await Task.WhenAll(output, error, child.WaitForExitAsync()).ConfigureAwait(false);
        if (child.ExitCode != 0) return "准备失败，请检查 Windows 可选功能、Windows 更新和本机 sshd 配置。";
        string status = (await output).Trim();
        return status == "READY" ? "Windows SSH 已就绪。" : status;
    }

    // Ask the OS which sockets its OpenSSH service owns. A WSL portproxy can be
    // listening on the usual port, even when Windows sshd uses a different one.
    private const string ListListeners = """
        $ErrorActionPreference='Stop'
        $service=Get-CimInstance Win32_Service -Filter "Name='sshd'"
        if (!$service) { exit 2 }
        if ($service.State -ne 'Running' -or $service.ProcessId -eq 0) { exit 2 }
        Get-NetTCPConnection -State Listen -OwningProcess $service.ProcessId -ErrorAction SilentlyContinue |
            ForEach-Object { '{0} {1}' -f $_.LocalAddress,$_.LocalPort }
        """;
    private static async Task<IPEndPoint[]> ListAsync(CancellationToken token)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe")) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", ListListeners }) start.ArgumentList.Add(arg);
        using var child = Process.Start(start) ?? throw new IOException("无法查询 Windows OpenSSH 服务。");
        try
        {
            Task<string> output = child.StandardOutput.ReadToEndAsync(token);
            await Task.WhenAll(output, child.StandardError.ReadToEndAsync(token), child.WaitForExitAsync(token)).ConfigureAwait(false);
            return (await output).Split(['\r','\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(ParseListener).OfType<IPEndPoint>().Distinct().ToArray();
        }
        finally { if (!child.HasExited) child.Kill(entireProcessTree: true); }
    }

    internal static IPEndPoint? ParseListener(string row)
    {
        var parts = row.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) ||
            !int.TryParse(parts[1], out int port) || port is < 1 or > 65535) return null;
        if (address.Equals(IPAddress.Any)) address = IPAddress.Loopback;
        if (address.Equals(IPAddress.IPv6Any)) address = IPAddress.IPv6Loopback;
        return new IPEndPoint(address, port);
    }

    internal static async Task<Stream> ConnectAsync(int preferred, CancellationToken token)
    {
        var endpoints = await ListAsync(token).ConfigureAwait(false);
        foreach (var ep in endpoints.OrderByDescending(e => e.Port == preferred)
                     .ThenByDescending(e => IPAddress.IsLoopback(e.Address)))
        {
            var socket = new Socket(ep.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try { await socket.ConnectAsync(ep, token).ConfigureAwait(false); return new NetworkStream(socket, ownsSocket: true); }
            catch (SocketException) { socket.Dispose(); }
            catch { socket.Dispose(); throw; }
        }
        throw new IOException("Windows OpenSSH Server 未就绪，请在被控端准备 SSH 服务。");
    }
}
