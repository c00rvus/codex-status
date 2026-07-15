using Codex.TaskbarStatus.Standalone.Hosting;

namespace Codex.TaskbarStatus.Tests;

public sealed class TrayIconActivationFilterTests
{
    [Fact]
    public void Version4UsesMouseUpAndIgnoresDoubleClickNotification()
    {
        var filter = new TrayIconActivationFilter();

        Assert.True(filter.ShouldOpenSettings(
            TrayIconActivationFilter.WmLButtonUp,
            usesVersion4: true,
            timestampMilliseconds: 1_000));
        Assert.False(filter.ShouldOpenSettings(
            TrayIconActivationFilter.WmLButtonDoubleClick,
            usesVersion4: true,
            timestampMilliseconds: 1_001));
        Assert.False(filter.ShouldOpenSettings(
            TrayIconActivationFilter.NinSelect,
            usesVersion4: true,
            timestampMilliseconds: 1_002));
    }

    [Fact]
    public void LegacyModeUsesOneLeftButtonUpFromDoubleClickSequence()
    {
        var filter = new TrayIconActivationFilter();

        Assert.True(filter.ShouldOpenSettings(
            TrayIconActivationFilter.WmLButtonUp,
            usesVersion4: false,
            timestampMilliseconds: 1_000));
        Assert.False(filter.ShouldOpenSettings(
            TrayIconActivationFilter.WmLButtonDoubleClick,
            usesVersion4: false,
            timestampMilliseconds: 1_050));
        Assert.False(filter.ShouldOpenSettings(
            TrayIconActivationFilter.WmLButtonUp,
            usesVersion4: false,
            timestampMilliseconds: 1_100));
    }

    [Fact]
    public void KeyboardSelectionOpensSettingsInVersion4Mode()
    {
        var filter = new TrayIconActivationFilter();

        Assert.True(filter.ShouldOpenSettings(
            TrayIconActivationFilter.NinKeySelect,
            usesVersion4: true,
            timestampMilliseconds: 1_000));
    }

    [Fact]
    public void DuplicateSelectionIsAcceptedAfterDebounceWindow()
    {
        var filter = new TrayIconActivationFilter();

        Assert.True(filter.ShouldOpenSettings(
            TrayIconActivationFilter.NinSelect,
            usesVersion4: true,
            timestampMilliseconds: 1_000));
        Assert.False(filter.ShouldOpenSettings(
            TrayIconActivationFilter.NinSelect,
            usesVersion4: true,
            timestampMilliseconds: 1_100));
        Assert.True(filter.ShouldOpenSettings(
            TrayIconActivationFilter.NinSelect,
            usesVersion4: true,
            timestampMilliseconds: 1_250));
    }

    [Theory]
    [InlineData(TrayIconActivationFilter.WmContextMenu, true, true)]
    [InlineData(TrayIconActivationFilter.WmRButtonUp, true, false)]
    [InlineData(TrayIconActivationFilter.WmContextMenu, false, false)]
    [InlineData(TrayIconActivationFilter.WmRButtonUp, false, true)]
    public void ContextMenuUsesTheNotificationForTheNegotiatedVersion(
        uint notification,
        bool usesVersion4,
        bool expected)
    {
        Assert.Equal(
            expected,
            TrayIconActivationFilter.ShouldShowContextMenu(notification, usesVersion4));
    }
}
