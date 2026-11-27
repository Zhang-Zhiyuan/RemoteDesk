using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RemoteDesk;

internal static class SshLanRoute
{
    internal sealed class CommitFailedException(Exception cause) : IOException("SSH 直达确认中断。", cause);
    internal const int OfferSize = 70;
    internal const int MaximumPreface = 16 * 1024;
    internal static bool Enabled => Environment.GetEnvironmentVariable("REMOTEDESK_SSH_RELAY_ONLY") != "1";

    internal static bool IsPrivateAddress(ReadOnlySpan<byte> ip) => ip.Length == 4 &&
        (ip[0] == 10 || ip[0] == 172 && ip[1] is >= 16 and <= 31 || ip[0] == 192 && ip[1] == 168);

    internal static async Task<RelayTls.RelayTlsConnection?> TryOpenAsync(byte[] offer, byte[] key, TimeSpan relayElapsed, CancellationToken token)
    {
        if (offer.Length != OfferSize || !IsPrivateAddress(offer.AsSpan(0, 4)) ||
            !CryptographicOperations.FixedTimeEquals(Proof("offer"u8, offer, key), offer.AsSpan(38))) return null;
        int port = BinaryPrimitives.ReadUInt16BigEndian(offer.AsSpan(4, 2));
        if (port < 1024) return null;
        var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        SslStream? tls = null;
        bool transferred = false;
        try
        {
            // Only the rendezvous is speculative. Once the ticket is sent, the host can commit
            // to this socket. Never send a fallback selection into an already committed session.
            using (var probe = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                probe.CancelAfter(TimeSpan.FromMilliseconds(Math.Clamp(.75 * relayElapsed.TotalMilliseconds, 25, 250)));
                try
                {
                    await client.ConnectAsync(new IPAddress(offer.AsSpan(0, 4)), port, probe.Token).ConfigureAwait(false);
                    tls = new SslStream(client.GetStream(), false, (_, certificate, _, _) => certificate is not null &&
                        CryptographicOperations.FixedTimeEquals(certificate.GetCertHash(HashAlgorithmName.SHA256), offer.AsSpan(6, 32)));
                    await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = "remotedesk-ssh",
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                    }, probe.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
                catch (Exception error) when (error is IOException or SocketException or AuthenticationException) { return null; }
            }
            byte[] ticket = Proof("client"u8, offer, key);
            try
            {
                using var commit = CancellationTokenSource.CreateLinkedTokenSource(token);
                commit.CancelAfter(TimeSpan.FromSeconds(2));
                await tls.WriteAsync(ticket, commit.Token).ConfigureAwait(false);
                byte[] ready = new byte[1];
                await tls.ReadExactlyAsync(ready, commit.Token).ConfigureAwait(false);
                if (ready[0] != 1) throw new IOException("SSH 直达通道未就绪。");
            }
            catch (Exception error) when (!token.IsCancellationRequested &&
                error is IOException or SocketException or OperationCanceledException)
            { throw new CommitFailedException(error); }
            finally { CryptographicOperations.ZeroMemory(ticket); }
            var result = new RelayTls.RelayTlsConnection(client, tls);
            transferred = true;
            return result;
        }
        finally
        {
            if (!transferred) { tls?.Dispose(); client.Dispose(); }
        }
    }

    private static byte[] Proof(ReadOnlySpan<byte> role, byte[] offer, byte[] key)
    {
        Span<byte> context = stackalloc byte[role.Length + 38];
        role.CopyTo(context);
        offer.AsSpan(0, 38).CopyTo(context[role.Length..]);
        return HMACSHA256.HashData(key, context);
    }
}
