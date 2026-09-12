using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;

namespace RemoteDesk;

// Installs only the existing hash-pinned companion, in a per-user versioned
// directory. No PATH/registry, system Python, drivers or machine-wide packages.
internal static class WindowsFfmpegDependency
{
    internal const string Version = "8.1.2";
    internal const string ExecutableSha256 = "1326dde4c84ff1f96fe6b8916c5bed29e163e9b5dccf995f6f3db069d143ec5e";
    internal const string ArchiveSha256 = "db580001caa24ac104c8cb856cd113a87b0a443f7bdf47d8c12b1d740584a2ec";
    internal const long MaximumArchiveBytes = 160L * 1024 * 1024;
    private const string InstallerResource = "RemoteDesk.InstallFfmpeg.ps1";
    private static readonly object Gate = new();
    private static Task? _installation;
    private static long _retryAfter;
    private static string _status = string.Empty;
    internal static string Status => Volatile.Read(ref _status);

    internal static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RemoteDesk", "Dependencies", "ffmpeg-" + Version);

    internal static IEnumerable<string> CompanionPaths(string? localAppData = null)
    {
        string root = localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root)) yield break;
        yield return Path.Combine(root, "RemoteDesk", "Dependencies", "ffmpeg-" + Version, "ffmpeg.exe");
        // Scheduled/admin launches can inherit an old PATH even though WinGet
        // already installed the user's encoder. Discovery must not require a reboot.
        yield return Path.Combine(root, "Microsoft", "WinGet", "Links", "ffmpeg.exe");
    }

    internal static bool NeedsGraphicsCaptureUpgrade(string? failureDetail) =>
        failureDetail?.Contains("does not expose the gfxcapture source filter", StringComparison.OrdinalIgnoreCase) == true;

    internal static void StartIfMissing(Action<string> log, bool requireGraphicsCapture = false)
    {
        if (!OperatingSystem.IsWindows() || (!requireGraphicsCapture && FfmpegH264Decoder.IsAvailable)) return;
        lock (Gate)
        {
            if (_installation is { IsCompleted: false } || Environment.TickCount64 < _retryAfter) return;
            _retryAfter = Environment.TickCount64 + (long)TimeSpan.FromMinutes(5).TotalMilliseconds;
            _installation = Task.Run(async () =>
            {
                void Report(string message)
                {
                    Volatile.Write(ref _status, message.Length > 300 ? message[..300] : message);
                    try { log(message); } catch { /* Optional UI logging. */ }
                }
                try
                {
                    // Recheck after a concurrent manual installation, before any download.
                    if (!requireGraphicsCapture && FfmpegH264Decoder.ResolveAvailableFfmpegPaths().Count != 0)
                    {
                        FfmpegH264Decoder.RefreshAvailablePaths();
                        Report("已发现新安装的 FFmpeg，重新启用硬件编码探测。");
                        return;
                    }
                    Report("编码组件缺失或不支持新采集接口，正在后台安装固定版本 FFmpeg（约 104 MB）；下载期间保留现有连接。");
                    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                    await InstallAsync(InstallDirectory, Report, deadline.Token).ConfigureAwait(false);
                    FfmpegH264Decoder.RefreshAvailablePaths();
                    Report("FFmpeg 已安装并通过 SHA-256 校验；JPEG 会自动重探 H.264，已有 H.264 会话保持运行。");
                }
                catch (Exception error)
                {
                    Report("硬件编码组件暂未安装成功，保持现有连接；稍后会自动重试。" +
                           (error is OperationCanceledException ? "下载或安装超时。" : error.Message));
                }
                finally
                {
                    lock (Gate) _retryAfter = Environment.TickCount64 + (long)TimeSpan.FromMinutes(5).TotalMilliseconds;
                }
            });
        }
    }

    internal static async Task InstallAsync(string destination, Action<string> log, CancellationToken token,
        string? verifiedArchive = null)
    {
        token.ThrowIfCancellationRequested();
        destination = Path.GetFullPath(destination);
        string parent = Path.GetDirectoryName(destination) ?? throw new IOException("依赖目录无效。");
        Directory.CreateDirectory(parent);
        // Coordinate two RemoteDesk processes sharing the same user profile.
        await using var lease = new FileStream(destination + ".install.lock", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous);
        if (HasVerifiedExecutable(destination)) return;
        string staging = Path.Combine(parent, ".ffmpeg-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            if (verifiedArchive is null)
            {
                // .NET's HTTP client uses the current proxy configuration and
                // validates HTTPS normally. Do not let legacy Windows PowerShell
                // proxy/TLS discovery stall an unattended installation.
                verifiedArchive = Path.Combine(staging, "ffmpeg.zip");
                await DownloadArchiveAsync(verifiedArchive, log, token).ConfigureAwait(false);
            }
            string script = Path.Combine(staging, "Install-RemoteDeskFfmpeg.ps1");
            using (Stream source = Assembly.GetExecutingAssembly().GetManifestResourceStream(InstallerResource)
                   ?? throw new IOException("内置编码组件安装器缺失。"))
            await using (var output = File.Create(script))
                await source.CopyToAsync(output, token).ConfigureAwait(false);
            string package = Path.Combine(staging, "package");
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            // Only this process executes our embedded installer; machine/user
            // execution policy is not changed. Archive and EXE hashes are pinned.
            foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                         "-File", script, "-DestinationDirectory", package })
                start.ArgumentList.Add(argument);
            if (verifiedArchive is not null)
            {
                start.ArgumentList.Add("-ArchivePath");
                start.ArgumentList.Add(Path.GetFullPath(verifiedArchive));
            }
            using var process = Process.Start(start) ?? throw new IOException("无法启动编码组件安装器。");
            using var job = WindowsKillOnCloseJob.TryCreateAndAssign(process);
            // Always drain both pipes, including a cancelled/timed-out child.
            Task<string> stdout = ReadBoundedOutputAsync(process.StandardOutput);
            Task<string> stderr = ReadBoundedOutputAsync(process.StandardError);
            try { await process.WaitForExitAsync(token).ConfigureAwait(false); }
            catch
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync().ConfigureAwait(false);
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                throw;
            }
            string errors = await stderr.ConfigureAwait(false);
            await stdout.ConfigureAwait(false);
            if (process.ExitCode != 0 || !HasVerifiedExecutable(package))
                throw new IOException("下载、哈希或编码能力校验失败。" + (errors.Length > 400 ? errors[..400] : errors));
            if (Directory.Exists(destination))
            {
                // Preserve a damaged/older managed installation for recovery.
                string backup = destination + ".previous-" + Guid.NewGuid().ToString("N");
                Directory.Move(destination, backup);
                log("已保留原编码组件目录：" + Path.GetFileName(backup));
            }
            Directory.Move(package, destination);
        }
        finally
        {
            // Delete only this call's generated staging child, never the parent
            // dependencies folder, a caller-supplied archive or installed files.
            string resolved = Path.GetFullPath(staging);
            if (Path.GetDirectoryName(resolved) == parent && Path.GetFileName(resolved).StartsWith(".ffmpeg-install-", StringComparison.Ordinal))
            {
                try { Directory.Delete(resolved, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static async Task<string> ReadBoundedOutputAsync(StreamReader reader)
    {
        var tail = new System.Text.StringBuilder();
        char[] buffer = new char[1024];
        int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0)
        {
            tail.Append(buffer, 0, count);
            if (tail.Length > 4096) tail.Remove(0, tail.Length - 4096);
        }
        return tail.ToString();
    }

    internal static async Task DownloadArchiveAsync(string destination, Action<string> log, CancellationToken token)
    {
        using var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15) };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        string[] sources = [
            "https://github.com/GyanD/codexffmpeg/releases/download/8.1.2/ffmpeg-8.1.2-essentials_build.zip",
            "https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-8.1.2-essentials_build.zip"
        ];
        Exception? failure = null;
        foreach (string source in sources)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
                idle.CancelAfter(TimeSpan.FromSeconds(30));
                using var response = await client.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, idle.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is long length && (length <= 0 || length > MaximumArchiveBytes))
                    throw new IOException("编码组件下载长度无效。");
                await using var input = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
                await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write,
                    FileShare.None, 65536, FileOptions.Asynchronous);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[65536];
                long received = 0, nextProgress = 10 * 1024 * 1024;
                int count;
                while ((count = await input.ReadAsync(buffer, idle.Token).ConfigureAwait(false)) != 0)
                {
                    received += count;
                    if (received > MaximumArchiveBytes) throw new IOException("编码组件下载超过大小限制。");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), idle.Token).ConfigureAwait(false);
                    idle.CancelAfter(TimeSpan.FromSeconds(30));
                    if (received >= nextProgress)
                    {
                        log($"正在下载硬件编码组件：{received / 1048576} MB / 约 104 MB。");
                        nextProgress = received + 10 * 1024 * 1024;
                    }
                }
                if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(ArchiveSha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("编码组件压缩包 SHA-256 不匹配，已拒绝安装。");
                return;
            }
            catch (Exception error) when (!token.IsCancellationRequested && error is HttpRequestException or IOException or OperationCanceledException)
            {
                failure = error;
                log("编码组件下载源暂不可用，尝试同一版本的备用源；仍须通过相同哈希校验。");
            }
        }
        throw new IOException("编码组件下载失败，请检查网络或使用随包离线安装器。", failure);
    }

    internal static bool HasVerifiedExecutable(string directory)
    {
        try
        {
            using var stream = File.OpenRead(Path.Combine(directory, "ffmpeg.exe"));
            return Convert.ToHexString(SHA256.HashData(stream)).Equals(ExecutableSha256, StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(directory, "FFMPEG-LICENSE.txt"))
                && File.Exists(Path.Combine(directory, "FFMPEG-README.txt"));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
