using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class FfmpegH264DecoderTests
{
    [Fact]
    public async Task BackendRaceInputOutlivesBorrowedViewerFrame()
    {
        byte[] pooledViewerBuffer =
            [0xEE, 1, 2, 3, 4, 0xEF];
        byte[] stableRaceInput =
            FfmpegH264Decoder.CopyBackendRaceInput(
                pooledViewerBuffer.AsMemory(1, 4));
        var releaseLoser =
            new TaskCompletionSource(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        Task<byte[]> delayedLoser = Task.Run(
            async () =>
            {
                await releaseLoser.Task;
                return stableRaceInput.ToArray();
            });

        pooledViewerBuffer.AsSpan(1, 4).Clear();
        releaseLoser.TrySetResult();

        Assert.Equal(
            new byte[] { 1, 2, 3, 4 },
            await delayedLoser);
        Assert.Equal(
            new byte[] { 0, 0, 0, 0 },
            pooledViewerBuffer.AsSpan(1, 4).ToArray());
    }

    [Fact]
    public void FfmpegResolverSharesVerifiedAppLocalBinaryWithViewerAndHost()
    {
        string appDirectory = Path.GetFullPath(
            Path.Combine("test-output", "resolver-app"));
        string pathDirectory = Path.GetFullPath(
            Path.Combine("test-output", "resolver-path"));
        string appLocal = Path.Combine(
            appDirectory,
            "ffmpeg.exe");
        string pathCandidate = Path.Combine(
            pathDirectory,
            "ffmpeg.exe");
        var existing = new HashSet<string>(
            [appLocal, pathCandidate],
            StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<string> resolved =
            FfmpegH264Decoder.ResolveAvailableFfmpegPaths(
                appDirectory,
                pathDirectory,
                existing.Contains,
                _ => true);

        Assert.Equal(
            [appLocal, pathCandidate],
            resolved);

        IReadOnlyList<string> brokenSidecar =
            FfmpegH264Decoder.ResolveAvailableFfmpegPaths(
                appDirectory,
                pathDirectory,
                existing.Contains,
                path => !string.Equals(
                    path,
                    appLocal,
                    StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            [pathCandidate],
            brokenSidecar);
    }

    [Fact]
    public void BuildDecoderArgumentsUsesBgraRawThrough1080p()
    {
        string arguments = FfmpegH264Decoder.BuildDecoderArguments(
            new Size(1920, 1080));

        Assert.DoesNotContain("-fflags nobuffer", arguments);
        Assert.Contains("-flags low_delay", arguments);
        Assert.Contains("-probesize 32", arguments);
        Assert.Contains(
            $"-max_alloc {FfmpegH264Decoder.MaximumFfmpegAllocationBytes}",
            arguments);
        Assert.Contains("-fpsprobesize 0", arguments);
        Assert.Contains("-thread_type slice", arguments);
        Assert.Contains("-threads 0", arguments);
        Assert.Contains(
            "-vf scale=1920:1080:flags=fast_bilinear,format=bgra",
            arguments);
        Assert.Contains("-f rawvideo", arguments);
        Assert.Contains("-c:v rawvideo -threads 1", arguments);
        Assert.Contains("-vsync 0", arguments);
        Assert.Contains("-pix_fmt bgra", arguments);
        Assert.Contains("-flush_packets 1", arguments);
        Assert.DoesNotContain("-c:v mjpeg", arguments);
    }

    [Fact]
    public void BuildDecoderArgumentsRejectsUnsafeFrameDimensions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FfmpegH264Decoder.BuildDecoderArguments(
                new Size(8192, 8192)));
    }

    [Fact]
    public void BuildDecoderArgumentsKeepsMjpegAbove1080p()
    {
        string arguments = FfmpegH264Decoder.BuildDecoderArguments(
            new Size(1920, 1081));

        Assert.Contains("-c:v mjpeg -threads 1", arguments);
        Assert.Contains("-pix_fmt yuvj444p", arguments);
        Assert.Contains("-q:v 2", arguments);
        Assert.DoesNotContain("-pix_fmt yuvj420p", arguments);
        Assert.DoesNotContain("-f rawvideo", arguments);
        Assert.DoesNotContain("format=bgra", arguments);
    }

    [Theory]
    [InlineData(
        (int)FfmpegH264Backend.Cuda,
        "-hwaccel cuda",
        "-hwaccel_output_format cuda")]
    [InlineData(
        (int)FfmpegH264Backend.D3D11Va,
        "-hwaccel d3d11va",
        "-hwaccel_output_format d3d11")]
    [InlineData(
        (int)FfmpegH264Backend.D3D12Va,
        "-hwaccel d3d12va",
        "-hwaccel_output_format d3d12")]
    [InlineData(
        (int)FfmpegH264Backend.Dxva2,
        "-hwaccel dxva2",
        "-hwaccel_output_format dxva2_vld")]
    [InlineData(
        (int)FfmpegH264Backend.Qsv,
        "-hwaccel qsv",
        "-hwaccel_output_format qsv")]
    public void HardwareSurfaceArgumentsDownloadBeforeBgraConversion(
        int backendValue,
        string accelerationArgument,
        string surfaceArgument)
    {
        string arguments =
            FfmpegH264Decoder.BuildDecoderArguments(
                new Size(1280, 720),
                (FfmpegH264Backend)backendValue);

        Assert.Contains(accelerationArgument, arguments);
        Assert.Contains(surfaceArgument, arguments);
        Assert.Contains(
            "-vf hwdownload,format=nv12," +
                "scale=1280:720:flags=fast_bilinear," +
                "format=bgra",
            arguments);
        Assert.DoesNotContain("-c:v h264_cuvid", arguments);
    }

    [Fact]
    public void QsvAndAmfCandidatesUseExplicitLowLatencyDecoders()
    {
        string qsv = FfmpegH264Decoder.BuildDecoderArguments(
            new Size(1280, 720),
            FfmpegH264Backend.Qsv);
        string amf = FfmpegH264Decoder.BuildDecoderArguments(
            new Size(1280, 720),
            FfmpegH264Backend.Amf);

        Assert.Contains("-c:v h264_qsv", qsv);
        Assert.Contains("-async_depth 1", qsv);
        Assert.Contains("-c:v h264_amf", amf);
        Assert.Contains("-decoder_mode low_latency", amf);
        Assert.Contains("-lowlatency 1", amf);
        Assert.Contains("-timestamp_mode decode", amf);
        Assert.Contains(
            "-vf scale=1280:720:flags=fast_bilinear," +
                "format=bgra",
            amf);
        Assert.DoesNotContain("hwdownload", amf);
    }

    [Fact]
    public void HardwareMjpegArgumentsDownloadSurfaceBeforeEncoding()
    {
        string arguments =
            FfmpegH264Decoder.BuildDecoderArguments(
                new Size(3840, 2160),
                FfmpegH264Backend.Cuda);

        Assert.Contains(
            "-vf hwdownload,format=nv12",
            arguments);
        Assert.Contains("-c:v mjpeg -threads 1", arguments);
        Assert.Contains("-pix_fmt yuvj444p", arguments);
        Assert.Contains("-q:v 2", arguments);
        Assert.DoesNotContain("format=bgra", arguments);
    }

    [Fact]
    public async Task RealFfmpegYuvj444pMjpegOutputLoadsAsDetachedBitmapWhenAvailable()
    {
        string? ffmpegPath = FfmpegH264Decoder.AvailablePath;
        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[]
                 {
                     "-hide_banner",
                     "-loglevel", "error",
                     "-f", "lavfi",
                     "-i", "testsrc2=size=64x64:rate=1",
                     "-frames:v", "1",
                     "-c:v", "mjpeg",
                     "-pix_fmt", "yuvj444p",
                     "-q:v", "2",
                     "-f", "image2pipe",
                     "pipe:1"
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process =
            Process.Start(startInfo) ??
            throw new InvalidOperationException("ffmpeg did not start.");
        using var encoded = new MemoryStream();
        Task copyOutput =
            process.StandardOutput.BaseStream.CopyToAsync(encoded);
        Task<string> readError = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(
            copyOutput,
            process.WaitForExitAsync(),
            readError);
        string error = await readError;

        Assert.True(
            process.ExitCode == 0,
            $"ffmpeg exited with {process.ExitCode}: {error}");
        Assert.NotEmpty(encoded.ToArray());
        using Bitmap bitmap =
            DetachedBitmapLoader.Load(
                encoded.GetBuffer(),
                offset: 0,
                checked((int)encoded.Length));
        Assert.Equal(new Size(64, 64), bitmap.Size);
    }

    [Fact]
    public void HardwareRaceListContainsOnlyPerAccessUnitCandidates()
    {
        Assert.Equal(
            [
                FfmpegH264Backend.Cuda,
                FfmpegH264Backend.D3D11Va,
                FfmpegH264Backend.D3D12Va,
                FfmpegH264Backend.Dxva2,
                FfmpegH264Backend.Qsv,
                FfmpegH264Backend.Amf
            ],
            FfmpegH264Decoder.HardwareRaceBackends);
        Assert.Equal(
            "Software",
            FfmpegH264Decoder.GetBackendName(
                FfmpegH264Backend.Software));
        Assert.False(FfmpegH264Decoder.IsHardwareBackend(
            FfmpegH264Backend.Software));
        Assert.All(
            FfmpegH264Decoder.HardwareRaceBackends,
            backend =>
            {
                Assert.True(
                    FfmpegH264Decoder.IsHardwareBackend(
                        backend));
                Assert.NotEqual(
                    "Software",
                    FfmpegH264Decoder.GetBackendName(
                        backend));
            });
    }

    [Fact]
    public void WinningBackendCacheIsScopedByPathAndFrameSize()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"ffmpeg-cache-test-{Guid.NewGuid():N}.exe");
        FfmpegH264BackendCacheKey key =
            FfmpegH264Decoder.CreateBackendCacheKey(
                path,
                new Size(1920, 1080));
        FfmpegH264BackendCacheKey otherSize =
            FfmpegH264Decoder.CreateBackendCacheKey(
                path,
                new Size(1280, 720));
        try
        {
            FfmpegH264Decoder.RememberWinningBackend(
                key,
                FfmpegH264Backend.Cuda);

            Assert.True(
                FfmpegH264Decoder.TryGetCachedBackend(
                    key,
                    out FfmpegH264Backend cached));
            Assert.Equal(FfmpegH264Backend.Cuda, cached);
            Assert.False(
                FfmpegH264Decoder.TryGetCachedBackend(
                    otherSize,
                    out _));
            Assert.False(
                FfmpegH264Decoder.ForgetWinningBackend(
                    key,
                    FfmpegH264Backend.Dxva2));
            Assert.True(
                FfmpegH264Decoder.TryGetCachedBackend(
                    key,
                    out _));
        }
        finally
        {
            Assert.True(
                FfmpegH264Decoder.ForgetWinningBackend(
                    key,
                    FfmpegH264Backend.Cuda));
        }
    }

    [Fact]
    public async Task FirstSuccessfulFrameWinsWithoutWaitingForSlowCandidate()
    {
        var slow = new TaskCompletionSource<FfmpegH264DecodeResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var winningBitmap = new Bitmap(2, 1);
        try
        {
            Task<FfmpegH264RaceWinner?> selection =
                FfmpegH264Decoder.SelectFirstSuccessfulAsync(
                [
                    slow.Task,
                    Task.FromResult(
                        FfmpegH264DecodeResult.Failed(51)),
                    Task.FromException<FfmpegH264DecodeResult>(
                        new InvalidOperationException(
                            "candidate failed")),
                    Task.FromResult(
                        FfmpegH264DecodeResult.Frame(
                            51,
                            winningBitmap))
                ]);

            FfmpegH264RaceWinner? winner =
                await selection.WaitAsync(
                    TimeSpan.FromSeconds(1));

            Assert.NotNull(winner);
            Assert.Equal(3, winner.Value.CandidateIndex);
            Assert.Same(
                winningBitmap,
                winner.Value.DecodeResult.Bitmap);
            Assert.False(slow.Task.IsCompleted);
        }
        finally
        {
            slow.TrySetResult(
                FfmpegH264DecodeResult.Failed(51));
            winningBitmap.Dispose();
        }
    }

    [Fact]
    public async Task HardwareFailuresStillUseSameAccessUnitSoftwareFrame()
    {
        var software =
            new TaskCompletionSource<FfmpegH264DecodeResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        Task<FfmpegH264RaceWinner?> selection =
            FfmpegH264Decoder.SelectFirstSuccessfulAsync(
            [
                software.Task,
                Task.FromResult(
                    FfmpegH264DecodeResult.Failed(61)),
                Task.FromResult(
                    FfmpegH264DecodeResult.Failed(61))
            ]);
        await Task.Yield();
        Assert.False(selection.IsCompleted);

        var softwareBitmap = new Bitmap(1, 2);
        software.SetResult(
            FfmpegH264DecodeResult.Frame(
                61,
                softwareBitmap));
        try
        {
            FfmpegH264RaceWinner? winner =
                await selection.WaitAsync(
                    TimeSpan.FromSeconds(1));

            Assert.NotNull(winner);
            Assert.Equal(0, winner.Value.CandidateIndex);
            Assert.Equal(
                61,
                winner.Value.DecodeResult.SubmissionId);
            Assert.Same(
                softwareBitmap,
                winner.Value.DecodeResult.Bitmap);
        }
        finally
        {
            softwareBitmap.Dispose();
        }
    }

    [Theory]
    [InlineData(1920, 1080, 0)]
    [InlineData(3840, 540, 0)]
    [InlineData(1920, 1081, 1)]
    [InlineData(3840, 2160, 1)]
    public void OutputModeUsesBoundedPixelThreshold(
        int width,
        int height,
        int expectedValue)
    {
        Assert.Equal(
            (FfmpegH264OutputMode)expectedValue,
            FfmpegH264Decoder.GetOutputMode(
                new Size(width, height)));
    }

    [Fact]
    public void DecodeWaitTimeoutBoundsOneExplicitAccessUnitTransaction()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(250), FfmpegH264Decoder.DecodeWaitTimeout);
    }

    [Fact]
    public async Task WriteAccessUnitAppendsAudBoundaryBeforeFlush()
    {
        await using var stream = new MemoryStream();

        await FfmpegH264Decoder.WriteAccessUnitAsync(
            stream,
            new byte[] { 0x00, 0x00, 0x01, 0x65, 0x88 },
            trimLeadingAud: false,
            CancellationToken.None);

        Assert.Equal(
            new byte[]
            {
                0x00, 0x00, 0x01, 0x65, 0x88,
                0x00, 0x00, 0x00, 0x01, 0x09, 0xF0
            },
            stream.ToArray());
    }

    [Fact]
    public async Task SubsequentAccessUnitRemovesDuplicateLeadingAud()
    {
        await using var stream = new MemoryStream();

        await FfmpegH264Decoder.WriteAccessUnitAsync(
            stream,
            new byte[]
            {
                0x00, 0x00, 0x00, 0x01, 0x09, 0xF0,
                0x00, 0x00, 0x01, 0x41, 0x88
            },
            trimLeadingAud: true,
            CancellationToken.None);

        Assert.Equal(
            new byte[]
            {
                0x00, 0x00, 0x01, 0x41, 0x88,
                0x00, 0x00, 0x00, 0x01, 0x09, 0xF0
            },
            stream.ToArray());
    }

    [Theory]
    [InlineData(new byte[] { 0x00, 0x00, 0x01, 0x09, 0xF0 })]
    [InlineData(new byte[] { 0x00, 0x00, 0x01, 0x65, 0x88 })]
    [InlineData(new byte[] { 0x12, 0x34, 0x56 })]
    public void TrimLeadingAudHandlesSingleOrNonAudNal(byte[] accessUnit)
    {
        ReadOnlyMemory<byte> trimmed =
            FfmpegH264Decoder.TrimLeadingAud(accessUnit);

        if (accessUnit.Length >= 5 && accessUnit[3] == 0x09)
        {
            Assert.True(trimmed.IsEmpty);
        }
        else
        {
            Assert.Equal(accessUnit, trimmed.ToArray());
        }
    }

    [Fact]
    public void DrainAvailableFrameSignalsClearsQueuedNotifications()
    {
        using var signal = new SemaphoreSlim(0);
        signal.Release(3);

        Assert.Equal(3, FfmpegH264Decoder.DrainAvailableFrameSignals(signal));
        Assert.False(signal.Wait(0));
    }

    [Fact]
    public void PartialJpegFrameLimitDropsOnlyOversizedDecoderOutput()
    {
        Assert.False(FfmpegH264Decoder.ShouldDropPartialJpegFrame(FfmpegH264Decoder.MaxDecodedJpegBytes));
        Assert.True(FfmpegH264Decoder.ShouldDropPartialJpegFrame(FfmpegH264Decoder.MaxDecodedJpegBytes + 1));
    }

    [Fact]
    public async Task RawFrameReaderFillsFragmentedFrameAndRejectsPartialEnd()
    {
        byte[] expected = [1, 2, 3, 4, 5];
        await using var fragmented =
            new OneByteReadStream(expected);
        byte[] destination = new byte[expected.Length];

        Assert.True(await FfmpegH264Decoder.ReadExactlyOrEndAsync(
            fragmented,
            destination,
            CancellationToken.None));
        Assert.Equal(expected, destination);
        Assert.False(await FfmpegH264Decoder.ReadExactlyOrEndAsync(
            fragmented,
            destination,
            CancellationToken.None));

        await using var partial =
            new OneByteReadStream([9, 8]);
        await Assert.ThrowsAsync<EndOfStreamException>(
            () => FfmpegH264Decoder.ReadExactlyOrEndAsync(
                partial,
                new byte[3],
                CancellationToken.None));
    }

    [Fact]
    public void BgraCopyCreatesDetachedBitmapWithExpectedPixels()
    {
        byte[] bgra =
        [
            0, 0, 255, 255,
            0, 255, 0, 255,
            255, 0, 0, 255,
            255, 255, 255, 255
        ];

        using Bitmap bitmap =
            FfmpegH264Decoder.CopyBgraToDetachedBitmap(
                bgra,
                bgra.Length,
                new Size(2, 2));
        Array.Clear(bgra);

        Assert.Equal(Color.FromArgb(255, 0, 0), bitmap.GetPixel(0, 0));
        Assert.Equal(Color.FromArgb(0, 255, 0), bitmap.GetPixel(1, 0));
        Assert.Equal(Color.FromArgb(0, 0, 255), bitmap.GetPixel(0, 1));
        Assert.Equal(Color.FromArgb(255, 255, 255), bitmap.GetPixel(1, 1));
    }

    [Fact]
    public void BgraCopyValidatesLengthAndSupportsSignedStrideOffsets()
    {
        Assert.Equal(
            8_294_400,
            FfmpegH264Decoder.CalculateRawFrameByteCount(
                new Size(1920, 1080)));
        Assert.Throws<ArgumentException>(
            () => FfmpegH264Decoder.CopyBgraToDetachedBitmap(
                new byte[15],
                byteCount: 15,
                new Size(2, 2)));

        var scan0 = new IntPtr(10_000);
        Assert.Equal(
            new IntPtr(10_032),
            FfmpegH264Decoder.GetBitmapRowPointer(
                scan0,
                row: 2,
                stride: 16));
        Assert.Equal(
            new IntPtr(9_968),
            FfmpegH264Decoder.GetBitmapRowPointer(
                scan0,
                row: 2,
                stride: -16));
    }

    [Fact]
    public async Task DecodeTransactionUsesOneCancellationTokenForWriteFlushAndRead()
    {
        using var gate = new SemaphoreSlim(1, 1);
        using var cancellation = new CancellationTokenSource();
        var steps = new List<string>();
        CancellationToken writeToken = default;
        CancellationToken flushToken = default;
        CancellationToken readToken = default;
        int desynchronizations = 0;

        string? result = await FfmpegH264Decoder.ExecuteDecodeTransactionAsync(
            gate,
            cancellation.Token,
            token =>
            {
                writeToken = token;
                steps.Add("write");
                return Task.CompletedTask;
            },
            token =>
            {
                flushToken = token;
                steps.Add("flush");
                return Task.CompletedTask;
            },
            token =>
            {
                readToken = token;
                steps.Add("read");
                return Task.FromResult("frame");
            },
            () => desynchronizations++);

        Assert.Equal("frame", result);
        Assert.Equal(["write", "flush", "read"], steps);
        Assert.Equal(cancellation.Token, writeToken);
        Assert.Equal(writeToken, flushToken);
        Assert.Equal(writeToken, readToken);
        Assert.Equal(0, desynchronizations);
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task DecodeTransactionCancellationInterruptsBlockedWriteAndDesynchronizes()
    {
        using var gate = new SemaphoreSlim(1, 1);
        using var cancellation = new CancellationTokenSource();
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool flushCalled = false;
        bool readCalled = false;
        int desynchronizations = 0;

        Task<byte[]?> transaction = FfmpegH264Decoder.ExecuteDecodeTransactionAsync(
            gate,
            cancellation.Token,
            async token =>
            {
                writeStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
            token =>
            {
                flushCalled = true;
                return Task.CompletedTask;
            },
            token =>
            {
                readCalled = true;
                return Task.FromResult(Array.Empty<byte>());
            },
            () => Interlocked.Increment(ref desynchronizations));

        await writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();
        byte[]? result = await transaction.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Null(result);
        Assert.False(flushCalled);
        Assert.False(readCalled);
        Assert.Equal(1, desynchronizations);
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task DecodeTransactionCancellationAlsoCoversWaitingForGate()
    {
        using var gate = new SemaphoreSlim(0, 1);
        using var cancellation = new CancellationTokenSource();
        bool writeCalled = false;
        int desynchronizations = 0;

        Task<string?> transaction = FfmpegH264Decoder.ExecuteDecodeTransactionAsync(
            gate,
            cancellation.Token,
            token =>
            {
                writeCalled = true;
                return Task.CompletedTask;
            },
            token => Task.CompletedTask,
            token => Task.FromResult("frame"),
            () => Interlocked.Increment(ref desynchronizations));

        Assert.False(transaction.IsCompleted);
        cancellation.Cancel();
        string? result = await transaction.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Null(result);
        Assert.False(writeCalled);
        Assert.Equal(1, desynchronizations);
        Assert.Equal(0, gate.CurrentCount);
    }

    [Fact]
    public async Task DecodeTransactionEndOfOutputDesynchronizesAndReleasesGate()
    {
        using var gate = new SemaphoreSlim(1, 1);
        int desynchronizations = 0;

        string? result = await FfmpegH264Decoder.ExecuteDecodeTransactionAsync(
            gate,
            CancellationToken.None,
            token => Task.CompletedTask,
            token => Task.CompletedTask,
            token => Task.FromException<string>(new EndOfStreamException()),
            () => Interlocked.Increment(ref desynchronizations));

        Assert.Null(result);
        Assert.Equal(1, desynchronizations);
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task DecodeTransactionGateRemainsHeldUntilMatchingReadCompletes()
    {
        using var gate = new SemaphoreSlim(1, 1);
        using var cancellation = new CancellationTokenSource();
        var firstReadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool secondWriteStarted = false;

        Task<string?> first = FfmpegH264Decoder.ExecuteDecodeTransactionAsync(
            gate,
            cancellation.Token,
            token => Task.CompletedTask,
            token => Task.CompletedTask,
            async token =>
            {
                firstReadStarted.SetResult();
                await releaseFirstRead.Task.WaitAsync(token);
                return "first";
            },
            () => { });

        await firstReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Task<string?> second = FfmpegH264Decoder.ExecuteDecodeTransactionAsync(
            gate,
            cancellation.Token,
            token =>
            {
                secondWriteStarted = true;
                return Task.CompletedTask;
            },
            token => Task.CompletedTask,
            token => Task.FromResult("second"),
            () => { });

        Assert.False(second.IsCompleted);
        Assert.False(secondWriteStarted);

        releaseFirstRead.SetResult();
        Assert.Equal("first", await first.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal("second", await second.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.True(secondWriteStarted);
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public void ExplicitAccessUnitBoundaryRequiresOutputForEverySubmission()
    {
        var pipeline = new FfmpegH264PipelineState();
        var published = new Bitmap(1, 1);

        Assert.True(pipeline.TrySubmit(101));

        Assert.True(pipeline.TryPublish(
            published,
            out long matchedId));
        Assert.Equal(101, matchedId);
        Assert.True(pipeline.TryTake(
            out FfmpegH264DecodedFrame decoded));
        Assert.Equal(101, decoded.SubmissionId);
        Assert.Same(published, decoded.Bitmap);
        Assert.Equal(0, pipeline.PendingInputCount);
        decoded.Bitmap.Dispose();
    }

    [Fact]
    public async Task SubmissionWithoutOutputTimesOutAsBridgeFailure()
    {
        var pipeline = new FfmpegH264PipelineState();
        using var gate = new SemaphoreSlim(1, 1);
        using var cancellation = new CancellationTokenSource();
        int desynchronizations = 0;

        Task<FfmpegH264DecodeResult?> transaction =
            FfmpegH264Decoder.ExecuteDecodeTransactionAsync(
                gate,
                cancellation.Token,
                token =>
                {
                    Assert.True(pipeline.TrySubmit(101));
                    return Task.CompletedTask;
                },
                token => Task.CompletedTask,
                async token =>
                {
                    await Task.Delay(
                        Timeout.InfiniteTimeSpan,
                        token);
                    return FfmpegH264DecodeResult.Failed(101);
                },
                () => Interlocked.Increment(
                    ref desynchronizations));

        Assert.False(transaction.IsCompleted);
        cancellation.Cancel();
        Assert.Null(await transaction.WaitAsync(
            TimeSpan.FromSeconds(1)));
        Assert.Equal(1, desynchronizations);
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public void PipelineKeepsBurstOutputsInSubmissionOrderWithoutLatestFrameOverwrite()
    {
        var pipeline = new FfmpegH264PipelineState();
        Bitmap[] bitmaps =
        [
            new Bitmap(1, 1),
            new Bitmap(2, 1),
            new Bitmap(3, 1)
        ];

        Assert.True(pipeline.TrySubmit(201));
        Assert.True(pipeline.TrySubmit(202));
        Assert.True(pipeline.TrySubmit(203));
        Assert.True(pipeline.TryPublish(bitmaps[0], out _));
        Assert.True(pipeline.TryPublish(bitmaps[1], out _));
        Assert.True(pipeline.TryPublish(bitmaps[2], out _));

        for (int index = 0; index < bitmaps.Length; index++)
        {
            Assert.True(pipeline.TryTake(
                out FfmpegH264DecodedFrame decoded));
            Assert.Equal(201 + index, decoded.SubmissionId);
            Assert.Same(bitmaps[index], decoded.Bitmap);
            decoded.Bitmap.Dispose();
        }

        Assert.False(pipeline.TryTake(out _));
    }

    [Fact]
    public void PipelineRejectsUnmatchedOrOverflowingDecoderOutput()
    {
        var pipeline = new FfmpegH264PipelineState();
        using var unmatched = new Bitmap(1, 1);

        Assert.False(pipeline.TryPublish(unmatched, out _));
        Assert.Equal(1, unmatched.Width);
        try
        {
            for (int index = 0;
                 index < FfmpegH264PipelineState.MaxDecodedFrames;
                 index++)
            {
                Assert.True(pipeline.TrySubmit(index + 1));
                Assert.True(pipeline.TryPublish(
                    new Bitmap(1, 1),
                    out _));
            }

            Assert.Equal(
                FfmpegH264PipelineState.MaxDecodedFrames,
                pipeline.DecodedFrameCount);
            Assert.True(pipeline.TrySubmit(999));
            using var overflowing = new Bitmap(1, 1);
            Assert.False(pipeline.TryPublish(
                overflowing,
                out _));
            Assert.Equal(1, overflowing.Width);
        }
        finally
        {
            pipeline.Reset();
        }
    }

    [Fact]
    public void PipelineResetCannotAssociateOldOutputWithNewSubmission()
    {
        var pipeline = new FfmpegH264PipelineState();
        var oldBitmap = new Bitmap(1, 1);
        Assert.True(pipeline.TrySubmit(301));
        Assert.True(pipeline.TryPublish(oldBitmap, out _));

        pipeline.Reset();

        Assert.Equal(0, pipeline.PendingInputCount);
        Assert.Equal(0, pipeline.DecodedFrameCount);
        Assert.False(pipeline.TryTake(out _));
        Assert.Throws<ArgumentException>(
            () => _ = oldBitmap.Width);
        Assert.True(pipeline.TrySubmit(401));
        var newBitmap = new Bitmap(1, 1);
        Assert.True(pipeline.TryPublish(
            newBitmap,
            out long matchedId));
        Assert.Equal(401, matchedId);
        Assert.True(pipeline.TryTake(
            out FfmpegH264DecodedFrame decoded));
        decoded.Bitmap.Dispose();
    }

    [Fact]
    public async Task RealFfmpegProducesOneMatchingFramePerExplicitAccessUnitWhenAvailable()
    {
        if (!FfmpegH264Decoder.IsAvailable)
        {
            return;
        }

        byte[] sample = Convert.FromBase64String(
            ThreeAllIdrAnnexBFixtureBase64);
        var parser = new AnnexBH264AccessUnitParser();
        var accessUnits = new List<AnnexBH264AccessUnit>();
        accessUnits.AddRange(parser.Append(sample));
        accessUnits.AddRange(parser.Complete());
        Assert.Equal(3, accessUnits.Count);
        Assert.Equal(
            sample.AsSpan(0, 656).ToArray(),
            accessUnits[0].Bytes.ToArray());
        Assert.Equal(
            sample.AsSpan(656, 60).ToArray(),
            accessUnits[1].Bytes.ToArray());
        Assert.Equal(
            sample.AsSpan(716, 60).ToArray(),
            accessUnits[2].Bytes.ToArray());

        using FfmpegH264Decoder? decoder =
            FfmpegH264Decoder.TryCreate(
                new Size(16, 16));
        if (decoder is null)
        {
            return;
        }

        var results = new List<FfmpegH264DecodeResult>();
        try
        {
            for (int index = 0; index < accessUnits.Count; index++)
            {
                FfmpegH264DecodeResult result =
                    await decoder.DecodeAsync(
                        index + 1,
                        accessUnits[index].Bytes).WaitAsync(
                        TimeSpan.FromSeconds(2));
                results.Add(result);
                Assert.Equal(
                    FfmpegH264DecodeStatus.Frame,
                    result.Status);
                Assert.True(
                    result.Bitmap is not null,
                    $"submission {index + 1} failed: {decoder.FailureDetail}");
                Assert.Equal(index + 1, result.SubmissionId);
            }

            Assert.Equal(3, results.Count);
        }
        finally
        {
            foreach (FfmpegH264DecodeResult result in results)
            {
                result.Bitmap?.Dispose();
            }
        }
    }

    [Fact]
    public async Task ConcurrentDisposeCancelsBackendSelectionWithoutThrowing()
    {
        if (!FfmpegH264Decoder.IsAvailable)
        {
            return;
        }

        FfmpegH264Decoder? decoder =
            FfmpegH264Decoder.TryCreate(
                new Size(17, 17));
        if (decoder is null)
        {
            return;
        }

        Task<FfmpegH264DecodeResult> decode =
            decoder.DecodeAsync(
                901,
                new byte[]
                {
                    0x00, 0x00, 0x00, 0x01,
                    0x09, 0xF0
                });
        await Task.Delay(10);
        decoder.Dispose();

        FfmpegH264DecodeResult result =
            await decode.WaitAsync(
                TimeSpan.FromSeconds(2));
        Assert.Equal(
            FfmpegH264DecodeStatus.Failed,
            result.Status);
        Assert.Null(result.Bitmap);
    }

    [Fact]
    public void DetachedBitmapLoaderDoesNotRetainEncodedStreamOrBuffer()
    {
        byte[] encoded;
        using (var source = new Bitmap(3, 2))
        {
            source.SetPixel(0, 0, Color.Red);
            source.SetPixel(2, 1, Color.Blue);
            using var encodedStream = new MemoryStream();
            source.Save(encodedStream, ImageFormat.Jpeg);
            encoded = encodedStream.ToArray();
        }

        byte[] padded = new byte[encoded.Length + 10];
        encoded.CopyTo(padded, 5);
        using Bitmap detached = DetachedBitmapLoader.Load(padded, 5, encoded.Length);
        Array.Clear(padded);

        Assert.Equal(3, detached.Width);
        Assert.Equal(2, detached.Height);
        using var output = new MemoryStream();
        detached.Save(output, ImageFormat.Png);
        Assert.True(output.Length > 0);
    }

    [Fact]
    public void DetachedBitmapLoaderRejectsDeclaredAndEncodedDimensionMismatch()
    {
        byte[] encoded;
        using (var source = new Bitmap(3, 2))
        using (var encodedStream = new MemoryStream())
        {
            source.Save(encodedStream, ImageFormat.Jpeg);
            encoded = encodedStream.ToArray();
        }

        Assert.Throws<InvalidDataException>(() =>
            DetachedBitmapLoader.Load(encoded, 0, encoded.Length, 4, 2));
    }

    [Fact]
    public void DetachedBitmapLoaderRejectsOversizedJpegBeforeBitmapAllocation()
    {
        byte[] forgedJpeg =
        [
            0xFF, 0xD8,
            0xFF, 0xC0,
            0x00, 0x11,
            0x08,
            0x20, 0x00,
            0x20, 0x00,
            0x03,
            0x01, 0x11, 0x00,
            0x02, 0x11, 0x00,
            0x03, 0x11, 0x00
        ];

        Assert.Throws<InvalidDataException>(() =>
            DetachedBitmapLoader.Load(
                forgedJpeg,
                0,
                forgedJpeg.Length));
    }

    private sealed class OneByteReadStream(byte[] bytes) :
        MemoryStream(bytes, writable: false)
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            return base.ReadAsync(
                buffer[..Math.Min(1, buffer.Length)],
                cancellationToken);
        }
    }

    private const string ThreeAllIdrAnnexBFixtureBase64 =
        "AAAAAQkQAAAAAWdCwArd7ARAAAADAEAAAA8jxIngAAAAAWjOD8gAAAEGBf//TdxF6b3m2Ui3lizYINkj7u94MjY0IC0gY29yZSAxNjUgcjMyMjIgYjM1NjA1YSAtIEguMjY0L01QRUctNCBBVkMgY29kZWMgLSBDb3B5bGVmdCAyMDAzLTIwMjUgLSBodHRwOi8vd3d3LnZpZGVvbGFuLm9yZy94MjY0Lmh0bWwgLSBvcHRpb25zOiBjYWJhYz0wIHJlZj0xIGRlYmxvY2s9MDowOjAgYW5hbHlzZT0wOjAgbWU9ZGlhIHN1Ym1lPTAgcHN5PTEgcHN5X3JkPTEuMDA6MC4wMCBtaXhlZF9yZWY9MCBtZV9yYW5nZT0xNiBjaHJvbWFfbWU9MSB0cmVsbGlzPTAgOHg4ZGN0PTAgY3FtPTAgZGVhZHpvbmU9MjEsMTEgZmFzdF9wc2tpcD0xIGNocm9tYV9xcF9vZmZzZXQ9MCB0aHJlYWRzPTEgbG9va2FoZWFkX3RocmVhZHM9MSBzbGljZWRfdGhyZWFkcz0wIG5yPTAgZGVjaW1hdGU9MSBpbnRlcmxhY2VkPTAgYmx1cmF5X2NvbXBhdD0wIGNvbnN0cmFpbmVkX2ludHJhPTAgYmZyYW1lcz0wIHdlaWdodHA9MCBrZXlpbnQ9MSBrZXlpbnRfbWluPTEgc2NlbmVjdXQ9MCBpbnRyYV9yZWZyZXNoPTAgcmM9Y3JmIG1idHJlZT0wIGNyZj0yMy4wIHFjb21wPTAuNjAgcXBtaW49MCBxcG1heD02OSBxcHN0ZXA9NCBpcF9yYXRpbz0xLjQwIGFxPTAAgAAAAWWIhDoRigACMXHAAEPKOAAIBeAAAAABCRAAAAABZ0LACt3sBEAAAAMAQAAADyPEieAAAAABaM4PyAAAAWWIggehGKAAJS8cAARnI4AAgT4AAAABCRAAAAABZ0LACt3sBEAAAAMAQAAADyPEieAAAAABaM4PyAAAAWWIhB6EYoAAlLxwABGcjgACBPg=";
}
