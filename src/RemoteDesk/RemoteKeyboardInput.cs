namespace RemoteDesk;

// Windows reports modifier VKs differently through low-level hooks and window
// messages. Physical identity uses the scan code/side, while callers retain the
// original pressed command for exact native release and disconnect cleanup.
internal static class RemoteKeyboardInput
{
    internal static RemoteKeyboardFlags NormalizeFlags(int virtualKey, int scanCode, RemoteKeyboardFlags flags) =>
        flags.HasFlag(RemoteKeyboardFlags.HasScanCode) && scanCode == 0x36 && virtualKey is 0x10 or 0xA1
            ? flags & ~RemoteKeyboardFlags.Extended : flags;

    internal static RemotePhysicalKey PhysicalKey(RemoteInputCommand command)
    {
        // Pause has an E1 sequence which the byte-scan + E0 wire metadata
        // cannot express. Injection uses VK_PAUSE, so its ownership must also
        // remain independent of hook/window-message scan variants.
        if (RequiresVirtualKey(command.Data)) return new(command.Data, 0, RemoteKeyboardFlags.None);
        var flags = NormalizeFlags(command.Data, command.X, (RemoteKeyboardFlags)command.Y);
        int virtualKey = command.Data;
        if (flags.HasFlag(RemoteKeyboardFlags.HasScanCode))
        {
            bool extended = flags.HasFlag(RemoteKeyboardFlags.Extended);
            virtualKey = (ModifierFamily(virtualKey), command.X) switch
            {
                (0x10, 0x2A) when !extended => 0xA0,
                (0x10, 0x36) when !extended => 0xA1,
                (0x11, 0x1D) => extended ? 0xA3 : 0xA2,
                (0x12, 0x38) => extended ? 0xA5 : 0xA4,
                _ => virtualKey
            };
            if (!extended) virtualKey = KeypadVirtualKey(virtualKey, command.X);
        }
        return new(virtualKey, command.X, flags);
    }

    internal static bool RequiresVirtualKey(int virtualKey) => virtualKey == 0x13; // VK_PAUSE

    // NumLock/Shift can change the VK between down and up on the same keypad
    // key. Only these known non-E0 scan/VK aliases share ownership; dedicated
    // navigation keys and unrelated VKs keep their existing identities.
    private static int KeypadVirtualKey(int virtualKey, int scanCode) => (scanCode, virtualKey) switch
    {
        (0x52, 0x60 or 0x2D) => 0x60,
        (0x4F, 0x61 or 0x23) => 0x61,
        (0x50, 0x62 or 0x28) => 0x62,
        (0x51, 0x63 or 0x22) => 0x63,
        (0x4B, 0x64 or 0x25) => 0x64,
        (0x4C, 0x65 or 0x0C) => 0x65,
        (0x4D, 0x66 or 0x27) => 0x66,
        (0x47, 0x67 or 0x24) => 0x67,
        (0x48, 0x68 or 0x26) => 0x68,
        (0x49, 0x69 or 0x21) => 0x69,
        (0x53, 0x6E or 0x2E) => 0x6E,
        _ => virtualKey
    };

    // Fallback is only for an endpoint/event lacking physical information.
    // Two physical keys must never fall back to a family/VK match (for example
    // a stray right-modifier key-up must not release a held left modifier).
    internal static bool MatchesLegacyRelease(RemoteInputCommand pressed, RemoteInputCommand released)
    {
        RemotePhysicalKey left = PhysicalKey(pressed), right = PhysicalKey(released);
        if (left.Flags.HasFlag(RemoteKeyboardFlags.HasScanCode) && right.Flags.HasFlag(RemoteKeyboardFlags.HasScanCode))
            return false;
        // Keep the original same-VK fallback when a keypad navigation down
        // was canonicalized, but its legacy up has no scan code to do so.
        if (pressed.Data == released.Data || left.VirtualKey == right.VirtualKey) return true;
        int family = ModifierFamily(left.VirtualKey);
        return family != 0 && family == ModifierFamily(right.VirtualKey) &&
            (left.VirtualKey == family || right.VirtualKey == family);
    }

    private static int ModifierFamily(int virtualKey) => virtualKey switch
    {
        0x10 or 0xA0 or 0xA1 => 0x10,
        0x11 or 0xA2 or 0xA3 => 0x11,
        0x12 or 0xA4 or 0xA5 => 0x12,
        _ => 0
    };
}
