using Codex.TaskbarStatus.Core;

namespace Codex.TaskbarStatus.Tests;

public sealed class PreviewPresentationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnabledMetrics_RemainVisibleAtZero_InEveryLayout(bool compact)
    {
        var presentation = PreviewPresentationFactory.Create(
            showActivity: true,
            showFiles: true,
            showSubagents: true,
            showElapsed: true,
            showSpinner: true,
            isActive: false,
            compact: compact,
            filesChangedCount: 0,
            totalSubagents: 0);

        Assert.Equal("0 files", presentation.FilesText);
        Assert.Equal("0 subagents", presentation.SubagentsText);
        Assert.True(presentation.ShowActivity);
        Assert.True(presentation.ShowElapsed);
        Assert.False(presentation.ShowSpinner);
    }

    [Fact]
    public void DisabledMetrics_AreNotRendered()
    {
        var presentation = PreviewPresentationFactory.Create(
            showActivity: false,
            showFiles: false,
            showSubagents: false,
            showElapsed: false,
            showSpinner: false,
            isActive: true,
            compact: false,
            filesChangedCount: 3,
            totalSubagents: 2);

        Assert.False(presentation.ShowActivity);
        Assert.Null(presentation.FilesText);
        Assert.Null(presentation.SubagentsText);
        Assert.False(presentation.ShowElapsed);
        Assert.False(presentation.ShowSpinner);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Spinner_IsVisibleOnlyDuringActiveRequests(bool isActive, bool expected)
    {
        var presentation = PreviewPresentationFactory.Create(
            showActivity: false,
            showFiles: false,
            showSubagents: false,
            showElapsed: false,
            showSpinner: true,
            isActive: isActive,
            compact: false,
            filesChangedCount: 0,
            totalSubagents: 0);

        Assert.Equal(expected, presentation.ShowSpinner);
    }
}
