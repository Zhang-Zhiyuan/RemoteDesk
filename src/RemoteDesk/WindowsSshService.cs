using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RemoteDesk;

internal static class WindowsSshService
{
    internal sealed record Preparation(byte Reason, string Message);
    private static readonly object PreparationLock = new();
    private static Task<Preparation>? _preparing;
    private static long _retryAfter;
    private static int _preparationStatus;
    internal static Task<Preparation> PrepareAsync(bool retry = false, bool enableDisabled = false)
    {
        lock (PreparationLock)
        {
            if (_preparing is not null && (!_preparing.IsCompleted || !retry && Environment.TickCount64 < _retryAfter))
                return _preparing;
            Volatile.Write(ref _preparationStatus, 25);
            return _preparing = Task.Run(async () =>
            {
                Preparation result;
                try { result = await PrepareCoreAsync(enableDisabled).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                { result = new(28, SshGateProtocol.NativeServiceFailure(28) + " " + ex.Message); }
                lock (PreparationLock)
                {
                    Volatile.Write(ref _preparationStatus, result.Reason);
                    _retryAfter = Environment.TickCount64 + 120_000;
                }
                return result;
            });
        }
    }

    private static async Task<Preparation> PrepareCoreAsync(bool enableDisabled)
    {
        using var resource = typeof(WindowsSshService).Assembly.GetManifestResourceStream("RemoteDesk.PrepareSsh.ps1")!;
        using var reader = new StreamReader(resource);
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        // The script is an embedded resource. Reading it from stdin avoids the Windows
        // command-line length limit and requires neither a temporary script nor a policy change.
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command",
            "& ([scriptblock]::Create([Console]::In.ReadToEnd()))" + (enableDisabled ? " -EnableDisabled" : "") })
            start.ArgumentList.Add(arg);
        using var child = Process.Start(start) ?? throw new IOException("无法准备 Windows OpenSSH。");
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        await child.StandardInput.WriteAsync(await reader.ReadToEndAsync().ConfigureAwait(false)).ConfigureAwait(false);
        child.StandardInput.Close();
        await Task.WhenAll(output, error, child.WaitForExitAsync()).ConfigureAwait(false);
        return ParsePreparation(child.ExitCode, (await output).Trim());
    }

    internal static Preparation ParsePreparation(int exitCode, string status)
    {
        var parts = status.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        byte reason = (exitCode == 0 ? parts.FirstOrDefault() : null) switch
        {
            "READY" => 0, "DISABLED" => 21, "INSTALL_FAILED" => 26, "RESTART_NEEDED" => 27,
            "PORT_BUSY" => 29, "CONFIG_CHECK" or "CUSTOM_SERVICE" => 30, "ADMIN_REQUIRED" => 31, _ => 28
        };
        string message = reason == 0 ? "Windows SSH 已就绪。" : SshGateProtocol.NativeServiceFailure(reason);
        if ((reason == 26 && parts.Length == 2 ||
             reason == 28 && parts.Length == 3 && parts[0] == "PREP_ERROR" &&
             parts[1] is "inspection" or "installation" or "configuration" or "startup") &&
            parts[^1].Length == 10 && parts[^1].StartsWith("0x", StringComparison.Ordinal) &&
            uint.TryParse(parts[^1].AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out _))
            message += " " + string.Join(" ", parts.Skip(1));
        return new(reason, message);
    }

    internal sealed class Unavailable(byte reason) : IOException(SshGateProtocol.NativeServiceFailure(reason)) { internal byte Reason => reason; }

    // Ask the OS which sockets its OpenSSH service owns. A WSL portproxy can be
    // listening on the usual port, even when Windows sshd uses a different one.
    private const string ListListeners = """
        $ErrorActionPreference='Stop'
        $service=Get-CimInstance Win32_Service -Filter "Name='sshd'"
        if (!$service) { exit 20 }
        if ($service.StartMode -eq 'Disabled') { exit 21 }
        if ($service.State -ne 'Running' -or $service.ProcessId -eq 0) { exit 22 }
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
            if (child.ExitCode != 0)
            {
                int reason = child.ExitCode is 20 or 21 or 22 ? child.ExitCode : 23;
                if (reason is 20 or 22)
                {
                    // Called only after host enrollment and an authenticated native SSH request.
                    // Service preparation can outlast the connection's handshake deadline.
                    _ = PrepareAsync();
                    int preparing = Volatile.Read(ref _preparationStatus);
                    if (preparing != 0) reason = preparing;
                }
                throw new Unavailable((byte)reason);
            }
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
        throw new Unavailable(24);
    }
}
