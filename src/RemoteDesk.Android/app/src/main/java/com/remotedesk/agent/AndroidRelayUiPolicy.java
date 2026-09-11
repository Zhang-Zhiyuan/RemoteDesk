package com.remotedesk.agent;

/** Settings changes are local; disabling publication must not require a server login. */
final class AndroidRelayUiPolicy {
    static final class LoginRequired extends java.io.IOException {
        LoginRequired() { super("请先在设备页登录此记录所属的服务器；服务器已更换时，请从在线列表重新连接。"); }
    }
    interface Save { void write(AndroidRelay.Options options) throws Exception; }

    static AndroidRelay.Options historyTarget(AndroidConnectionHistory.Node node, AndroidRelay.Options saved) throws Exception {
        if (saved == null || !node.host.equals(AndroidConnectionHistory.normalizeHost(saved.serverAddress)) || node.port != saved.port)
            throw new LoginRequired();
        AndroidRelay.Options previous = AndroidRelay.Options.parse(node.relayConfiguration);
        if (!previous.deviceId.equals(node.relayDeviceId) || !previous.tlsCertificateSha256.equals(saved.tlsCertificateSha256))
            throw new LoginRequired();
        return saved.target(node.relayDeviceId); // Never reconnect with a logged-out historical access token.
    }

    static AndroidRelay.Options publish(AndroidRelay.Options previous, boolean enabled, Save save) throws Exception {
        AndroidRelay.Options next = new AndroidRelay.Options(previous.serverAddress, previous.port,
            previous.accessToken, previous.tlsCertificateSha256, previous.deviceId, enabled,
            previous.sshPort, previous.adminUsername, previous.sshHostKeySha256);
        save.write(next); // Return a new effective state only after persistence succeeds.
        return next;
    }

    private AndroidRelayUiPolicy() { }
}
