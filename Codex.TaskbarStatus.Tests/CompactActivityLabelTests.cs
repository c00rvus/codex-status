using Codex.TaskbarStatus.Core;

namespace Codex.TaskbarStatus.Tests;

public sealed class CompactActivityLabelTests
{
    private static readonly DateTimeOffset StartedAt = new(
        2026,
        7,
        15,
        12,
        0,
        0,
        TimeSpan.Zero);

    [Theory]
    [InlineData(CodexExecutionStatuses.Waiting, "Waiting")]
    [InlineData(CodexExecutionStatuses.Idle, "Ready")]
    [InlineData(CodexExecutionStatuses.Completed, "Done")]
    [InlineData(CodexExecutionStatuses.Aborted, "Stopped")]
    [InlineData(CodexExecutionStatuses.Error, "Failed")]
    [InlineData(null, "Ready")]
    [InlineData("unknown", "Ready")]
    public void Resolve_NonRunningStatus_ReturnsFixedLabel(string? status, string expected)
    {
        var label = CompactActivityLabel.Resolve(
            status,
            "request-1",
            StartedAt,
            StartedAt.AddMinutes(1));

        Assert.Equal(expected, label);
    }

    [Fact]
    public void Resolve_RunningStatus_RemainsStableForEntireExecution()
    {
        var first = CompactActivityLabel.Resolve(
            CodexExecutionStatuses.Running,
            "request-1",
            StartedAt,
            StartedAt.AddMilliseconds(100));
        var last = CompactActivityLabel.Resolve(
            CodexExecutionStatuses.Running,
            "request-1",
            StartedAt,
            StartedAt.AddDays(2));

        Assert.Equal(first, last);
    }

    [Fact]
    public void Resolve_RunningStatus_DistributesWordsAcrossExecutions()
    {
        var labels = Enumerable.Range(0, 64)
            .Select(request => CompactActivityLabel.Resolve(
                CodexExecutionStatuses.Running,
                $"request-{request}",
                StartedAt,
                StartedAt.AddHours(request)))
            .ToArray();

        Assert.True(labels.Distinct(StringComparer.Ordinal).Count() > 1);
    }

    [Fact]
    public void Resolve_RunningStatus_IsDeterministicForSameRequest()
    {
        var first = CompactActivityLabel.Resolve(
            CodexExecutionStatuses.Running,
            "request-42",
            StartedAt,
            StartedAt);
        var second = CompactActivityLabel.Resolve(
            CodexExecutionStatuses.Running,
            "request-42",
            StartedAt.AddYears(1),
            StartedAt.AddYears(2));

        Assert.Equal(first, second);
    }

    [Fact]
    public void Resolve_RunningStatus_HandlesMissingStartAndFutureStart()
    {
        var withoutStart = CompactActivityLabel.Resolve(
            CodexExecutionStatuses.Running,
            null,
            null,
            StartedAt);
        var futureStart = CompactActivityLabel.Resolve(
            CodexExecutionStatuses.Running,
            null,
            StartedAt.AddMinutes(1),
            StartedAt);

        Assert.Contains(withoutStart, new[] { "Thinking", "Working", "Reasoning", "Exploring" });
        Assert.Contains(futureStart, new[] { "Thinking", "Working", "Reasoning", "Exploring" });
    }
}
