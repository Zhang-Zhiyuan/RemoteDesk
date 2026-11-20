using System.Text.Json;

namespace RemoteDesk;

public sealed partial class MainForm
{
    private readonly CheckBox _fleetAutoUpdate = new() { Text = "本机空闲时自动安装中继上的新版", AutoSize = true };
    private Button _fleetCheck = null!;
    private bool _fleetWorking;
    private DateTime _nextFleetAttempt;

    private bool CanApplyFleetUpdate(RelayConnectionOptions options) =>
        !_isClosing && !IsDisposed && _settings.Relay.AutoUpdate && _settings.Relay.RegisterThisDevice &&
        string.Equals(_settings.Relay.TlsCertificateSha256, options.TlsCertificateSha256, StringComparison.OrdinalIgnoreCase) &&
        (_hostServer.IsRunning || Volatile.Read(ref _sshHostOptions).IsAvailable) && !_hostServer.HasConnectedClients &&
        !_relayHostConnector.HasActiveBridges && !_viewerClient.IsConnected && !_viewerActionInProgress &&
        !IsViewerReconnecting() && _additionalScreens.Count == 0 && _sshOpening is null &&
        !_sshTerminals.Any(terminal => terminal.IsActive) && !_relayOperationInProgress;

    private async Task HandleFleetOfferAsync(RelayConnectionOptions options, JsonElement offer, CancellationToken token)
    {
        if (_fleetWorking || token.IsCancellationRequested || DateTime.UtcNow < _nextFleetAttempt || !CanApplyFleetUpdate(options)) return;
        _fleetWorking = true;
        string? file = null;
        try
        {
            using var publisher = FleetUpdate.LoadPublisher();
            var update = FleetUpdate.Verify(offer, publisher);
            if (update.Version <= FleetUpdate.InstalledVersion ||
                RemoteDeskBuildInfo.CompareBuildStamps(update.Build, RemoteDeskBuildInfo.BuildStamp) is not > 0) return;
            if (!await Task.Run(() => RemoteUpdater.CanApplyRemoteUpdate, token)) return;
            string target = RemoteUpdater.GetCurrentPackagePath();
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteDesk", "FleetUpdate");
            Directory.CreateDirectory(directory);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("更新目录指向其他位置。");
            string retry = Path.Combine(directory, "attempt");
            var previous = new FileInfo(retry);
            if (previous.Exists && (previous.Attributes & FileAttributes.ReparsePoint) == 0 && previous.Length == 64 &&
                previous.LastWriteTimeUtc.AddHours(1) > DateTime.UtcNow && File.ReadAllText(retry) == update.Sha256) return;
            SetRelayStatus($"正在下载 RemoteDesk {update.Version}，空闲时安装。", MutedTextColor);
            foreach (var orphan in new DirectoryInfo(directory).EnumerateFiles("*.exe"))
                if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(orphan.Name), "N", out _) &&
                    (orphan.Attributes & FileAttributes.ReparsePoint) == 0 && orphan.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-1))
                    RemoteUpdater.DeleteRejectedReceivedPackage(orphan.FullName);
            file = Path.Combine(directory, $"{Guid.NewGuid():N}.exe");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            await update.DownloadAsync(options, file, timeout.Token);
            if (token.IsCancellationRequested || !CanApplyFleetUpdate(options)) return;
            int port = _hostServer.IsRunning ? _hostServer.ListeningPort : 0;
            string marker = Path.Combine(directory, $"{Guid.NewGuid():N}.tmp");
            try
            {
                using (var writer = new StreamWriter(new FileStream(marker, FileMode.CreateNew))) writer.Write(update.Sha256);
                File.Move(marker, retry, overwrite: true);
            }
            finally { File.Delete(marker); }
            bool Idle() => !token.IsCancellationRequested && !IsDisposed &&
                (bool)Invoke(new Func<bool>(() => CanApplyFleetUpdate(options)));
            try
            {
                await Task.Run(() => RemoteUpdater.ScheduleApplyAndRestart(file, target, Environment.ProcessId, port,
                    msg => _diagnosticLog.Append("UPDATE", msg), Environment.Exit, update.Sha256, update.Build, Idle), token);
            }
            catch (OperationCanceledException) { File.Delete(retry); throw; }
            file = null; // The updater now owns this file, including rollback and cleanup.
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _nextFleetAttempt = DateTime.UtcNow.AddMinutes(2);
            if (!_isClosing && !IsDisposed) SetRelayStatus($"自动更新稍后重试：{ex.Message}", DangerColor);
        }
        finally
        {
            if (file is not null) RemoteUpdater.DeleteRejectedReceivedPackage(file);
            _fleetWorking = false;
        }
    }

    private async Task CheckFleetAsync()
    {
        _fleetCheck.Enabled = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_relayOperationCancellation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            var options = CreateRelayOptions(_settings.Relay.DeviceId);
            var (client, stream) = await RelayTls.ConnectAsync(options, timeout.Token);
            using (client)
            using (stream)
            {
                await RelayTls.WriteJsonAsync(stream, new { version = 1, role = "update-check", token = options.AccessToken }, timeout.Token);
                using var reply = await RelayTls.ReadJsonAsync(stream, timeout.Token);
                RelayTls.EnsureSuccess(reply.RootElement);
                var versions = reply.RootElement.GetProperty("versions").EnumerateObject()
                    .Select(item => item.Value.GetString()).Distinct().ToArray();
                SetRelayStatus(versions.Length == 0 ? "中继还没有已发布的更新包。" :
                    $"中继版本 {string.Join(" / ", versions)}，已通知 {reply.RootElement.GetProperty("notified").GetInt32()} 台可升级设备。忙碌设备将在空闲时更新。",
                    MutedTextColor);
            }
        }
        catch (Exception ex) { if (!_isClosing) SetRelayStatus($"无法检查更新，请确认中继已升级：{ex.Message}", DangerColor); }
        finally { if (!_isClosing && !IsDisposed) _fleetCheck.Enabled = IsRelayConfigured(); }
    }
}
