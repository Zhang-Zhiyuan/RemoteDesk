using System.Text.Json;
using RemoteDesk;

// Uses only previously generated fixtures. No network or desktop input.
internal static class RecoveryProbe
{
    public static void Run(string fixtureDirectory, string outputPath)
    {
        if (File.Exists(outputPath)) throw new IOException("Refusing to overwrite an existing report.");
        var rows = new List<object>();
        foreach (string fixture in new[] { "static-current-gop1.h264", "static-research-gop30.h264" })
        {
            var parser = new AnnexBH264AccessUnitParser();
            List<AnnexBH264AccessUnit> units = [..parser.Append(File.ReadAllBytes(Path.Combine(fixtureDirectory, fixture))), ..parser.Complete()];
            try
            {
                Check(units.Count == 180, "Expected 180 complete access units.");
                bool independent = units.All(unit => unit.IsIdr);
                for (int round = 0; round < 3; round++)
                {
                    Check(MediaFoundationD3D11H264Decoder.TryCreate(new(1920, 1080, 60,
                        AllSamplesIndependent: independent), out var created, out var capability), capability.Detail);
                    using var decoder = created!;
                    int decoded = 0;
                    for (int index = 0; index < units.Count; index++)
                        decoded += Decode(decoder, units[index], index);
                    Check(decoded == 180, $"Only {decoded}/180 frames decoded.");
                    Check(decoder.NotifyAccessUnitGap(out var gapFailure), gapFailure);
                    Check(decoder.ReferenceState == MediaFoundationD3D11ReferenceState.NeedsIndependentFrame,
                        "Gap did not invalidate the reference chain.");
                    bool orphanRejected = false;
                    if (!independent)
                    {
                        var orphan = decoder.DecodeAccessUnit(units[1].Bytes, 181 * 10_000_000L / 60);
                        orphan.Frame?.Dispose();
                        orphanRejected = orphan.Status == MediaFoundationD3D11DecodeStatus.Failed;
                        Check(orphanRejected, "Dependent frame decoded after a known gap.");
                    }
                    int recovered = 0;
                    for (int index = 0; index < 30; index++)
                        recovered += Decode(decoder, units[index], 182 + index);
                    Check(recovered == 30, "Reference chain did not recover at the next IDR.");
                    Check(decoder.ResetForDiscontinuity(out var resetFailure), resetFailure);
                    int restarted = 0;
                    for (int index = 0; index < 30; index++)
                        restarted += Decode(decoder, units[index], 212 + index);
                    Check(restarted == 30, "Decoder failed after reconnect-style reset.");
                    rows.Add(new { Fixture = fixture, Round = round + 1, Decoded = decoded,
                        GapInvalidatedChain = true, OrphanRejectionApplicable = !independent,
                        OrphanRejected = orphanRejected, Recovered = recovered, Restarted = restarted });
                    Console.WriteLine($"{fixture} round {round + 1}: {decoded} decoded, gap recovery {recovered}, reset {restarted}");
                }
            }
            finally { foreach (var unit in units) unit.Dispose(); }
        }
        File.WriteAllText(outputPath, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static int Decode(MediaFoundationD3D11H264Decoder decoder, AnnexBH264AccessUnit unit, int sequence)
    {
        var result = decoder.DecodeAccessUnit(unit.Bytes, sequence * 10_000_000L / 60);
        using var frame = result.Frame;
        Check(result.Status != MediaFoundationD3D11DecodeStatus.Failed, result.Detail);
        if (frame is null) return 0;
        Check(frame.VisibleWidth == 1920 && frame.VisibleHeight == 1080, "Wrong output dimensions.");
        return 1;
    }

    private static void Check(bool condition, string? detail)
    {
        if (!condition) throw new InvalidOperationException(detail ?? "Recovery probe failed.");
    }
}
