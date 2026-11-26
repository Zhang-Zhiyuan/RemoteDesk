using Microsoft.Win32;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;

namespace RemoteDesk;

internal static class WslSshGuest
{
    internal static async Task<int> RunAsync(Guid channel, Guid distribution, CancellationToken stopping)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var pipe = new NamedPipeClientStream(".", "RemoteDesk.WslSsh." + channel.ToString("N"),
            PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        using var identity = WindowsIdentity.GetCurrent();
        using var reg = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss\" + distribution.ToString("B"));
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            await pipe.WriteAsync(new byte[] { 4 }, timeout.Token).ConfigureAwait(false);
            return 1;
        }
        if (reg?.GetValue("DistributionName") is not string name || !WslSshTargets.ValidName(name) ||
            !(await new WslSshTargets().ReadAsync(timeout.Token)).Any(d => d.Id == distribution.ToString("D")))
        {
            await pipe.WriteAsync(new byte[] { 5 }, timeout.Token).ConfigureAwait(false);
            return 1;
        }
        System.Net.Sockets.TcpClient? forwarded = null;
        using (var lookup = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token))
        {
            lookup.CancelAfter(TimeSpan.FromSeconds(4));
            try
            {
                forwarded = await WslForwardedSsh.ConnectAsync(name, lookup.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lookup.IsCancellationRequested && !timeout.IsCancellationRequested) { }
        }
        if (forwarded is not null)
        {
            using (forwarded)
            {
                await pipe.WriteAsync(new byte[] { 1 }, timeout.Token).ConfigureAwait(false);
                await SshCommandProxy.BridgeAsync(pipe, pipe, forwarded.GetStream(), forwarded, stopping).ConfigureAwait(false);
            }
            return 0;
        }
        using var resource = typeof(WslSshGuest).Assembly.GetManifestResourceStream("RemoteDesk.WslSshGuest.py")!;
        using var reader = new StreamReader(resource);
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "wsl.exe")) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "--distribution", name, "--exec", "/usr/bin/python3", "-I", "-u", "-c",
            (await reader.ReadToEndAsync()).Replace("\r\n", "\n") }) start.ArgumentList.Add(arg);
        using var guest = Process.Start(start) ?? throw new IOException("无法启动 WSL SSH 通道。");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        try
        {
            var errors = guest.StandardError.BaseStream.CopyToAsync(Stream.Null, cancel.Token);
            Stream output = guest.StandardOutput.BaseStream;
            byte[] ready = new byte[1];
            int read = await output.ReadAsync(ready, timeout.Token).ConfigureAwait(false);
            if (read != 1 || ready[0] is not (1 or 2)) ready[0] = 6;
            await pipe.WriteAsync(ready, timeout.Token).ConfigureAwait(false);
            if (ready[0] != 1) return 1;
            var send = pipe.CopyToAsync(guest.StandardInput.BaseStream, cancel.Token);
            var receive = output.CopyToAsync(pipe, cancel.Token);
            if (await Task.WhenAny(send, receive).ConfigureAwait(false) == send)
            {
                await send.ConfigureAwait(false);
                guest.StandardInput.Close();
            }
            await receive.ConfigureAwait(false);
            await errors.ConfigureAwait(false);
            return 0;
        }
        finally
        {
            cancel.Cancel();
            if (!guest.HasExited) guest.Kill(entireProcessTree: true);
        }
    }
}
