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
    public string? CurrentTool { get; init; }
    public string? ErrorMessage { get; init; }
    public int ToolCount { get; init; }
    public int FilesChangedCount { get; init; }
    public int TotalSubagents { get; init; }
    public DateTimeOffset? StartedAtUtc { get; init; }
    public DateTimeOffset? WaitingSinceAtUtc { get; init; }
    public DateTimeOffset? StoppedAtUtc { get; init; }
    public DateTimeOffset LastUpdatedAtUtc { get; init; } = DateTimeOffset.MinValue;
    public string Source { get; init; } = "None";
    public bool IsUnreadCompletion { get; init; }

    public bool IsActive => CodexStatusReader.IsActiveState(Status, StoppedAtUtc);

    public bool RequiresAttention =>
        Status is CodexExecutionStatuses.Waiting or CodexExecutionStatuses.Error;

    public bool ShouldNotifyAttention =>
        Status == CodexExecutionStatuses.Waiting ||
        (Status == CodexExecutionStatuses.Error && StoppedAtUtc is not null);

    public string TaskKey => !string.IsNullOrWhiteSpace(SessionId)
        ? !string.IsNullOrWhiteSpace(TurnId)
            ? $"session:{SessionId}|turn:{TurnId}"
            : $"session:{SessionId}|started:{StartedAtUtc?.UtcTicks ?? 0}"
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

    public int RunningCount =>
        Tasks.Count(task => task.Status == CodexExecutionStatuses.Running);

    public int ReadyCount => Tasks.Count(task => !task.IsActive && !task.RequiresAttention);

    public int AttentionCount => Tasks.Count(task => task.RequiresAttention);
}

internal sealed class CodexStatusReader : IDisposable
{
    private static readonly TimeSpan HookRefreshInterval = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan AbandonedActiveAge = TimeSpan.FromDays(7);
    private static readonly TimeSpan BootBoundaryTolerance = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MissingUnreadSignalRetention = TimeSpan.FromMinutes(15);

    private readonly StatusFileStore _statusStore = new();
    private readonly StatusSessionStore _statusSessionStore = new();
    private readonly RolloutStatusReader _rolloutReader = new();
    private readonly CodexDesktopUnreadThreadReader _unreadReader = new();
    private readonly DateTimeOffset _activeExecutionEpochUtc = EstimateActiveExecutionEpochUtc();
    private IReadOnlyList<CodexExecutionState> _cachedHookStates = [];
    private IReadOnlyList<CodexExecutionState> _cachedRollouts = [];
    private readonly HashSet<string> _cachedPrioritySessionIds =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _cachedReviewedTaskKeys =
        new(StringComparer.Ordinal);
    private CodexDesktopUnreadState? _cachedUnreadState;
    private CodexStatusBoardSnapshot? _cachedBoard;
    private DateTimeOffset _nextHookReadAt = DateTimeOffset.MinValue;
    private bool _hookStatesInitialized;
    private bool _rolloutsInitialized;

    public CodexStatusSnapshot Read() => ReadBoard().Primary;

    public CodexStatusBoardSnapshot ReadBoard(
        IReadOnlySet<string>? reviewedTaskKeys = null)
    {
        var now = DateTimeOffset.UtcNow;
        var unread = _unreadReader.Read();
        var boardInvalidated = !ReferenceEquals(unread, _cachedUnreadState);
        _cachedUnreadState = unread;

        if (!_hookStatesInitialized || now >= _nextHookReadAt)
        {
            _cachedHookStates = ReadHookStates();
            _hookStatesInitialized = true;
            _nextHookReadAt = now + HookRefreshInterval;
            boardInvalidated = true;
        }

        var prioritySessionsChanged =
            !_cachedPrioritySessionIds.SetEquals(unread.ThreadIds);
        if (!_rolloutsInitialized
            || prioritySessionsChanged
            || _rolloutReader.HasPendingChanges)
        {
            _cachedRollouts = _rolloutReader.ReadRecent(unread.ThreadIds);
            _rolloutsInitialized = true;
            _cachedPrioritySessionIds.Clear();
            _cachedPrioritySessionIds.UnionWith(unread.ThreadIds);
            boardInvalidated = true;
        }

        var reviewedTasksChanged = reviewedTaskKeys is null
            ? _cachedReviewedTaskKeys.Count > 0
            : !_cachedReviewedTaskKeys.SetEquals(reviewedTaskKeys);
        if (reviewedTasksChanged)
        {
            _cachedReviewedTaskKeys.Clear();
            if (reviewedTaskKeys is not null)
            {
                _cachedReviewedTaskKeys.UnionWith(reviewedTaskKeys);
            }

            boardInvalidated = true;
        }

        if (!boardInvalidated && _cachedBoard is not null)
        {
            return _cachedBoard;
        }

        var states = BuildMergedStates(_cachedHookStates, _cachedRollouts);
        var mapped = states
            .Select(item => Map(
                item.State,
                item.Source,
                item.State.SessionId is { } sessionId && unread.ThreadIds.Contains(sessionId)))
            .ToArray();
        _cachedBoard = BuildBoard(
            mapped,
            reviewedTaskKeys,
            unread.IsAvailable,
            now,
            _activeExecutionEpochUtc);
        return _cachedBoard;
    }

