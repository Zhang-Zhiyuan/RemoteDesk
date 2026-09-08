using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace RemoteDesk;

internal sealed class RelayProvisioner
{
    private const string InstallerResourceName =
        "RemoteDesk.Relay.install_remotedesk_relay.sh";
    private const string ServerResourceName =
        "RemoteDesk.Relay.remotedesk_relay_server.py";
    private const string ResultMarker = "REMOTEDESK_RELAY_RESULT=";
    // SSH.NET expects chmod-style octal digits, not their decimal bitmask values.
    internal const short TemporaryDirectoryMode = 700;
    internal const short TemporaryRequestMode = 600;

    public async Task<RelayProvisionResult> ProvisionAsync(
        RelayProvisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        using var operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operationTimeout.CancelAfter(TimeSpan.FromMinutes(10));
        CancellationToken operationToken = operationTimeout.Token;
        string proposedToken = Convert.ToHexString(
            RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        string suffix = Guid.NewGuid().ToString("N");
        string remoteDirectory = $"/tmp/remotedesk-relay-{suffix}";
        string remoteInstaller = $"{remoteDirectory}/install.sh";
        string remoteServer = $"{remoteDirectory}/server.py";
        string remoteInput = $"{remoteDirectory}/request";
        bool directoryCreated = false;
        string? capturedFingerprint = null;
        string? mismatchFingerprint = null;

        void ValidateHostKey(object? _, HostKeyEventArgs args)
        {
            string actual = NormalizeSshFingerprint(
                args.FingerPrintSHA256);
            string configuredExpected = NormalizeSshFingerprint(
                request.ExpectedSshHostKeySha256);
            string expected = configuredExpected.Length > 0
                ? configuredExpected
                : capturedFingerprint ?? string.Empty;
            bool matches = expected.Length == 0 ||
                string.Equals(
                    actual,
                    expected,
                    StringComparison.Ordinal);
            if (matches)
            {
                capturedFingerprint ??= actual;
            }

            if (!matches)
            {
                mismatchFingerprint = actual;
            }

            args.CanTrust = matches;
        }

        var connectionInfo = new ConnectionInfo(
            request.ServerAddress.Trim(),
            request.SshPort,
            request.AdminUsername.Trim(),
            new PasswordAuthenticationMethod(
                request.AdminUsername.Trim(),
                request.AdminPassword))
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        try
        {
            using (var sftp = new SftpClient(connectionInfo))
            {
                sftp.OperationTimeout = TimeSpan.FromSeconds(20);
                sftp.HostKeyReceived += ValidateHostKey;
                await ConnectAsync(
                        sftp,
                        () => mismatchFingerprint,
                        operationToken)
                    .ConfigureAwait(false);
                sftp.CreateDirectory(remoteDirectory);
                directoryCreated = true;
                // Make the directory owner-only before uploading any secrets.
                sftp.ChangePermissions(remoteDirectory, TemporaryDirectoryMode);
                await using Stream installer = OpenResource(
                    InstallerResourceName);
                await using Stream server = OpenResource(
                    ServerResourceName);
                await sftp.UploadFileAsync(
                        installer,
                        remoteInstaller,
                        operationToken)
                    .ConfigureAwait(false);
                await sftp.UploadFileAsync(
                        server,
                        remoteServer,
                        operationToken)
                    .ConfigureAwait(false);
                byte[] requestBytes = Encoding.UTF8.GetBytes($"{proposedToken}\n{request.RelayPort}\n");
                try
                {
                    using var input = new MemoryStream(requestBytes, writable: false);
                    await sftp.UploadFileAsync(input, remoteInput, operationToken).ConfigureAwait(false);
                    sftp.ChangePermissions(remoteInput, TemporaryRequestMode);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(requestBytes);
                }
                sftp.Disconnect();
            }

            using var ssh = new SshClient(connectionInfo);
            ssh.HostKeyReceived += ValidateHostKey;
            await ConnectAsync(
                    ssh,
                    () => mismatchFingerprint,
                    operationToken)
                .ConfigureAwait(false);
            bool isRoot = await IsRootAsync(ssh, operationToken)
                .ConfigureAwait(false);
            string commandText = BuildInstallCommand(remoteDirectory, isRoot);
            SshInputCommandResult installCommand =
                await ExecuteCommandWithInputAsync(
                    ssh,
                    commandText,
                    isRoot ? string.Empty : $"{request.AdminPassword}\n",
                    operationToken);
            if (installCommand.ExitStatus != 0)
            {
                string error = SanitizeRemoteError(
                    installCommand.Error.Replace(request.AdminPassword, "[已隐藏]", StringComparison.Ordinal));
                throw new InvalidOperationException(
                    error.Length == 0
                        ? $"中继安装失败（退出码 {installCommand.ExitStatus}）。"
                        : $"中继安装失败：{error}");
            }

            RelayInstallResponse install = ParseResult(
                installCommand.Output);
            ssh.Disconnect();
            return new RelayProvisionResult(
                request.ServerAddress.Trim(),
                install.Port,
                install.AccessToken,
                RelayTls.NormalizeFingerprint(
                    install.TlsCertificateSha256),
                capturedFingerprint ?? throw new InvalidOperationException(
                    "服务器未返回 SSH 主机指纹。"),
                install.Installed);
        }
        catch (SshAuthenticationException ex)
        {
            throw new InvalidOperationException(
                "SSH 管理员账号或密码错误。",
                ex);
        }
        catch (SshConnectionException ex)
            when (mismatchFingerprint is not null)
        {
            throw new InvalidOperationException(
                $"SSH 主机指纹已变化，已拒绝连接。当前指纹：{mismatchFingerprint}",
                ex);
        }
        catch (SshConnectionException ex)
        {
            throw new IOException(
                $"无法连接 SSH 服务：{ex.Message}",
                ex);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("中继配置超过 10 分钟，请检查服务器网络和软件包源后重试。");
        }
        catch (SshException ex)
        {
            throw new IOException($"SSH/SFTP 操作失败：{SanitizeRemoteError(ex.Message)}", ex);
        }
        finally
        {
            if (directoryCreated)
            {
                await TryDeleteTemporaryFilesAsync(
                    connectionInfo,
                    ValidateHostKey,
                    remoteDirectory,
                    remoteInstaller,
                    remoteServer,
                    remoteInput)
                .ConfigureAwait(false);
            }
        }
    }

    internal static string BuildInstallCommand(string remoteDirectory, bool isRoot)
    {
        const string prefix = "/tmp/remotedesk-relay-";
        if (!remoteDirectory.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(remoteDirectory[prefix.Length..], "N", out _))
        {
            throw new ArgumentException("安装临时目录无效。", nameof(remoteDirectory));
        }
        // sudo receives only its password. The elevated shell reads the installer
        // input separately, also when sudo is configured with NOPASSWD.
        string shell = $"sh -c 'exec sh {remoteDirectory}/install.sh {remoteDirectory}/server.py < {remoteDirectory}/request'";
        return isRoot ? shell : $"sudo -k -S -p '' -- {shell}";
    }

    private static async Task ConnectAsync(
        BaseClient client,
        Func<string?> mismatchFingerprint,
        CancellationToken cancellationToken)
    {
        try
        {
            await client.ConnectAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SshConnectionException ex)
            when (mismatchFingerprint() is { } mismatch)
        {
            throw new InvalidOperationException(
                $"SSH 主机指纹已变化，已拒绝连接。当前指纹：{mismatch}",
                ex);
        }
    }

    private static async Task<bool> IsRootAsync(
        SshClient ssh,
        CancellationToken cancellationToken)
    {
        using SshCommand command = ssh.CreateCommand("id -u");
        await command.ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);
        return command.ExitStatus == 0 &&
            string.Equals(
                command.Result.Trim(),
                "0",
                StringComparison.Ordinal);
    }

