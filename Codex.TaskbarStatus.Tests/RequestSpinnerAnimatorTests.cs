using Codex.TaskbarStatus.Core;

namespace Codex.TaskbarStatus.Tests;

public sealed class RequestSpinnerAnimatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Catalog_ContainsEnabledSpinnersWithValidFramesAndIntervals()
    {
        Assert.Equal(47, AgentSpinnerCatalog.All.Count);
        Assert.Equal(
            AgentSpinnerCatalog.All.Count,
            AgentSpinnerCatalog.All.Select(spinner => spinner.Name).Distinct(StringComparer.Ordinal).Count());

        var excludedSpinners = new[]
        {
            "arrow",
            "clock",
            "double-arrow",
            "earth",
            "hearts",
            "moon",
            "speaker",
            "weather",
        };
        Assert.All(
            excludedSpinners,
            name => Assert.DoesNotContain(AgentSpinnerCatalog.All, spinner => spinner.Name == name));

        Assert.All(AgentSpinnerCatalog.All, spinner =>
        {
            Assert.NotEmpty(spinner.Frames);
            Assert.All(spinner.Frames, frame => Assert.NotEmpty(frame));
            Assert.InRange(spinner.Interval.TotalMilliseconds, 50, 400);
        });
    }

    [Fact]
    public void GetFrame_KeepsOneSpinnerForTheRequestAndUsesElapsedTime()
    {
        var randomCalls = 0;
        var animator = new RequestSpinnerAnimator(
        [
            new AgentSpinnerDefinition("first", ["a0", "a1"], TimeSpan.FromMilliseconds(100)),
            new AgentSpinnerDefinition("second", ["b0", "b1"], TimeSpan.FromMilliseconds(100)),
        ],
        _ =>
        {
            randomCalls++;
            return 0;
        });

        var first = animator.GetFrame(true, "session", "turn-1", Start, Start);
        var second = animator.GetFrame(true, "session", "turn-1", Start, Start.AddMilliseconds(100));
        var wrapped = animator.GetFrame(true, "session", "turn-1", Start, Start.AddMilliseconds(200));

        Assert.Equal("first", first?.Definition.Name);
        Assert.Equal("a0", first?.Text);
        Assert.Equal("a1", second?.Text);
        Assert.Equal("a0", wrapped?.Text);
        Assert.Equal(1, randomCalls);
    }

    [Fact]
    public void GetFrame_NewRequestChoosesAgainWithoutRepeatingThePreviousSpinner()
    {
        var animator = new RequestSpinnerAnimator(
        [
            new AgentSpinnerDefinition("first", ["a"], TimeSpan.FromMilliseconds(100)),
            new AgentSpinnerDefinition("second", ["b"], TimeSpan.FromMilliseconds(100)),
        ],
        _ => 0);

        var first = animator.GetFrame(true, "session", "turn-1", Start, Start);
        var second = animator.GetFrame(true, "session", "turn-2", Start.AddSeconds(1), Start.AddSeconds(1));

        Assert.Equal("first", first?.Definition.Name);
        Assert.Equal("second", second?.Definition.Name);
    }

    [Fact]
    public void GetFrame_InactiveHidesSpinnerAndNextActivationStartsANewSelection()
    {
        var randomCalls = 0;
        var animator = new RequestSpinnerAnimator(
        [new AgentSpinnerDefinition("only", ["frame"], TimeSpan.FromMilliseconds(100))],
        _ =>
        {
            randomCalls++;
            return 0;
        });

        Assert.Null(animator.GetFrame(false, "session", "turn-1", Start, Start));
        Assert.Equal(0, randomCalls);

        Assert.NotNull(animator.GetFrame(true, "session", "turn-1", Start, Start));
        Assert.Null(animator.GetFrame(false, "session", "turn-1", Start, Start.AddSeconds(1)));
        Assert.NotNull(animator.GetFrame(true, "session", "turn-2", Start.AddSeconds(2), Start.AddSeconds(2)));
        Assert.Equal(2, randomCalls);
    }

    [Fact]
    public void CreateRequestKey_UsesStartTimeOnlyWhenTurnIdIsUnavailable()
    {
        var withTurn = RequestSpinnerAnimator.CreateRequestKey("session", "turn", Start);
        var withSameTurnAndDifferentTimestamp = RequestSpinnerAnimator.CreateRequestKey(
            "session",
            "turn",
            Start.AddTicks(1));
        var withoutTurn = RequestSpinnerAnimator.CreateRequestKey("session", null, Start);
        var withoutTurnAndDifferentTimestamp = RequestSpinnerAnimator.CreateRequestKey(
            "session",
            null,
            Start.AddTicks(1));

        Assert.Equal(withTurn, withSameTurnAndDifferentTimestamp);
        Assert.NotEqual(withoutTurn, withoutTurnAndDifferentTimestamp);
    }
}
