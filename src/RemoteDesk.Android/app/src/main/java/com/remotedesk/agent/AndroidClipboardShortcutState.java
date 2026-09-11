package com.remotedesk.agent;

// Per viewer input authority; no process-wide held modifier state.
final class AndroidClipboardShortcutState {
    private final java.util.HashSet<Integer> held = new java.util.HashSet<>();

    synchronized int key(int virtualKey, boolean down) {
        if (isModifier(virtualKey)) {
            if (down) held.add(virtualKey); else held.remove(virtualKey);
            return 0;
        }
        boolean control = held.contains(0x11) || held.contains(0xa2) || held.contains(0xa3);
        boolean alt = held.contains(0x12) || held.contains(0xa4) || held.contains(0xa5);
        if (!down || !control || alt) return 0;
        return virtualKey == 0x41 || virtualKey == 0x43 || virtualKey == 0x58 || virtualKey == 0x56 ? virtualKey : 0;
    }

    static boolean isModifier(int key) {
        return key == 0x10 || key == 0x11 || key == 0x12 || (key >= 0xa0 && key <= 0xa5);
    }

    synchronized void reset() { held.clear(); }
}
