using System.Diagnostics;
using System.Text.Json;
using RemoteDesk;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

// Isolated GPU timestamp benchmark, never runs on a production session. The
// deliberate query completion waits below are ONLY measurement infrastructure.
internal static class NativeDamageRefinementProbe
{
    public static void Run(string outputDirectory)
    {
        string output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output)) throw new IOException("Preserving prior measurement evidence.");
        Directory.CreateDirectory(output);
        using var device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
        using var context = device.ImmediateContext;
        using var a = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, 1024, 1024, 1, 1, BindFlags.ShaderResource));
        using var b = device.CreateTexture2D(a.Description);
        byte[] pixels = new byte[1024 * 1024 * 4];
        new Random(19248).NextBytes(pixels);
        context.UpdateSubresource(pixels, a, 0, 4096);
        // Every tile changes by just one bit; this also validates all 64 group
        // slots and defeats partial sampling / approximate content hashes.
        for (int tile = 0; tile < 64; tile++)
        {
            int x = tile % 8 * 128 + tile * 17 % 128, y = tile / 8 * 128 + tile * 31 % 128;
            pixels[(y * 1024 + x) * 4 + tile % 3] ^= 1;
        }
        context.UpdateSubresource(pixels, b, 0, 4096);
        var samples = new List<object>();
        string? failure = null;
        try
        {
            foreach (int count in new[] { 6, 64 })
            foreach (bool changing in new[] { false, true })
            {
                using var refiner = new D3D11NativeDamageRefiner(device);
                int[] tiles = Enumerable.Range(0, count).ToArray();
                refiner.Begin(a, Coarse(1), tiles, Budget()); refiner.Complete(1, Budget());
                for (int i = 0; i < 90; i++)
                {
                    long sequence = i + 2;
                    using var disjoint = device.CreateQuery(new QueryDescription(QueryType.TimestampDisjoint));
                    using var start = device.CreateQuery(new QueryDescription(QueryType.Timestamp));
                    using var end = device.CreateQuery(new QueryDescription(QueryType.Timestamp));
                    context.Begin(disjoint); context.End(start);
                    long cpuStart = Stopwatch.GetTimestamp();
                    if (!refiner.Begin(changing && (i & 1) == 0 ? b : a, Coarse(sequence), tiles, Budget()))
                        throw new IOException(refiner.Failure ?? "Comparison was not submitted.");
                    double submitMs = Stopwatch.GetElapsedTime(cpuStart).TotalMilliseconds;
                    context.End(end); context.End(disjoint);
                    context.Flush(); // Benchmark-only query submission, not a frame wait in the refiner.
                    var watch = Stopwatch.StartNew();
                    QueryDataTimestampDisjoint timing;
                    while (!context.GetData(disjoint, out timing) || !refiner.TryPoll(Budget()))
                    {
                        if (refiner.IsFailed || watch.ElapsedMilliseconds > 3000)
                            throw new IOException(refiner.Failure ?? "GPU measurement timeout.");
                        Thread.Sleep(1);
                    }
                    if (timing.Disjoint || !context.GetData(start, out ulong first) || !context.GetData(end, out ulong last))
                        throw new IOException("Invalid GPU timestamp interval.");
                    var result = refiner.Complete(sequence, Budget());
                    for (int tile = 0; tile < 64; tile++)
                        if (result[tile] != (changing || tile >= count ? sequence : 1))
                            throw new IOException($"Exact comparison missed a one-bit change: tile {tile}, sequence {sequence}.");
                    if (i >= 10) samples.Add(new { count, changing, submitMs, gpuMs = (last - first) * 1000d / timing.Frequency });
                }
                Console.WriteLine($"GPU native comparison: {count} tiles, changing={changing}, 90 exact samples completed.");
            }
        }
        catch (Exception ex) { failure = ex.ToString(); throw; }
        finally
        {
            File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new
            {
                passed = failure is null, failure, samples,
                scope = "Isolated GPU timestamp and CPU submission microbenchmark: 1024x1024 BGRA native textures, 6 or 64 requested 128px tiles. No network, decoder, presentation or input-to-photon latency claim. First 10 samples of each phase excluded as warm-up. GPU completion waits are probe-only."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
    private static NativeDetailRenderBudget Budget() => new(Stopwatch.GetTimestamp() + Stopwatch.Frequency);
    private static NativeSurfaceManifest Coarse(long sequence) => new(sequence, new Size(1024, 1024), Enumerable.Repeat(sequence, 64).ToArray());
}
