package com.remotedesk.agent;

import java.io.IOException;
import java.util.ArrayList;
import java.util.Collections;
import java.util.LinkedHashSet;
import java.util.List;

/** One explicitly confirmed batch; cancellation also fences the next file. */
final class AndroidFileUploadQueue<T> {
    static final int MAX_FILES = 32;
    enum State { WAITING, SENDING, SAVED, SENT_UNCONFIRMED, FAILED, CANCELLED }
    static final class Entry<T> {
        final T source;
        final String name;
        final long bytes;
        State state = State.WAITING;
        String detail = "等待发送";
        Entry(T source, String name, long bytes) { this.source = source; this.name = name; this.bytes = bytes; }
    }
    final List<Entry<T>> entries;
    private boolean cancelled;
    private AndroidFileSender sender;
    private boolean activeCancellationSupported = true;

    AndroidFileUploadQueue(List<Entry<T>> entries) {
        if (entries.isEmpty() || entries.size() > MAX_FILES) throw new IllegalArgumentException("Invalid batch size");
        this.entries = Collections.unmodifiableList(new ArrayList<>(entries));
    }

    static <T> List<T> selection(List<T> selected, T fallback) throws IOException {
        LinkedHashSet<T> unique = new LinkedHashSet<>();
        if (selected != null) for (T source : selected) if (source != null) unique.add(source);
        if (fallback != null) unique.add(fallback);
        if (unique.isEmpty()) throw new IOException("未选择可读取的文件");
        if (unique.size() > MAX_FILES) throw new IOException("每批最多选择 " + MAX_FILES + " 个文件，请分批发送");
        return new ArrayList<>(unique);
    }

    static String fileName(String name) throws IOException {
        if (name == null) throw new IOException("文件未提供名称，请先保存到手机本地");
        String safe = name.replaceAll("[\\\\/\\p{Cntrl}]", "_");
        if (safe.trim().isEmpty() || safe.length() > 240) throw new IOException("文件名为空或过长，请重命名后再发送");
        return safe;
    }

    static void validateSize(String fileName, long bytes) throws IOException {
        if (bytes < 0) throw new IOException(fileName + "：未提供大小，请先下载到手机本地再选择发送");
        if (bytes > AndroidFileSender.MAX_BYTES) throw new IOException(fileName + "：单个文件不能超过 1 GB");
    }

    synchronized AndroidFileSender start(Entry<T> entry) {
        return start(entry, true);
    }

    synchronized AndroidFileSender start(Entry<T> entry, boolean canCancelActive) {
        if (cancelled || sender != null || entry.state != State.WAITING) return null;
        activeCancellationSupported = canCancelActive;
        entry.state = State.SENDING;
        entry.detail = "准备发送";
        sender = new AndroidFileSender();
        return sender;
    }

    synchronized void progress(Entry<T> entry, String detail) { entry.detail = detail; }

    synchronized void finish(Entry<T> entry, boolean success, boolean receipt, String detail) {
        entry.state = success ? (receipt ? State.SAVED : State.SENT_UNCONFIRMED) :
            (cancelled ? State.CANCELLED : State.FAILED);
        entry.detail = entry.state == State.CANCELLED ? detail + "；若数据已发送，请检查远端是否已保存" : detail;
        sender = null;
    }

    synchronized void cancel() {
        cancelled = true;
        if (sender != null) {
            if (activeCancellationSupported) sender.cancel();
            else sender.cancelBeforeStart();
        }
        for (Entry<T> entry : entries) if (entry.state == State.WAITING) {
            entry.state = State.CANCELLED; entry.detail = "未开始，已取消";
        }
    }

    synchronized boolean cancelled() { return cancelled; }

    synchronized String cancellationStatus() {
        return sender != null && !activeCancellationSupported
            ? "已取消未开始的文件；远端不支持中止，当前文件将继续完成发送与保存确认。"
            : "正在取消当前文件和剩余队列…";
    }

    synchronized String summary() {
        int saved = 0, unconfirmed = 0, failed = 0, stopped = 0;
        for (Entry<T> entry : entries) {
            if (entry.state == State.SAVED) saved++;
            else if (entry.state == State.SENT_UNCONFIRMED) unconfirmed++;
            else if (entry.state == State.FAILED) failed++;
            else if (entry.state == State.CANCELLED) stopped++;
        }
        StringBuilder text = new StringBuilder("共 ").append(entries.size()).append(" 个文件 · 已确认保存 ")
            .append(saved).append(" · 已发送待核实 ").append(unconfirmed);
        if (failed > 0) text.append(" · 失败 ").append(failed);
        if (stopped > 0) text.append(" · 取消 ").append(stopped);
        if (cancelled && sender != null && !activeCancellationSupported)
            text.append("\n").append(cancellationStatus());
        for (int i = 0; i < entries.size(); i++) {
            Entry<T> entry = entries.get(i);
            text.append("\n\n").append(i + 1).append(". ").append(entry.name).append("\n").append(entry.detail);
        }
        return text.append("\n\n文件仅保存到接收目录，不会自动粘贴到远端应用。").toString();
    }
}
