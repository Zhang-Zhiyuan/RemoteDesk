package com.remotedesk.agent;

// Per viewer input authority; no process-wide held modifier state.
final class AndroidClipboardShortcutState {
    private final java.util.HashSet<Integer> held = new java.util.HashSet<>();

    synchronized int key(int virtualKey, boolean down) {
        return key(virtualKey, 0, 0, down);
    }

    synchronized int key(int virtualKey, int scanCode, int flags, boolean down) {
        if (isModifier(virtualKey)) {
            int identity = modifierIdentity(virtualKey, scanCode, flags);
            if (down) held.add(identity); else release(identity);
            return 0;
        }
        boolean control = held.contains(0x11) || held.contains(0xa2) || held.contains(0xa3);
        boolean alt = held.contains(0x12) || held.contains(0xa4) || held.contains(0xa5);
        boolean shift = held.contains(0x10) || held.contains(0xa0) || held.contains(0xa1);
        boolean meta = held.contains(0x5b) || held.contains(0x5c);
        if (!down || !control || alt || shift || meta) return 0;
        return virtualKey == 0x41 || virtualKey == 0x43 || virtualKey == 0x58 || virtualKey == 0x56 ? virtualKey : 0;
    }

    // Only identify held modifiers, never inject physical keys or derive text/case.
    // Old virtual-key-only shortcuts still use their original generic identity.
    static int modifierIdentity(int virtualKey, int scanCode, int flags) {
        if ((flags & 1) == 0 || (flags & ~3) != 0 || scanCode < 1 || scanCode > 0xff) return virtualKey;
        boolean extended = (flags & 2) != 0;
        int family = modifierFamily(virtualKey);
        if (family == 0x10) {
            if (scanCode == 0x2a) return 0xa0;
            if (scanCode == 0x36) return 0xa1; // Right Shift is not an E0 key, even from older senders.
        }
        if (family == 0x11 && scanCode == 0x1d) return extended ? 0xa3 : 0xa2;
        if (family == 0x12 && scanCode == 0x38) return extended ? 0xa5 : 0xa4;
        return virtualKey;
    }

    private static int modifierFamily(int key) {
        return key >= 0xa0 && key <= 0xa5 ? 0x10 + (key - 0xa0) / 2 : key;
    }

    private void release(int identity) {
        // An exact physical/generic owner may coexist with a separate owner of
        // the same family. Never remove the latter just because one key rose.
        if (held.remove(identity)) return;
        int family = modifierFamily(identity);
        if (identity != family) {
            // A legacy generic down followed by a physical/sided up must not
            // leave an unscoped alias latched. Known opposite sides stay held.
            held.remove(family);
        } else if (family >= 0x10 && family <= 0x12) {
            // No side was supplied and no matching generic press was tracked.
            // Release this ambiguous family rather than leave modifiers stuck;
            // a legacy sender cannot preserve independent sides in this case.
            held.removeIf(key -> modifierFamily(key) == family);
        }
    }

    static boolean isModifier(int key) {
        return key == 0x10 || key == 0x11 || key == 0x12 || key == 0x5b || key == 0x5c || (key >= 0xa0 && key <= 0xa5);
    }

    synchronized boolean permitsBareKey(int virtualKey) { return !isModifier(virtualKey) && held.isEmpty(); }

    synchronized void reset() { held.clear(); }
}
