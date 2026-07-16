namespace Codex.TaskbarStatus.Core;

public enum PreviewIndicatorKind
{
    Activity,
    Files,
    Subagents,
    Elapsed,
    FiveHourUsage,
    WeeklyUsage,
}

public static class PreviewIndicatorOrder
{
    private static readonly PreviewIndicatorKind[] DefaultItems =
    [
        PreviewIndicatorKind.Activity,
        PreviewIndicatorKind.Files,
        PreviewIndicatorKind.Subagents,
        PreviewIndicatorKind.Elapsed,
        PreviewIndicatorKind.FiveHourUsage,
        PreviewIndicatorKind.WeeklyUsage,
    ];

    private static readonly IReadOnlyDictionary<string, PreviewIndicatorKind> IndicatorsById =
        new Dictionary<string, PreviewIndicatorKind>(StringComparer.OrdinalIgnoreCase)
        {
            ["activity"] = PreviewIndicatorKind.Activity,
            ["files"] = PreviewIndicatorKind.Files,
            ["subagents"] = PreviewIndicatorKind.Subagents,
            ["elapsed"] = PreviewIndicatorKind.Elapsed,
            ["fiveHourUsage"] = PreviewIndicatorKind.FiveHourUsage,
            ["weeklyUsage"] = PreviewIndicatorKind.WeeklyUsage,
        };

    public static IReadOnlyList<PreviewIndicatorKind> Default { get; } =
        Array.AsReadOnly(DefaultItems);

    public static IReadOnlyList<PreviewIndicatorKind> Normalize(
        IEnumerable<PreviewIndicatorKind>? requestedOrder)
    {
        var normalized = new List<PreviewIndicatorKind>(DefaultItems.Length);
        var seen = new HashSet<PreviewIndicatorKind>();

        if (requestedOrder is not null)
        {
            foreach (var indicator in requestedOrder)
            {
                if (Enum.IsDefined(indicator) && seen.Add(indicator))
                {
                    normalized.Add(indicator);
                }
            }
        }

        foreach (var indicator in DefaultItems)
        {
            if (seen.Add(indicator))
            {
                normalized.Add(indicator);
            }
        }

        return normalized;
    }

    public static bool TryParseId(string? id, out PreviewIndicatorKind indicator)
    {
        if (!string.IsNullOrWhiteSpace(id) &&
            IndicatorsById.TryGetValue(id.Trim(), out indicator))
        {
            return true;
        }

        indicator = default;
        return false;
    }
}

public enum PreviewLeadingItem
{
    Spinner,
    Activity,
}

public sealed record PreviewIndicatorPresentation(
    PreviewIndicatorKind Kind,
    string? Text,
    IReadOnlyList<PreviewLeadingItem> LeadingItems);

public sealed record PreviewPresentation(
    IReadOnlyList<PreviewIndicatorPresentation> Items,
    double ActivityMaxWidth,
    double FilesMaxWidth,
    double SubagentsMaxWidth,
    double HorizontalPadding,
    double VerticalPadding,
    double RowSpacing,
    double LeadingSpacing,
    double TextFontSize,
    double SpinnerFontSize,
    double SpinnerHeight,
    double SeparatorHeight,
    double ElapsedMaxWidth,
    double BatteryWidth,
    double BatteryHeight,
    double BatteryTerminalWidth,
    double BatteryTerminalHeight,
    double UsageSpacing,
    double UsageMaxWidth)
{
    private PreviewIndicatorPresentation? ActivityItem =>
        Items.FirstOrDefault(item => item.Kind == PreviewIndicatorKind.Activity);

    public IReadOnlyList<PreviewLeadingItem> LeadingItems =>
        ActivityItem?.LeadingItems ?? Array.Empty<PreviewLeadingItem>();

    public bool ShowActivity => LeadingItems.Contains(PreviewLeadingItem.Activity);
    public bool ShowSpinner => LeadingItems.Contains(PreviewLeadingItem.Spinner);
    public bool ShowElapsed => Items.Any(item => item.Kind == PreviewIndicatorKind.Elapsed);
    public bool ShowFiveHourUsage =>
        Items.Any(item => item.Kind == PreviewIndicatorKind.FiveHourUsage);
    public bool ShowWeeklyUsage =>
        Items.Any(item => item.Kind == PreviewIndicatorKind.WeeklyUsage);
    public string? FilesText =>
        Items.FirstOrDefault(item => item.Kind == PreviewIndicatorKind.Files)?.Text;
    public string? SubagentsText =>
        Items.FirstOrDefault(item => item.Kind == PreviewIndicatorKind.Subagents)?.Text;
}

