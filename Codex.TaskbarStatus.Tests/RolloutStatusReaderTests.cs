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
        var state = new RolloutStatusReader(_directory).ReadLatest();

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

        var state = new RolloutStatusReader(_directory).ReadLatest();

        Assert.NotNull(state);
        Assert.Equal("active-thread", state.SessionId);
        Assert.Equal("active-turn", state.TurnId);
        Assert.Equal(CodexExecutionStatuses.Running, state.Status);
        Assert.Equal("Gerando resposta", state.Activity);
        Assert.Equal(DateTimeOffset.Parse("2026-07-13T04:00:01Z"), state.StartedAtUtc);
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
