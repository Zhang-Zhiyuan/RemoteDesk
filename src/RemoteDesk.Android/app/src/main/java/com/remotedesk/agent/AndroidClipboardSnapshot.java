package com.remotedesk.agent;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;

/** Correlated, read-only clipboard state. Unavailable is never an empty clipboard. */
final class AndroidClipboardSnapshot {
    static final int MAX_TEXT_CHARS = 256_000;
    static final String EMPTY_REVISION = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    static final String UNAVAILABLE = "Android 剪贴板暂不可读取，可能受系统后台读取限制；请让 RemoteDesk 位于手机前台后重试。不会清空另一端的剪贴板。";
    final String requestId;
    final boolean success;
    final String revision;
    final boolean hasText;
    final boolean changed;
    final String text;
    final String statusMessage;

    AndroidClipboardSnapshot(String requestId, boolean success, String revision,
        boolean hasText, boolean changed, String text, String statusMessage) throws IOException {
        validateRequest(requestId, revision);
        if (text == null || text.length() > MAX_TEXT_CHARS || statusMessage == null ||
            statusMessage.length() > 4096) throw new IOException("Invalid clipboard snapshot length.");
        if ((!success || !changed || !hasText) && !text.isEmpty())
            throw new IOException("Clipboard snapshot contains unexpected text.");
        if (success && (revision.isEmpty() || (changed && hasText && text.isEmpty())))
            throw new IOException("Incomplete clipboard snapshot.");
        if (!success && (hasText || changed))
            throw new IOException("Unavailable clipboard snapshot cannot announce a change.");
        if (success && ((!hasText && !revision.equals(EMPTY_REVISION)) ||
            (hasText && revision.equals(EMPTY_REVISION)) ||
            (changed && !revision.equals(revisionOf(text)))))
            throw new IOException("Clipboard snapshot revision does not match its text.");
        this.requestId = requestId;
        this.success = success;
        this.revision = revision;
        this.hasText = hasText;
        this.changed = changed;
        this.text = text;
        this.statusMessage = statusMessage;
    }

    static AndroidClipboardSnapshot request(String requestId, String knownRevision) throws IOException {
        return new AndroidClipboardSnapshot(requestId, false, knownRevision, false, false, "", "");
    }

    static AndroidClipboardSnapshot capture(String requestId, String knownRevision, String text) throws IOException {
        validateRequest(requestId, knownRevision);
        if (text == null || text.length() > MAX_TEXT_CHARS)
            throw new IOException("剪贴板文字超过 256000 字符或不可用，未截断或发送。");
        String revision = revisionOf(text);
        boolean changed = !revision.equals(knownRevision);
        return new AndroidClipboardSnapshot(requestId, true, revision, !text.isEmpty(), changed,
            changed ? text : "", "");
    }

    static AndroidClipboardSnapshot unavailable(String requestId) throws IOException {
        return new AndroidClipboardSnapshot(requestId, false, "", false, false, "", UNAVAILABLE);
    }

    static void validateRequest(String requestId, String revision) throws IOException {
        if (requestId == null || requestId.isEmpty() || requestId.length() > 64)
            throw new IOException("Invalid clipboard snapshot request ID.");
        if (revision == null || (!revision.isEmpty() && !revision.matches("[0-9a-f]{64}")))
            throw new IOException("Invalid clipboard snapshot revision.");
    }

    static String revisionOf(String text) throws IOException {
        requireValidUnicode(text);
        try {
            byte[] digest = MessageDigest.getInstance("SHA-256").digest(text.getBytes(StandardCharsets.UTF_8));
            char[] hex = new char[digest.length * 2];
            String digits = "0123456789abcdef";
            for (int i = 0; i < digest.length; i++) {
                hex[i * 2] = digits.charAt((digest[i] & 255) >>> 4);
                hex[i * 2 + 1] = digits.charAt(digest[i] & 15);
            }
            return new String(hex);
        } catch (NoSuchAlgorithmException error) {
            throw new IOException("SHA-256 is unavailable.", error);
        }
    }

    static void requireValidUnicode(String text) throws IOException {
        // Do not silently replace malformed UTF-16. The .NET snapshot codec also
        // uses strict UTF-8; valid Unicode (including emoji) hashes identically.
        for (int i = 0; i < text.length(); i++) {
            char c = text.charAt(i);
            if (Character.isHighSurrogate(c)) {
                if (++i >= text.length() || !Character.isLowSurrogate(text.charAt(i)))
                    throw new IOException("Clipboard text contains invalid Unicode.");
            } else if (Character.isLowSurrogate(c)) {
                throw new IOException("Clipboard text contains invalid Unicode.");
            }
        }
    }

    static boolean isNegotiated(int hostCapabilities, int viewerCapabilities) {
        int requiredHost = RemoteDeskProtocol.CAPABILITY_CLIPBOARD_TEXT |
            RemoteDeskProtocol.CAPABILITY_CLIPBOARD_SNAPSHOT_V1;
        return (hostCapabilities & requiredHost) == requiredHost &&
            (viewerCapabilities & RemoteDeskProtocol.CAPABILITY_CLIPBOARD_SNAPSHOT_V1) != 0;
    }
}
