package com.remotedesk.agent;

import java.nio.ByteBuffer;
import java.nio.CharBuffer;
import java.nio.charset.CharacterCodingException;
import java.nio.charset.CodingErrorAction;
import java.nio.charset.StandardCharsets;
import java.io.EOFException;
import java.net.ConnectException;
import java.net.NoRouteToHostException;
import java.net.SocketTimeoutException;
import java.net.UnknownHostException;
import java.security.GeneralSecurityException;
import java.util.Base64;

final class AndroidViewerStatusText {
    static final String CAPTURE_TARGET_TRAILER_PREFIX =
        "RemoteDesk.CaptureTargetStatus/v1|";

    private AndroidViewerStatusText() {
    }

    static String forDisplay(String message) {
        if (message == null || message.isEmpty()) {
            return message;
        }

        int lastLineStart = message.lastIndexOf('\n') + 1;
        if (lastLineStart <= 1 ||
            !message.startsWith(CAPTURE_TARGET_TRAILER_PREFIX, lastLineStart) ||
            message.indexOf(CAPTURE_TARGET_TRAILER_PREFIX) != lastLineStart ||
            !isValidCaptureTargetTrailer(message.substring(lastLineStart))) {
            return message;
        }

        return message.substring(0, lastLineStart - 1);
    }

    static String connectionFailure(Throwable failure) {
        for (Throwable current = failure; current != null; current = current.getCause()) {
            if (current instanceof AndroidRelay.IdentityFailure) {
                // This type contains only fixed local messages, never server-supplied text or credentials.
                return current.getMessage();
            }
            if (current instanceof UnknownHostException) {
                return "无法解析远端地址，请检查主机名";
            }
            if (current instanceof NoRouteToHostException) {
                return "无法到达远端，请检查网络连接";
            }
            if (current instanceof SocketTimeoutException) {
                return "连接超时，请检查网络与防火墙";
            }
            if (current instanceof ConnectException) {
                return "无法连接到远端，请检查地址、端口与服务状态";
            }
            if (current instanceof SecurityException ||
                current instanceof GeneralSecurityException) {
                return "安全连接失败，请检查口令与双方版本";
            }
            if (current instanceof EOFException) {
                return "远端提前关闭了连接";
            }
        }

        if (failure == null) {
            return "未知错误";
        }
        String message = failure.getMessage();
        return message == null || message.trim().isEmpty()
            ? failure.getClass().getSimpleName()
            : message.trim();
    }

    private static boolean isValidCaptureTargetTrailer(String trailer) {
        String body = trailer.substring(CAPTURE_TARGET_TRAILER_PREFIX.length());
        String[] fields = body.split("\\|", -1);
        if (fields.length != 4 ||
            (!("available".equals(fields[0])) &&
                !("unavailable".equals(fields[0]))) ||
            !isCanonicalTextField(fields[1]) ||
            !isCanonicalTextField(fields[2])) {
            return false;
        }
        try {
            return !fields[3].isEmpty() &&
                fields[3].chars().allMatch(character -> character >= '0' && character <= '9') &&
                Long.parseLong(fields[3]) <= Integer.MAX_VALUE;
        } catch (NumberFormatException ignored) {
            return false;
        }
    }

    private static boolean isCanonicalTextField(String encoded) {
        if (encoded.isEmpty() || encoded.length() > 2_048) {
            return false;
        }
        try {
            byte[] bytes = Base64.getDecoder().decode(encoded);
            CharBuffer decoded = StandardCharsets.UTF_8.newDecoder()
                .onMalformedInput(CodingErrorAction.REPORT)
                .onUnmappableCharacter(CodingErrorAction.REPORT)
                .decode(ByteBuffer.wrap(bytes));
            String text = decoded.toString();
            return !text.trim().isEmpty() &&
                text.length() <= 512 &&
                text.chars().noneMatch(Character::isISOControl) &&
                encoded.equals(Base64.getEncoder().encodeToString(bytes));
        } catch (IllegalArgumentException | CharacterCodingException ignored) {
            return false;
        }
    }
}
