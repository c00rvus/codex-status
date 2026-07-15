using Codex.TaskbarStatus.Core;

namespace Codex.TaskbarStatus.Tests;

public sealed class RolloutStatusReaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"CodexRollouts-{Guid.NewGuid():N}");

    [Fact]
    public async Task ReadLatest_UsesUserRolloutAndToleratesMalformedLines()
    {
        Directory.CreateDirectory(_directory);
        var userRollout = Path.Combine(_directory, "rollout-user.jsonl");
        await File.WriteAllLinesAsync(userRollout,
        [
            """{"timestamp":"2026-07-13T03:00:00Z","type":"session_meta","payload":{"id":"user-thread","thread_source":"user","cwd":"D:\\Projetos\\widget","originator":"Codex Desktop"}}""",
            "not json",
            """{"timestamp":"2026-07-13T03:00:01Z","type":"event_msg","payload":{"type":"task_started","turn_id":"turn-1"}}""",
            """{"timestamp":"2026-07-13T03:00:02Z","type":"event_msg","payload":{"type":"patch_apply_end","success":true,"changes":{"src/A.cs":{"type":"update"}}}}""",
            """{"timestamp":"2026-07-13T03:00:03Z","type":"event_msg","payload":{"type":"token_count","info":{"model_context_window":100000,"total_token_usage":{"input_tokens":500,"output_tokens":50,"total_tokens":550}}}}""",
            """{"timestamp":"2026-07-13T03:00:04Z","type":"event_msg","payload":{"type":"task_complete","turn_id":"turn-1"}}""",
        ]);
        File.SetLastWriteTimeUtc(userRollout, new DateTime(2026, 7, 13, 3, 0, 4, DateTimeKind.Utc));

        var subagentRollout = Path.Combine(_directory, "rollout-subagent.jsonl");
        await File.WriteAllLinesAsync(subagentRollout,
        [
            """{"timestamp":"2026-07-13T03:10:00Z","type":"session_meta","payload":{"id":"subagent-thread","thread_source":"subagent","originator":"Codex Desktop"}}""",
            """{"timestamp":"2026-07-13T03:10:01Z","type":"event_msg","payload":{"type":"task_started","turn_id":"subagent-turn"}}""",
        ]);
        File.SetLastWriteTimeUtc(subagentRollout, new DateTime(2026, 7, 13, 3, 10, 1, DateTimeKind.Utc));

        var before = await File.ReadAllTextAsync(userRollout);
        using var reader = new RolloutStatusReader(_directory);
        var state = reader.ReadLatest();

        Assert.NotNull(state);
        Assert.Equal("user-thread", state.SessionId);
        Assert.Equal("turn-1", state.TurnId);
        Assert.Equal(CodexExecutionStatuses.Completed, state.Status);
        Assert.Equal(1, state.ToolCount);
        Assert.Equal("src/A.cs", Assert.Single(state.FilesChanged));
        Assert.Equal(550, state.TotalTokens);
        Assert.Equal(100000, state.ModelContextWindow);
        Assert.Equal(before, await File.ReadAllTextAsync(userRollout));
    }

    [Fact]
    public async Task ReadLatest_ReadsActiveRolloutWhileCodexWriterKeepsItOpen()
    {
        Directory.CreateDirectory(_directory);
        var rollout = Path.Combine(_directory, "rollout-active.jsonl");
        await File.WriteAllLinesAsync(rollout,
        [
            """{"timestamp":"2026-07-13T04:00:00Z","type":"session_meta","payload":{"id":"active-thread","thread_source":"user","cwd":"D:\\Projetos\\widget","originator":"Codex Desktop"}}""",
            """{"timestamp":"2026-07-13T04:00:01Z","type":"event_msg","payload":{"type":"task_started","turn_id":"active-turn"}}""",
            """{"timestamp":"2026-07-13T04:00:01.200Z","type":"session_meta","payload":{"id":"active-thread","thread_source":"user","cwd":"D:\\Projetos\\widget","originator":"Codex Desktop"}}""",
            """{"timestamp":"2026-07-13T04:00:02Z","type":"event_msg","payload":{"type":"agent_message"}}""",
        ]);

        await using var codexWriter = new FileStream(
            rollout,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);

        using var reader = new RolloutStatusReader(_directory);
        var state = reader.ReadLatest();

        Assert.NotNull(state);
        Assert.Equal("active-thread", state.SessionId);
        Assert.Equal("active-turn", state.TurnId);
        Assert.Equal(CodexExecutionStatuses.Running, state.Status);
        Assert.Equal(CodexActivityLabels.GeneratingResponse, state.Activity);
        Assert.Equal(DateTimeOffset.Parse("2026-07-13T04:00:01Z"), state.StartedAtUtc);
    }

    [Fact]
    public async Task ReadLatest_ReusesCachedStateWhenFileDidNotChange()
    {
        Directory.CreateDirectory(_directory);
        var rollout = Path.Combine(_directory, "rollout-cached.jsonl");
        await WriteRolloutAsync(
            rollout,
            SessionMeta("cached-thread", "2026-07-13T05:00:00Z"),
            TaskStarted("cached-turn", "2026-07-13T05:00:01Z"));

        using var reader = new RolloutStatusReader(_directory);
        var first = Assert.IsType<CodexExecutionState>(reader.ReadLatest());
        var second = Assert.IsType<CodexExecutionState>(reader.ReadLatest());

        Assert.Same(first, second);
        Assert.Equal(CodexExecutionStatuses.Running, second.Status);
    }

    [Fact]
    public async Task ReadLatest_AppliesOnlyCompleteAppendedLines()
    {
        Directory.CreateDirectory(_directory);
        var rollout = Path.Combine(_directory, "rollout-incremental.jsonl");
        await WriteRolloutAsync(
            rollout,
            SessionMeta("incremental-thread", "2026-07-13T06:00:00Z"),
            TaskStarted("incremental-turn", "2026-07-13T06:00:01Z"));

        using var reader = new RolloutStatusReader(_directory);
        var running = Assert.IsType<CodexExecutionState>(reader.ReadLatest());

        var completedLine = TaskCompleted("incremental-turn", "2026-07-13T06:00:02Z");
        var splitAt = completedLine.Length / 2;
        await File.AppendAllTextAsync(rollout, completedLine[..splitAt]);

        var whilePartial = Assert.IsType<CodexExecutionState>(reader.ReadLatest());
        Assert.Same(running, whilePartial);
        Assert.Equal(CodexExecutionStatuses.Running, whilePartial.Status);
        Assert.Null(whilePartial.StoppedAtUtc);

        await File.AppendAllTextAsync(rollout, completedLine[splitAt..] + Environment.NewLine);

        var completed = Assert.IsType<CodexExecutionState>(reader.ReadLatest());
        Assert.Same(running, completed);
        Assert.Equal(CodexExecutionStatuses.Completed, completed.Status);
        Assert.Equal(DateTimeOffset.Parse("2026-07-13T06:00:02Z"), completed.StoppedAtUtc);
    }

    [Fact]
    public async Task ReadLatest_RebuildsStateAfterFileIsTruncated()
    {
        Directory.CreateDirectory(_directory);
        var rollout = Path.Combine(_directory, "rollout-truncated.jsonl");
        await WriteRolloutAsync(
            rollout,
            SessionMeta("old-thread", "2026-07-13T07:00:00Z"),
            TaskStarted("old-turn", "2026-07-13T07:00:01Z"),
            PatchApplied("2026-07-13T07:00:02Z"),
            TaskCompleted("old-turn", "2026-07-13T07:00:03Z"));

        using var reader = new RolloutStatusReader(_directory);
        var oldState = reader.ReadLatest();
        Assert.NotNull(oldState);
        Assert.Equal(1, oldState.ToolCount);

        await WriteRolloutAsync(
            rollout,
            SessionMeta("replacement-thread", "2026-07-13T07:10:00Z"),
            TaskStarted("replacement-turn", "2026-07-13T07:10:01Z"));

        var replacement = reader.ReadLatest();
        Assert.NotNull(replacement);
        Assert.NotSame(oldState, replacement);
        Assert.Equal("replacement-thread", replacement.SessionId);
        Assert.Equal("replacement-turn", replacement.TurnId);
        Assert.Equal(CodexExecutionStatuses.Running, replacement.Status);
        Assert.Equal(0, replacement.ToolCount);
    }

    [Fact]
    public async Task ReadLatest_DetectsSamePathReplacementEvenWhenItIsLarger()
    {
        Directory.CreateDirectory(_directory);
        var rollout = Path.Combine(_directory, "rollout-replaced.jsonl");
        await WriteRolloutAsync(
            rollout,
            SessionMeta("short-thread", "2026-07-13T08:00:00Z"),
            TaskStarted("short-turn", "2026-07-13T08:00:01Z"));

        using var reader = new RolloutStatusReader(_directory);
        var oldState = reader.ReadLatest();
        Assert.NotNull(oldState);
        var oldLength = new FileInfo(rollout).Length;

        var replacementLines = new List<string>
        {
            SessionMeta("larger-replacement-thread", "2026-07-13T08:10:00Z"),
            TaskStarted("larger-replacement-turn", "2026-07-13T08:10:01Z"),
        };
        replacementLines.AddRange(Enumerable.Repeat(
            AgentMessage("2026-07-13T08:10:02Z"),
            20));
        await WriteRolloutAsync(rollout, replacementLines.ToArray());

        Assert.True(new FileInfo(rollout).Length > oldLength);
        var replacement = reader.ReadLatest();

        Assert.NotNull(replacement);
        Assert.NotSame(oldState, replacement);
        Assert.Equal("larger-replacement-thread", replacement.SessionId);
        Assert.Equal("larger-replacement-turn", replacement.TurnId);
        Assert.Equal(CodexActivityLabels.GeneratingResponse, replacement.Activity);
    }

    [Fact]
    public async Task ReadLatest_WatcherDiscoversNewEligibleSessionAfterInitialPartialFile()
    {
        Directory.CreateDirectory(_directory);
        var oldRollout = Path.Combine(_directory, "rollout-old.jsonl");
        await WriteRolloutAsync(
            oldRollout,
            SessionMeta("old-thread", "2026-07-13T09:00:00Z"),
            TaskCompleted("old-turn", "2026-07-13T09:00:01Z"));

        using var reader = new RolloutStatusReader(_directory);
        Assert.Equal("old-thread", reader.ReadLatest()?.SessionId);

        var nestedDirectory = Path.Combine(_directory, "2026", "07", "13");
        Directory.CreateDirectory(nestedDirectory);
        var newRollout = Path.Combine(nestedDirectory, "rollout-new.jsonl");
        await File.WriteAllTextAsync(newRollout, string.Empty);

        // Exercise the race where the Created notification arrives before
        // Codex has written session_meta to the new rollout.
        Assert.Equal("old-thread", reader.ReadLatest()?.SessionId);
        await WriteRolloutAsync(
            newRollout,
            SessionMeta("new-thread", "2026-07-13T09:10:00Z"),
            TaskStarted("new-turn", "2026-07-13T09:10:01Z"));
        File.SetLastWriteTimeUtc(newRollout, DateTime.UtcNow.AddMinutes(1));

        var discovered = await WaitForStateAsync(
            reader,
            state => state?.SessionId == "new-thread",
            TimeSpan.FromSeconds(5));

        Assert.NotNull(discovered);
        Assert.Equal("new-turn", discovered.TurnId);
        Assert.Equal(CodexExecutionStatuses.Running, discovered.Status);
    }

    [Fact]
    public async Task ReadLatest_FallbackRescanFindsNewSessionAndSkipsNewerSubagent()
    {
        Directory.CreateDirectory(_directory);
        var oldRollout = Path.Combine(_directory, "rollout-old.jsonl");
        await WriteRolloutAsync(
            oldRollout,
            SessionMeta("user-thread", "2026-07-13T10:00:00Z"),
            TaskStarted("user-turn", "2026-07-13T10:00:01Z"));

        // A zero interval deterministically exercises the periodic safety scan,
        // independent of whether the OS watcher delivers its notification.
        using var reader = new RolloutStatusReader(_directory, TimeSpan.Zero);
        var original = Assert.IsType<CodexExecutionState>(reader.ReadLatest());

        var subagent = Path.Combine(_directory, "rollout-subagent-newer.jsonl");
        await File.WriteAllLinesAsync(subagent,
        [
            """{"timestamp":"2026-07-13T10:10:00Z","type":"session_meta","payload":{"id":"subagent-thread","thread_source":"subagent","originator":"Codex Desktop"}}""",
            TaskStarted("subagent-turn", "2026-07-13T10:10:01Z"),
        ]);
        File.SetLastWriteTimeUtc(subagent, DateTime.UtcNow.AddMinutes(2));

        var afterSubagent = Assert.IsType<CodexExecutionState>(reader.ReadLatest());
        Assert.Same(original, afterSubagent);
        Assert.Equal("user-thread", afterSubagent.SessionId);

        var newUser = Path.Combine(_directory, "rollout-user-newer.jsonl");
        await WriteRolloutAsync(
            newUser,
            SessionMeta("new-user-thread", "2026-07-13T10:20:00Z"),
            TaskStarted("new-user-turn", "2026-07-13T10:20:01Z"));
        File.SetLastWriteTimeUtc(newUser, DateTime.UtcNow.AddMinutes(3));

        var newest = reader.ReadLatest();
        Assert.NotNull(newest);
        Assert.Equal("new-user-thread", newest.SessionId);
        Assert.Equal("new-user-turn", newest.TurnId);
    }

    private static async Task<CodexExecutionState?> WaitForStateAsync(
        RolloutStatusReader reader,
        Func<CodexExecutionState?, bool> predicate,
        TimeSpan timeout)
    {
        var expiresAt = DateTime.UtcNow + timeout;
        CodexExecutionState? state;
        do
        {
            state = reader.ReadLatest();
            if (predicate(state))
            {
                return state;
            }

            await Task.Delay(25);
        }
        while (DateTime.UtcNow < expiresAt);

        return state;
    }

    private static Task WriteRolloutAsync(string path, params string[] lines)
    {
        return File.WriteAllLinesAsync(path, lines);
    }

    private static string SessionMeta(string sessionId, string timestamp)
    {
        return $$$"""{"timestamp":"{{{timestamp}}}","type":"session_meta","payload":{"id":"{{{sessionId}}}","thread_source":"user","originator":"Codex Desktop"}}""";
    }

    private static string TaskStarted(string turnId, string timestamp)
    {
        return $$$"""{"timestamp":"{{{timestamp}}}","type":"event_msg","payload":{"type":"task_started","turn_id":"{{{turnId}}}"}}""";
    }

    private static string TaskCompleted(string turnId, string timestamp)
    {
        return $$$"""{"timestamp":"{{{timestamp}}}","type":"event_msg","payload":{"type":"task_complete","turn_id":"{{{turnId}}}"}}""";
    }

    private static string PatchApplied(string timestamp)
    {
        return "{\"timestamp\":\"" + timestamp
            + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"patch_apply_end\",\"success\":true,\"changes\":{\"src/A.cs\":{\"type\":\"update\"}}}}";
    }

    private static string AgentMessage(string timestamp)
    {
        return $$$"""{"timestamp":"{{{timestamp}}}","type":"event_msg","payload":{"type":"agent_message"}}""";
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
