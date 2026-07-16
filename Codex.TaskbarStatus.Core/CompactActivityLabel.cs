namespace Codex.TaskbarStatus.Core;

/// <summary>
/// Produces the short activity word used by the compact taskbar preview.
/// Active requests rotate through a stable sequence while terminal states use
/// a fixed label that can be understood without the expanded flyout.
/// </summary>
public static class CompactActivityLabel
{
    public static readonly TimeSpan RotationInterval = TimeSpan.FromSeconds(4);

    private static readonly string[] RunningLabels =
    [
        "Thinking",
        "Working",
        "Reasoning",
        "Exploring",
    ];

    public static string Resolve(
        string? status,
        string? requestKey,
        DateTimeOffset? startedAtUtc,
        DateTimeOffset nowUtc)
    {
        return status switch
        {
            CodexExecutionStatuses.Running => ResolveRunning(
                requestKey,
                startedAtUtc,
                nowUtc),
            CodexExecutionStatuses.Waiting => "Waiting",
            CodexExecutionStatuses.Completed => "Done",
            CodexExecutionStatuses.Aborted => "Stopped",
            CodexExecutionStatuses.Error => "Failed",
            _ => "Ready",
        };
    }

    private static string ResolveRunning(
        string? requestKey,
        DateTimeOffset? startedAtUtc,
        DateTimeOffset nowUtc)
    {
        var startedAt = startedAtUtc ?? DateTimeOffset.UnixEpoch;
        var elapsed = nowUtc - startedAt;
        var bucket = elapsed > TimeSpan.Zero
            ? elapsed.Ticks / RotationInterval.Ticks
            : 0;
        var seed = StableHash(requestKey);
        var index = (int)((seed + (ulong)bucket) % (ulong)RunningLabels.Length);
        return RunningLabels[index];
    }

    private static ulong StableHash(string? value)
    {
        // FNV-1a keeps the initial label stable across processes, unlike
        // string.GetHashCode(), whose seed is randomized by the runtime.
        var hash = 14695981039346656037UL;
        if (string.IsNullOrEmpty(value))
        {
            return hash;
        }

        foreach (var character in value)
        {
            hash ^= character;
            hash *= 1099511628211UL;
        }

        return hash;
    }
}
