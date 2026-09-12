namespace RemoteDesk;

internal enum ViewerVideoMode
{
    Automatic = 0,
    StableJpeg = 1,
    // Kept for settings/API compatibility. This old choice now permits JPEG
    // recovery, just like Automatic: locking Windows must not end a session.
    ForceH264 = 2
}