    private static async Task<SshInputCommandResult>
        ExecuteCommandWithInputAsync(
            SshClient ssh,
            string commandText,
            string inputText,
            CancellationToken cancellationToken)
    {
        using SshCommand command = ssh.CreateCommand(
            commandText);
        Task execute = command.ExecuteAsync(
            cancellationToken);
        byte[] inputBytes = Encoding.UTF8.GetBytes(
            inputText);
        try
        {
            using Stream input = command.CreateInputStream();
            await input.WriteAsync(
                    inputBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            await input.FlushAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(inputBytes);
            inputText = string.Empty;
        }

        await execute.ConfigureAwait(false);
        return new SshInputCommandResult(
            command.ExitStatus ?? -1,
            command.Result,
            command.Error);
    }

    private static Stream OpenResource(string name) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream(name) ??
        throw new InvalidOperationException(
            $"内置中继资源缺失：{name}");

    internal static RelayInstallResponse ParseResult(string output)
    {
        string? line = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(value =>
                value.StartsWith(ResultMarker, StringComparison.Ordinal));
        if (line is null)
        {
            throw new InvalidOperationException(
                "服务端安装完成后未返回有效配置，请检查 systemd 日志。");
        }

        try
        {
            RelayInstallResponse? result = JsonSerializer.Deserialize<
                RelayInstallResponse>(
                line[ResultMarker.Length..],
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            if (result is null ||
                result.Port is <= 0 or > 65535 ||
                string.IsNullOrEmpty(result.AccessToken) || result.AccessToken.Length < 32 ||
                RelayTls.NormalizeFingerprint(
                    result.TlsCertificateSha256).Length != 64)
            {
                throw new JsonException("返回字段无效");
            }

            return result;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "服务端返回的中继配置无效。",
                ex);
        }
    }

    private static async Task TryDeleteTemporaryFilesAsync(
        ConnectionInfo connectionInfo,
        EventHandler<HostKeyEventArgs> hostKeyValidator,
        string directory,
        params string[] paths)
    {
        try
        {
            using var sftp = new SftpClient(connectionInfo);
            sftp.OperationTimeout = TimeSpan.FromSeconds(5);
            sftp.HostKeyReceived += hostKeyValidator;
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(5));
            await sftp.ConnectAsync(timeout.Token).ConfigureAwait(false);
            foreach (string path in paths)
            {
                if (sftp.Exists(path))
                {
                    sftp.DeleteFile(path);
                }
            }
            if (sftp.Exists(directory))
            {
                sftp.DeleteDirectory(directory);
            }

            sftp.Disconnect();
        }
        catch
        {
        }
    }

