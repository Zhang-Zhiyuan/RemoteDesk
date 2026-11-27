"""A short-lived LAN data socket for an already authenticated relay SSH session."""
import asyncio
import contextlib
import datetime
import hashlib
import hmac
import ipaddress
import os
import socket
import ssl
import struct
import tempfile


CAPABILITY = b"RDLAN1\0\0"
MAX_PREFIX = 16 * 1024
OFFER_SIZE = 4 + 2 + 32 + 32
PRIVATE = tuple(ipaddress.ip_network(prefix) for prefix in ("10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"))


class CommitFailed(ConnectionError):
    """Only after sending the one-use TLS ticket, before any SSH bytes are exposed."""


def eligible(address):
    try:
        ip = ipaddress.IPv4Address(address)
    except (ValueError, TypeError):
        return False
    return any(ip in network for network in PRIVATE)


def local_address(writer):
    endpoint = writer.get_extra_info("sockname")
    if os.environ.get("REMOTEDESK_SSH_RELAY_ONLY") != "1" and endpoint and eligible(endpoint[0]):
        return endpoint[0]
    return None


def _server_identity():
    from cryptography import x509
    from cryptography.hazmat.backends import default_backend
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import ec
    from cryptography.x509.oid import NameOID

    backend = default_backend()
    key = ec.generate_private_key(ec.SECP256R1(), backend)
    name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "RemoteDesk SSH session")])
    now = datetime.datetime.now(datetime.timezone.utc)
    cert = (x509.CertificateBuilder().subject_name(name).issuer_name(name).public_key(key.public_key())
            .serial_number(x509.random_serial_number()).not_valid_before(now - datetime.timedelta(minutes=5))
            .not_valid_after(now + datetime.timedelta(hours=1)).sign(key, hashes.SHA256(), backend))
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.minimum_version = ssl.TLSVersion.TLSv1_2
    # SSLContext loads a PEM path. Only this user can access the temporary
    # directory; both key and certificate are removed before anything listens.
    with tempfile.TemporaryDirectory() as directory:
        pem = os.path.join(directory, "identity.pem")
        with open(pem, "xb") as output:
            output.write(cert.public_bytes(serialization.Encoding.PEM))
            output.write(key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8,
                                           serialization.NoEncryption()))
        context.load_cert_chain(pem)
    return context, cert.fingerprint(hashes.SHA256())


class SshLanOffer:
    def __init__(self, key):
        self.key = key
        self.listener = None
        self.context = None
        self.ticket = None

    async def listen(self, address):
        if not eligible(address):
            raise OSError("No LAN interface")
        self.context, pin = await asyncio.get_running_loop().run_in_executor(None, _server_identity)
        self.listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self.listener.setblocking(False)
        self.listener.bind((address, 0))
        self.listener.listen(4)
        binding = ipaddress.IPv4Address(address).packed + struct.pack("!H", self.listener.getsockname()[1]) + pin
        self.ticket = hmac.digest(self.key, b"client" + binding, "sha256")
        return binding + hmac.digest(self.key, b"offer" + binding, "sha256")

    async def _accept_authenticated(self):
        loop = asyncio.get_running_loop()
        while True:
            # At most one pending TLS handshake per authenticated relay session.
            client, _ = await loop.sock_accept(self.listener)
            transport = None
            transferred = False
            try:
                client.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
                reader = asyncio.StreamReader()
                protocol = asyncio.StreamReaderProtocol(reader)
                transport, _ = await loop.connect_accepted_socket(lambda: protocol, client,
                                                                  ssl=self.context, ssl_handshake_timeout=1)
                writer = asyncio.StreamWriter(transport, protocol, reader, loop)
                reply = await asyncio.wait_for(reader.readexactly(32), 1)
                if hmac.compare_digest(reply, self.ticket):
                    transferred = True
                    return reader, writer
            except (OSError, EOFError, asyncio.TimeoutError):
                pass
            finally:
                if transport is None:
                    client.close()
                elif not transferred:
                    transport.abort()

    async def select(self, relay_reader):
        fallback = asyncio.create_task(relay_reader.readexactly(1))
        accepted = asyncio.create_task(self._accept_authenticated())
        transferred = False
        try:
            try:
                done, _ = await asyncio.wait((fallback, accepted), return_when=asyncio.FIRST_COMPLETED)
            finally:
                fallback.cancel()
                accepted.cancel()
                await asyncio.gather(fallback, accepted, return_exceptions=True)
            if fallback in done:
                if fallback.result() != b"\0":
                    raise ValueError("SSH 路径选择无效。")
                return None
            winner = accepted.result()
            transferred = True
            return winner
        finally:
            if not transferred and accepted.done() and not accepted.cancelled() and not accepted.exception():
                accepted.result()[1].transport.abort()

    def close(self):
        if self.listener is not None:
            self.listener.close()


