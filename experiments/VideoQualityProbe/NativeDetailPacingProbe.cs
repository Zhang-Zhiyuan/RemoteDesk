using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using RemoteDesk;
using RemoteDesk.NativeDetail;

// Actual MF decoding and visible D3D11 presentation, driven at 60 Hz with a
// one-frame LOCAL deadline. No GPU query/readback/Flush/wait is added to the
// measured loop. This tests submission pacing, NOT input-to-photon latency or
// production capture/transport. Synthetic content and pressure flags are explicit.
internal static class NativeDetailPacingProbe
{
    private const int FramesPerPhase = 120, Fps = 60;
    private static readonly Rectangle Viewport = new(0,0,960,640);

    internal static void Run(string fixtures, string prior, string output)
    {
        if (Directory.Exists(output)) throw new IOException("Preserving previous pacing evidence.");
        Directory.CreateDirectory(output);
        WindowsFormsSynchronizationContext.AutoInstall = false;
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        using var original = new Bitmap(Path.Combine(fixtures,"native-reference.png"));
        using var edited = new Bitmap(Path.Combine(fixtures,"native-edited.png"));
        var sourcePixels = new[] { NativeDetailProbe.Rgba(original), NativeDetailProbe.Rgba(edited) };
        var accessUnits = new[] {
            NativeDetailProbe.LastIndependentAu(Path.Combine(fixtures,"10mbps-static-1080p-gop1.h264")),
            NativeDetailProbe.LastIndependentAu(Path.Combine(prior,"edited-base.h264")) };
        if (!MediaFoundationD3D11H264Decoder.TryCreate(new(1920,1080,Fps,AllSamplesIndependent:true), out var created, out var capability))
            throw new IOException(capability.Detail);
        using var decoder = created!; using var device = decoder.AcquireDeviceLease();
        using var window = new ProbeWindow { ClientSize = new(960,600), Text = "RemoteDesk GPU pacing test — closes automatically",
            ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new(40,40) };
        window.Show(); Application.DoEvents();
        Check(window.Visible && IsWindowVisible(window.Handle),"The owned preview window was not made visible.");
        if (!D3D11HwndVideoPresenter.TryCreate(device, new(window.Handle,1920,1080,Fps,
                EnableEdgeEnhancement:false, EnableExperimentalUpscaling:true, UpscalingAlgorithm:ExperimentalUpscalingAlgorithm.Nis),
                out var candidate, out var presentationCapability)) throw new IOException(presentationCapability.Detail);
        using var presenter = candidate!;
        using var pacingWaiter = new WindowsHighResolutionPacingWaiter();
        // Render a 4K back buffer even though the owned preview is smaller.
        // This is renderer load, not a claim that preview pixels are 1:1.
        Check(presenter.Resize(original.Width,original.Height).IsSuccess,"Cannot allocate the 4K presentation target.");
        uint power = SetThreadExecutionState(0x80000003);
        var summaries = new List<object>(); var samples = new List<Sample>();
        string? failure = null;
        Task<bool>? preparation = null;
        long serial = 0;
        var source = new DetailSource(new(1,1,original.Width,original.Height));
        var context = new DetailContext(1,1,original.Width,original.Height);
        var cache = new DetailCache(); cache.Reset(context,new(0,0,960,640));
        var adapter = new NativeDetailGpuProbe.Adapter();
        DetailManifest manifest = source.Observe(sourcePixels[0],0); cache.Present(manifest);
        Fill(source,cache,manifest,250);
        long simulatedSourceTime = 1000;
        int picture = 0;
        try
        {
            // Decode/present warm-up is outside the observation windows. Native
            // resource preparation is deliberately still cold at phase 1.
            for (int i = 0; i < 30; i++)
            {
                long pts = ++serial * 10_000_000L / Fps;
                var decoded = decoder.DecodeAccessUnit(accessUnits[0],pts);
                using var frame = decoded.Frame ?? throw new IOException(decoded.Detail);
                Check(presenter.Present(frame,new(0,0,1920,1080)).IsSuccess,"Base warm-up presentation failed.");
                Application.DoEvents();
            }
            var phases = new[] { "base-before", "cold-detail", "cached-detail", "base-middle", "busy-skip", "changing-detail", "base-after" };
            foreach (string phase in phases)
            {
                bool enabled = phase is "cold-detail" or "cached-detail" or "busy-skip" or "changing-detail";
                if (phase == "cold-detail") preparation = presenter.PrepareNativeDetailsAsync(); // NEVER await in a frame.
                var rows = new List<Sample>();
                long step = Stopwatch.Frequency / Fps, next = Stopwatch.GetTimestamp();
                int missedSlots = 0;
                for (int i = 0; i < FramesPerPhase; i++)
                {
                    if (window.IsDisposed) throw new IOException("The owned test window was closed; no further presentation.");
                    // Fixture preparation is NOT the production capture path.
                    // Rebuild only four changed native tiles; record its cost
                    // separately, outside the renderer's frame deadline.
                    double fixtureMs = 0;
                    if (phase == "changing-detail" && i % 12 == 0)
                    {
                        var fixtureWatch = Stopwatch.StartNew(); picture = 1-picture;
                        manifest = source.Observe(sourcePixels[picture],simulatedSourceTime);
                        Check(cache.Present(manifest),"Changing source manifest was rejected.");
                        simulatedSourceTime += 250; Fill(source,cache,manifest,simulatedSourceTime); simulatedSourceTime += 1000;
                        fixtureMs = fixtureWatch.Elapsed.TotalMilliseconds;
                    }
                    WaitUntil(next,pacingWaiter);
                    long due = next, start = Stopwatch.GetTimestamp();
                    if (start >= next + step)
                    {
                        int skipped = (int)((start-next)/step); missedSlots += skipped; due += skipped*step;
                    }
                    next = due+step; // Never build a catch-up queue of old frames.
                    long pts = ++serial * 10_000_000L / Fps;
                    var decoded = decoder.DecodeAccessUnit(accessUnits[picture],pts);
                    using var frame = decoded.Frame ?? throw new IOException(decoded.Detail);
                    Check(frame.HasExplicitSampleTime && frame.SampleTime100Nanoseconds == pts,"Decoder source correlation failed.");
                    var batch = enabled ? adapter.Convert(cache,manifest,Viewport,pts) : null;
                    var budget = new NativeDetailRenderBudget(next);
                    if (phase == "busy-skip") budget = (i % 4) switch
                    {
                        0 => budget with { InputPending = true },
                        1 => budget with { BaseFrameBacklogged = true },
                        2 => budget with { TransportCongested = true },
                        _ => new NativeDetailRenderBudget(1)
                    };
                    long presentStarted = Stopwatch.GetTimestamp();
                    var result = presenter.Present(frame,new(0,0,1920,1080),nativeDetails:batch,nativeDetailBudget:budget);
                    long end = Stopwatch.GetTimestamp();
                    Check(result.IsSuccess,result.Detail);
                    Check(presenter.NativeDetailUploadedTiles <= 2,"Native upload burst exceeded its limit.");
                    if (!enabled || phase == "busy-skip")
                        Check(!presenter.NativeDetailActive && presenter.NativeDetailUploadedTiles == 0,"Busy/default frame submitted native work.");
                    var row = new Sample(phase,i,Milliseconds(presentStarted-start),Milliseconds(end-presentStarted),
                        Milliseconds(Math.Max(0,end-next)),Milliseconds(Math.Max(0,start-due)),fixtureMs,
                        presenter.NativeDetailActive,presenter.NativeDetailUploadedTiles);
                    rows.Add(row); samples.Add(row);
                    Application.DoEvents();
                }
                if (phase is "cold-detail" or "cached-detail" or "changing-detail")
                    Check(rows.Count(row => row.DetailActive) >= 10,"Native detail never made sustained progress under real deadlines.");
                summaries.Add(new { phase, frames = rows.Count, missedScheduleSlots = missedSlots,
                    detailFrames = rows.Count(row => row.DetailActive), uploadedTiles = rows.Sum(row => row.UploadedTiles),
                    decodeAndAdapterMs = Percentiles(rows.Select(row => row.DecodeAndAdapterMs)),
                    presentCallMs = Percentiles(rows.Select(row => row.PresentCallMs)),
                    cpuDeadlineOverrunMs = Percentiles(rows.Select(row => row.CpuDeadlineOverrunMs)),
                    lateSubmissionsOver1Ms = rows.Count(row => row.CpuDeadlineOverrunMs > 1),
                    fixturePreparationMs = Percentiles(rows.Select(row => row.FixturePreparationMs)) });
                Console.WriteLine($"{phase}: {rows.Count} presented, {rows.Count(row => row.DetailActive)} native, {missedSlots} missed schedule slots");
            }
            Check(preparation is { IsCompletedSuccessfully: true } && preparation.Result,"Native preparation did not complete.");
            presenter.DisableNativeDetails();
            Check(presenter.NativeDetailAtlasBytes == 0,"Explicit disable retained the native atlas.");
        }
        catch (Exception error) { failure = error.GetType().Name + ": " + error.Message; throw; }
        finally
        {
            if (power != 0) SetThreadExecutionState(power);
            File.WriteAllText(Path.Combine(output,"summary.json"),JsonSerializer.Serialize(new
            {
                functionalPassed = failure is null, failure, fps = Fps, outputSize = original.Size,
                pacingDriver = nameof(WindowsHighResolutionPacingWaiter),
                displayedPreviewSize = new Size(960,600), sourceFrames = serial, summaries, samples,
                scope = "Actual hardware MF decoding and visible D3D11/NIS/native-atlas submission, 60Hz synthetic driver with a one-frame local deadline. No GPU completion wait/query/Flush/readback added to the measured loop. Input/backlog/congestion flags are synthetic. Fixture native damage/compression is excluded and recorded separately. NOT real host capture, WAN, input ACK, DWM scan-out or photon latency; functionalPassed does NOT authorize release."
            },new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static void Fill(DetailSource source, DetailCache cache, DetailManifest manifest, long now)
    {
        var existing = cache.Patches.Select(p => p.Tile).ToHashSet();
        for (int tile = 0; tile < manifest.Context.TileCount; tile++)
        {
            var bounds = manifest.Context.Tile(tile);
            if (existing.Contains(tile) || !new Rectangle(bounds.X,bounds.Y,bounds.Width,bounds.Height).IntersectsWith(Viewport)) continue;
            var transfer = source.Build(manifest,tile,now) ?? throw new IOException("Native fixture tile was unstable.");
            for (int offset = 0; offset < transfer.Encoded.Length; offset += DetailWire.ChunkBytes)
                Check(cache.Receive(transfer.Chunk(offset),now) is DetailReceiveResult.Partial or DetailReceiveResult.Applied,"Fixture tile assembly failed.");
        }
    }
    private static void WaitUntil(long timestamp,WindowsHighResolutionPacingWaiter waiter)
    {
        long remaining;
        while ((remaining = timestamp-Stopwatch.GetTimestamp()) > 0)
            waiter.Wait(TimeSpan.FromSeconds(remaining/(double)Stopwatch.Frequency),CancellationToken.None);
    }
    private static object Percentiles(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        double At(double q) => sorted[Math.Clamp((int)Math.Ceiling(sorted.Length*q)-1,0,sorted.Length-1)];
        return new { p50=At(.5),p95=At(.95),p99=At(.99),max=sorted[^1] };
    }
    private static double Milliseconds(long ticks) => ticks*1000d/Stopwatch.Frequency;
    private static void Check(bool valid,string reason) { if (!valid) throw new IOException(reason); }
    private sealed record Sample(string Phase,int Index,double DecodeAndAdapterMs,double PresentCallMs,
        double CpuDeadlineOverrunMs,double SchedulingLatenessMs,double FixturePreparationMs,bool DetailActive,int UploadedTiles);
    private sealed class ProbeWindow : Form { protected override bool ShowWithoutActivation => true; }
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
}
