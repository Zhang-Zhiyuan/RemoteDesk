namespace RemoteDesk;

internal readonly record struct CaptureTargetStateSnapshot(
    CaptureTargetInfo Target,
    bool IsAvailable,
    int Generation);

internal readonly record struct CaptureTargetTopologyPublication(
    IReadOnlyList<CaptureTargetInfo>? Targets,
    bool AvailabilityChanged,
    bool GenerationChanged,
    CaptureTargetStateSnapshot Snapshot)
{
    public bool HasChanges =>
        Targets is not null ||
        AvailabilityChanged ||
        GenerationChanged;
}

/// <summary>
/// De-duplicates 250 ms display-topology observations. A capture generation
/// transition is published once even when the selected target id and
/// availability stay unchanged, because bounds/metadata changes fence both
/// the host frame source and the viewer presentation chain.
/// </summary>
internal sealed class CaptureTargetTopologyMonitorState
{
    private readonly object _syncRoot = new();
    private CaptureTargetInfo[] _targets;
    private bool _isAvailable;
    private int _generation;

    public CaptureTargetTopologyMonitorState(
        IReadOnlyList<CaptureTargetInfo> targets,
        CaptureTargetStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(snapshot.Target);
        _targets = targets.ToArray();
        _isAvailable = snapshot.IsAvailable;
        _generation = snapshot.Generation;
    }

    public CaptureTargetTopologyPublication Observe(
        IReadOnlyList<CaptureTargetInfo> targets,
        CaptureTargetStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(snapshot.Target);

        lock (_syncRoot)
        {
            CaptureTargetInfo[] currentTargets =
                targets.ToArray();
            bool targetsChanged = !TargetListsEqual(
                _targets,
                currentTargets);
            bool availabilityChanged =
                _isAvailable != snapshot.IsAvailable;
            bool generationChanged =
                _generation != snapshot.Generation;

            _targets = currentTargets;
            _isAvailable = snapshot.IsAvailable;
            _generation = snapshot.Generation;

            return new CaptureTargetTopologyPublication(
                targetsChanged ? currentTargets : null,
                availabilityChanged,
                generationChanged,
                snapshot);
        }
    }

    private static bool TargetListsEqual(
        IReadOnlyList<CaptureTargetInfo> left,
        IReadOnlyList<CaptureTargetInfo> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Count; index++)
        {
            if (!string.Equals(
                    left[index].Id,
                    right[index].Id,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    left[index].DisplayName,
                    right[index].DisplayName,
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
