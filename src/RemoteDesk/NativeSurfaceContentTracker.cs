namespace RemoteDesk;

// A proof is useful only for this exact pair of immutable source surfaces.
// Missing/late/mismatched proofs never preserve a potentially stale character.
internal sealed record NativeSurfaceComparison(long SourceSequence, long ReferenceSequence, int[] UnchangedTiles,
    int[]? ChangedTiles = null);

internal sealed class NativeSurfaceContentTracker
{
    private NativeSurfaceManifest? _raw, _refined;
    public NativeSurfaceManifest? Current => _refined;

    public void ValidateNext(NativeSurfaceManifest raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (_raw is null) return;
        if (raw.Size != _raw.Size || raw.Sequence <= _raw.Sequence)
            throw new ArgumentException("Native capture identity went backwards or changed dimensions.", nameof(raw));
        for (int i = 0; i < raw.Count; i++)
            if (raw[i] < _raw[i]) throw new ArgumentException("Native damage version went backwards.", nameof(raw));
    }

    public NativeSurfaceManifest Commit(NativeSurfaceManifest raw, NativeSurfaceComparison? comparison,
        ReadOnlySpan<int> proofRequiredTiles = default)
    {
        ValidateNext(raw);
        // The bounded manifest has at most 2048 tiles. No per-frame hash sets,
        // no quadratic searches through the sparse proof for every desktop tile.
        Span<byte> evidence = stackalloc byte[raw.Count]; evidence.Clear();
        bool validProof = _raw is not null && comparison is not null &&
            comparison.SourceSequence == raw.Sequence && comparison.ReferenceSequence == _raw.Sequence &&
            comparison.UnchangedTiles is { Length: <= NativeDetailPresentation.MaximumTiles } &&
            (comparison.ChangedTiles?.Length ?? 0) <= NativeDetailPresentation.MaximumTiles - comparison.UnchangedTiles.Length;
        if (validProof)
        {
            validProof = Mark(comparison!.UnchangedTiles, evidence, 1) && Mark(comparison.ChangedTiles, evidence, 2);
            if (!validProof) evidence.Clear();
        }
        if (proofRequiredTiles.Length > NativeDetailPresentation.MaximumTiles)
            throw new ArgumentOutOfRangeException(nameof(proofRequiredTiles));
        foreach (int tile in proofRequiredTiles)
        {
            if (tile < 0 || tile >= raw.Count || (evidence[tile] & 4) != 0)
                throw new ArgumentException("Invalid requested comparison tile.", nameof(proofRequiredTiles));
            evidence[tile] |= 4;
        }
        long[] versions = raw.CopyVersions();
        if (_raw is not null)
        {
            for (int i = 0; i < versions.Length; i++)
            {
                // Requested detail needs actual comparison evidence. A missing
                // result must invalidate it even if DXGI reported no damage;
                // metadata alone remains sufficient only outside that ROI.
                versions[i] = (evidence[i] & 3) == 1 || (evidence[i] == 0 && raw[i] == _raw[i])
                    ? _refined![i] : raw.Sequence;
            }
        }
        _raw = raw;
        return _refined = new(raw.Sequence, raw.Size, versions);
    }

    private static bool Mark(int[]? tiles, Span<byte> evidence, byte value)
    {
        if (tiles is null) return true;
        foreach (int tile in tiles)
        {
            if (tile < 0 || tile >= evidence.Length || evidence[tile] != 0) return false;
            evidence[tile] = value;
        }
        return true;
    }
}
