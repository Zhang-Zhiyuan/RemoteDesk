package com.remotedesk.agent;

import java.io.IOException;
import java.util.UUID;
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
    private Pending pending;
    Status request(int capabilities, int action, AndroidFileReceiveLocation.Writer writer,
                   BooleanSupplier connected, long timeoutMillis) throws Exception {
        if (!connected.getAsBoolean()) throw new IOException("连接已断开，隐私屏状态未确认。");
        if ((capabilities & RemoteDeskProtocol.CAPABILITY_PRIVACY_SCREEN) == 0)
            throw new IOException("此被控系统或版本不支持隐私屏。Linux / 安卓被控端暂不支持；普通黑窗会同时遮住远端画面。");
        Pending current = new Pending();
        synchronized (this) {
            if (pending != null) throw new IOException("正在确认隐私屏状态，请稍候。");
            pending = current;
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
        } finally { synchronized (this) { if (pending == current) pending = null; } }
    }
    synchronized void receive(Status status) {
        if (pending != null && pending.id.equals(status.id) && pending.status == null) {
            pending.status = status; pending.done.countDown();
        }
    }
}
