package com.remotedesk.agent;

/** Pure edit policy; accessibility hint text is never treated as user content. */
final class AndroidFocusedTextEdit {
    final String text;
    final int cursor;

    private AndroidFocusedTextEdit(String text, int cursor) {
        this.text = text;
        this.cursor = cursor;
    }

    static AndroidFocusedTextEdit create(CharSequence observed, boolean showingHint,
            int selectionStart, int selectionEnd, String inserted, boolean deleteBeforeCursor) {
        String current = showingHint || observed == null ? "" : observed.toString();
        String addition = inserted == null ? "" : inserted;
        int first = selection(selectionStart, current.length());
        int last = selection(selectionEnd, current.length());
        int start = Math.min(first, last);
        int end = Math.max(first, last);
        if (deleteBeforeCursor && start == end && start > 0) {
            start = current.offsetByCodePoints(start, -1);
        }
        return new AndroidFocusedTextEdit(current.substring(0, start) + addition + current.substring(end),
                                          start + addition.length());
    }

    private static int selection(int value, int length) {
        return value < 0 ? length : Math.min(value, length);
    }
}
