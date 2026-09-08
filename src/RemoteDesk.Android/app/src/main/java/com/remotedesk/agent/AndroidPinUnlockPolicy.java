package com.remotedesk.agent;

final class AndroidPinUnlockPolicy {
    enum InputDecision { SEND, WAIT, STOP }

    static InputDecision inputDecision(int entered, int nextDigit, int fieldChecks) {
        if (nextDigit < 0 || fieldChecks < 0) return InputDecision.STOP;
        if (entered < 0 || entered == nextDigit) return InputDecision.SEND;
        return entered < nextDigit && fieldChecks < 8 ? InputDecision.WAIT : InputDecision.STOP;
    }

    static boolean validPin(String value) {
        return value != null && value.matches("[0-9]{4,16}");
    }

    static int digitLabel(CharSequence value) {
        if (value == null || value.length() != 1) return -1;
        char digit = value.charAt(0);
        return digit >= '0' && digit <= '9' ? digit - '0' : -1;
    }

    static boolean explicitlyEmptyPrompt(CharSequence value) {
        return enteredDigitCount(value) == 0;
    }

    static int enteredDigitCount(CharSequence value) {
        if (value == null) return -1;
        String text = value.toString();
        java.util.regex.Matcher chinese = java.util.regex.Pattern.compile("已输入\\s*([0-9]{1,2})\\s*个值").matcher(text);
        if (chinese.find()) return Integer.parseInt(chinese.group(1));
        java.util.regex.Matcher english = java.util.regex.Pattern.compile("(?i)\\b([0-9]{1,2}) of [0-9]+\\b").matcher(text);
        return english.find() ? Integer.parseInt(english.group(1)) : -1;
    }
}
