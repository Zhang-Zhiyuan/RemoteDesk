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
        if (role is not ("server" or "client") || nonce.Length != 32)
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

    internal static async Task<bool> AuthenticateServerAsync(Stream stream, string deviceKey,
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
        byte[] reply = new byte[32];
        await stream.ReadExactlyAsync(reply, token).ConfigureAwait(false);
        bool valid = CryptographicOperations.FixedTimeEquals(reply, ComputeProof(deviceKey, "client", id, nonce));
        if (!valid) await stream.WriteAsync(new byte[] { 0 }, token).ConfigureAwait(false);
        return valid;
    }

    internal static async Task AuthenticateClientAsync(Stream stream, string deviceKey,
        string deviceId, CancellationToken token)
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
        await stream.WriteAsync(ComputeProof(deviceKey, "client", id, nonce), token).ConfigureAwait(false);
        byte[] status = new byte[1];
        await stream.ReadExactlyAsync(status, token).ConfigureAwait(false);
        if (status[0] == 0) throw new UnauthorizedAccessException("目标拒绝了设备密钥。");
        if (status[0] == 2) throw new IOException("目标的 SSH 服务不可达，请检查目标机器的 SSH 服务和本机 SSH 端口。");
        if (status[0] != 1) throw new InvalidDataException("目标返回了未知 SSH 通道状态。");
    }
}
