using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RemoteDesk;

internal sealed record FleetUpdate(Version Version, string Build, long Size, string Sha256)
{
    internal static readonly Version InstalledVersion = typeof(FleetUpdate).Assembly.GetName().Version!;
    internal const string Platform = "win-x64";
    private static readonly Lazy<EventWaitHandle> RelayReady = new(() => new EventWaitHandle(false,
        EventResetMode.ManualReset, @"Local\RemoteDesk.RelayOnline." + Environment.ProcessId));
    internal static void SignalRelayReady()
    {
        try { RelayReady.Value.Set(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        { System.Diagnostics.Trace.TraceWarning("中继更新健康检查暂不可用：{0}", error.Message); }
    }

    internal static FleetUpdate Verify(JsonElement ticket, X509Certificate2 publisher)
    {
        var version = ticket.GetProperty("version").GetString() ?? "";
        var build = ticket.GetProperty("build").GetString() ?? "";
        var platform = ticket.GetProperty("platform").GetString();
        var size = ticket.GetProperty("size").GetInt64();
        var hash = ticket.GetProperty("sha256").GetString() ?? "";
        if (platform != Platform || !Regex.IsMatch(version, @"\A(0|[1-9][0-9]{0,4})(\.(0|[1-9][0-9]{0,4})){2}\z") ||
            !Regex.IsMatch(build, @"\A[0-9]{14}\z") || !Regex.IsMatch(hash, @"\A[0-9a-f]{64}\z") || size is <= 0 or > 268435456)
            throw new InvalidDataException("更新清单格式无效。");
        string signed = string.Join('\n', "RemoteDesk/fleet/v1", version, build, platform, size.ToString(CultureInfo.InvariantCulture), hash);
        using var rsa = publisher.GetRSAPublicKey();
        if (rsa is null || !rsa.VerifyData(Encoding.ASCII.GetBytes(signed), ticket.GetProperty("signature").GetBytesFromBase64(),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) throw new CryptographicException("更新发布者签名无效。");
        return new(Version.Parse(version), build, size, hash);
    }

    internal static X509Certificate2 LoadPublisher()
    {
        using var stream = typeof(FleetUpdate).Assembly.GetManifestResourceStream("RemoteDesk.FleetPublisher.cer")!;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return new(memory.ToArray());
    }

    internal async Task DownloadAsync(RelayConnectionOptions options, string path, CancellationToken token)
    {
        var (client, stream) = await RelayTls.ConnectAsync(options, token).ConfigureAwait(false);
        using (client)
        using (stream)
        {
            await RelayTls.WriteJsonAsync(stream, new { version = 1, role = "update-package", token = options.AccessToken,
                updatePlatform = Platform, sha256 = Sha256 }, token).ConfigureAwait(false);
            using var response = await RelayTls.ReadJsonAsync(stream, token).ConfigureAwait(false);
            RelayTls.EnsureSuccess(response.RootElement);
            if (response.RootElement.GetProperty("size").GetInt64() != Size) throw new InvalidDataException("更新大小有误。");
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536];
            long remaining = Size;
            while (remaining > 0)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("下载未完成。");
                hash.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                remaining -= read;
            }
            if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), Sha256, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException("更新包 SHA-256 校验失败。");
        }
    }
}
