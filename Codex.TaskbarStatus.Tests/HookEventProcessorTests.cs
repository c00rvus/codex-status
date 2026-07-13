using Codex.TaskbarStatus.Core;

namespace Codex.TaskbarStatus.Tests;

public sealed class HookEventProcessorTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 13, 3, 15, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"CodexTaskbarStatus-{Guid.NewGuid():N}");
    private readonly StatusFileStore _store;
    private readonly HookEventProcessor _processor;

    public HookEventProcessorTests()
    {
        _store = new StatusFileStore(Path.Combine(_directory, "status.json"));
        _processor = new HookEventProcessor(_store, new FixedTimeProvider(Now));
    }

    [Fact]
    public async Task SessionStart_CapturesExecutionMetadataAndPersistsJson()
    {
        var state = await _processor.ProcessAsync("""
            {
              "hook_event_name": "SessionStart",
              "session_id": "session-42",
              "turn_id": "turn-7",
              "transcript_path": "C:\\rollouts\\session-42.jsonl",
              "cwd": "D:\\Work\\sample-project",
              "model": "gpt-5.4"
            }
            """);

        Assert.Equal(CodexExecutionStatuses.Running, state.Status);
        Assert.Equal("session-42", state.SessionId);
        Assert.Equal("turn-7", state.TurnId);
        Assert.Equal(@"C:\rollouts\session-42.jsonl", state.TranscriptPath);
        Assert.Equal(@"D:\Work\sample-project", state.Cwd);
        Assert.Equal("gpt-5.4", state.Model);
        Assert.Equal(Now, state.StartedAtUtc);
        Assert.Equal(Now, state.LastUpdatedAtUtc);

        var persisted = await _store.ReadAsync();
        Assert.Equal("session-42", persisted.SessionId);
        Assert.Contains("\"sessionId\"", await File.ReadAllTextAsync(_store.FilePath));
    }

    [Fact]
    public async Task ToolEvents_CountToolsAndCollectUniqueApplyPatchFiles()
    {
        await _processor.ProcessAsync("""{"hook_event_name":"UserPromptSubmit","session_id":"s1","turn_id":"t1"}""");

        var state = await _processor.ProcessAsync("""
            {
              "hook_event_name": "PreToolUse",
              "tool_name": "functions.apply_patch",
              "tool_input": {
                "patch": "*** Begin Patch\n*** Update File: src/A.cs\n*** Add File: src/B.cs\n*** Update File: SRC/a.cs\n*** End Patch"
              }
            }
            """);

        Assert.Equal(1, state.ToolCount);
        Assert.Equal("functions.apply_patch", state.CurrentTool);
        Assert.Equal(new[] { "src/A.cs", "src/B.cs" }, state.FilesChanged.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(Now, state.LastToolAtUtc);

        state = await _processor.ProcessAsync("""
            {
              "hookEventName": "PostToolUse",
              "toolName": "apply_patch",
              "toolInput": { "file_path": "src/C.cs" }
            }
            """);

        Assert.Equal(1, state.ToolCount);
        Assert.Equal(3, state.FilesChanged.Count);
        Assert.Contains("src/C.cs", state.FilesChanged);
    }

    [Fact]
    public async Task PermissionAndSubagentEvents_ExposeWaitingAndActivityCounts()
    {
        await _processor.ProcessAsync("""{"hook_event_name":"UserPromptSubmit"}""");
        var state = await _processor.ProcessAsync("""{"hook_event_name":"SubagentStart"}""");
        state = await _processor.ProcessAsync("""{"hook_event_name":"SubagentStart"}""");

        Assert.Equal(2, state.ActiveSubagents);
        Assert.Equal(2, state.TotalSubagents);

        state = await _processor.ProcessAsync("""{"hook_event_name":"SubagentStop"}""");
        Assert.Equal(1, state.ActiveSubagents);

        state = await _processor.ProcessAsync("""
            { "hook_event_name": "PermissionRequest", "tool_name": "shell_command" }
            """);

        Assert.Equal(CodexExecutionStatuses.Waiting, state.Status);
        Assert.Equal("Aguardando permissão", state.Activity);
        Assert.Equal("shell_command", state.CurrentTool);
        Assert.Equal(Now, state.WaitingSinceAtUtc);
    }

    [Fact]
    public async Task Stop_MarksTheExecutionCompleted()
    {
        await _processor.ProcessAsync("""{"hook_event_name":"UserPromptSubmit"}""");
        var state = await _processor.ProcessAsync("""{"hook_event_name":"Stop"}""");

        Assert.Equal(CodexExecutionStatuses.Completed, state.Status);
        Assert.Equal("Concluído", state.Activity);
        Assert.Equal(Now, state.StoppedAtUtc);
        Assert.Null(state.CurrentTool);
        Assert.Null(state.WaitingSinceAtUtc);
    }

    [Fact]
    public async Task UserPromptSubmit_WithoutTurnIdDoesNotReuseThePreviousRequestId()
    {
        await _processor.ProcessAsync(
            """{"hook_event_name":"SessionStart","session_id":"session","turn_id":"old-turn"}""");

        var state = await _processor.ProcessAsync(
            """{"hook_event_name":"UserPromptSubmit","session_id":"session"}""");

        Assert.Null(state.TurnId);
        Assert.Equal(Now, state.StartedAtUtc);
    }

    [Theory]
    [InlineData("failed", CodexExecutionStatuses.Error)]
    [InlineData("cancelled_by_user", CodexExecutionStatuses.Aborted)]
    public async Task StopReason_DistinguishesErrorAndAborted(string reason, string expectedStatus)
    {
        var state = await _processor.ProcessAsync($$"""
            { "hook_event_name": "Stop", "stop_reason": "{{reason}}" }
            """);

        Assert.Equal(expectedStatus, state.Status);
    }

    [Fact]
    public async Task InvalidJson_DoesNotOverwriteTheLastValidState()
    {
        await _processor.ProcessAsync("""{"hook_event_name":"SessionStart","session_id":"keep-me"}""");
        var before = await File.ReadAllTextAsync(_store.FilePath);

        var state = await _processor.ProcessAsync("{ definitely not json");
        var after = await File.ReadAllTextAsync(_store.FilePath);

        Assert.Equal("keep-me", state.SessionId);
        Assert.Equal(before, after);
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
