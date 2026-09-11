package com.remotedesk.agent;

import java.io.IOException;
import java.io.InputStream;
import java.io.InterruptedIOException;
import java.security.MessageDigest;
import java.util.UUID;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.function.BooleanSupplier;
import java.util.function.Consumer;

/** One explicit, confirmed document upload. Transport-independent: direct and relay share it. */
final class AndroidFileSender {
    static final long MAX_BYTES = 1024L * 1024 * 1024;
    interface Writer { void write(byte[] payload) throws Exception; }
    final String transferId = UUID.randomUUID().toString().replace("-", "");
    private final AtomicBoolean cancelled = new AtomicBoolean();
    private final CountDownLatch receipt = new CountDownLatch(1);
    private volatile boolean accepted;
    private volatile String receiptMessage = "";
    long receiptTimeoutMillis = 120_000L;

    void cancel() { cancelled.set(true); }

    synchronized void receive(RemoteDeskTransport.ControlMessage message) {
        if (message.kind != RemoteDeskProtocol.CONTROL_FILE_TRANSFER_RECEIPT ||
                !transferId.equals(message.transferId) || receipt.getCount() == 0) return;
        accepted = message.success;
        receiptMessage = message.statusMessage == null ? "" : message.statusMessage;
        receipt.countDown();
    }

    private void check(BooleanSupplier current) throws IOException {
        if (cancelled.get() || Thread.currentThread().isInterrupted()) throw new InterruptedIOException("已取消文件传输");
        if (!current.getAsBoolean()) throw new IOException("连接已结束，文件传输已停止");
        if (receipt.getCount() == 0 && !accepted) throw new IOException(receiptMessage);
    }

    String send(InputStream input, String name, long length, int capabilities, Writer writer,
        BooleanSupplier current, Consumer<String> progress) throws Exception {
        if (length < 0 || length > MAX_BYTES) throw new IOException("文件大小无效或超过 1 GB 上限");
        boolean started = false;
        try {
            check(current);
            writer.write(RemoteDeskTransport.encodeFileTransferStart(transferId, name, length));
            started = true;
            MessageDigest hash = MessageDigest.getInstance("SHA-256");
            byte[] buffer = new byte[32 * 1024];
            long offset = 0;
            int lastPercent = -10;
            while (offset < length) {
                check(current);
                int read = input.read(buffer, 0, (int) Math.min(buffer.length, length - offset));
                if (read <= 0) throw new IOException("文件在传输过程中被截断或无法继续读取");
                check(current);
                hash.update(buffer, 0, read);
                writer.write(RemoteDeskTransport.encodeFileTransferChunk(transferId, offset, buffer, read));
                offset += read;
                int percent = (int) (offset * 100 / length);
                if (percent >= lastPercent + 10) { progress.accept("正在发送 " + name + "：" + percent + "%"); lastPercent = percent; }
            }
            check(current);
            if (input.read() != -1) throw new IOException("文件大小已改变，请重新选择后发送");
            if ((capabilities & RemoteDeskProtocol.CAPABILITY_FILE_CHECKSUM) != 0)
                writer.write(RemoteDeskTransport.encodeFileTransferChecksum(transferId, hex(hash.digest())));
            check(current);
            writer.write(RemoteDeskTransport.encodeFileTransferComplete(transferId));
            if ((capabilities & RemoteDeskProtocol.CAPABILITY_FILE_TRANSFER_RECEIPT) == 0)
                return "数据已发送；对方是旧版，请检查远端接收目录确认是否保存。";
            progress.accept("数据已发送，正在等待远端保存确认…");
            long deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(receiptTimeoutMillis);
            while (!receipt.await(100, TimeUnit.MILLISECONDS)) {
                check(current);
                if (System.nanoTime() >= deadline) throw new IOException("等待保存确认超时，文件可能已保存，请检查远端接收目录后再重试");
            }
            check(current);
            return receiptMessage.isEmpty() ? "远端已确认保存文件" : receiptMessage;
        } catch (Exception failure) {
            if (started && current.getAsBoolean() && (capabilities & RemoteDeskProtocol.CAPABILITY_FILE_TRANSFER_CANCEL) != 0) {
                try { writer.write(RemoteDeskTransport.encodeFileTransferCancel(transferId, "手机发送端取消或传输失败")); }
                catch (Exception ignored) { }
            }
            throw failure;
        }
    }

    private static String hex(byte[] bytes) {
        char[] digits = "0123456789abcdef".toCharArray();
        StringBuilder result = new StringBuilder(bytes.length * 2);
        for (byte value : bytes) { result.append(digits[(value & 255) >>> 4]); result.append(digits[value & 15]); }
        return result.toString();
    }
}
