using System.Security.Cryptography;
using System.Text;

namespace RemoteDesk;

// Independent, mutually authenticated device gate. It never activates a desktop
// session. After this gate the original SSH protocol provides its own host-key
// validation, user authentication and encryption, including through the relay.
internal static class SshGateProtocol
{
    private static readonly byte[] Magic = "RDS1"u8.ToArray();
    internal const int ChallengeLength = 104;

    internal static byte[] ComputeProof(string deviceKey, string role, string deviceId, byte[] nonce)
    {
        if (role is not ("server" or "client" or "client-lan" or "lan-session") || nonce.Length != 32)
            throw new ArgumentException("Invalid SSH gate proof context.");
        string id = Guid.ParseExact(deviceId, "D").ToString("D");
        byte[] key = SHA256.HashData(Encoding.UTF8.GetBytes(deviceKey));
        byte[] context = Encoding.UTF8.GetBytes("RemoteDesk SSH gate v1\0" + role + "\0" + id);
        byte[] message = new byte[context.Length + nonce.Length];
        context.CopyTo(message, 0);
        nonce.CopyTo(message, context.Length);
        try { return HMACSHA256.HashData(key, message); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    internal static async Task<byte[]> SendServerChallengeAsync(Stream stream, string deviceKey,
        string deviceId, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(deviceKey)) throw new InvalidOperationException("请先设置设备密钥。");
        string id = Guid.ParseExact(deviceId, "D").ToString("D");
        byte[] nonce = RandomNumberGenerator.GetBytes(32);
        byte[] challenge = new byte[ChallengeLength];
        Magic.CopyTo(challenge, 0);
        nonce.CopyTo(challenge, 4);
        Encoding.ASCII.GetBytes(id).CopyTo(challenge, 36);
        ComputeProof(deviceKey, "server", id, nonce).CopyTo(challenge, 72);
        await stream.WriteAsync(challenge, token).ConfigureAwait(false);
        return nonce;
    }

    internal static async Task<bool> AuthenticateServerAsync(Stream stream, string deviceKey,
        string deviceId, CancellationToken token, byte[]? sentNonce = null)
    {
        byte[] nonce = sentNonce ?? await SendServerChallengeAsync(stream, deviceKey, deviceId, token).ConfigureAwait(false);
        byte[] reply = new byte[32];
        await stream.ReadExactlyAsync(reply, token).ConfigureAwait(false);
        bool valid = CryptographicOperations.FixedTimeEquals(reply, ComputeProof(deviceKey, "client", deviceId, nonce));
        if (!valid) await stream.WriteAsync(new byte[] { 0 }, token).ConfigureAwait(false);
        return valid;
    }

    internal static async Task AuthenticateClientAsync(Stream stream, string deviceKey,
        string deviceId, CancellationToken token)
        => _ = await OpenClientTransportAsync(stream, deviceKey, deviceId, false, token).ConfigureAwait(false);

    internal static async Task<RelayTls.RelayTlsConnection?> OpenClientTransportAsync(Stream stream, string deviceKey,
        string deviceId, bool allowLan, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(deviceKey)) throw new InvalidOperationException("请输入目标设备密钥，不是 SSH 登录密码。");
        string id = Guid.ParseExact(deviceId, "D").ToString("D");
        byte[] challenge = new byte[ChallengeLength];
        await stream.ReadExactlyAsync(challenge, token).ConfigureAwait(false);
        if (!challenge.AsSpan(0, 4).SequenceEqual(Magic) ||
            !challenge.AsSpan(36, 36).SequenceEqual(Encoding.ASCII.GetBytes(id)))
            throw new InvalidDataException("SSH 通道的设备身份不匹配，已停止连接。");
        byte[] nonce = challenge.AsSpan(4, 32).ToArray();
        if (!CryptographicOperations.FixedTimeEquals(challenge.AsSpan(72, 32), ComputeProof(deviceKey, "server", id, nonce)))
            throw new UnauthorizedAccessException("目标设备密钥错误或设备身份校验失败，尚未发送 SSH 登录凭据。");
        bool tryLan = allowLan && SshLanRoute.Enabled && nonce.AsSpan().StartsWith("RDLAN1\0\0"u8);
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        await stream.WriteAsync(ComputeProof(deviceKey, tryLan ? "client-lan" : "client", id, nonce), token).ConfigureAwait(false);
        byte[] status = new byte[1];
        await stream.ReadExactlyAsync(status, token).ConfigureAwait(false);
        if (status[0] == 4 && tryLan)
        {
            byte[] offer = new byte[SshLanRoute.OfferSize];
            await stream.ReadExactlyAsync(offer, token).ConfigureAwait(false);
            byte[] sessionKey = ComputeProof(deviceKey, "lan-session", id, nonce);
            try
            {
                var direct = await SshLanRoute.TryOpenAsync(offer, sessionKey,
                    System.Diagnostics.Stopwatch.GetElapsedTime(started), token).ConfigureAwait(false);
                if (direct is null) await stream.WriteAsync(new byte[] { 0 }, token).ConfigureAwait(false);
                return direct;
            }
            finally { CryptographicOperations.ZeroMemory(sessionKey); }
        }
        if (status[0] == 0) throw new UnauthorizedAccessException("目标拒绝了设备密钥。");
        if (status[0] == 2) throw new IOException("目标的 SSH 服务不可达，请检查目标机器的 SSH 服务和本机 SSH 端口。");
        if (status[0] == 3) throw new IOException("所选 WSL 的 SSH 服务不可达。请确认该发行版已运行，并在其中启动 OpenSSH Server。");
        if (status[0] is >= 20 and <= 32) throw new IOException(NativeServiceFailure(status[0]));
        if (status[0] != 1) throw new InvalidDataException("目标返回了未知 SSH 通道状态。");
        return null;
    }

    // Also compiled into the standalone OpenSSH proxy, which has no host-service dependency.
    internal static string NativeServiceFailure(byte reason) => reason switch
    {
        20 => "目标 Windows 尚未安装 OpenSSH Server。",
        21 => "Windows 的 sshd 服务已禁用。请在目标电脑的“公网中继 → 本机 SSH”点“启用 Windows SSH（手动启动）”。",
        22 => "目标 Windows 的 sshd 服务已停止。请在“公网中继 → 本机 SSH”点“检查并准备 Windows SSH”。",
        23 => "目标 Windows 无法确认 OpenSSH 组件或 sshd 服务的监听状态。",
        24 => "目标 Windows 的 sshd 没有可连接的监听端口。",
        25 => "目标 Windows 正在准备 OpenSSH Server，稍后重试。",
        26 => "Windows 未能安装 OpenSSH Server。可挂载与系统版本匹配的「语言和可选功能」介质，再点「检查并准备 Windows SSH」。",
        27 => "目标 Windows 安装 OpenSSH 后需要重启。",
        28 => "Windows OpenSSH 准备失败，请查看被控端的 Windows 事件记录。",
        29 => "Windows SSH 的端口已有监听或端口映射。请在目标电脑为 Windows SSH 配置独立端口。",
        30 => "请在目标电脑检查现有 sshd 服务的路径、配置文件及主机密钥。",
        31 => "安装或启动 Windows OpenSSH 需要在目标电脑以管理员身份运行 RemoteDesk。",
        32 => "Windows 报告已安装 OpenSSH Server，但尚未注册 sshd 服务。请在 Windows 可选功能中检查安装状态。",
        _ => "目标的 SSH 服务不可达。"
    };
}