async def choose_host_route(address, key, relay_reader, relay_writer, ssh_reader):
    offer = SshLanOffer(key)
    direct = None
    try:
        try:
            endpoint = await offer.listen(address)
        except (OSError, ImportError, ValueError):
            relay_writer.write(b"\1")
            await relay_writer.drain()
            return None
        relay_writer.write(b"\4" + endpoint)
        await relay_writer.drain()
        # Send sshd's initial bytes over the relay while the viewer considers the offer.
        # Replay exactly those bytes if it selects the private TLS connection instead.
        async def send_prefix():
            prefix = await ssh_reader.read(MAX_PREFIX)
            relay_writer.write(prefix)
            return prefix
        preface = asyncio.create_task(send_prefix())
        try:
            direct = await offer.select(relay_reader)
        finally:
            if not preface.done():
                preface.cancel()
            await asyncio.gather(preface, return_exceptions=True)
        prefix = b"" if preface.cancelled() else preface.result()
        if direct is not None:
            direct[1].write(b"\1" + prefix)
            await direct[1].drain()
        result, direct = direct, None
        return result
    finally:
        offer.close()
        if direct is not None:
            direct[1].transport.abort()


async def try_candidate(offer, key, relay_elapsed):
    binding = offer[:38]
    if len(offer) != OFFER_SIZE or not hmac.compare_digest(offer[38:], hmac.digest(key, b"offer" + binding, "sha256")):
        return None
    address, port = str(ipaddress.IPv4Address(binding[:4])), int.from_bytes(binding[4:6], "big")
    if not eligible(address) or port < 1024 or os.environ.get("REMOTEDESK_SSH_RELAY_ONLY") == "1":
        return None
    writer = None
    async def probe():
        nonlocal writer
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        context.minimum_version = ssl.TLSVersion.TLSv1_2
        context.check_hostname = False
        context.verify_mode = ssl.CERT_NONE
        reader, writer = await asyncio.open_connection(address, port, ssl=context, server_hostname="remotedesk-ssh")
        cert = writer.get_extra_info("ssl_object").getpeercert(binary_form=True)
        if not hmac.compare_digest(hashlib.sha256(cert).digest(), binding[6:]):
            raise ValueError("SSH 直达身份不匹配。")
        return reader, hmac.digest(key, b"client" + binding, "sha256")
    try:
        try:
            reader, proof = await asyncio.wait_for(probe(), max(.025, min(.25, .75 * relay_elapsed)))
        except (OSError, EOFError, ValueError, asyncio.TimeoutError):
            return None
        async def commit():
            writer.write(proof)
            await writer.drain()
            if await reader.readexactly(1) != b"\1":
                raise ConnectionError("SSH 直达通道未就绪。")
        try:
            await asyncio.wait_for(commit(), 2)
        except (OSError, EOFError, asyncio.TimeoutError) as error:
            raise CommitFailed("SSH 直达确认中断。") from error
        direct = reader, writer
        writer = None
        return direct
    finally:
        if writer is not None:
            writer.transport.abort()


async def keep_relay_lease(relay_reader, relay_writer, data_writer, *, allow_prefix=False):
    # Either side closing (including a disabled service or cancelled terminal)
    # must release the other socket and the existing relay SSH quota.
    # Once payload takes the LAN route, the control socket can be silent for hours.
    # Keep its NAT mapping alive and detect a lost peer without injecting SSH bytes.
    for writer in (relay_writer, data_writer):
        sock = writer.get_extra_info("socket")
        if sock is not None:
            for level, option, value in ((socket.SOL_SOCKET, "SO_KEEPALIVE", 1), (socket.IPPROTO_TCP, "TCP_KEEPIDLE", 20),
                                         (socket.IPPROTO_TCP, "TCP_KEEPINTVL", 5), (socket.IPPROTO_TCP, "TCP_KEEPCNT", 4)):
                with contextlib.suppress(OSError, AttributeError):
                    sock.setsockopt(level, getattr(socket, option), value)

    async def watch_control():
        remaining = MAX_PREFIX if allow_prefix else 0
        while remaining >= 0:
            block = await relay_reader.read(remaining + 1)
            if not block:
                break
            remaining -= len(block)
    control = asyncio.create_task(watch_control())
    data = asyncio.create_task(data_writer.wait_closed())
    try:
        await asyncio.wait((control, data), return_when=asyncio.FIRST_COMPLETED)
    finally:
        relay_writer.transport.abort()
        data_writer.transport.abort()
        control.cancel()
        # wait_closed is also awaited by the normal bridge owner.
        with contextlib.suppress(OSError, ConnectionError):
            await asyncio.gather(control, data, return_exceptions=True)
