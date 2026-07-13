using System.Text.Json;
using Codex.TaskbarStatus.Core;

namespace Codex.TaskbarStatus.Tests;

public sealed class CodexWidgetSettingsTests
{
    [Fact]
    public void FromJson_EmptySettings_UsesDefaults()
    {
        var settings = CodexWidgetSettings.FromJson("{}");

        Assert.True(settings.ShowActivity);
        Assert.True(settings.ShowFiles);
        Assert.True(settings.ShowAgents);
        Assert.True(settings.ShowElapsed);
        Assert.True(settings.ShowFiveHourUsage);
        Assert.True(settings.ShowWeeklyUsage);
        Assert.True(settings.ShowPulse);
        Assert.False(settings.Compact);
        Assert.False(settings.HideWhenIdle);
        Assert.Equal(CodexWidgetSettings.DefaultSpinnerColor, settings.SpinnerColor);
        Assert.Equal(PreviewIndicatorOrder.Default, settings.IndicatorOrder);
    }

    [Fact]
    public void FromJson_LegacyJson_PreservesFlagsAndAddsDefaultColor()
    {
        var settings = CodexWidgetSettings.FromJson(
            """{"showActivity":false,"showFiles":true,"showAgents":false,"showElapsed":true,"showPulse":false,"compact":true,"hideWhenIdle":true}""");

        Assert.False(settings.ShowActivity);
        Assert.True(settings.ShowFiles);
        Assert.False(settings.ShowAgents);
        Assert.True(settings.ShowElapsed);
        Assert.True(settings.ShowFiveHourUsage);
        Assert.True(settings.ShowWeeklyUsage);
        Assert.False(settings.ShowPulse);
        Assert.True(settings.Compact);
        Assert.True(settings.HideWhenIdle);
        Assert.Equal(CodexWidgetSettings.DefaultSpinnerColor, settings.SpinnerColor);
        Assert.Equal(PreviewIndicatorOrder.Default, settings.IndicatorOrder);
    }

    [Theory]
    [InlineData("#abcdef", "#ABCDEF")]
    [InlineData("  #0123aF  ", "#0123AF")]
    public void FromJson_ValidSpinnerColor_NormalizesToCanonicalRgb(string value, string expected)
    {
        var settings = CodexWidgetSettings.FromJson($$"""{"spinnerColor":"{{value}}"}""");

        Assert.Equal(expected, settings.SpinnerColor);
    }

    [Theory]
    [InlineData("#abcdef", "#ABCDEF")]
    [InlineData("  #0123aF  ", "#0123AF")]
    public void TryNormalizeSpinnerColor_ValidValue_ReturnsCanonicalRgb(
        string value,
        string expected)
    {
        var isValid = CodexWidgetSettings.TryNormalizeSpinnerColor(value, out var normalized);

        Assert.True(isValid);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("3B9EFF")]
    [InlineData("#12345")]
    [InlineData("#1234567")]
    [InlineData("#GG00FF")]
    public void TryNormalizeSpinnerColor_InvalidValue_ReturnsFalse(string? value)
    {
        var isValid = CodexWidgetSettings.TryNormalizeSpinnerColor(value, out var normalized);

        Assert.False(isValid);
        Assert.Equal(CodexWidgetSettings.DefaultSpinnerColor, normalized);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("\"3B9EFF\"")]
    [InlineData("\"#12345\"")]
    [InlineData("\"#1234567\"")]
    [InlineData("\"#GG00FF\"")]
    [InlineData("123")]
    public void FromJson_InvalidSpinnerColor_FallsBackWithoutResettingFlags(string jsonValue)
    {
        var settings = CodexWidgetSettings.FromJson(
            $$"""{"showActivity":false,"compact":true,"spinnerColor":{{jsonValue}}}""");

        Assert.False(settings.ShowActivity);
        Assert.True(settings.Compact);
        Assert.Equal(CodexWidgetSettings.DefaultSpinnerColor, settings.SpinnerColor);
    }

