package com.remotedesk.agent;

import android.content.Context;
import android.net.ConnectivityManager;
import android.net.Network;
import android.net.NetworkCapabilities;
import java.io.IOException;
import java.net.Socket;
import java.net.SocketTimeoutException;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.ExecutorCompletionService;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Future;
import java.util.concurrent.RejectedExecutionException;
import java.util.concurrent.ThreadPoolExecutor;
import java.util.concurrent.TimeUnit;
import java.util.function.LongSupplier;
import javax.net.ssl.SSLSocket;

/** Socket-local network selection. Never activate cellular, bind the process, or change a VPN. */
final class AndroidRelayNetworkSelector {
    // A native resolver may outlive cancellation. Bound queued requests as well
    // as threads so repeated reconnects cannot accumulate work indefinitely.
    private static final ExecutorService WORKERS = new ThreadPoolExecutor(8, 8, 0L, TimeUnit.MILLISECONDS,
        new ArrayBlockingQueue<>(64), runnable -> {
        Thread thread = new Thread(runnable, "RelayNetworkPath");
        thread.setDaemon(true);
        return thread;
    });
    private static final Selector SHARED = new Selector(System::nanoTime);
    private AndroidRelayNetworkSelector() { }

    /** A pending-connection handle: the existing UI/service cancellation closes every candidate. */
    static final class Dial extends Socket {
        private final Set<Socket> owned = new HashSet<>();
        private boolean closed;
        synchronized void checkOpen() throws IOException {
            if (closed || Thread.currentThread().isInterrupted()) throw new IOException("中转连接已取消。");
        }
        synchronized void add(Socket socket) throws IOException {
            if (closed) { AndroidRelay.close(socket); throw new IOException("中转连接已取消。"); }
            owned.add(socket);
        }
        synchronized void replace(Socket raw, Socket tls) throws IOException {
            owned.remove(raw);
            add(tls);
        }
        synchronized void release(Socket socket) {
            owned.remove(socket);
            AndroidRelay.close(socket);
        }
        synchronized <T extends Socket> T take(T socket) throws IOException {
            checkOpen();
            owned.remove(socket);
            return socket;
        }
        @Override public synchronized boolean isClosed() { return closed; }
        @Override public synchronized void close() {
            closed = true;
            for (Socket socket : owned) AndroidRelay.close(socket);
            owned.clear();
        }
    }

    @FunctionalInterface interface Connector<T extends Socket> { T connect(String path, Dial dial) throws Exception; }
    private static final class Result<T extends Socket> {
        final String path;
        final T socket;
        Result(String path, T socket) { this.path = path; this.socket = socket; }
    }
    private static final class Preference {
        final String path;
        final long expires;
        Preference(String path, long expires) { this.path = path; this.expires = expires; }
    }

    /** Pure-Java policy is tested without requiring a specific handset or Android network stack. */
    static final class Selector {
        private final Map<String, Preference> cache = new HashMap<>();
        private final LongSupplier clock;
        private final long timeoutNanos;
        private final ExecutorService workers;
        Selector(LongSupplier clock) { this(clock, AndroidRelay.TIMEOUT_MS); }
        Selector(LongSupplier clock, long timeoutMillis) {
            this(clock, timeoutMillis, WORKERS);
        }
        Selector(LongSupplier clock, long timeoutMillis, ExecutorService workers) {
            this.clock = clock;
            this.timeoutNanos = TimeUnit.MILLISECONDS.toNanos(timeoutMillis);
            this.workers = workers;
        }

