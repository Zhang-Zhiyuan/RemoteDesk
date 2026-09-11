using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace RemoteDesk;

internal static class RelayAdminLogin
{
    internal const string ResourceName = "RemoteDesk.Relay.read_remotedesk_relay_config.py";
    private const int MaximumResponseBytes = 65536;

    internal static string ReadCommand(bool root)
    {
        using Stream source = typeof(RelayAdminLogin).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("内置服务器登录组件缺失。");
        using var data = new MemoryStream();
        source.CopyTo(data);
        string encoded = Convert.ToBase64String(data.ToArray());
        string command = $"python3 -c \"exec(__import__('base64').b64decode('{encoded}'))\"";
        return root ? command : "sudo -k -S -p '' -- " + command;
    }

    internal static RelayProvisionResult ParseResponse(string text, string server, string identity)
    {
        if (text.Length > MaximumResponseBytes)
            throw new InvalidOperationException("服务器返回的配置过大，原配置未更改。");
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            JsonElement value = document.RootElement;
            if (value.ValueKind != JsonValueKind.Object)
                throw new JsonException();
            if (value.TryGetProperty("errorCode", out JsonElement error))
            {
                if (error.ValueKind != JsonValueKind.String)
                    throw new JsonException();
                throw new InvalidOperationException(error.GetString() switch
                {
                    "not_configured" => "服务器尚未安装中继，请使用“部署 / 更新服务器”。",
                    "permission_denied" => "此账号不能读取中继配置，请使用 root 或有 sudo 权限的管理员。",
                    _ => "服务器中继配置无效，请检查服务器安装状态。"
                });
            }
            if (!value.TryGetProperty("version", out JsonElement version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out int versionNumber) || versionNumber != 1 ||
                !value.TryGetProperty("port", out JsonElement port) || port.ValueKind != JsonValueKind.Number ||
                !port.TryGetInt32(out int portNumber) || portNumber is <= 0 or > 65535 ||
                !value.TryGetProperty("accessToken", out JsonElement accessToken) || accessToken.ValueKind != JsonValueKind.String ||
                !value.TryGetProperty("tlsCertificateSha256", out JsonElement tls) || tls.ValueKind != JsonValueKind.String)
                throw new JsonException();
            string access = accessToken.GetString() ?? "", fingerprint = tls.GetString() ?? "";
            if (access.Length is < 32 or > 4096 || fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit))
                throw new JsonException();
            var options = new RelayConnectionOptions(server, portNumber, access,
                fingerprint, Guid.NewGuid().ToString()).Validate();
            return new(server, options.Port, options.AccessToken, options.TlsCertificateSha256, identity, false);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or FormatException)
        {
            throw new InvalidOperationException("服务器中继配置无效，原配置未更改。");
        }
    }

    internal static async Task<RelayProvisionResult> LoginAsync(RelayProvisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ServerAddress) || request.SshPort is <= 0 or > 65535 ||
            string.IsNullOrWhiteSpace(request.AdminUsername) || string.IsNullOrEmpty(request.AdminPassword) ||
            request.AdminPassword.Length > 4096 || request.AdminPassword.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new InvalidOperationException("请输入有效的服务器地址、SSH 端口和 root / 管理员密码；不是设备密钥。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(40));
        CancellationToken token = deadline.Token;
        string? observed = null;
        bool mismatch = false;
        var connection = new ConnectionInfo(request.ServerAddress.Trim(), request.SshPort, request.AdminUsername.Trim(),
            new PasswordAuthenticationMethod(request.AdminUsername.Trim(), request.AdminPassword))
        { Timeout = TimeSpan.FromSeconds(12) };
        using var ssh = new SshClient(connection);
        ssh.HostKeyReceived += (_, args) =>
        {
            string actual = RelayProvisioner.NormalizeSshFingerprint(args.FingerPrintSHA256);
            string expected = RelayProvisioner.NormalizeSshFingerprint(request.ExpectedSshHostKeySha256);
            args.CanTrust = (expected.Length == 0 || expected == actual) && (observed is null || observed == actual);
            if (args.CanTrust) observed = actual;
            else mismatch = true;
        };
        try
        {
            await ssh.ConnectAsync(token).ConfigureAwait(false);
            bool root = request.AdminUsername.Trim() == "root";
            using SshCommand command = ssh.CreateCommand(ReadCommand(root));
            command.CommandTimeout = TimeSpan.FromSeconds(12);
            Task execute = command.ExecuteAsync(token);
            byte[] passwordInput = root ? [] : Encoding.UTF8.GetBytes(request.AdminPassword + "\n");
            try
            {
                using Stream input = command.CreateInputStream();
                await input.WriteAsync(passwordInput, token).ConfigureAwait(false);
                await input.FlushAsync(token).ConfigureAwait(false);
            }
            finally { CryptographicOperations.ZeroMemory(passwordInput); }
            Task<byte[]> output = ReadBoundedAsync(command.OutputStream, deadline);
            Task<byte[]> errors = ReadBoundedAsync(command.ExtendedOutputStream, deadline);
            await Task.WhenAll(execute, output, errors).ConfigureAwait(false);
            if (command.ExitStatus != 0)
                throw new InvalidOperationException("无法读取中继配置；请检查 root / sudo 权限及服务器 Python 3。");
            RelayProvisionResult result = ParseResponse(Encoding.UTF8.GetString(await output.ConfigureAwait(false)),
                request.ServerAddress.Trim(), observed ?? throw new InvalidOperationException("未取得服务器 SSH 身份。"));
            // Enrollment is committed by the caller only after a real pinned
            // relay directory request has accepted the internally obtained key.
            await RelayTunnelClient.ListDevicesAsync(new(result.ServerAddress, result.RelayPort,
                result.AccessToken, result.TlsCertificateSha256, Guid.NewGuid().ToString()), token).ConfigureAwait(false);
            return result;
        }
        catch (SshException) when (mismatch)
        {
            throw new InvalidOperationException("服务器 SSH 身份已变化，未发送 root 密码；请先核实服务器是否更换或重装。");
        }
        catch (SshAuthenticationException)
        {
            throw new InvalidOperationException("服务器 root / 管理员密码错误，或 SSH 禁止密码登录。");
        }
        catch (SshException)
        {
            throw new IOException("无法登录服务器 SSH；请检查地址、SSH 端口和网络。原配置未更改。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("服务器登录超时，原配置未更改。");
        }
        finally { ssh.Disconnect(); }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationTokenSource deadline)
    {
        CancellationToken token = deadline.Token;
        using var output = new MemoryStream();
        byte[] buffer = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + count > MaximumResponseBytes)
            {
                deadline.Cancel(); // Stop the command and the other reader, not just this stream.
                throw new IOException("服务器返回的配置过大，登录已停止。");
            }
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
}