    [Fact]
    public void ToJson_RoundTripUsesCamelCaseAndPreservesValues()
    {
        var original = new CodexWidgetSettings
        {
            ShowFiles = false,
            ShowAgents = false,
            ShowFiveHourUsage = false,
            Compact = true,
            SpinnerColor = "#12abef",
        };
        Assert.True(original.MoveIndicator(PreviewIndicatorKind.WeeklyUsage, -5));

        var json = original.ToJson();
        using var document = JsonDocument.Parse(json);
        Assert.Equal("#12ABEF", document.RootElement.GetProperty("spinnerColor").GetString());
        Assert.True(document.RootElement.GetProperty("compact").GetBoolean());
        Assert.False(document.RootElement.GetProperty("showFiles").GetBoolean());
        Assert.False(document.RootElement.GetProperty("showFiveHourUsage").GetBoolean());
        Assert.Equal(
            "weeklyUsage",
            document.RootElement.GetProperty("indicatorOrder")[0].GetString());

        var restored = CodexWidgetSettings.FromJson(json);
        Assert.Equal("#12ABEF", restored.SpinnerColor);
        Assert.True(restored.Compact);
        Assert.False(restored.ShowFiles);
        Assert.False(restored.ShowAgents);
        Assert.False(restored.ShowFiveHourUsage);
        Assert.Equal(original.IndicatorOrder, restored.IndicatorOrder);
    }

    [Fact]
    public void FromJson_MalformedJson_ReturnsDefaults()
    {
        var settings = CodexWidgetSettings.FromJson("{not-json");

        Assert.Equal(CodexWidgetSettings.DefaultSpinnerColor, settings.SpinnerColor);
        Assert.True(settings.ShowActivity);
        Assert.False(settings.Compact);
    }

    [Fact]
    public void FromJson_UnknownPropertiesAreIgnoredCaseInsensitively()
    {
        var settings = CodexWidgetSettings.FromJson(
            """{"SHOWACTIVITY":false,"SPINNERCOLOR":"#445566","futureSetting":42}""");

        Assert.False(settings.ShowActivity);
        Assert.Equal("#445566", settings.SpinnerColor);
    }

    [Fact]
    public void FromJson_OrderIgnoresUnknownsAndDuplicatesThenAppendsMissingIndicators()
    {
        var settings = CodexWidgetSettings.FromJson(
            """{"indicatorOrder":["weeklyUsage","FILES","futureMetric","weeklyUsage","activity"]}""");

        Assert.Equal(
            [
                PreviewIndicatorKind.WeeklyUsage,
                PreviewIndicatorKind.Files,
                PreviewIndicatorKind.Activity,
                PreviewIndicatorKind.Subagents,
                PreviewIndicatorKind.Elapsed,
                PreviewIndicatorKind.FiveHourUsage,
            ],
            settings.IndicatorOrder);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("{}")]
    public void FromJson_InvalidOrderUsesDefaultWithoutResettingOtherSettings(string jsonValue)
    {
        var settings = CodexWidgetSettings.FromJson(
            $$"""{"showFiles":false,"indicatorOrder":{{jsonValue}}}""");

        Assert.False(settings.ShowFiles);
        Assert.Equal(PreviewIndicatorOrder.Default, settings.IndicatorOrder);
    }

    [Fact]
    public void MoveIndicator_ClampsAtBoundsAndReturnsWhetherOrderChanged()
    {
        var settings = new CodexWidgetSettings();

        Assert.False(settings.MoveIndicator(PreviewIndicatorKind.Activity, -1));
        Assert.False(settings.MoveIndicator(PreviewIndicatorKind.WeeklyUsage, 1));
        Assert.True(settings.MoveIndicator(PreviewIndicatorKind.WeeklyUsage, -100));
        Assert.Equal(PreviewIndicatorKind.WeeklyUsage, settings.IndicatorOrder[0]);
        Assert.False(settings.MoveIndicator((PreviewIndicatorKind)999, 1));
        Assert.False(settings.MoveIndicator(PreviewIndicatorKind.Files, 0));
    }
}
