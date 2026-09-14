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
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.atomic.AtomicBoolean;
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
    private static final Selector SHARED = new Selector(System::nanoTime, AndroidRelay.TIMEOUT_MS, WORKERS, true);
    private AndroidRelayNetworkSelector() { }

    /** A pending-connection handle: the existing UI/service cancellation closes every candidate. */
    static final class Dial extends Socket {
        private final Set<Socket> owned = new HashSet<>();
        final boolean probeOnly;
        private boolean closed;
        Dial() { this(false); }
        Dial(boolean probeOnly) { this.probeOnly = probeOnly; }
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
    /** Pure-Java policy is tested without requiring a specific handset or Android network stack. */
    static final class Selector {
        private final Map<String, RelayPathStability> cache = new HashMap<>();
        private final LongSupplier clock;
        private final long timeoutNanos;
        private final ExecutorService workers;
        private final boolean backgroundProbes;
        private final AtomicBoolean probing = new AtomicBoolean();
        Selector(LongSupplier clock) { this(clock, AndroidRelay.TIMEOUT_MS); }
        Selector(LongSupplier clock, long timeoutMillis) {
            this(clock, timeoutMillis, WORKERS);
        }
        Selector(LongSupplier clock, long timeoutMillis, ExecutorService workers) {
            this(clock, timeoutMillis, workers, false);
        }
        Selector(LongSupplier clock, long timeoutMillis, ExecutorService workers, boolean backgroundProbes) {
            this.clock = clock;
            this.timeoutNanos = TimeUnit.MILLISECONDS.toNanos(timeoutMillis);
            this.workers = workers;
            this.backgroundProbes = backgroundProbes;
        }

        private long nowMillis() { return TimeUnit.NANOSECONDS.toMillis(clock.getAsLong()); }

        <T extends Socket> T connect(String endpointKey, List<String> alternatives,
                Dial dial, Connector<T> connector) throws Exception {
            return connect(endpointKey, alternatives, dial, connector, true);
        }

        <T extends Socket> T connect(String endpointKey, List<String> alternatives,
                Dial dial, Connector<T> connector, boolean allowProbes) throws Exception {
            List<String> paths = new ArrayList<>();
            paths.add("default"); paths.addAll(alternatives);
            String key = endpointKey + "/" + alternatives;
            RelayPathStability stability;
            synchronized (cache) {
                if (cache.size() >= 32 && !cache.containsKey(key)) cache.clear();
                stability = cache.computeIfAbsent(key, unused -> new RelayPathStability());
            }
            String cached = stability.preferred(nowMillis());
            if (cached != null && paths.remove(cached)) paths.add(0, cached);
            else cached = null;
            final String preferredPath = cached;
            AtomicBoolean preferredFailed = new AtomicBoolean();
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
                    tasks.add(completed.submit(() -> attempt(path, dial, connector, preferredPath, preferredFailed)));
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
                            if (!alternatives.isEmpty()) {
                                stability.connected(result.path, nowMillis(), preferredFailed.get() ? preferredPath : null);
                                if (backgroundProbes && allowProbes) startProbe(stability, paths, connector);
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
                            tasks.add(completed.submit(() -> attempt(path, dial, connector, preferredPath, preferredFailed)));
                            remaining++;
                        }
                    }
                }
                throw failure == null ? new IOException("没有可用的中转网络路径。") : failure;
            } catch (Exception error) {
                if (error instanceof RejectedExecutionException)
                    throw new IOException("中转连接过于频繁，请稍后重试。", error);
                throw error;
            } finally {
                dial.close(); // Winner was detached. Every late/queued loser remains owned.
                for (Future<?> task : tasks) task.cancel(true);
            }
        }

        private static <T extends Socket> Result<T> attempt(String path, Dial dial, Connector<T> connector,
                String preferred, AtomicBoolean failed) throws Exception {
            try { dial.checkOpen(); return new Result<>(path, connector.connect(path, dial)); }
            catch (Exception error) {
                if (!dial.isClosed() && !Thread.currentThread().isInterrupted() && path.equals(preferred)) failed.set(true);
                throw error;
            }
        }

        private <T extends Socket> void startProbe(RelayPathStability stability, List<String> paths, Connector<T> connector) {
            if (!probing.compareAndSet(false, true)) return;
            long scheduledAt = nowMillis();
            if (!stability.beginProbe(scheduledAt)) { probing.set(false); return; }
            Thread audit = new Thread(() -> {
                try {
                    Thread.sleep(1000); // Never delay initial authentication/video.
                    probeRound(stability, paths, connector, 2000, scheduledAt);
                } catch (Exception ignored) { /* Optional TLS-only measurement. */ }
                finally { probing.set(false); }
            }, "RelayPathQuality");
            audit.setDaemon(true);
            try { audit.start(); } catch (RuntimeException unavailable) { probing.set(false); }
        }

        <T extends Socket> void probeRound(RelayPathStability stability, List<String> paths,
                Connector<T> connector, long timeoutMillis) throws Exception {
            probeRound(stability, paths, connector, timeoutMillis, nowMillis());
        }

        private <T extends Socket> void probeRound(RelayPathStability stability, List<String> paths,
                Connector<T> connector, long timeoutMillis, long observedAt) throws Exception {
            List<Dial> owners = new ArrayList<>();
            List<Future<Double>> tasks = new ArrayList<>();
            CountDownLatch finished = new CountDownLatch(paths.size());
            long deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(timeoutMillis);
            try {
                for (String path : paths) {
                    Dial owner = new Dial(true); owners.add(owner);
                    tasks.add(workers.submit(() -> {
                        T result = null;
                        long started = System.nanoTime();
                        try {
                            owner.checkOpen(); result = connector.connect(path, owner);
                            return owner.isClosed() ? Double.POSITIVE_INFINITY : (System.nanoTime() - started) / 1_000_000.0;
                        } catch (Exception error) { return Double.POSITIVE_INFINITY; }
                        finally { AndroidRelay.close(result); owner.close(); finished.countDown(); }
                    }));
                }
                Map<String, Double> observations = new HashMap<>();
                for (int i = 0; i < tasks.size(); i++) {
                    double value;
                    try { value = tasks.get(i).get(Math.max(1, deadline - System.nanoTime()), TimeUnit.NANOSECONDS); }
                    catch (java.util.concurrent.ExecutionException | java.util.concurrent.TimeoutException error) { value = Double.POSITIVE_INFINITY; }
                    observations.put(paths.get(i), value);
                }
                stability.observeRound(observedAt, observations);
            } finally {
                for (Dial owner : owners) owner.close();
                for (int i = tasks.size(); i < paths.size(); i++) finished.countDown();
                // Closing each owned socket cancels networking. Keep the one
                // audit slot until queued/native work actually ends; do not use
                // Future.cancel(), which marks an uninterruptible call done early.
                finished.await();
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
        boolean allowProbes = false;
        try {
            ConnectivityManager manager = context == null ? null :
                (ConnectivityManager) context.getApplicationContext().getSystemService(Context.CONNECTIVITY_SERVICE);
            if (manager != null) {
                Network active = manager.getActiveNetwork();
                NetworkCapabilities activeCaps = manager.getNetworkCapabilities(active);
                boolean vpn = activeCaps != null && activeCaps.hasTransport(NetworkCapabilities.TRANSPORT_VPN);
                allowProbes = !vpn && activeCaps != null && activeCaps.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED);
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
        final String expectedNetwork = defaultKey;
        return SHARED.connect(key, alternatives, dial,
            (path, owner) -> {
                if (owner.probeOnly && !probeNetworkUnchanged(context, expectedNetwork))
                    throw new IOException("网络已变化，跳过旧线路采样。");
                return AndroidRelay.connectBoundSocket(options, networks.get(path), owner, configure);
            }, allowProbes);
    }

    private static boolean probeNetworkUnchanged(Context context, String expected) {
        try {
            ConnectivityManager manager = context == null ? null :
                (ConnectivityManager) context.getApplicationContext().getSystemService(Context.CONNECTIVITY_SERVICE);
            if (manager == null) return false;
            Network active = manager.getActiveNetwork();
            NetworkCapabilities caps = manager.getNetworkCapabilities(active);
            return caps != null && !caps.hasTransport(NetworkCapabilities.TRANSPORT_VPN) &&
                caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN) &&
                caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED) &&
                caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED) &&
                expected.equals(String.valueOf(active) + "/" + String.valueOf(manager.getLinkProperties(active)));
        } catch (RuntimeException unavailable) { return false; }
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
