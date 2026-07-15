using Codex.TaskbarStatus.Core;

namespace Codex.TaskbarStatus.Standalone.Widget;

internal sealed class CodexStatusSnapshot
{
    public string Status { get; init; } = CodexExecutionStatuses.Idle;
    public string Activity { get; init; } = CodexActivityLabels.NoActiveExecution;
    public string? SessionId { get; init; }
    public string? TurnId { get; init; }
    public string? Cwd { get; init; }
    public string? Model { get; init; }
    public int ToolCount { get; init; }
    public int FilesChangedCount { get; init; }
    public int TotalSubagents { get; init; }
    public DateTimeOffset? StartedAtUtc { get; init; }
    public DateTimeOffset? StoppedAtUtc { get; init; }
    public DateTimeOffset LastUpdatedAtUtc { get; init; } = DateTimeOffset.MinValue;
    public string Source { get; init; } = "None";

    public TimeSpan Elapsed(DateTimeOffset now)
    {
        if (StartedAtUtc is null)
        {
            return TimeSpan.Zero;
        }

        var end = StoppedAtUtc ?? now;
        return end > StartedAtUtc ? end - StartedAtUtc.Value : TimeSpan.Zero;
    }
}

internal sealed class CodexStatusReader : IDisposable
{
    private readonly StatusFileStore _statusStore = new();
    private readonly RolloutStatusReader _rolloutReader = new();
    private CodexExecutionState? _cachedRollout;
    private DateTimeOffset _nextRolloutReadAt = DateTimeOffset.MinValue;

    public CodexStatusSnapshot Read()
    {
        var now = DateTimeOffset.UtcNow;
        var hookState = File.Exists(StatusFileStore.DefaultPath) ? _statusStore.Read() : null;

        if (now >= _nextRolloutReadAt)
        {
            _cachedRollout = _rolloutReader.ReadLatest();
            _nextRolloutReadAt = now.AddSeconds(1.5);
        }

        var state = SelectState(hookState, _cachedRollout, out var source);
        return state is null ? new CodexStatusSnapshot() : Map(state, source);
    }

    public void Dispose() => _rolloutReader.Dispose();

    private static CodexExecutionState? SelectState(
        CodexExecutionState? hookState,
        CodexExecutionState? rolloutState,
        out string source)
    {
        if (hookState is null)
        {
            source = rolloutState is null ? "None" : "Local session (fallback)";
            return rolloutState;
        }

        if (rolloutState is null)
        {
            source = "Hooks";
            return hookState;
        }

        if (string.Equals(hookState.SessionId, rolloutState.SessionId, StringComparison.Ordinal))
        {
            source = "Hooks + local session";
            return Merge(hookState, rolloutState);
        }

        if (rolloutState.LastUpdatedAtUtc > hookState.LastUpdatedAtUtc)
        {
            source = "Local session (fallback)";
            return rolloutState;
        }

        source = "Hooks";
        return hookState;
    }

    private static CodexExecutionState Merge(
        CodexExecutionState hookState,
        CodexExecutionState rolloutState)
    {
        var rolloutIsNewer = rolloutState.LastUpdatedAtUtc >= hookState.LastUpdatedAtUtc;
        return new CodexExecutionState
        {
            Status = rolloutIsNewer ? rolloutState.Status : hookState.Status,
            Activity = rolloutIsNewer ? rolloutState.Activity : hookState.Activity,
            SessionId = hookState.SessionId ?? rolloutState.SessionId,
            TurnId = rolloutIsNewer
                ? rolloutState.TurnId ?? hookState.TurnId
                : hookState.TurnId ?? rolloutState.TurnId,
            TranscriptPath = hookState.TranscriptPath ?? rolloutState.TranscriptPath,
            Cwd = hookState.Cwd ?? rolloutState.Cwd,
            Model = hookState.Model ?? rolloutState.Model,
            ToolCount = Math.Max(hookState.ToolCount, rolloutState.ToolCount),
            FilesChanged = hookState.FilesChanged
                .Concat(rolloutState.FilesChanged)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            ActiveSubagents = hookState.ActiveSubagents,
            TotalSubagents = Math.Max(hookState.TotalSubagents, rolloutState.TotalSubagents),
            InputTokens = Math.Max(hookState.InputTokens, rolloutState.InputTokens),
            OutputTokens = Math.Max(hookState.OutputTokens, rolloutState.OutputTokens),
            TotalTokens = Math.Max(hookState.TotalTokens, rolloutState.TotalTokens),
            ModelContextWindow = hookState.ModelContextWindow ?? rolloutState.ModelContextWindow,
            StartedAtUtc = rolloutIsNewer
                ? rolloutState.StartedAtUtc ?? hookState.StartedAtUtc
                : hookState.StartedAtUtc ?? rolloutState.StartedAtUtc,
            StoppedAtUtc = rolloutIsNewer
                ? rolloutState.StoppedAtUtc ?? hookState.StoppedAtUtc
                : hookState.StoppedAtUtc ?? rolloutState.StoppedAtUtc,
            LastUpdatedAtUtc = rolloutIsNewer
                ? rolloutState.LastUpdatedAtUtc
                : hookState.LastUpdatedAtUtc,
        };
    }

    private static CodexStatusSnapshot Map(CodexExecutionState state, string source)
    {
        return new CodexStatusSnapshot
        {
            Status = state.Status,
            Activity = CodexActivityLabels.ToEnglish(state.Activity),
            SessionId = state.SessionId,
            TurnId = state.TurnId,
            Cwd = state.Cwd,
            Model = state.Model,
            ToolCount = state.ToolCount,
            FilesChangedCount = state.FilesChanged.Count,
            TotalSubagents = state.TotalSubagents,
            StartedAtUtc = state.StartedAtUtc,
            StoppedAtUtc = state.StoppedAtUtc,
            LastUpdatedAtUtc = state.LastUpdatedAtUtc,
            Source = source,
        };
    }
}
