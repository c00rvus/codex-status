using Codex.TaskbarStatus.Core;
using Codex.TaskbarStatus.Standalone.Widget;

namespace Codex.TaskbarStatus.Tests;

public sealed class CodexStatusReaderTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-07-30T12:00:00Z");
    private static readonly DateTimeOffset CurrentBoot =
        DateTimeOffset.Parse("2026-07-30T10:34:00Z");

    [Fact]
    public void BuildBoard_PreBootRunningTaskIsRemovedBeforePrimaryFallback()
    {
        var ghost = new CodexStatusSnapshot
        {
            Status = CodexExecutionStatuses.Running,
            Activity = CodexActivityLabels.GeneratingResponse,
            SessionId = "shutdown-session",
            TurnId = "shutdown-turn",
            StartedAtUtc = DateTimeOffset.Parse("2026-07-30T03:51:32Z"),
            LastUpdatedAtUtc = DateTimeOffset.Parse("2026-07-30T03:51:47Z"),
        };

        var board = CodexStatusReader.BuildBoard(
            [ghost],
            reviewedTaskKeys: null,
            unreadSignalAvailable: false,
            Now,
            CurrentBoot);

        Assert.Equal(CodexExecutionStatuses.Idle, board.Primary.Status);
        Assert.Empty(board.Tasks);
        Assert.Equal(0, board.ActiveCount);
    }

    [Fact]
    public void BuildBoard_CurrentBootRunningTaskRemainsActive()
    {
        var live = new CodexStatusSnapshot
        {
            Status = CodexExecutionStatuses.Running,
            Activity = CodexActivityLabels.GeneratingResponse,
            SessionId = "current-session",
            TurnId = "current-turn",
            StartedAtUtc = DateTimeOffset.Parse("2026-07-30T11:50:00Z"),
            LastUpdatedAtUtc = DateTimeOffset.Parse("2026-07-30T11:59:00Z"),
        };

        var board = CodexStatusReader.BuildBoard(
            [live],
            reviewedTaskKeys: null,
            unreadSignalAvailable: false,
            Now,
            CurrentBoot);

        Assert.Same(live, board.Primary);
        Assert.Single(board.Tasks, live);
        Assert.Equal(1, board.ActiveCount);
    }

    [Fact]
    public void BuildBoard_PreBootCompletedTaskIsNotDiscardedAsGhost()
    {
        var completed = new CodexStatusSnapshot
        {
            Status = CodexExecutionStatuses.Completed,
            Activity = CodexActivityLabels.Completed,
            SessionId = "completed-session",
            TurnId = "completed-turn",
            StartedAtUtc = DateTimeOffset.Parse("2026-07-30T03:45:00Z"),
            StoppedAtUtc = DateTimeOffset.Parse("2026-07-30T03:50:00Z"),
            LastUpdatedAtUtc = DateTimeOffset.Parse("2026-07-30T03:50:00Z"),
            IsUnreadCompletion = true,
        };

        var board = CodexStatusReader.BuildBoard(
            [completed],
            reviewedTaskKeys: null,
            unreadSignalAvailable: true,
            Now,
            CurrentBoot);

        Assert.Same(completed, board.Primary);
        Assert.Single(board.Tasks, completed);
        Assert.Equal(0, board.ActiveCount);
    }

    [Fact]
    public void BuildBoard_ActiveTaskWithoutTimestampIsDiscarded()
    {
        var unknown = new CodexStatusSnapshot
        {
            Status = CodexExecutionStatuses.Running,
            SessionId = "unknown-session",
        };

        var board = CodexStatusReader.BuildBoard(
            [unknown],
            reviewedTaskKeys: null,
            unreadSignalAvailable: false,
            Now,
            CurrentBoot);

        Assert.Equal(CodexExecutionStatuses.Idle, board.Primary.Status);
        Assert.Empty(board.Tasks);
    }

    [Fact]
    public void Merge_PendingHookAttentionWinsOverNewerRolloutActivity()
    {
        var hook = new CodexExecutionState
        {
            SessionId = "session",
            TurnId = "turn",
            Status = CodexExecutionStatuses.Waiting,
            Activity = CodexActivityLabels.WaitingForPermission,
            CurrentTool = "shell_command",
            WaitingSinceAtUtc = DateTimeOffset.Parse("2026-07-28T20:00:00Z"),
            LastUpdatedAtUtc = DateTimeOffset.Parse("2026-07-28T20:00:00Z"),
        };
        var rollout = new CodexExecutionState
        {
            SessionId = "session",
            TurnId = "turn",
            Status = CodexExecutionStatuses.Running,
            Activity = CodexActivityLabels.GeneratingResponse,
            LastUpdatedAtUtc = DateTimeOffset.Parse("2026-07-28T20:00:01Z"),
        };

        var merged = CodexStatusReader.Merge(hook, rollout);

        Assert.Equal(CodexExecutionStatuses.Waiting, merged.Status);
        Assert.Equal(CodexActivityLabels.WaitingForPermission, merged.Activity);
        Assert.Equal("shell_command", merged.CurrentTool);
        Assert.Equal(hook.WaitingSinceAtUtc, merged.WaitingSinceAtUtc);
    }

    [Fact]
    public void Merge_StaleAttentionFromPreviousTurnDoesNotOverrideNewTurn()
    {
        var hook = new CodexExecutionState
        {
            SessionId = "session",
            TurnId = "old-turn",
            Status = CodexExecutionStatuses.Waiting,
            Activity = CodexActivityLabels.WaitingForPermission,
            WaitingSinceAtUtc = DateTimeOffset.Parse("2026-07-28T19:00:00Z"),
            StoppedAtUtc = DateTimeOffset.Parse("2026-07-28T19:01:00Z"),
            FilesChanged = ["old-file.cs"],
            ToolCount = 4,
            LastUpdatedAtUtc = DateTimeOffset.Parse("2026-07-28T19:00:00Z"),
        };
        var rollout = new CodexExecutionState
        {
            SessionId = "session",
            TurnId = "new-turn",
            Status = CodexExecutionStatuses.Running,
            Activity = CodexActivityLabels.Running,
            LastUpdatedAtUtc = DateTimeOffset.Parse("2026-07-28T20:00:00Z"),
        };

        var merged = CodexStatusReader.Merge(hook, rollout);

        Assert.Equal(CodexExecutionStatuses.Running, merged.Status);
        Assert.Equal("new-turn", merged.TurnId);
        Assert.Null(merged.WaitingSinceAtUtc);
        Assert.Null(merged.StoppedAtUtc);
        Assert.Empty(merged.FilesChanged);
        Assert.Equal(0, merged.ToolCount);
        Assert.True(CodexStatusReader.IsActiveState(merged.Status, merged.StoppedAtUtc));
    }

    [Fact]
    public void TaskKey_UsesStartTimeWhenHookTurnIdIsUnavailable()
    {
        var first = new CodexStatusSnapshot
        {
            SessionId = "session",
            StartedAtUtc = DateTimeOffset.Parse("2026-07-28T19:00:00Z"),
        };
        var second = new CodexStatusSnapshot
        {
            SessionId = "session",
            StartedAtUtc = DateTimeOffset.Parse("2026-07-28T20:00:00Z"),
        };

        Assert.NotEqual(first.TaskKey, second.TaskKey);
    }

    [Theory]
    [InlineData(CodexExecutionStatuses.Waiting, true)]
    [InlineData(CodexExecutionStatuses.Error, true)]
    [InlineData(CodexExecutionStatuses.Running, false)]
    [InlineData(CodexExecutionStatuses.Completed, false)]
    public void Snapshot_ClassifiesAttentionStates(string status, bool expected)
    {
        var snapshot = new CodexStatusSnapshot { Status = status };

        Assert.Equal(expected, snapshot.RequiresAttention);
    }
}
