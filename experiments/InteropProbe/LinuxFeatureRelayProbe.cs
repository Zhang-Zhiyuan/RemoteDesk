using System.Diagnostics;
using System.Text.Json;
using RemoteDesk;

// Reuse the existing pinned local relay login without exporting its token to a
// shell, argv, environment or report. The child creates its OWN Linux/Xvfb host
// and random relay identity; it does not replace an installed registration.
internal static class LinuxFeatureRelayProbe
{
    internal static async Task<int> RunAsync(RelayConnectionOptions options, string target, string output)
    {
        if (target != "zzy@10.7.163.74" || options.ServerAddress != "8.138.5.232")
            throw new InvalidOperationException("Only the explicitly authorized physical Linux and relay are supported.");
        string script = Path.GetFullPath("experiments/run_feature_audit.py");
        if (!File.Exists(script)) throw new FileNotFoundException("Run this probe from the repository root.");
        string childOutput = Path.Combine(output, "linux-features");
        if (Directory.Exists(childOutput)) throw new IOException("Preserving previous feature-audit evidence.");
        var start = new ProcessStartInfo("python")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string arg in new[] { "-u", script, "--linux", target, "--output", childOutput, "--relay-stdin" })
            start.ArgumentList.Add(arg);
        using var child = Process.Start(start) ?? throw new IOException("Could not start the owned feature audit.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        Task<string> stdout = child.StandardOutput.ReadToEndAsync();
        Task<string> stderr = child.StandardError.ReadToEndAsync();
        try
        {
            await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                serverAddress = options.ServerAddress, port = options.Port,
                accessToken = options.AccessToken, tlsCertificateSha256 = options.TlsCertificateSha256
            }));
            child.StandardInput.Close();
            await child.WaitForExitAsync(timeout.Token);
            _ = await stdout; _ = await stderr; // Do not relay arbitrary credential-adjacent subprocess output.
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(childOutput, "result.json")));
            bool complete = child.ExitCode == 0 && report.RootElement.GetProperty("complete").GetBoolean();
            Console.WriteLine($"Public-relay Windows/Linux feature audit complete={complete}; evidence={childOutput}");
            return complete ? 0 : 1;
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
            }
        }
    }
}
