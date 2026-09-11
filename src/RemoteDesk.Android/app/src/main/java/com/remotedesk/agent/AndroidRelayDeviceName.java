package com.remotedesk.agent;

/** Shared relay label only. Never changes the operating-system name or device control key. */
final class AndroidRelayDeviceName {
    static final String UNSUPPORTED = "此中继服务器尚不支持共享命名，请先更新服务器，再刷新在线列表。";
    private AndroidRelayDeviceName() { }

    static String normalize(String value) {
        if (value == null) throw new IllegalArgumentException("设备名称必须是文字。");
        // String.strip() isn't available on every supported Android version.
        int start = 0, end = value.length();
        while (start < end && isSpace(value.charAt(start))) start++;
        while (end > start && isSpace(value.charAt(end - 1))) end--;
        String name = value.substring(start, end);
        boolean invalid = name.length() > 80;
        for (int i = 0; !invalid && i < name.length();) {
            int point = name.codePointAt(i), type = Character.getType(point);
            invalid = type == Character.CONTROL || type == Character.FORMAT || type == Character.SURROGATE ||
                type == Character.LINE_SEPARATOR || type == Character.PARAGRAPH_SEPARATOR;
            i += Character.charCount(point);
        }
        if (invalid) throw new IllegalArgumentException("请使用不超过 80 个字符的单行名称，不要包含控制字符。");
        return name;
    }

    private static boolean isSpace(char ch) { return Character.isWhitespace(ch) || Character.isSpaceChar(ch); }

    static String errorMessage(String detail) {
        if (detail.contains("未知的中继连接类型")) return UNSUPPORTED;
        if (detail.contains("无法读取")) return "服务器的设备命名记录无法读取，请检查服务器存储；原文件未修改。";
        if (detail.contains("保存失败")) return "设备名称保存失败，请检查服务器磁盘和目录权限；原名称未更改。";
        if (detail.contains("已离线") || detail.contains("不存在")) return "设备已离线或不存在，请刷新在线列表后重试。";
        if (detail.contains("上限")) return "已达到服务器的设备命名数量上限。";
        if (detail.contains("名称") && (detail.contains("80") || detail.contains("文字")))
            return "请使用不超过 80 个字符的单行名称，不要包含控制字符。";
        return "共享名称保存失败，请刷新在线列表核对后重试。";
    }
}
