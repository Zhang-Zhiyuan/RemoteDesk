namespace RemoteDesk;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; stop.Cancel(); };
        if (args.FirstOrDefault() is "--pair" or "--pair-direct")
        {
            int status = 0;
            try { await SshPublicKeyPairing.RunAsync(args, stop.Token); }
            catch (OperationCanceledException) { return 1; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or
                System.ComponentModel.Win32Exception or InvalidOperationException)
            { Console.Error.WriteLine(ex.Message); status = 1; }
            if (!Console.IsInputRedirected && !Console.IsOutputRedirected)
            { Console.WriteLine("按回车关闭。"); Console.ReadLine(); }
            return status;
        }
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
