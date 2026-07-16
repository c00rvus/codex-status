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
    public void Resolve_RunningStatus_RemainsStableWithinFourSecondBucket()
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
            StartedAt.AddMilliseconds(3_999));

        Assert.Equal(first, last);
    }

    [Fact]
    public void Resolve_RunningStatus_RotatesEveryFourSeconds()
    {
        var labels = Enumerable.Range(0, 4)
            .Select(bucket => CompactActivityLabel.Resolve(
                CodexExecutionStatuses.Running,
                "request-1",
                StartedAt,
                StartedAt.AddSeconds(bucket * 4)))
            .ToArray();

        Assert.Equal(4, labels.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(labels[0], CompactActivityLabel.Resolve(
            CodexExecutionStatuses.Running,
            "request-1",
            StartedAt,
            StartedAt.AddSeconds(16)));
    }

    [Fact]
    public void Resolve_RunningStatus_IsDeterministicForSameRequestAndTime()
    {
        var now = StartedAt.AddSeconds(12);

        var first = CompactActivityLabel.Resolve(
            CodexExecutionStatuses.Running,
            "request-42",
            StartedAt,
            now);
        var second = CompactActivityLabel.Resolve(
            CodexExecutionStatuses.Running,
            "request-42",
            StartedAt,
            now);

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
