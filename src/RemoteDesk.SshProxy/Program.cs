namespace RemoteDesk;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; stop.Cancel(); };
        if (args is ["--wsl-ssh", var channel, var distro] && Guid.TryParseExact(channel, "N", out var pipeId) &&
            Guid.TryParseExact(distro, "D", out var distroId))
        {
            try { return await WslSshGuest.RunAsync(pipeId, distroId, stop.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException or
                System.ComponentModel.Win32Exception) { return 1; }
        }
        // stdout is exclusively the SSH transport. Do not initialize the GUI,
        // print a banner, change its encoding, or read credentials from stdin.
        using Stream input = Console.OpenStandardInput();
        using Stream output = Console.OpenStandardOutput();
        return await SshCommandProxy.RunAsync(args, input, output, Console.Error, stop.Token)
            .ConfigureAwait(false);
    }
}