    internal static string NormalizeSshFingerprint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string trimmed = value.Trim();
        // SSH.NET returns bare Base64, while OpenSSH includes "SHA256:".
        // Normalize both (including legacy saved pins) without changing the
        // case-sensitive key data or weakening host-key mismatch checks.
        string digest = trimmed.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase)
            ? trimmed[7..]
            : trimmed;
        return $"SHA256:{digest.TrimEnd('=')}";
    }

    private static string SanitizeRemoteError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return string.Empty;
        }

        string normalized = error.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 400
            ? normalized
            : normalized[..400];
    }

    private static void ValidateRequest(RelayProvisionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ServerAddress))
        {
            throw new InvalidOperationException("请输入公网 Linux 服务器地址。");
        }

        if (request.SshPort is <= 0 or > 65535)
        {
            throw new InvalidOperationException("SSH 端口无效。");
        }

        if (request.RelayPort is <= 0 or > 65535)
        {
            throw new InvalidOperationException("中继端口无效。");
        }

        if (string.IsNullOrWhiteSpace(request.AdminUsername))
        {
            throw new InvalidOperationException("请输入管理员账号。");
        }

        if (string.IsNullOrEmpty(request.AdminPassword))
        {
            throw new InvalidOperationException("请输入管理员密码。");
        }

        if (request.AdminPassword.IndexOfAny(['\r', '\n']) >= 0)
        {
            throw new InvalidOperationException(
                "管理员密码不能包含换行符。");
        }
    }

    internal sealed class RelayInstallResponse
    {
        public bool Installed { get; set; }

        public int Port { get; set; }

        public string AccessToken { get; set; } = string.Empty;

        public string TlsCertificateSha256 { get; set; } = string.Empty;
    }

    private sealed record SshInputCommandResult(
        int ExitStatus,
        string Output,
        string Error);
}
