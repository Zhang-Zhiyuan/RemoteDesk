package com.remotedesk.agent;

import android.content.Context;

/** Serialize read/modify/write across the main screen and viewer connection thread. */
final class AndroidConnectionHistoryStore {
    static synchronized AndroidConnectionHistory load(Context context) throws Exception {
        return AndroidConnectionHistory.decode(AndroidPasswordStore.loadHistory(context));
    }

    static synchronized void remember(Context context, String previousId, String host, int port,
            String password, AndroidRelay.Options relay, String name) throws Exception {
        remember(context, previousId, host, port, password, relay, name, "");
    }
    static synchronized void remember(Context context, String previousId, String host, int port,
            String password, AndroidRelay.Options relay, String name, String deviceId) throws Exception {
        AndroidConnectionHistory history = load(context);
        if (history.remember(previousId, host, port, relay == null ? "" : relay.deviceId, password,
                relay == null ? "" : relay.json().toString(), name, System.currentTimeMillis(), deviceId) != null)
            AndroidPasswordStore.saveHistory(context, history.encode());
    }

    static synchronized void add(Context context, String host, int port, String password, String remark, boolean autoPort) throws Exception {
        AndroidConnectionHistory history = load(context);
        AndroidConnectionHistory.Node node = history.remember("", host, port, "", password, "", "", System.currentTimeMillis());
        if (!remark.trim().isEmpty()) history.rename(node.id, remark);
        history.autoPort(node.id, autoPort);
        AndroidPasswordStore.saveHistory(context, history.encode());
    }

    static synchronized void rename(Context context, String id, String remark) throws Exception {
        AndroidConnectionHistory history = load(context);
        if (history.rename(id, remark)) AndroidPasswordStore.saveHistory(context, history.encode());
    }

    static synchronized void remove(Context context, String id) throws Exception {
        AndroidConnectionHistory history = load(context);
        if (history.remove(id)) AndroidPasswordStore.saveHistory(context, history.encode());
    }
}
