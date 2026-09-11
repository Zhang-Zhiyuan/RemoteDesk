using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using RemoteDesk;

// Explicitly operated maintenance tool for the owner's configured relay.
// Credentials stay in memory. No input events, clipboard access or settings writes.
internal static class RelayWindowsUpdateProbe
{
    internal static async Task<int> RunAsync(JsonElement config, string output)
    {
        RemoteDeskSettings settings = new AppSettingsService().Load();
        RelaySettings saved = settings.Relay;
        if (string.IsNullOrWhiteSpace(saved.ServerAddress) || !config.TryGetProperty("expectedServer", out var expectedServer) ||
            string.IsNullOrWhiteSpace(expectedServer.GetString()) || saved.ServerAddress != expectedServer.GetString() ||
            string.IsNullOrEmpty(saved.SshHostKeySha256))
            throw new InvalidOperationException("Unexpected saved relay identity; nothing changed.");
        var options = new RelayConnectionOptions(saved.ServerAddress, saved.RelayPort,
            AppSettingsService.UnprotectSecret(saved.ProtectedAccessToken), saved.TlsCertificateSha256!,
            saved.DeviceId).Validate();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        IReadOnlyList<RelayOnlineDevice> devices = await RelayTunnelClient.ListDevicesAsync(options, timeout.Token);
        var report = new
        {
            server = options.ServerAddress,
            port = options.Port,
            inspectedAtUtc = DateTimeOffset.UtcNow,
            route = "Public relay TLS only; saved pinned identity and login",
            localDeviceId = saved.DeviceId,
            devices = devices.Select(device => new
            {
                device.DeviceId, device.MachineName, device.OriginalMachineName, device.Platform,
                device.BuildStamp, device.Busy, device.LastSeenSeconds,
                device.DirectAddresses, device.DirectPort,
                credentialSources = Credentials(settings, options, device).Select(item => item.Source).ToArray()
            }).ToArray(),
            savedWindowsHistory = settings.Viewer.RecentDevices
                .Where(device => device.Platform == RemoteDevicePlatforms.Windows)
                .Select(device => new { device.DeviceId, device.MachineName, device.Remark,
                    device.Address, device.Port, device.BuildStamp, hasSavedKey = !string.IsNullOrEmpty(device.ProtectedPassword) }).ToArray(),
            savedRelayKeyDeviceIds = settings.Relay.DeviceKeys
                .Where(key => key.Scope == RelayDeviceKeys.Scope(options)).Select(key => key.DeviceId).ToArray()
        };
        Program.Save(Path.Combine(output, "inventory.json"), report);
        Console.WriteLine(JsonSerializer.Serialize(report, Program.Json));
        if (!config.TryGetProperty("action", out var action) || action.GetString() == "inventory") return 0;
        if (action.GetString() != "apply") throw new InvalidOperationException("Unknown maintenance action.");

        string package = Path.GetFullPath(config.GetProperty("package").GetString()!);
        string expectedHash = config.GetProperty("sha256").GetString()!;
        string expectedBuild = config.GetProperty("buildStamp").GetString()!;
        RemoteUpdater.ValidateRemoteUpdatePackageName(package);
        using (var input = File.OpenRead(package))
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(input)), expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Package differs from the verified release hash.");
        if (RemoteDeskBuildInfo.ReadExecutableBuildStamp(package) != expectedBuild || !RemoteUpdateTrust.CanUseAsCurrentPackage(package))
            throw new InvalidOperationException("Package build identity is invalid.");
        string[] selectedIds = config.GetProperty("devices").EnumerateArray().Select(value => Guid.Parse(value.GetString()!).ToString("D")).Distinct().ToArray();
        RelayOnlineDevice[] selected = selectedIds.Select(id => devices.Single(device => device.DeviceId == id)).ToArray();
        if (selected.Any(device => device.Platform != RemoteDevicePlatforms.Windows))
            throw new InvalidOperationException("Only explicitly selected online Windows devices can be updated.");
        var results = new List<object>();
        foreach (RelayOnlineDevice device in selected)
        {
            var credentials = Credentials(settings, options, device);
            if (config.TryGetProperty("credentials", out var supplied) && supplied.TryGetProperty(device.DeviceId, out var key))
                credentials.Insert(0, ("explicit per-device key supplied in memory", key.GetString()!));
            var result = new Dictionary<string, object?>
            {
                ["deviceId"] = device.DeviceId, ["machineName"] = device.MachineName,
                ["beforeBuild"] = device.BuildStamp, ["expectedBuild"] = expectedBuild,
                ["route"] = "Public relay TLS/TCP only; no IP/direct fallback", ["startedAtUtc"] = DateTimeOffset.UtcNow
            };
            try
            {
                if (device.Busy) throw new InvalidOperationException("Device has an active session; no update sent.");
                if (credentials.Count == 0) throw new InvalidOperationException("No saved or supplied device key; no authentication attempted.");
                await ApplyAsync(options with { DeviceId = device.DeviceId }, device, credentials, package, expectedBuild, result);
                result["success"] = true;
            }
            catch (Exception error)
            {
                result["success"] = false;
                result["failureType"] = error.GetType().Name;
                // Do not serialize options, settings or supplied credentials.
                result["failure"] = error is UnauthorizedAccessException ? "Device authentication was rejected." : error.Message;
                Console.WriteLine($"FAILED {device.MachineName}: {result["failure"]}");
            }
            result["finishedAtUtc"] = DateTimeOffset.UtcNow;
            results.Add(result);
            Program.Save(Path.Combine(output, device.DeviceId + ".json"), result);
            Program.Save(Path.Combine(output, "updates.json"), results);
        }
        return results.All(item => ((Dictionary<string, object?>)item)["success"] is true) ? 0 : 1;
    }

    private static async Task ApplyAsync(RelayConnectionOptions target, RelayOnlineDevice device,
        List<(string Source, string Secret)> credentials, string package, string expectedBuild,
        Dictionary<string, object?> result)
    {
        string? authenticatedKey = null;
        RemoteViewerClient? connected = null;
        RemoteDeviceDescriptor? before = null;
        foreach (var credential in credentials.DistinctBy(item => item.Secret).Take(4))
        {
            var attempt = new RemoteViewerClient();
            try
            {
                before = await ConnectAndIdentifyAsync(attempt, target, credential.Secret);
                authenticatedKey = credential.Secret;
                connected = attempt;
                result["credentialSource"] = credential.Source;
                break;
            }
            catch (UnauthorizedAccessException)
            {
                await attempt.DisconnectAsync();
                attempt.Dispose();
                await Task.Delay(1000);
            }
            catch
            {
                await attempt.DisconnectAsync();
                attempt.Dispose();
                throw;
            }
        }
        if (connected is null || before is null || authenticatedKey is null) throw new UnauthorizedAccessException();
        using (connected)
        {
            try
            {
                result["authenticatedBeforeBuild"] = before.BuildStamp;
                result["authenticatedDeviceId"] = before.DeviceId;
                if (string.CompareOrdinal(before.BuildStamp, expectedBuild) > 0)
                    throw new InvalidOperationException("Remote build is newer than the candidate; downgrade refused.");
                if (before.BuildStamp == expectedBuild)
                {
                    result["alreadyCurrent"] = true;
                    Console.WriteLine($"CURRENT {device.MachineName}: {before.BuildStamp}");
                    return;
                }
                if (!before.Capabilities.HasFlag(RemoteDeviceCapabilities.RemoteUpdate))
                    throw new InvalidOperationException("Device does not support authenticated self-update.");
                var accepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                var disconnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                connected.ConnectedChanged += value => { if (!value) disconnected.TrySetResult(true); };
                connected.FileTransferStatusReceived += (success, message) =>
                {
                    Console.WriteLine($"UPDATE {device.MachineName}: {message}");
                    if (!success) failed.TrySetResult(message);
                    if (success && message.Contains("正在应用远程更新", StringComparison.Ordinal)) accepted.TrySetResult(true);
                };
                Console.WriteLine($"SENDING {device.MachineName}: {before.BuildStamp} -> {expectedBuild}");
                Task sending = connected.SendRemoteUpdateAsync(package);
                Task first = await Task.WhenAny(sending, failed.Task, Task.Delay(TimeSpan.FromMinutes(10)));
                if (first != sending)
                {
                    await connected.DisconnectAsync();
                    try { await sending; } catch { /* teardown terminates the owned transfer */ }
                    if (failed.Task.IsCompletedSuccessfully) throw new IOException(failed.Task.Result);
                    throw new TimeoutException("Bounded package transfer deadline exceeded.");
                }
                await sending;
                result["packageSentAtUtc"] = DateTimeOffset.UtcNow;
                await Task.WhenAny(accepted.Task, failed.Task, disconnected.Task, Task.Delay(TimeSpan.FromSeconds(50)));
                if (failed.Task.IsCompletedSuccessfully) throw new IOException(failed.Task.Result);
                result["remoteAcceptedUpdate"] = accepted.Task.IsCompletedSuccessfully;
            }
            finally { await connected.DisconnectAsync(); }
        }

        Console.WriteLine($"WAITING {device.MachineName}: restart and relay re-registration");
        var watch = Stopwatch.StartNew();
        string? lastBuild = null;
        while (watch.Elapsed < TimeSpan.FromMinutes(3))
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                IReadOnlyList<RelayOnlineDevice> online = await RelayTunnelClient.ListDevicesAsync(target, deadline.Token);
                RelayOnlineDevice? current = online.SingleOrDefault(item => item.DeviceId == target.DeviceId);
                lastBuild = current?.BuildStamp;
                if (current?.BuildStamp != expectedBuild) continue;
                using var verifier = new RemoteViewerClient();
                long frames = 0;
                TimeSpan? lastRtt = null;
                verifier.FrameReceived += _ => Interlocked.Increment(ref frames);
                verifier.RoundTripUpdated += value => lastRtt = value;
                try
                {
                    RemoteDeviceDescriptor after = await ConnectAndIdentifyAsync(verifier, target, authenticatedKey);
                    if (after.BuildStamp != expectedBuild) throw new IOException("Authenticated endpoint has not applied the expected build.");
                    await Task.Delay(TimeSpan.FromSeconds(8));
                    if (!verifier.IsConnected) throw new IOException("Updated endpoint did not retain the verification connection.");
                    result["afterBuild"] = after.BuildStamp;
                    result["afterDeviceId"] = after.DeviceId;
                    result["verificationFrames"] = Interlocked.Read(ref frames);
                    result["verificationRttMs"] = lastRtt?.TotalMilliseconds;
                    result["reconnectedThroughRelay"] = true;
                    result["verifiedAtUtc"] = DateTimeOffset.UtcNow;
                    Console.WriteLine($"VERIFIED {device.MachineName}: {after.BuildStamp}, relay reconnect, frames={frames}");
                    return;
                }
                finally { await verifier.DisconnectAsync(); }
            }
            catch (Exception error) when (error is IOException or TimeoutException or System.Net.Sockets.SocketException or OperationCanceledException)
            {
                Console.WriteLine($"WAITING {device.MachineName}: {error.GetType().Name}; retrying verification only");
            }
        }
        throw new TimeoutException($"No verified updated endpoint after restart; last directory build: {lastBuild ?? "offline"}. No second update was sent.");
    }

    private static async Task<RemoteDeviceDescriptor> ConnectAndIdentifyAsync(RemoteViewerClient client,
        RelayConnectionOptions target, string secret)
    {
        var identity = new TaskCompletionSource<RemoteDeviceDescriptor>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe(RemoteDeviceDescriptor value)
        {
            if (value.DeviceId is not null) identity.TrySetResult(value);
        }
        client.DeviceInfoReceived += Observe;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await client.ConnectViaRelayAsync(target, secret, ViewerVideoMode.Automatic, deadline.Token);
            RemoteDeviceDescriptor descriptor = await identity.Task.WaitAsync(TimeSpan.FromSeconds(20), deadline.Token);
            if (descriptor.Platform != RemoteDevicePlatforms.Windows || !RemoteDeviceIdentity.Same(descriptor.DeviceId, target.DeviceId))
                throw new InvalidOperationException("Authenticated platform/device identity differs from the selected Windows node.");
            if (string.IsNullOrWhiteSpace(descriptor.BuildStamp)) throw new InvalidOperationException("Authenticated build identity is unavailable.");
            return descriptor;
        }
        finally { client.DeviceInfoReceived -= Observe; }
    }

    private static List<(string Source, string Secret)> Credentials(RemoteDeskSettings settings,
        RelayConnectionOptions options, RelayOnlineDevice device)
    {
        var result = new List<(string Source, string Secret)>();
        void Add(string source, string? protectedSecret)
        {
            string value = AppSettingsService.UnprotectSecret(protectedSecret);
            if (!string.IsNullOrWhiteSpace(value) && !result.Any(item => item.Secret == value)) result.Add((source, value));
        }
        Add("saved per-device relay key", RelayDeviceKeys.Find(settings.Relay.DeviceKeys, options with { DeviceId = device.DeviceId }));
        if (string.Equals(device.DeviceId, settings.Relay.DeviceId, StringComparison.OrdinalIgnoreCase))
            Add("local host key", settings.Host.ProtectedPassword);
        foreach (SavedRemoteDevice recent in settings.Viewer.RecentDevices)
        {
            bool identityMatches = string.Equals(recent.DeviceId, device.DeviceId, StringComparison.OrdinalIgnoreCase);
            bool addressAndNameMatch = recent.Address is not null && device.DirectAddresses.Contains(recent.Address) &&
                (recent.MachineName == device.MachineName || recent.MachineName == device.OriginalMachineName);
            if (identityMatches || addressAndNameMatch) Add("saved matching direct-connection key", recent.ProtectedPassword);
        }
        return result;
    }
}
