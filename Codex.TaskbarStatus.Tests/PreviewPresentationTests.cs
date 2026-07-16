using Codex.TaskbarStatus.Core;

namespace Codex.TaskbarStatus.Tests;

public sealed class PreviewPresentationTests
{
    [Fact]
    public void EnabledMetrics_RemainVisibleAtZero_InRegularLayout()
    {
        var presentation = PreviewPresentationFactory.Create(
            showActivity: true,
            showFiles: true,
            showSubagents: true,
            showElapsed: true,
            showSpinner: true,
            isActive: false,
            compact: false,
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

    [Fact]
    public void ActivePreview_PlacesSpinnerBeforeActivityInTheLeadingField()
    {
        var presentation = PreviewPresentationFactory.Create(
            showActivity: true,
            showFiles: false,
            showSubagents: false,
            showElapsed: false,
            showSpinner: true,
            isActive: true,
            compact: false,
            filesChangedCount: 0,
            totalSubagents: 0);

        Assert.Equal(
            [PreviewLeadingItem.Spinner, PreviewLeadingItem.Activity],
            presentation.LeadingItems);
    }

    [Fact]
    public void CompactLayout_ShowsOnlyActivityAndEnabledUsageLimits()
    {
        var regular = PreviewPresentationFactory.Create(
            new CodexWidgetSettings { Compact = false },
            isActive: false,
            filesChangedCount: 0,
            totalSubagents: 0);
        var compact = PreviewPresentationFactory.Create(
            new CodexWidgetSettings { Compact = true },
            isActive: false,
            filesChangedCount: 0,
            totalSubagents: 0);

        Assert.True(compact.HorizontalPadding < regular.HorizontalPadding);
        Assert.True(compact.RowSpacing < regular.RowSpacing);
        Assert.Equal(6, compact.RowSpacing);
        Assert.True(compact.LeadingSpacing < regular.LeadingSpacing);
        Assert.True(compact.TextFontSize < regular.TextFontSize);
        Assert.Equal(
            [
                PreviewIndicatorKind.Activity,
                PreviewIndicatorKind.FiveHourUsage,
                PreviewIndicatorKind.WeeklyUsage,
            ],
            compact.Items.Select(item => item.Kind));
        Assert.Null(compact.FilesText);
        Assert.Null(compact.SubagentsText);
        Assert.False(compact.ShowElapsed);
        Assert.True(compact.BatteryWidth < regular.BatteryWidth);
        Assert.True(compact.BatteryHeight < regular.BatteryHeight);
        Assert.True(compact.BatteryTerminalHeight < regular.BatteryTerminalHeight);
        Assert.True(compact.UsageSpacing < regular.UsageSpacing);
        Assert.True(compact.UsageMaxWidth < regular.UsageMaxWidth);
    }

    [Fact]
    public void CompactActivePreview_PlacesSpinnerBeforeActivity()
    {
        var settings = new CodexWidgetSettings
        {
            Compact = true,
            ShowPulse = true,
            ShowActivity = true,
        };

        var presentation = PreviewPresentationFactory.Create(
            settings,
            isActive: true,
            filesChangedCount: 3,
            totalSubagents: 2);
        var activity = Assert.Single(
            presentation.Items,
            item => item.Kind == PreviewIndicatorKind.Activity);

        Assert.Equal(
            [PreviewLeadingItem.Spinner, PreviewLeadingItem.Activity],
            activity.LeadingItems);
    }

    [Fact]
    public void CompactLayout_ExcludesDisabledUsageLimits()
    {
        var settings = new CodexWidgetSettings
        {
            Compact = true,
            ShowFiveHourUsage = false,
            ShowWeeklyUsage = true,
        };

        var presentation = PreviewPresentationFactory.Create(
            settings,
            isActive: false,
            filesChangedCount: 3,
            totalSubagents: 2);

        Assert.Equal(
            [PreviewIndicatorKind.Activity, PreviewIndicatorKind.WeeklyUsage],
            presentation.Items.Select(item => item.Kind));
    }

    [Fact]
    public void ConfiguredOrder_DrivesEveryVisiblePreviewItem()
    {
        var settings = new CodexWidgetSettings();
        settings.MoveIndicator(PreviewIndicatorKind.WeeklyUsage, -5);
        settings.MoveIndicator(PreviewIndicatorKind.Elapsed, -3);

        var presentation = PreviewPresentationFactory.Create(
            settings,
            isActive: false,
            filesChangedCount: 0,
            totalSubagents: 0);

        Assert.Equal(
            [
                PreviewIndicatorKind.WeeklyUsage,
                PreviewIndicatorKind.Elapsed,
                PreviewIndicatorKind.Activity,
                PreviewIndicatorKind.Files,
                PreviewIndicatorKind.Subagents,
                PreviewIndicatorKind.FiveHourUsage,
            ],
            presentation.Items.Select(item => item.Kind));
        Assert.Equal("0 files", presentation.FilesText);
        Assert.Equal("0 subagents", presentation.SubagentsText);
        Assert.True(presentation.ShowFiveHourUsage);
        Assert.True(presentation.ShowWeeklyUsage);
    }

    [Fact]
    public void DisabledUsageIndicatorsAreExcludedWithoutChangingStoredOrder()
    {
        var settings = new CodexWidgetSettings
        {
            ShowFiveHourUsage = false,
            ShowWeeklyUsage = false,
        };

        var presentation = PreviewPresentationFactory.Create(
            settings,
            isActive: false,
            filesChangedCount: 0,
            totalSubagents: 0);

        Assert.False(presentation.ShowFiveHourUsage);
        Assert.False(presentation.ShowWeeklyUsage);
        Assert.Equal(PreviewIndicatorOrder.Default, settings.IndicatorOrder);
    }

    [Fact]
    public void ReorderedActivityKeepsSpinnerBeforeStatusInsideTheSameItem()
    {
        var settings = new CodexWidgetSettings();
        settings.MoveIndicator(PreviewIndicatorKind.Activity, 5);

        var presentation = PreviewPresentationFactory.Create(
            settings,
            isActive: true,
            filesChangedCount: 0,
            totalSubagents: 0);
        var activity = Assert.Single(
            presentation.Items,
            item => item.Kind == PreviewIndicatorKind.Activity);

        Assert.Equal(
            [PreviewLeadingItem.Spinner, PreviewLeadingItem.Activity],
            activity.LeadingItems);
        Assert.Equal(PreviewIndicatorKind.Activity, presentation.Items[^1].Kind);
    }
}
