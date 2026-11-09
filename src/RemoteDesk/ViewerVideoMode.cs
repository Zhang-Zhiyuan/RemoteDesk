namespace RemoteDesk;

internal enum ViewerVideoMode
{
    Automatic = 0,
    // Numeric values retained only to migrate old settings/API callers.
    StableJpeg = 1,
    ForceH264 = 2
}
