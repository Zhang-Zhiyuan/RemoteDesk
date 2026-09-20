package com.remotedesk.agent;

import java.util.ArrayList;
import java.util.List;

/** A committed local IME composition or shortcut is queued atomically. */
final class AndroidViewerKeyboard {
    static final int MAX_TEXT_LENGTH = 128;

    static List<AndroidViewerInputQueue.Command> text(String text, long route, long generation) {
        return text(text, route, generation, "");
    }

    static List<AndroidViewerInputQueue.Command> text(String text, long route, long generation, String remotePlatform) {
        if (text == null || text.isEmpty() || text.length() > MAX_TEXT_LENGTH) return new ArrayList<>();
        boolean androidText = "Android".equalsIgnoreCase(remotePlatform);
        List<AndroidViewerInputQueue.Command> result = new ArrayList<>();
        for (int index = 0; index < text.length();) {
            int cp = text.codePointAt(index);
            index += Character.charCount(cp);
            if (cp == '\r' && index < text.length() && text.charAt(index) == '\n') continue;
            if (cp == '\r' || cp == '\n' || cp == '\t') {
                if (androidText) {
                    // A literal tab in committed text must not become Android's
                    // toolbar Tab shortcut (Quick Settings). Old Android hosts
                    // already support newline/tab scalars through TextInput.
                    result.add(command(RemoteDeskProtocol.INPUT_TEXT, cp == '\t' ? '\t' : '\n', route, generation));
                } else {
                    int key = cp == '\t' ? 0x09 : 0x0D;
                    result.add(command(RemoteDeskProtocol.INPUT_KEY_DOWN, key, route, generation));
                    result.add(command(RemoteDeskProtocol.INPUT_KEY_UP, key, route, generation));
                }
            } else if (!Character.isISOControl(cp) && !(cp >= 0xD800 && cp <= 0xDFFF)) {
                result.add(command(RemoteDeskProtocol.INPUT_TEXT, cp, route, generation));
            }
        }
        return result;
    }

    static List<AndroidViewerInputQueue.Command> shortcut(long route, long generation, int... keys) {
        List<AndroidViewerInputQueue.Command> result = new ArrayList<>();
        if (keys == null || keys.length > 4) return result;
        for (int key : keys) result.add(command(RemoteDeskProtocol.INPUT_KEY_DOWN, key, route, generation));
        for (int i = keys.length - 1; i >= 0; i--) result.add(command(RemoteDeskProtocol.INPUT_KEY_UP, keys[i], route, generation));
        return result;
    }

    private static AndroidViewerInputQueue.Command command(int kind, int value, long route, long generation) {
        return new AndroidViewerInputQueue.Command(kind, RemoteDeskProtocol.MOUSE_NONE, 0, 0, value, route, generation);
    }
}
