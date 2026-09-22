package com.remotedesk.agent;

/** Existing host key mapping; never apply Android global keys to a desktop. */
final class AndroidViewerNavigation {
    static final int BACK = 0x1b, HOME = 0x24, RECENTS = 0x7b;
    private AndroidViewerNavigation() { }
    static boolean isAndroid(String platform) { return "Android".equalsIgnoreCase(platform); }
    static boolean allowed(String platform, int key) {
        return isAndroid(platform) && (key == BACK || key == HOME || key == RECENTS);
    }
}
