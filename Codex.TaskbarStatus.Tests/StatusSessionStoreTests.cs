using Codex.TaskbarStatus.Core;

namespace Codex.TaskbarStatus.Tests;

public sealed class StatusSessionStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 28, 20, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"CodexSessionStatus-{Guid.NewGuid():N}");

    [Fact]
    public async Task HookProcessor_KeepsAttentionStateForParallelSessions()
    {
        var latestStore = new StatusFileStore(Path.Combine(_directory, "status.json"));
        var sessions = new StatusSessionStore(Path.Combine(_directory, "sessions"));
        var processor = new HookEventProcessor(
            latestStore,
            new FixedTimeProvider(Now),
            sessions);

        await processor.ProcessAsync(
            """{"hook_event_name":"UserPromptSubmit","session_id":"session-a","turn_id":"turn-a"}""");
        await processor.ProcessAsync(
            """{"hook_event_name":"PermissionRequest","session_id":"session-a","turn_id":"turn-a","tool_name":"shell_command"}""");
        await processor.ProcessAsync(
            """{"hook_event_name":"UserPromptSubmit","session_id":"session-b","turn_id":"turn-b"}""");

        var states = sessions.ReadRecent();
        var waiting = Assert.Single(states, state => state.SessionId == "session-a");
        var running = Assert.Single(states, state => state.SessionId == "session-b");

        Assert.Equal(CodexExecutionStatuses.Waiting, waiting.Status);
        Assert.Equal(CodexActivityLabels.WaitingForPermission, waiting.Activity);
        Assert.Equal(CodexExecutionStatuses.Running, running.Status);
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

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
