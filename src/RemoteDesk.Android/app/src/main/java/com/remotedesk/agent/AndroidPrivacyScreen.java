package com.remotedesk.agent;

import java.io.IOException;
import java.util.UUID;
import java.util.HashMap;
import java.util.Map;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.function.BooleanSupplier;

/** Queries and actions are correlated and scoped to one authenticated connection. */
final class AndroidPrivacyScreen {
    static final class Status {
        final String id, note;
        final boolean success, enabled, allowed;
        Status(String id, boolean success, boolean enabled, boolean allowed, String note) {
            this.id = id; this.success = success; this.enabled = enabled; this.allowed = allowed; this.note = note;
        }
    }
    private static final class Pending {
        final String id = UUID.randomUUID().toString();
        final CountDownLatch done = new CountDownLatch(1);
        volatile Status status;
    }
    // UI-thread state, scoped to the authenticated viewer connection. A query
    // must never block a restore command or overwrite a newer command's reply.
    static final class Controls {
        Status status;
        int command;
        boolean queryBusy;
        volatile long revision;
        long begin(int action) {
            if (action == 0) {
                if (queryBusy || command != 0) return -1;
                queryBusy = true;
            } else {
                if (command == 2 || (action == 1 && command != 0)) return -1;
                command = action;
                revision++;
            }
            return revision;
        }
        boolean complete(int action, long requestRevision, Status result) {
            if (action == 0) queryBusy = false;
            if (requestRevision != revision) return false;
            if (action != 0) command = 0;
            status = result;
            return true;
        }
        String label() {
            if (command == 1) return "恢复本地显示";
            if (command == 2) return "正在恢复显示…";
            if (status == null) return "隐私屏…";
            return status.enabled ? "关闭隐私屏" : status.allowed ? "开启隐私屏" : "隐私屏：未允许";
        }
    }
    private final Map<String, Pending> pending = new HashMap<>();
    Status request(int capabilities, int action, AndroidFileReceiveLocation.Writer writer,
                   BooleanSupplier connected, long timeoutMillis) throws Exception {
        if (!connected.getAsBoolean()) throw new IOException("连接已断开，隐私屏状态未确认。");
        if ((capabilities & RemoteDeskProtocol.CAPABILITY_PRIVACY_SCREEN) == 0)
            throw new IOException("此被控系统或版本不支持隐私屏。Linux / 安卓被控端暂不支持；普通黑窗会同时遮住远端画面。");
        Pending current = new Pending();
        synchronized (this) {
            if (pending.size() >= 3) throw new IOException("正在等待隐私屏回复，请稍后重试。");
            pending.put(current.id, current);
        }
        try {
            writer.write(RemoteDeskTransport.encodePrivacyScreenRequest(current.id, action));
            long deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(timeoutMillis);
            while (!current.done.await(100, TimeUnit.MILLISECONDS)) {
                if (!connected.getAsBoolean()) throw new IOException("连接已断开，隐私屏状态未确认。");
                if (System.nanoTime() >= deadline) throw new IOException("未收到隐私屏确认，状态未知；不要假定本地屏幕已遮挡。请刷新。");
            }
            if (!connected.getAsBoolean()) throw new IOException("连接已改变，请重新确认隐私屏。");
            return current.status;
        } finally { synchronized (this) { pending.remove(current.id); } }
    }
    synchronized void receive(Status status) {
        Pending current = pending.get(status.id);
        if (current != null && current.status == null) {
            current.status = status; current.done.countDown();
        }
    }
}
