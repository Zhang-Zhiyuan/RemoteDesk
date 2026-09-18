package com.remotedesk.agent;

import java.io.IOException;
import java.util.UUID;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.function.BooleanSupplier;

/** Per-connection preflight. It never sends file bytes or uses a previous peer's location. */
final class AndroidFileReceiveLocation {
    interface Writer { void write(byte[] payload) throws Exception; }
    static final class Location {
        final String directory, note;
        Location(String directory, String note) { this.directory = directory; this.note = note; }
        String file(String name) {
            if (directory.isEmpty()) return "位置未确认：远端未提供完整接收目录";
            String separator = directory.contains("\\") && !directory.startsWith("/") ? "\\" : "/";
            return directory.replaceAll("[/\\\\]+$", "") + separator + name;
        }
    }
    private static final class Pending {
        final String id = UUID.randomUUID().toString();
        final CountDownLatch completed = new CountDownLatch(1);
        volatile RemoteDeskTransport.ControlMessage reply;
    }
    private volatile Pending pending;

    Location request(int capabilities, Writer writer, BooleanSupplier connected) throws Exception {
        return request(capabilities, writer, connected, 10_000);
    }

    Location request(int capabilities, Writer writer, BooleanSupplier connected, long timeoutMillis) throws Exception {
        if (!connected.getAsBoolean()) throw new IOException("连接已断开，尚未发送文件。");
        if ((capabilities & RemoteDeskProtocol.CAPABILITY_FILE_RECEIVE_LOCATION) == 0)
            return new Location("", "远端版本不支持报告完整接收目录。位置未确认；建议更新被控端后再传输。");
        Pending request = new Pending();
        synchronized (this) {
            if (pending != null) throw new IOException("正在读取接收目录，请稍候。");
            pending = request;
        }
        try {
            writer.write(RemoteDeskTransport.encodeFileReceiveLocationRequest(request.id));
            long deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(timeoutMillis);
            while (!request.completed.await(100, TimeUnit.MILLISECONDS)) {
                if (!connected.getAsBoolean()) throw new IOException("连接已断开，尚未发送文件。");
                if (System.nanoTime() >= deadline) throw new IOException("读取远端接收位置超时，尚未发送文件，请重试。");
            }
            if (!connected.getAsBoolean()) throw new IOException("连接已改变，请重新确认接收位置。");
            RemoteDeskTransport.ControlMessage reply = request.reply;
            if (!reply.success || reply.text == null || reply.text.trim().isEmpty())
                throw new IOException(reply.statusMessage == null || reply.statusMessage.isEmpty()
                    ? "远端未能确认接收目录，未开始传输。" : reply.statusMessage);
            return new Location(reply.text, reply.statusMessage == null ? "" : reply.statusMessage);
        } finally {
            synchronized (this) { if (pending == request) pending = null; }
        }
    }

    synchronized void receive(RemoteDeskTransport.ControlMessage message) {
        Pending request = pending;
        if (message.kind == RemoteDeskProtocol.CONTROL_FILE_RECEIVE_LOCATION && request != null &&
            request.id.equals(message.transferId) && request.reply == null) {
            request.reply = message;
            request.completed.countDown();
        }
    }
}