    public void Dispose() => _rolloutReader.Dispose();

    internal static CodexStatusBoardSnapshot BuildBoard(
        IReadOnlyList<CodexStatusSnapshot> mapped,
        IReadOnlySet<string>? reviewedTaskKeys,
        bool unreadSignalAvailable,
        DateTimeOffset now,
        DateTimeOffset activeExecutionEpochUtc)
    {
        // An execution cannot survive a Windows restart. Filter abandoned
        // active states before every selection, including the final primary
        // fallback, so an abruptly interrupted rollout cannot restart the
        // taskbar animation after the next boot.
        var currentMapped = mapped
            .Where(snapshot =>
                !snapshot.IsActive ||
                IsFreshActive(snapshot, now, activeExecutionEpochUtc))
            .ToArray();
        var visibleMapped = reviewedTaskKeys is null || reviewedTaskKeys.Count == 0
            ? currentMapped
            : currentMapped
                .Where(snapshot =>
                    snapshot.IsActive ||
                    !reviewedTaskKeys.Contains(snapshot.TaskKey))
                .ToArray();

        var primary = visibleMapped
            .Where(snapshot => snapshot.RequiresAttention && IsFresh(snapshot, now))
            .OrderByDescending(snapshot => snapshot.LastUpdatedAtUtc)
            .FirstOrDefault()
            ?? visibleMapped
            .Where(snapshot => snapshot.IsActive)
            .OrderByDescending(snapshot => snapshot.LastUpdatedAtUtc)
            .FirstOrDefault()
            ?? visibleMapped.OrderByDescending(snapshot => snapshot.LastUpdatedAtUtc).FirstOrDefault()
            ?? new CodexStatusSnapshot();

        var tasks = visibleMapped
            .Where(snapshot => ShouldShowInTaskList(snapshot, unreadSignalAvailable, now))
            .OrderByDescending(snapshot => snapshot.IsActive)
            .ThenByDescending(snapshot => snapshot.LastUpdatedAtUtc)
            .Take(20)
            .ToArray();

        return new CodexStatusBoardSnapshot
        {
            Primary = primary,
            Tasks = tasks,
            UnreadSignalAvailable = unreadSignalAvailable,
        };
    }

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

    private IReadOnlyList<CodexExecutionState> ReadHookStates()
    {
        var states = _statusSessionStore.ReadRecent().ToList();
        if (File.Exists(StatusFileStore.DefaultPath))
        {
            states.Add(_statusStore.Read());
        }

        return states
            .Where(state => state.Status != CodexExecutionStatuses.Idle)
            .GroupBy(StateIdentity, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(state => state.LastUpdatedAtUtc)
                .First())
            .ToArray();
    }

