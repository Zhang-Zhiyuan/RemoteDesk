using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class CheckScriptNetworkTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiagnosticHelpersActuallyProbeIpv4AndIpv6Loopback(bool ipv6)
    {
        Assert.True(!ipv6 || Socket.OSSupportsIPv6,
            "The IPv6 integration fixture requires enabled OS IPv6 support; it does not change network settings.");
        IPAddress address = ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var tcp = new TcpListener(address, 0);
        tcp.Start();
        using var udp = new UdpClient(new IPEndPoint(address, 0));
        int tcpPort = ((IPEndPoint)tcp.LocalEndpoint).Port;
        int udpPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        Task server = Task.Run(async () =>
        {
            for (int i = 0; i < 3; i++)
            {
                using var peer = await tcp.AcceptTcpClientAsync(deadline.Token);
                using var stream = peer.GetStream();
                var auth = await Protocol.AuthenticateServerDetailedAsync(stream, "owned-diagnostic-key", deadline.Token);
                using var session = auth.Session;
                Assert.Equal(i == 1, auth.IsAuthenticated);
            }
        });
        Task discovery = Task.Run(async () =>
        {
            UdpReceiveResult request = await udp.ReceiveAsync(deadline.Token);
            Assert.Equal("RemoteDesk.Discover.v1", Encoding.UTF8.GetString(request.Buffer));
            await udp.SendAsync("owned-discovery-reply"u8.ToArray(), request.RemoteEndPoint, deadline.Token);
        });
        string scriptPath = FindCheckScript().Replace("'", "''");
        // Parse and run just these pure network helpers, not the script's build,
        // deployment, process checks, Android actions or interactive prompting.
        string script = $$"""
            $ErrorActionPreference='Stop'
            $parseErrors=$null
            $ast=[Management.Automation.Language.Parser]::ParseFile('{{scriptPath}}',[ref]$null,[ref]$parseErrors)
            if($parseErrors.Count -gt 0) { throw ($parseErrors | Out-String) }
            $names=@('Test-RemoteDeskTcpHandshake','Test-RemoteDeskAuthentication','Test-RemoteDeskUdpDiscovery','Read-ExactBytes','Disable-UdpConnectionReset','Resolve-RemoteDeskTargets','Resolve-IPv4Targets','Get-LocalIpSummary')
            foreach($fn in $ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst]},$false)) {
                if($fn.Name -in $names) { . ([scriptblock]::Create($fn.Extent.Text)) }
            }
            $resolver=if(Get-Command Resolve-RemoteDeskTargets -ErrorAction SilentlyContinue){'Resolve-RemoteDeskTargets'}else{'Resolve-IPv4Targets'}
            # Isolated IPv6-only adapter fixture; never modify the machine's adapters.
            function Get-NetIPAddress {
                param($ErrorAction)
                foreach($ip in @('2001:db8::42','::1','fe80::1%7','127.0.0.1','169.254.1.2')) {
                    [pscustomobject]@{IPAddress=$ip;PrefixOrigin='Manual'}
                }
            }
            @{
                localSummary=Get-LocalIpSummary
                resolved=@(& $resolver '{{address}}')
                bracketed=@(& $resolver '[::1]')
                scoped=@(& $resolver 'fe80::1%7')
                empty=@(& $resolver '  ')
                magic=Test-RemoteDeskTcpHandshake '{{address}}' {{tcpPort}}
                auth=Test-RemoteDeskAuthentication '{{address}}' {{tcpPort}} 'owned-diagnostic-key'
                rejected=Test-RemoteDeskAuthentication '{{address}}' {{tcpPort}} 'owned-wrong-key'
                discovery=Test-RemoteDeskUdpDiscovery '{{address}}' {{udpPort}}
            }|ConvertTo-Json -Compress
            """;
        // This test project targets Windows. Use its built-in shell so running
        // the diagnostics regression does not require installing PowerShell 7.
        string shell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(shell) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string value in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(value);
        using var process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            Assert.True(process.ExitCode == 0, await stderr);
            using JsonDocument result = JsonDocument.Parse(await stdout);
            var value = result.RootElement;
            Assert.Equal("RemoteDesk TCP handshake OK", value.GetProperty("magic").GetString());
            Assert.Equal("RemoteDesk authentication OK", value.GetProperty("auth").GetString());
            Assert.Equal("RemoteDesk authentication rejected", value.GetProperty("rejected").GetString());
            Assert.Equal($"UDP discovery response from {new IPEndPoint(address, udpPort)}", value.GetProperty("discovery").GetString());
            Assert.Equal("2001:db8::42", value.GetProperty("localSummary").GetString());
            Assert.Equal(address.ToString(), Assert.Single(value.GetProperty("resolved").EnumerateArray()).GetString());
            Assert.Equal("::1", Assert.Single(value.GetProperty("bracketed").EnumerateArray()).GetString());
            Assert.Equal("fe80::1%7", Assert.Single(value.GetProperty("scoped").EnumerateArray()).GetString());
            Assert.Empty(value.GetProperty("empty").EnumerateArray());
            await Task.WhenAll(server, discovery).WaitAsync(deadline.Token);
        }
        finally
        {
            deadline.Cancel(); tcp.Stop(); udp.Dispose();
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            try { await Task.WhenAll(server, discovery).WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }
    }

    private static string FindCheckScript()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                string path = Path.Combine(directory.FullName, "scripts", "Invoke-RemoteDeskCheck.ps1");
                if (File.Exists(path)) return path;
            }
        throw new FileNotFoundException("Repository check script not found.");
    }
}