        <T extends Socket> T connect(String endpointKey, List<String> alternatives,
                Dial dial, Connector<T> connector) throws Exception {
            List<String> paths = new ArrayList<>();
            paths.add("default"); paths.addAll(alternatives);
            String key = endpointKey + "/" + alternatives;
            Preference cached;
            synchronized (cache) { cached = cache.get(key); }
            if (cached != null && cached.expires <= clock.getAsLong()) cached = null;
            if (cached != null && paths.remove(cached.path)) paths.add(0, cached.path);
            else cached = null;
            ExecutorCompletionService<Result<T>> completed = new ExecutorCompletionService<>(workers);
            List<Future<Result<T>>> tasks = new ArrayList<>();
            // Even a single network uses this deadline. A synchronous DNS call
            // cannot be cancelled by closing a Socket before connect starts.
            long deadline = System.nanoTime() + timeoutNanos;
            Exception failure = null;
            int remaining = 0;
            boolean othersStarted = cached == null;
            try {
                int initial = cached == null ? paths.size() : 1;
                for (int index = 0; index < initial; index++) {
                    String path = paths.get(index);
                    tasks.add(completed.submit(() -> {
                        dial.checkOpen();
                        return new Result<>(path, connector.connect(path, dial));
                    }));
                    remaining++;
                }
                long hedgeAt = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(150);
                while (remaining > 0 || !othersStarted) {
                    dial.checkOpen();
                    long left = deadline - System.nanoTime();
                    if (left <= 0) throw new SocketTimeoutException("连接中转服务器超时。");
                    // Short polling keeps closing the existing pending Socket a
                    // prompt cancellation even while network DNS is blocked.
                    Future<Result<T>> item = completed.poll(Math.min(left, TimeUnit.MILLISECONDS.toNanos(30)), TimeUnit.NANOSECONDS);
                    if (item != null) {
                        remaining--;
                        try {
                            Result<T> result = item.get();
                            T selected = dial.take(result.socket);
                            long expires = cached != null && cached.path.equals(result.path)
                                ? cached.expires : clock.getAsLong() + TimeUnit.SECONDS.toNanos(60);
                            if (!alternatives.isEmpty()) synchronized (cache) {
                                if (cache.size() >= 32) cache.clear();
                                cache.put(key, new Preference(result.path, expires));
                            }
                            return selected;
                        } catch (java.util.concurrent.ExecutionException error) {
                            Throwable cause = error.getCause();
                            Exception next = cause instanceof Exception ? (Exception) cause : new IOException("中转握手失败。");
                            if (failure == null || next instanceof AndroidRelay.IdentityFailure) failure = next;
                        }
                    }
                    if (!othersStarted && (remaining == 0 || System.nanoTime() >= hedgeAt)) {
                        othersStarted = true;
                        for (int index = 1; index < paths.size(); index++) {
                            String path = paths.get(index);
                            tasks.add(completed.submit(() -> {
                                dial.checkOpen();
                                return new Result<>(path, connector.connect(path, dial));
                            }));
                            remaining++;
                        }
                    }
                }
                throw failure == null ? new IOException("没有可用的中转网络路径。") : failure;
            } catch (Exception error) {
                synchronized (cache) { cache.remove(key); }
                if (error instanceof RejectedExecutionException)
                    throw new IOException("中转连接过于频繁，请稍后重试。", error);
                throw error;
            } finally {
                dial.close(); // Winner was detached. Every late/queued loser remains owned.
                for (Future<?> task : tasks) task.cancel(true);
            }
        }
    }

    static boolean allowAlternative(boolean vpnActive, boolean internet, boolean validated,
            boolean unmetered, boolean notVpn, boolean notRestricted) {
        return !vpnActive && internet && validated && unmetered && notVpn && notRestricted;
    }

    @SuppressWarnings("deprecation") // Snapshot is used only for this dial; bind/handshake failure always falls back.
    static SSLSocket connect(Context context, AndroidRelay.Options options, Dial dial) throws Exception {
        return connect(context, options, dial, socket -> { });
    }

    @SuppressWarnings("deprecation")
    static SSLSocket connect(Context context, AndroidRelay.Options options, Dial dial,
            java.util.function.Consumer<Socket> configure) throws Exception {
        Map<String, Network> networks = new HashMap<>();
        List<String> alternatives = new ArrayList<>();
        String defaultKey = "system";
        try {
            ConnectivityManager manager = context == null ? null :
                (ConnectivityManager) context.getApplicationContext().getSystemService(Context.CONNECTIVITY_SERVICE);
            if (manager != null) {
                Network active = manager.getActiveNetwork();
                NetworkCapabilities activeCaps = manager.getNetworkCapabilities(active);
                boolean vpn = activeCaps != null && activeCaps.hasTransport(NetworkCapabilities.TRANSPORT_VPN);
                defaultKey = String.valueOf(active) + "/" + String.valueOf(manager.getLinkProperties(active));
                // Only already-connected, validated, unmetered alternatives.
                // No requestNetwork(), cellular activation or process-wide binding.
                if (!vpn && activeCaps != null && activeCaps.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN) &&
                        !isLocalHost(options.serverAddress)) {
                    for (Network network : manager.getAllNetworks()) {
                        NetworkCapabilities caps = manager.getNetworkCapabilities(network);
                        if (network.equals(active) || caps == null || !allowAlternative(vpn,
                            caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET),
                            caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED),
                            caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED),
                            caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN),
                            caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_RESTRICTED))) continue;
                        String id = network.toString() + "/" + String.valueOf(manager.getLinkProperties(network));
                        networks.put(id, network); alternatives.add(id);
                        if (alternatives.size() >= 3) break;
                    }
                }
            }
        } catch (RuntimeException unavailable) { networks.clear(); alternatives.clear(); }
        java.util.Collections.sort(alternatives);
        String key = options.serverAddress + ":" + options.port + "/" + options.tlsCertificateSha256 + "/" + defaultKey;
        return SHARED.connect(key, alternatives, dial,
            (path, owner) -> AndroidRelay.connectBoundSocket(options, networks.get(path), owner, configure));
    }

    private static boolean isLocalHost(String host) {
        if (host.equalsIgnoreCase("localhost") || host.endsWith(".local") || host.contains(":")) return true;
        if (!host.matches("[0-9.]+")) return false;
        try {
            java.net.InetAddress address = java.net.InetAddress.getByName(host);
            return address.isAnyLocalAddress() || address.isLoopbackAddress() || address.isSiteLocalAddress() ||
                address.isLinkLocalAddress() || address.isMulticastAddress();
        } catch (Exception invalid) { return true; }
    }
}