    private static IReadOnlyList<(CodexExecutionState State, string Source)> BuildMergedStates(
        IReadOnlyList<CodexExecutionState> hookStates,
        IReadOnlyList<CodexExecutionState> rolloutStates)
    {
        var states = rolloutStates
            .GroupBy(StateIdentity, StringComparer.Ordinal)
            .Select(group => (State: group.OrderByDescending(state => state.LastUpdatedAtUtc).First(),
                Source: "Local session (fallback)"))
            .ToList();

        foreach (var hookState in hookStates)
        {
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
            // Active snapshots have already passed the boot/session boundary
            // filter in BuildBoard.
            return true;
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

    private static bool IsFreshActive(
        CodexStatusSnapshot snapshot,
        DateTimeOffset now,
        DateTimeOffset activeExecutionEpochUtc) =>
        snapshot.LastUpdatedAtUtc != DateTimeOffset.MinValue
        && snapshot.LastUpdatedAtUtc >= activeExecutionEpochUtc
        && now - snapshot.LastUpdatedAtUtc <= AbandonedActiveAge;

    private static bool IsFresh(CodexStatusSnapshot snapshot, DateTimeOffset now) =>
        snapshot.LastUpdatedAtUtc == DateTimeOffset.MinValue
        || now - snapshot.LastUpdatedAtUtc <= AbandonedActiveAge;

    private static DateTimeOffset EstimateActiveExecutionEpochUtc()
    {
        try
        {
            var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
            return DateTimeOffset.UtcNow - uptime - BootBoundaryTolerance;
        }
        catch (ArgumentOutOfRangeException)
        {
            // If the system clock is outside DateTimeOffset's representable
            // range, retain the legacy age guard instead of hiding live work.
            return DateTimeOffset.MinValue;
        }
    }

    internal static CodexExecutionState Merge(
        CodexExecutionState hookState,
        CodexExecutionState rolloutState)
    {
        var differentKnownTurns =
            !string.IsNullOrWhiteSpace(hookState.TurnId) &&
            !string.IsNullOrWhiteSpace(rolloutState.TurnId) &&
            !string.Equals(
                hookState.TurnId,
                rolloutState.TurnId,
                StringComparison.Ordinal);
        var hookHasPendingAttention =
            !differentKnownTurns &&
            hookState.Status == CodexExecutionStatuses.Waiting &&
            hookState.WaitingSinceAtUtc is not null;
        var rolloutIsNewer =
            !hookHasPendingAttention &&
            rolloutState.LastUpdatedAtUtc >= hookState.LastUpdatedAtUtc;
        var newerState = rolloutIsNewer ? rolloutState : hookState;
        return new CodexExecutionState
        {
            Status = rolloutIsNewer ? rolloutState.Status : hookState.Status,
            Activity = rolloutIsNewer ? rolloutState.Activity : hookState.Activity,
            TaskTitle = differentKnownTurns
                ? newerState.TaskTitle
                : rolloutIsNewer
                    ? rolloutState.TaskTitle ?? hookState.TaskTitle
                    : hookState.TaskTitle ?? rolloutState.TaskTitle,
            SessionId = hookState.SessionId ?? rolloutState.SessionId,
            TurnId = rolloutIsNewer
                ? rolloutState.TurnId ?? hookState.TurnId
                : hookState.TurnId ?? rolloutState.TurnId,
            TranscriptPath = hookState.TranscriptPath ?? rolloutState.TranscriptPath,
            Cwd = hookState.Cwd ?? rolloutState.Cwd,
            Model = hookState.Model ?? rolloutState.Model,
            CurrentTool = differentKnownTurns
                ? newerState.CurrentTool
                : hookHasPendingAttention
                    ? hookState.CurrentTool
                    : rolloutIsNewer
                        ? rolloutState.CurrentTool ?? hookState.CurrentTool
                        : hookState.CurrentTool ?? rolloutState.CurrentTool,
            ErrorMessage = differentKnownTurns
                ? newerState.ErrorMessage
                : rolloutIsNewer
                    ? rolloutState.ErrorMessage ?? hookState.ErrorMessage
                    : hookState.ErrorMessage ?? rolloutState.ErrorMessage,
            ToolCount = differentKnownTurns
                ? newerState.ToolCount
                : Math.Max(hookState.ToolCount, rolloutState.ToolCount),
            FilesChanged = differentKnownTurns
                ? [.. newerState.FilesChanged]
                : hookState.FilesChanged
                    .Concat(rolloutState.FilesChanged)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            ActiveSubagents = differentKnownTurns
                ? newerState.ActiveSubagents
                : hookState.ActiveSubagents,
            TotalSubagents = differentKnownTurns
                ? newerState.TotalSubagents
                : Math.Max(hookState.TotalSubagents, rolloutState.TotalSubagents),
            InputTokens = differentKnownTurns
                ? newerState.InputTokens
                : Math.Max(hookState.InputTokens, rolloutState.InputTokens),
            OutputTokens = differentKnownTurns
                ? newerState.OutputTokens
                : Math.Max(hookState.OutputTokens, rolloutState.OutputTokens),
            TotalTokens = differentKnownTurns
                ? newerState.TotalTokens
                : Math.Max(hookState.TotalTokens, rolloutState.TotalTokens),
            ModelContextWindow = hookState.ModelContextWindow ?? rolloutState.ModelContextWindow,
            StartedAtUtc = differentKnownTurns
                ? newerState.StartedAtUtc
                : rolloutIsNewer
                    ? rolloutState.StartedAtUtc ?? hookState.StartedAtUtc
                    : hookState.StartedAtUtc ?? rolloutState.StartedAtUtc,
            WaitingSinceAtUtc = differentKnownTurns
                ? newerState.WaitingSinceAtUtc
                : hookHasPendingAttention
                    ? hookState.WaitingSinceAtUtc
                    : rolloutIsNewer
                        ? rolloutState.WaitingSinceAtUtc ?? hookState.WaitingSinceAtUtc
                        : hookState.WaitingSinceAtUtc ?? rolloutState.WaitingSinceAtUtc,
            StoppedAtUtc = differentKnownTurns
                ? newerState.StoppedAtUtc
                : rolloutIsNewer
                    ? rolloutState.StoppedAtUtc ?? hookState.StoppedAtUtc
                    : hookState.StoppedAtUtc ?? rolloutState.StoppedAtUtc,
            LastUpdatedAtUtc = hookState.LastUpdatedAtUtc >= rolloutState.LastUpdatedAtUtc
                ? hookState.LastUpdatedAtUtc
                : rolloutState.LastUpdatedAtUtc,
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
            CurrentTool = state.CurrentTool,
            ErrorMessage = state.ErrorMessage,
            ToolCount = state.ToolCount,
            FilesChangedCount = state.FilesChanged.Count,
            TotalSubagents = state.TotalSubagents,
            StartedAtUtc = state.StartedAtUtc,
            WaitingSinceAtUtc = state.WaitingSinceAtUtc,
            StoppedAtUtc = state.StoppedAtUtc,
            LastUpdatedAtUtc = state.LastUpdatedAtUtc,
            Source = source,
            IsUnreadCompletion = isUnreadCompletion,
        };
    }
}
