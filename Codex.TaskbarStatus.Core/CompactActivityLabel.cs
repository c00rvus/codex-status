namespace Codex.TaskbarStatus.Core;

/// <summary>
/// Produces the short activity word used by the compact taskbar preview.
/// Each active request receives one stable word while terminal states use a
/// fixed label that can be understood without the expanded flyout.
/// </summary>
public static class CompactActivityLabel
{
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
            CodexExecutionStatuses.Running => ResolveRunning(requestKey),
            CodexExecutionStatuses.Waiting => "Waiting",
            CodexExecutionStatuses.Completed => "Done",
            CodexExecutionStatuses.Aborted => "Stopped",
            CodexExecutionStatuses.Error => "Failed",
            _ => "Ready",
        };
    }

    private static string ResolveRunning(string? requestKey)
    {
        // The request key contains the session/turn identity. Time is
        // deliberately excluded so the selected word cannot change midway
        // through an execution.
        var seed = StableHash(requestKey);
        var index = (int)(seed % (ulong)RunningLabels.Length);
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
