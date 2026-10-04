using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RemoteDesk;

internal static class SshCommandProxy
{
    internal static async Task<int> RunAsync(string[] args, Stream input, Stream output, TextWriter error,
        CancellationToken token)
    {
        if ((args.Length != 2 && args.Length != 4) || args[0] != "--stdio" || !Guid.TryParseExact(args[1], "D", out Guid id) || id == Guid.Empty ||
            (args.Length == 4 && (args[2] != "--config-root" || !Path.IsPathFullyQualified(args[3]) || args[3].Any(char.IsControl))))
        {
            await error.WriteLineAsync("RemoteDesk SSH 代理只供 OpenSSH 使用。请在 RemoteDesk 在线设备中配置终端 SSH 别名，再运行 ssh 别名。");
            return 2;
        }
        try
        {
            // Explicit alternate root is for portable setups and isolated tests.
            // It changes paths only, never DPAPI user binding or TLS/device auth.
            string? root = args.Length == 4 ? Path.GetFullPath(args[3]) : null;
            SshCommandProfile profile = SshCommandProfiles.Read(id, root is null ? null : Path.Combine(root, "ssh"));
            var credentials = ReadCredentials(profile, root is null ? SshCommandProfiles.DefaultSettingsPath : Path.Combine(root, "settings.json"));
            using var connection = await SshRelayClient.OpenAsync(credentials.Options, credentials.DeviceKey, token).ConfigureAwait(false);
            await BridgeAsync(input, output, connection.Stream, connection, token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return 130; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or
            CryptographicException or System.ComponentModel.Win32Exception or System.Net.Sockets.SocketException or TimeoutException or System.Security.Authentication.AuthenticationException)
        {
            // stdout belongs entirely to OpenSSH. Never emit a log, banner,
            // credential, prompt or stack trace into its binary protocol stream.
            await error.WriteLineAsync("RemoteDesk SSH：" + ex.Message);
            return 1;
        }
    }

    internal sealed record Credentials(RelayConnectionOptions Options, string DeviceKey)
    {
        public override string ToString() => "SshCommandCredentials(<redacted>)";
    }

    internal static Credentials ReadCredentials(SshCommandProfile profile, string settingsPath)
    {
        profile = profile.Validate();
        try
        {
            // Unlike the GUI's settings loader this is strictly read-only: no
            // migration, corrupt-file renaming, default creation, or fallback.
            using var file = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length > 2 * 1024 * 1024) throw new InvalidDataException("RemoteDesk 配置过大，未读取凭据。");
            using JsonDocument document = JsonDocument.Parse(file, new JsonDocumentOptions { MaxDepth = 32 });
            JsonElement relay = Required(document.RootElement, "Relay", JsonValueKind.Object);
            string pin = Text(relay, "TlsCertificateSha256");
            string server = Text(relay, "ServerAddress");
            if (!relay.TryGetProperty("RelayPort", out var portValue) || portValue.ValueKind != JsonValueKind.Number || !portValue.TryGetInt32(out int port))
                throw new InvalidDataException("请先在 RemoteDesk 中登录中继服务器。");
            var options = new RelayConnectionOptions(server, port, Unprotect(Text(relay, "ProtectedAccessToken")),
                pin, profile.DeviceId).Validate();
            if (SshCommandProfiles.ComputeRelayScope(options) != profile.RelayScope ||
                !options.TlsCertificateSha256.Equals(profile.RelayPin, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("中继登录身份已变化，此 SSH 别名已停用；请重新配置，未使用旧凭据。");
            string? protectedDeviceKey = null;
            if (relay.TryGetProperty("DeviceKeys", out var keys) && keys.ValueKind == JsonValueKind.Array)
            {
                if (keys.GetArrayLength() > 50) throw new InvalidDataException("设备密钥记录超过上限，请在 RemoteDesk 中修复配置。");
                foreach (JsonElement key in keys.EnumerateArray())
                {
                    if (key.ValueKind != JsonValueKind.Object) continue;
                    if (OptionalText(key, "Scope").Equals(profile.RelayScope, StringComparison.OrdinalIgnoreCase) &&
                        Guid.TryParse(OptionalText(key, "DeviceId"), out Guid keyId) && keyId.ToString("D") == profile.DeviceId)
                    { protectedDeviceKey = OptionalText(key, "ProtectedPassword"); break; }
                }
            }
            if (string.IsNullOrEmpty(protectedDeviceKey) && Guid.TryParse(OptionalText(relay, "DeviceId"), out Guid localId) &&
                localId.ToString("D") == profile.DeviceId && document.RootElement.TryGetProperty("Host", out var host) &&
                host.ValueKind == JsonValueKind.Object)
                protectedDeviceKey = OptionalText(host, "ProtectedPassword");
            if (string.IsNullOrEmpty(protectedDeviceKey))
                throw new InvalidDataException("未保存此设备的密钥，请在 RemoteDesk 中重新配置该 SSH 别名。");
            return new Credentials(options, Unprotect(protectedDeviceKey));
        }
        catch (JsonException) { throw new InvalidDataException("RemoteDesk 配置无法读取，原文件未修改；请在应用中检查配置。"); }
    }

    private static string Unprotect(string secret)
    {
        if (secret.Length is < 1 or > 65536) throw new InvalidDataException("未保存有效登录凭据，请在 RemoteDesk 中重新配置。");
        try
        {
            byte[] bytes = ProtectedData.Unprotect(Convert.FromBase64String(secret),
                Encoding.UTF8.GetBytes("RemoteDesk.HostPassword.v1"), DataProtectionScope.CurrentUser);
            try
            {
                string value = Encoding.UTF8.GetString(bytes);
                if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("已保存凭据为空，请在 RemoteDesk 中重新配置。");
                return value;
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        { throw new InvalidDataException("无法解密当前 Windows 用户的 RemoteDesk 凭据，请使用配置别名时的用户登录。"); }
    }

    private static JsonElement Required(JsonElement value, string name, JsonValueKind kind)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var child) || child.ValueKind != kind)
            throw new InvalidDataException("缺少有效 RemoteDesk 中继登录，请在应用中重新登录。");
        return child;
    }
    private static string Text(JsonElement value, string name) => Required(value, name, JsonValueKind.String).GetString()!;
    private static string OptionalText(JsonElement value, string name) => value.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.String ? child.GetString()! : "";

    internal static async Task BridgeAsync(Stream input, Stream output, Stream remote, IDisposable owner, CancellationToken token)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var registration = stop.Token.Register(owner.Dispose);
        // Windows redirected stdin may remain blocked after network EOF. Put
        // that read on the pool and do not wait for it after the peer closes;
        // exiting this console process releases its handles (never the parent).
        Task upload = Task.Run(async () => await input.CopyToAsync(remote, 32768, stop.Token).ConfigureAwait(false), CancellationToken.None);
        Task download = Task.Run(async () => { await remote.CopyToAsync(output, 32768, stop.Token).ConfigureAwait(false); await output.FlushAsync(stop.Token).ConfigureAwait(false); }, CancellationToken.None);
        try { await await Task.WhenAny(upload, download).ConfigureAwait(false); }
        finally
        {
            stop.Cancel();
            _ = upload.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            _ = download.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
