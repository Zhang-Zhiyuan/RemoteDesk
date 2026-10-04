using System.Runtime.InteropServices;

namespace RemoteDesk;

// Kept separate from route creation/elevation so the console proxy can reuse
// pinned socket route selection without shipping any privileged route writer.
internal sealed partial class WindowsRelayRouteBackend
{
    internal static bool IsPhysicalInterface(uint index)
    {
        try
        {
            var row = new NativeInterface { Index = index };
            return GetIfEntry2(ref row) == 0 && row.IsPhysical;
        }
        catch { return false; }
    }

    // MIB_IF_ROW2 includes 257-WCHAR alias/description arrays, two 32-byte
    // hardware addresses and 20 UInt64 counters. Explicit offsets retain the
    // full native buffer while exposing only the read-only policy fields.
    [StructLayout(LayoutKind.Explicit, Size = 1352)]
    internal struct NativeInterface
    {
        [FieldOffset(8)] internal uint Index;
        [FieldOffset(12)] internal Guid Id;
        [FieldOffset(1128)] internal uint Type;
        [FieldOffset(1132)] internal uint Tunnel;
        [FieldOffset(1152)] internal byte Flags;
        [FieldOffset(1156)] internal uint OperationalStatus;
        internal bool IsPhysical => Type is 6 or 71 && Tunnel == 0 && OperationalStatus == 1 &&
            (Flags & 1) != 0 && (Flags & (2 | 8 | 16 | 32)) == 0;
    }

    [DllImport("iphlpapi.dll")] private static extern uint GetIfEntry2(ref NativeInterface row);
}
