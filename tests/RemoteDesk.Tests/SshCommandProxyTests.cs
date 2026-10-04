using System.Net;
using System.Net.Sockets;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class SshCommandProxyTests
{
    private const string DeviceId = "01234567-89ab-cdef-0123-456789abcdef";
    private static RelayConnectionOptions Options => new("relay.example.invalid", 56567, new string('k', 64), new string('A', 64), DeviceId);
    private static SshCommandProfile Profile => new(Guid.NewGuid(), "5090", DeviceId,
        SshCommandProfiles.ComputeRelayScope(Options), Options.TlsCertificateSha256, "zzy", null);

    [Theory]
    [InlineData("5090", true)]
    [InlineData("2080Ti_HYX", true)]
    [InlineData("dev-node", true)]
    [InlineData("", false)]
    [InlineData("-x", false)]
    [InlineData("a.b", false)]
    [InlineData("a b", false)]
    [InlineData("a\nHost *", false)]
    [InlineData("a%h", false)]
    [InlineData("a$(id)", false)]
    [InlineData("?", false)]
    public void AliasIsOneLiteralHostToken(string alias, bool expected) => Assert.Equal(expected, SshCommandProfiles.IsValidAlias(alias));

    [Fact]
    public void ProfileScopeMatchesExistingDeviceKeyStore()
    {
        Assert.Equal(RelayDeviceKeys.Scope(Options), SshCommandProfiles.ComputeRelayScope(Options));
        Assert.Equal(RelayDeviceKeys.Scope(Options with { ServerAddress = " RELAY.example.invalid. " }), SshCommandProfiles.ComputeRelayScope(Options));
    }

    [Fact]
    public void ProfileContainsNoCredentialsAndOnlySameTargetCanReplaceItsIdentity()
    {
        using var fixture = new Fixture();
        var profile = Profile;
        SshCommandProfiles.Save(profile, fixture.Directory);
        Assert.Equal(profile, SshCommandProfiles.Read(profile.Id, fixture.Directory));
        SshCommandProfiles.Save(profile with { Username = "new-user" }, fixture.Directory);
        Assert.Equal("new-user", Assert.Single(SshCommandProfiles.ReadAll(fixture.Directory)).Username);
        Assert.Throws<InvalidDataException>(() => SshCommandProfiles.Save(profile with { Id = Guid.NewGuid(), Alias = "5090" }, fixture.Directory));
        Assert.Throws<InvalidDataException>(() => SshCommandProfiles.Save(profile with { DeviceId = Guid.NewGuid().ToString("D") }, fixture.Directory));
        var fields = typeof(SshCommandProfile).GetProperties().Select(p => p.Name).ToArray();
        Assert.DoesNotContain(fields, field => field.Contains("Password") || field.Contains("Token") || field.Contains("Secret"));
        SshCommandProfiles.Remove(profile.Id, fixture.Directory);
        Assert.Empty(SshCommandProfiles.ReadAll(fixture.Directory));
    }

    [Fact]
    public void CorruptOrMisidentifiedProfileIsReadOnlyFailure()
    {
        using var fixture = new Fixture();
        var profile = Profile;
        SshCommandProfiles.Save(profile, fixture.Directory);
        string path = Path.Combine(fixture.Directory, "profiles", profile.Id.ToString("D") + ".json");
        File.WriteAllText(path, "broken-json");
        Assert.Throws<InvalidDataException>(() => SshCommandProfiles.Read(profile.Id, fixture.Directory));
        Assert.Equal("broken-json", File.ReadAllText(path));
        File.WriteAllText(path, JsonSerializer.Serialize(profile with { Id = Guid.NewGuid() }));
        Assert.Throws<InvalidDataException>(() => SshCommandProfiles.Read(profile.Id, fixture.Directory));
    }

    [Fact]
    public void ProfileSaveRejectsRedirectedAncestorWithoutWritingTarget()
    {
        using var fixture = new Fixture();
        string target = Path.Combine(fixture.Directory, "actual"), link = Path.Combine(fixture.Directory, "redirect");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(link, target);
        try
        {
            Assert.Throws<IOException>(() => SshCommandProfiles.Save(Profile, Path.Combine(link, "ssh")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void ProfileCannotWriteARecordLargerThanItsReaderAccepts()
    {
        using var fixture = new Fixture();
        string directory = Path.Combine(fixture.Directory, "ssh");
        var profile = Profile with { IdentityFile = Path.Combine(fixture.Directory, new string('中', 12000)) };
        Assert.Throws<InvalidDataException>(() => SshCommandProfiles.Save(profile, directory));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void DeviceCredentialsAreReadFromCurrentUserSettingsWithoutMutations()
    {
        using var fixture = new Fixture();
        string settingsPath = fixture.WriteSettings();
        byte[] before = File.ReadAllBytes(settingsPath);
        var credentials = SshCommandProxy.ReadCredentials(Profile, settingsPath);
        Assert.Equal(Options, credentials.Options);
        Assert.Equal("owned-device-key", credentials.DeviceKey);
        Assert.DoesNotContain("owned-device-key", credentials.ToString());
        Assert.Equal(before, File.ReadAllBytes(settingsPath));
        Assert.Single(Directory.GetFiles(fixture.Directory));
    }

    [Theory]
    [InlineData("logout")]
    [InlineData("different-pin")]
    [InlineData("different-address")]
    [InlineData("missing-device-key")]
    [InlineData("invalid-dpapi")]
    [InlineData("string-port")]
    public void InvalidOrWithdrawnLoginFailsBeforeAnyNetwork(string scenario)
    {
        using var fixture = new Fixture();
        string path = fixture.WriteSettings();
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        switch (scenario)
        {
            case "logout": node["Relay"]!["ProtectedAccessToken"] = null; break;
            case "different-pin": node["Relay"]!["TlsCertificateSha256"] = new string('B', 64); break;
            case "different-address": node["Relay"]!["ServerAddress"] = "another.invalid"; break;
            case "missing-device-key": node["Relay"]!["DeviceKeys"] = new System.Text.Json.Nodes.JsonArray(); break;
            case "invalid-dpapi": node["Relay"]!["ProtectedAccessToken"] = "not-dpapi"; break;
            case "string-port": node["Relay"]!["RelayPort"] = "56567"; break;
        }
        File.WriteAllText(path, node.ToJsonString());
        byte[] before = File.ReadAllBytes(path);
        Assert.ThrowsAny<Exception>(() => SshCommandProxy.ReadCredentials(Profile, path));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void CorruptGuiSettingsAreNeverRenamedByProxy()
    {
        using var fixture = new Fixture();
        string path = Path.Combine(fixture.Directory, "settings.json");
        File.WriteAllText(path, "{broken:secret-data}");
        var error = Assert.Throws<InvalidDataException>(() => SshCommandProxy.ReadCredentials(Profile, path));
        Assert.DoesNotContain("secret-data", error.Message);
        Assert.Equal("{broken:secret-data}", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(fixture.Directory));
    }

    [Theory]
    [InlineData("--help", "")]
    [InlineData("--stdio", "../settings.json")]
    [InlineData("--stdio", "00000000-0000-0000-0000-000000000000")]
    public async Task InvalidArgumentsWriteOnlyStderr(string command, string value)
    {
        using var stdout = new MemoryStream();
        using var stderr = new StringWriter();
        int result = await SshCommandProxy.RunAsync([command, value], Stream.Null, stdout, stderr, CancellationToken.None);
        Assert.Equal(2, result);
        Assert.Empty(stdout.ToArray());
        Assert.Contains("SSH", stderr.ToString());
    }

    [Fact]
    public async Task StdioBridgePreservesArbitraryBytesAndClosesWhenRemoteEnds()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, timeout.Token);
        using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
        using var input = new BlockingInput();
        using var output = new MemoryStream();
        Task bridge = SshCommandProxy.BridgeAsync(input, output, client.GetStream(), client, timeout.Token);
        byte[] data = Enumerable.Range(0, 8192).Select(i => (byte)i).ToArray();
        await peer.GetStream().WriteAsync(data, timeout.Token);
        peer.Client.Shutdown(SocketShutdown.Send);
        await bridge.WaitAsync(timeout.Token);
        Assert.Equal(data, output.ToArray());
        input.Release();
    }

    [Fact]
    public async Task StdioRemoteEofPreservesFinalBytesForSlowNativeReader()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, timeout.Token);
        using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
        string pipeName = "RemoteDesk-owned-stdio-" + Guid.NewGuid().ToString("N");
        using var output = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096);
        using var reader = new NamedPipeClientStream(".", pipeName, PipeDirection.In, PipeOptions.Asynchronous);
        Task accepting = output.WaitForConnectionAsync(timeout.Token);
        await reader.ConnectAsync(timeout.Token);
        await accepting;
        using var input = new BlockingInput();
        Task bridge = SshCommandProxy.BridgeAsync(input, output, client.GetStream(), client, timeout.Token);
        byte[] expected = Enumerable.Range(0, 60000).Select(i => (byte)i).ToArray();
        await peer.GetStream().WriteAsync(expected, timeout.Token);
        peer.Client.Shutdown(SocketShutdown.Send);
        // The native SSH consumer intentionally does not drain its pipe yet.
        await Task.Delay(100, timeout.Token);
        byte[] received = new byte[expected.Length];
        await reader.ReadExactlyAsync(received, timeout.Token);
        await bridge.WaitAsync(timeout.Token);
        Assert.Equal(expected, received);
    }

    private sealed class BlockingInput : Stream
    {
        private readonly TaskCompletionSource<int> _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Release() => _pending.TrySetResult(0);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _pending.Task.GetAwaiter().GetResult();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => _pending.Task;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => new(_pending.Task);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Release(); base.Dispose(disposing); }
    }

    private sealed class Fixture : IDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "RemoteDeskSshCommandTests", Guid.NewGuid().ToString("N"));
        internal Fixture() => System.IO.Directory.CreateDirectory(Directory);
        internal string WriteSettings()
        {
            string path = Path.Combine(Directory, "settings.json");
            var options = Options;
            var settings = new RemoteDeskSettings();
            settings.Relay.ServerAddress = options.ServerAddress;
            settings.Relay.RelayPort = options.Port;
            settings.Relay.TlsCertificateSha256 = options.TlsCertificateSha256;
            settings.Relay.ProtectedAccessToken = AppSettingsService.ProtectSecret(options.AccessToken);
            settings.Relay.DeviceKeys = RelayDeviceKeys.Remember([], options, AppSettingsService.ProtectSecret("owned-device-key")!);
            File.WriteAllText(path, JsonSerializer.Serialize(settings));
            return path;
        }
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
