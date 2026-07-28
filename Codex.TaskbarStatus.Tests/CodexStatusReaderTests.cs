using Codex.TaskbarStatus.Core;
using Codex.TaskbarStatus.Standalone.Widget;

namespace Codex.TaskbarStatus.Tests;

public sealed class CodexStatusReaderTests
{
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
