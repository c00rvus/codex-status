using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Codex.TaskbarStatus.Core;

public sealed class CodexWidgetSettings
{
    public const string DefaultSpinnerColor = "#3B9EFF";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private string _spinnerColor = DefaultSpinnerColor;
    private List<PreviewIndicatorKind> _indicatorOrder = [.. PreviewIndicatorOrder.Default];

    public bool ShowActivity { get; set; } = true;
    public bool ShowFiles { get; set; } = true;
    public bool ShowAgents { get; set; } = true;
    public bool ShowElapsed { get; set; } = true;
    public bool ShowFiveHourUsage { get; set; } = true;
    public bool ShowWeeklyUsage { get; set; } = true;
    public bool ShowPulse { get; set; } = true;
    public bool ShowAttentionNotifications { get; set; } = true;
    public bool Compact { get; set; }
    public bool HideWhenIdle { get; set; }

    public string SpinnerColor
    {
        get => _spinnerColor;
        set => _spinnerColor = NormalizeSpinnerColor(value);
    }

    public IReadOnlyList<PreviewIndicatorKind> IndicatorOrder => _indicatorOrder;

    public static CodexWidgetSettings FromJson(string? json)
    {
        var settings = new CodexWidgetSettings();
        if (string.IsNullOrWhiteSpace(json))
        {
            return settings;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return settings;
            }

            settings.ShowActivity = ReadBoolean(root, "showActivity", settings.ShowActivity);
            settings.ShowFiles = ReadBoolean(root, "showFiles", settings.ShowFiles);
            settings.ShowAgents = ReadBoolean(root, "showAgents", settings.ShowAgents);
            settings.ShowElapsed = ReadBoolean(root, "showElapsed", settings.ShowElapsed);
            settings.ShowFiveHourUsage = ReadBoolean(
                root,
                "showFiveHourUsage",
                settings.ShowFiveHourUsage);
            settings.ShowWeeklyUsage = ReadBoolean(
                root,
                "showWeeklyUsage",
                settings.ShowWeeklyUsage);
            settings.ShowPulse = ReadBoolean(root, "showPulse", settings.ShowPulse);
            settings.ShowAttentionNotifications = ReadBoolean(
                root,
                "showAttentionNotifications",
                settings.ShowAttentionNotifications);
            settings.Compact = ReadBoolean(root, "compact", settings.Compact);
            settings.HideWhenIdle = ReadBoolean(root, "hideWhenIdle", settings.HideWhenIdle);

            if (TryGetProperty(root, "spinnerColor", out var spinnerColor) &&
                spinnerColor.ValueKind == JsonValueKind.String)
            {
                settings.SpinnerColor = spinnerColor.GetString() ?? DefaultSpinnerColor;
            }

            settings._indicatorOrder = ReadIndicatorOrder(root);
        }
        catch (JsonException)
        {
            return new CodexWidgetSettings();
        }

        return settings;
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public bool MoveIndicator(PreviewIndicatorKind indicator, int offset)
    {
        if (offset == 0)
        {
            return false;
        }

        var currentIndex = _indicatorOrder.IndexOf(indicator);
        if (currentIndex < 0)
        {
            return false;
        }

        var targetIndex = Math.Clamp(currentIndex + offset, 0, _indicatorOrder.Count - 1);
        if (targetIndex == currentIndex)
        {
            return false;
        }

        _indicatorOrder.RemoveAt(currentIndex);
        _indicatorOrder.Insert(targetIndex, indicator);
        return true;
    }

    public static string NormalizeSpinnerColor(string? value)
    {
        return TryNormalizeSpinnerColor(value, out var normalized)
            ? normalized
            : DefaultSpinnerColor;
    }

    public static bool TryNormalizeSpinnerColor(string? value, out string normalized)
    {
        normalized = DefaultSpinnerColor;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Trim();
        if (candidate.Length != 7 || candidate[0] != '#' ||
            !uint.TryParse(
                candidate.AsSpan(1),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out _))
        {
            return false;
        }

        normalized = candidate.ToUpperInvariant();
        return true;
    }

    private static bool ReadBoolean(JsonElement root, string name, bool fallback)
    {
        if (!TryGetProperty(root, name, out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }

    private static List<PreviewIndicatorKind> ReadIndicatorOrder(JsonElement root)
    {
        if (!TryGetProperty(root, "indicatorOrder", out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return [.. PreviewIndicatorOrder.Default];
        }

        var requestedOrder = new List<PreviewIndicatorKind>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String &&
                PreviewIndicatorOrder.TryParseId(item.GetString(), out var indicator))
            {
                requestedOrder.Add(indicator);
            }
        }

        return [.. PreviewIndicatorOrder.Normalize(requestedOrder)];
    }

    private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