public static class PreviewPresentationFactory
{
    public static PreviewPresentation Create(
        CodexWidgetSettings settings,
        bool isActive,
        int filesChangedCount,
        int totalSubagents)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return Create(
            settings.IndicatorOrder,
            settings.ShowActivity,
            settings.ShowFiles,
            settings.ShowAgents,
            settings.ShowElapsed,
            settings.ShowFiveHourUsage,
            settings.ShowWeeklyUsage,
            settings.ShowPulse,
            isActive,
            settings.Compact,
            filesChangedCount,
            totalSubagents);
    }

    // Compatibility overload for callers that have not adopted configurable order yet.
    public static PreviewPresentation Create(
        bool showActivity,
        bool showFiles,
        bool showSubagents,
        bool showElapsed,
        bool showSpinner,
        bool isActive,
        bool compact,
        int filesChangedCount,
        int totalSubagents)
    {
        return Create(
            PreviewIndicatorOrder.Default,
            showActivity,
            showFiles,
            showSubagents,
            showElapsed,
            showFiveHourUsage: false,
            showWeeklyUsage: false,
            showSpinner,
            isActive,
            compact,
            filesChangedCount,
            totalSubagents);
    }

    public static PreviewPresentation Create(
        IReadOnlyList<PreviewIndicatorKind>? indicatorOrder,
        bool showActivity,
        bool showFiles,
        bool showSubagents,
        bool showElapsed,
        bool showFiveHourUsage,
        bool showWeeklyUsage,
        bool showSpinner,
        bool isActive,
        bool compact,
        int filesChangedCount,
        int totalSubagents)
    {
        var leadingItems = new List<PreviewLeadingItem>(2);
        if (showSpinner && isActive)
        {
            leadingItems.Add(PreviewLeadingItem.Spinner);
        }
        if (showActivity)
        {
            leadingItems.Add(PreviewLeadingItem.Activity);
        }

        var items = new List<PreviewIndicatorPresentation>();
        foreach (var indicator in PreviewIndicatorOrder.Normalize(indicatorOrder))
        {
            PreviewIndicatorPresentation? item = indicator switch
            {
                PreviewIndicatorKind.Activity when leadingItems.Count > 0 =>
                    new(indicator, null, leadingItems),
                PreviewIndicatorKind.Files when showFiles && !compact =>
                    new(indicator, FormatCount(filesChangedCount, "file", "files"), []),
                PreviewIndicatorKind.Subagents when showSubagents && !compact =>
                    new(indicator, FormatCount(totalSubagents, "subagent", "subagents"), []),
                PreviewIndicatorKind.Elapsed when showElapsed && !compact =>
                    new(indicator, null, []),
                PreviewIndicatorKind.FiveHourUsage when showFiveHourUsage =>
                    new(indicator, null, []),
                PreviewIndicatorKind.WeeklyUsage when showWeeklyUsage =>
                    new(indicator, null, []),
                _ => null,
            };

            if (item is not null)
            {
                items.Add(item);
            }
        }

        return new PreviewPresentation(
            items,
            compact ? 92 : 148,
            compact ? 70 : 86,
            compact ? 78 : 92,
            compact ? 4 : 8,
            compact ? 3 : 4,
            compact ? 6 : 8,
            compact ? 3 : 6,
            compact ? 11 : 12,
            compact ? 13 : 14,
            compact ? 16 : 18,
            compact ? 14 : 16,
            compact ? 52 : 60,
            compact ? 18 : 22,
            compact ? 8 : 10,
            2,
            compact ? 4 : 5,
            compact ? 2 : 3,
            compact ? 54 : 66);
    }

    private static string FormatCount(int count, string singular, string plural) =>
        $"{count} {(count == 1 ? singular : plural)}";
}
