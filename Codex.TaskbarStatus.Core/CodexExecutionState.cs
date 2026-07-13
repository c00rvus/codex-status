namespace Codex.TaskbarStatus.Core;

public static class CodexExecutionStatuses
{
    public const string Idle = "idle";
    public const string Running = "running";
    public const string Waiting = "waiting";
    public const string Completed = "completed";
    public const string Error = "error";
    public const string Aborted = "aborted";
}

public sealed record CodexExecutionState
{
    public int SchemaVersion { get; set; } = 1;

    public string Status { get; set; } = CodexExecutionStatuses.Idle;

    public string Activity { get; set; } = CodexActivityLabels.Waiting;

    public string? SessionId { get; set; }

    public string? TurnId { get; set; }

    public string? TranscriptPath { get; set; }

    public string? Cwd { get; set; }

    public string? Model { get; set; }

    public string? CurrentTool { get; set; }

    public string? ErrorMessage { get; set; }

    public int ToolCount { get; set; }

    public List<string> FilesChanged { get; set; } = [];

    public int ActiveSubagents { get; set; }

    public int TotalSubagents { get; set; }

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    public long TotalTokens { get; set; }

    public long? ModelContextWindow { get; set; }

    public DateTimeOffset? StartedAtUtc { get; set; }

    public DateTimeOffset? LastToolAtUtc { get; set; }

    public DateTimeOffset? WaitingSinceAtUtc { get; set; }

    public DateTimeOffset? StoppedAtUtc { get; set; }

    public DateTimeOffset LastUpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
