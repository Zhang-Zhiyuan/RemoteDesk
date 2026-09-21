package com.remotedesk.agent;

import java.util.Locale;
import java.util.function.Function;

/** Use the same type for public Downloads and app-private file sharing. */
final class AndroidFileMimeType {
    static final String BINARY = "application/octet-stream";

    private AndroidFileMimeType() { }

    static String extension(String fileName) {
        if (fileName == null) return "";
        String name = fileName.trim();
        int dot = name.lastIndexOf('.');
        return dot <= 0 || dot == name.length() - 1 ? ""
            : name.substring(dot + 1).toLowerCase(Locale.ROOT);
    }

    static String resolve(String fileName, Function<String, String> lookup) {
        String extension = extension(fileName);
        String type = extension.isEmpty() ? null : lookup.apply(extension);
        return type == null || type.isEmpty() ? BINARY : type;
    }
}
