using Codex.TaskbarStatus.Core;

namespace Codex.TaskbarStatus.Standalone.Widget;

internal sealed class CodexStatusSnapshot
{
    public string Status { get; init; } = CodexExecutionStatuses.Idle;
    public string Activity { get; init; } = CodexActivityLabels.NoActiveExecution;
    public string? TaskTitle { get; init; }
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
    public bool IsUnreadCompletion { get; init; }

    public bool IsActive => CodexStatusReader.IsActiveState(Status, StoppedAtUtc);

    public string TaskKey => !string.IsNullOrWhiteSpace(SessionId)
        ? $"session:{SessionId}|turn:{TurnId ?? string.Empty}"
        : $"source:{Source}|started:{StartedAtUtc?.UtcTicks ?? 0}";

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

internal sealed class CodexStatusBoardSnapshot
{
    public CodexStatusSnapshot Primary { get; init; } = new();

    public IReadOnlyList<CodexStatusSnapshot> Tasks { get; init; } = [];

    public bool UnreadSignalAvailable { get; init; }

    public int ActiveCount => Tasks.Count(task => task.IsActive);

    public int ReadyCount => Tasks.Count(task => !task.IsActive);
}

internal sealed class CodexStatusReader : IDisposable
{
    private static readonly TimeSpan RolloutRefreshInterval = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan AbandonedActiveAge = TimeSpan.FromDays(7);
    private static readonly TimeSpan MissingUnreadSignalRetention = TimeSpan.FromMinutes(15);

    private readonly StatusFileStore _statusStore = new();
    private readonly RolloutStatusReader _rolloutReader = new();
    private readonly CodexDesktopUnreadThreadReader _unreadReader = new();
    private IReadOnlyList<CodexExecutionState> _cachedRollouts = [];
    private DateTimeOffset _nextRolloutReadAt = DateTimeOffset.MinValue;

    public CodexStatusSnapshot Read() => ReadBoard().Primary;

    public CodexStatusBoardSnapshot ReadBoard()
    {
        var now = DateTimeOffset.UtcNow;
        var unread = _unreadReader.Read();
        var hookState = File.Exists(StatusFileStore.DefaultPath) ? _statusStore.Read() : null;

        if (now >= _nextRolloutReadAt)
        {
            _cachedRollouts = _rolloutReader.ReadRecent(unread.ThreadIds);
            _nextRolloutReadAt = now + RolloutRefreshInterval;
        }

        var states = BuildMergedStates(hookState, _cachedRollouts);
        var mapped = states
            .Select(item => Map(
                item.State,
                item.Source,
                item.State.SessionId is { } sessionId && unread.ThreadIds.Contains(sessionId)))
            .ToArray();

        var primary = mapped
            .Where(snapshot => snapshot.IsActive && IsFreshActive(snapshot, now))
            .OrderByDescending(snapshot => snapshot.LastUpdatedAtUtc)
            .FirstOrDefault()
            ?? mapped.OrderByDescending(snapshot => snapshot.LastUpdatedAtUtc).FirstOrDefault()
            ?? new CodexStatusSnapshot();

        var tasks = mapped
            .Where(snapshot => ShouldShowInTaskList(snapshot, unread.IsAvailable, now))
            .OrderByDescending(snapshot => snapshot.IsActive)
            .ThenByDescending(snapshot => snapshot.LastUpdatedAtUtc)
            .Take(20)
            .ToArray();

        return new CodexStatusBoardSnapshot
        {
            Primary = primary,
            Tasks = tasks,
            UnreadSignalAvailable = unread.IsAvailable,
        };
    }

    public void Dispose() => _rolloutReader.Dispose();

    internal static bool IsActiveState(string? status, DateTimeOffset? stoppedAtUtc)
    {
        if (stoppedAtUtc is not null)
        {
            return false;
        }

        return status is CodexExecutionStatuses.Running
            or CodexExecutionStatuses.Waiting
            or CodexExecutionStatuses.Error;
    }

    private static IReadOnlyList<(CodexExecutionState State, string Source)> BuildMergedStates(
        CodexExecutionState? hookState,
        IReadOnlyList<CodexExecutionState> rolloutStates)
    {
        var states = rolloutStates
            .GroupBy(StateIdentity, StringComparer.Ordinal)
            .Select(group => (State: group.OrderByDescending(state => state.LastUpdatedAtUtc).First(),
                Source: "Local session (fallback)"))
            .ToList();

        if (hookState is null)
        {
            return states;
        }

        var matchingIndex = !string.IsNullOrWhiteSpace(hookState.SessionId)
            ? states.FindIndex(item => string.Equals(
                item.State.SessionId,
                hookState.SessionId,
                StringComparison.Ordinal))
            : -1;

        if (matchingIndex >= 0)
        {
            states[matchingIndex] = (
                Merge(hookState, states[matchingIndex].State),
                "Hooks + local session");
        }
        else if (IsActiveState(hookState.Status, hookState.StoppedAtUtc) || states.Count == 0)
        {
            states.Add((hookState, "Hooks"));
        }

        return states;
    }

    private static string StateIdentity(CodexExecutionState state)
    {
        if (!string.IsNullOrWhiteSpace(state.SessionId))
        {
            return $"session:{state.SessionId}";
        }

        if (!string.IsNullOrWhiteSpace(state.TranscriptPath))
        {
            return $"transcript:{state.TranscriptPath}";
        }

        return $"turn:{state.TurnId}|started:{state.StartedAtUtc?.UtcTicks ?? 0}";
    }

    private static bool ShouldShowInTaskList(
        CodexStatusSnapshot snapshot,
        bool unreadSignalAvailable,
        DateTimeOffset now)
    {
        if (snapshot.IsActive)
        {
            return IsFreshActive(snapshot, now);
        }

        if (snapshot.Status is not (CodexExecutionStatuses.Completed
            or CodexExecutionStatuses.Aborted
            or CodexExecutionStatuses.Error))
        {
            return false;
        }

        if (unreadSignalAvailable)
        {
            return snapshot.IsUnreadCompletion;
        }

        // The unread key is an internal Codex Desktop detail. If a future
        // version removes it, keep very recent results visible rather than
        // silently losing every completion.
        return now - snapshot.LastUpdatedAtUtc <= MissingUnreadSignalRetention;
    }

    private static bool IsFreshActive(CodexStatusSnapshot snapshot, DateTimeOffset now) =>
        snapshot.LastUpdatedAtUtc == DateTimeOffset.MinValue
        || now - snapshot.LastUpdatedAtUtc <= AbandonedActiveAge;

    private static CodexExecutionState Merge(
        CodexExecutionState hookState,
        CodexExecutionState rolloutState)
    {
        var rolloutIsNewer = rolloutState.LastUpdatedAtUtc >= hookState.LastUpdatedAtUtc;
        return new CodexExecutionState
        {
            Status = rolloutIsNewer ? rolloutState.Status : hookState.Status,
            Activity = rolloutIsNewer ? rolloutState.Activity : hookState.Activity,
            TaskTitle = rolloutIsNewer
                ? rolloutState.TaskTitle ?? hookState.TaskTitle
                : hookState.TaskTitle ?? rolloutState.TaskTitle,
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

    private static CodexStatusSnapshot Map(
        CodexExecutionState state,
        string source,
        bool isUnreadCompletion)
    {
        return new CodexStatusSnapshot
        {
            Status = state.Status,
            Activity = CodexActivityLabels.ToEnglish(state.Activity),
            TaskTitle = state.TaskTitle,
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
            IsUnreadCompletion = isUnreadCompletion,
        };
    }
}
