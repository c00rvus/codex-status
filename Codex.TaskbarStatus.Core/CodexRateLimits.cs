using System.Globalization;
using System.Text.Json;

namespace Codex.TaskbarStatus.Core;

public enum RateLimitAvailability
{
    Unknown,
    Available,
    Disabled,
}

public sealed record RateLimitWindowState(
    RateLimitAvailability Availability,
    double? UsedPercent,
    DateTimeOffset? ResetsAtUtc)
{
    public static RateLimitWindowState Unknown { get; } = new(
        RateLimitAvailability.Unknown,
        null,
        null);

    public static RateLimitWindowState Disabled { get; } = new(
        RateLimitAvailability.Disabled,
        null,
        null);

    public double? RemainingPercent => UsedPercent is { } used ? 100d - used : null;

    public bool IsStaleAt(
        DateTimeOffset now,
        DateTimeOffset? observedAtUtc,
        TimeSpan fallbackWindow)
    {
        if (Availability != RateLimitAvailability.Available)
        {
            return false;
        }

        if (ResetsAtUtc is { } resetsAtUtc)
        {
            return now >= resetsAtUtc;
        }

        return observedAtUtc is null || now >= observedAtUtc.Value + fallbackWindow;
    }
}

public sealed record CodexRateLimitSnapshot(
    RateLimitWindowState FiveHour,
    RateLimitWindowState Weekly,
    DateTimeOffset? ObservedAtUtc,
    string? LimitId,
    string? PlanType)
{
    public static CodexRateLimitSnapshot Unknown { get; } = new(
        RateLimitWindowState.Unknown,
        RateLimitWindowState.Unknown,
        null,
        null,
        null);

    public bool HasKnownData => FiveHour.Availability != RateLimitAvailability.Unknown
        || Weekly.Availability != RateLimitAvailability.Unknown;
}

public static class CodexRateLimitParser
{
    private const int FiveHourWindowMinutes = 300;
    private const int WeeklyWindowMinutes = 10_080;

    public static bool TryParseAppServerResponse(
        string json,
        out CodexRateLimitSnapshot snapshot)
    {
        snapshot = CodexRateLimitSnapshot.Unknown;

        if (!TryParseDocument(json, out var document))
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var container = root;
            if (TryGet(root, "result", out var result) && result.ValueKind == JsonValueKind.Object)
            {
                container = result;
            }
            else if (TryGet(root, "params", out var parameters)
                && parameters.ValueKind == JsonValueKind.Object)
            {
                container = parameters;
            }

            if (!TryFindAppServerRateLimits(container, out var rateLimits))
            {
                return false;
            }

            return TryParseRateLimits(
                rateLimits,
                DateTimeOffset.UtcNow,
                out snapshot);
        }
    }

    public static bool TryParseRolloutEvent(
        string json,
        out CodexRateLimitSnapshot snapshot)
    {
        return TryParseRolloutEvent(json, DateTimeOffset.UtcNow, out snapshot);
    }

    public static bool TryParseRolloutEvent(
        string json,
        DateTimeOffset fallbackObservedAtUtc,
        out CodexRateLimitSnapshot snapshot)
    {
        snapshot = CodexRateLimitSnapshot.Unknown;

        if (!TryParseDocument(json, out var document))
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !HasStringValue(root, "type", "event_msg")
                || !TryGet(root, "payload", out var payload)
                || payload.ValueKind != JsonValueKind.Object
                || !HasStringValue(payload, "type", "token_count")
                || !TryGet(payload, "rate_limits", out var rateLimits)
                || rateLimits.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var observedAtUtc = ReadTimestamp(root, "timestamp")
                ?? fallbackObservedAtUtc.ToUniversalTime();

            return TryParseRateLimits(rateLimits, observedAtUtc, out snapshot);
        }
    }

    private static bool TryFindAppServerRateLimits(
        JsonElement container,
        out JsonElement rateLimits)
    {
        if (TryGet(container, "rateLimitsByLimitId", out var byLimitId)
            && byLimitId.ValueKind == JsonValueKind.Object)
        {
            if (TryGet(byLimitId, "codex", out rateLimits)
                && rateLimits.ValueKind == JsonValueKind.Object)
            {
                return true;
            }

            foreach (var property in byLimitId.EnumerateObject())
            {
                if (IsCodexLimitId(property.Name)
                    && property.Value.ValueKind == JsonValueKind.Object)
                {
                    rateLimits = property.Value;
                    return true;
                }
            }
        }

        if (TryGet(container, "rateLimits", out rateLimits)
            && rateLimits.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        rateLimits = default;
        return false;
    }

    private static bool TryParseRateLimits(
        JsonElement rateLimits,
        DateTimeOffset observedAtUtc,
        out CodexRateLimitSnapshot snapshot)
    {
        snapshot = CodexRateLimitSnapshot.Unknown;

        var limitId = ReadString(rateLimits, "limit_id", "limitId");
        if (!string.IsNullOrWhiteSpace(limitId) && !IsCodexLimitId(limitId))
        {
            return false;
        }

        var slots = new[]
        {
            ParseSlot(rateLimits, "primary"),
            ParseSlot(rateLimits, "secondary"),
        };

        ParsedSlot? validFiveHour = null;
        ParsedSlot? validWeekly = null;
        var hasFiveHourSlot = false;
        var hasUnknownSlot = false;

        foreach (var slot in slots)
        {
            switch (slot.Kind)
            {
                case WindowKind.FiveHour:
                    hasFiveHourSlot = true;
                    if (slot.State.Availability == RateLimitAvailability.Available)
                    {
                        validFiveHour = slot;
                    }
                    break;

                case WindowKind.Weekly:
                    if (slot.State.Availability == RateLimitAvailability.Available)
                    {
                        validWeekly = slot;
                    }
                    break;

                case WindowKind.Unknown when slot.IsPresent:
                    hasUnknownSlot = true;
                    break;
            }
        }

        if (validFiveHour is null && validWeekly is null)
        {
            return false;
        }

        var fiveHour = validFiveHour?.State
            ?? (hasFiveHourSlot || hasUnknownSlot || validWeekly is null
                ? RateLimitWindowState.Unknown
                : RateLimitWindowState.Disabled);
        var weekly = validWeekly?.State ?? RateLimitWindowState.Unknown;

        snapshot = new CodexRateLimitSnapshot(
            fiveHour,
            weekly,
            observedAtUtc.ToUniversalTime(),
            limitId,
            ReadString(rateLimits, "plan_type", "planType"));
        return true;
    }

    private static ParsedSlot ParseSlot(JsonElement rateLimits, string name)
    {
        if (!TryGet(rateLimits, name, out var slot)
            || slot.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return ParsedSlot.Empty;
        }

        if (slot.ValueKind != JsonValueKind.Object)
        {
            return new ParsedSlot(true, WindowKind.Unknown, RateLimitWindowState.Unknown);
        }

        var windowMinutes = ReadInt32(slot, "window_minutes", "windowDurationMins");
        var kind = ClassifyWindow(windowMinutes);
        if (kind == WindowKind.Unknown)
        {
            return new ParsedSlot(true, kind, RateLimitWindowState.Unknown);
        }

        var usedPercent = ReadValidPercent(slot, "used_percent", "usedPercent");
        if (usedPercent is null)
        {
            return new ParsedSlot(true, kind, RateLimitWindowState.Unknown);
        }

        return new ParsedSlot(
            true,
            kind,
            new RateLimitWindowState(
                RateLimitAvailability.Available,
                usedPercent,
                ReadUnixTimestamp(slot, "resets_at", "resetsAt")));
    }

    private static WindowKind ClassifyWindow(int? minutes)
    {
        // Older Codex builds occasionally reported one elapsed minute less.
        return minutes switch
        {
            FiveHourWindowMinutes or FiveHourWindowMinutes - 1 => WindowKind.FiveHour,
            WeeklyWindowMinutes or WeeklyWindowMinutes - 1 => WindowKind.Weekly,
            _ => WindowKind.Unknown,
        };
    }

    private static double? ReadValidPercent(JsonElement element, params string[] names)
    {
        if (!TryGetAny(element, names, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var percent)
            || !double.IsFinite(percent)
            || percent < 0d
            || percent > 100d)
        {
            return null;
        }

        return percent;
    }

    private static DateTimeOffset? ReadUnixTimestamp(JsonElement element, params string[] names)
    {
        if (!TryGetAny(element, names, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var seconds))
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement element, string name)
    {
        var raw = ReadString(element, name);
        return DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var timestamp)
            ? timestamp
            : null;
    }

    private static int? ReadInt32(JsonElement element, params string[] names)
    {
        return TryGetAny(element, names, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
                ? number
                : null;
    }

    private static string? ReadString(JsonElement element, params string[] names)
    {
        return TryGetAny(element, names, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private static bool HasStringValue(JsonElement element, string name, string expected)
    {
        return TryGet(element, name, out var value)
            && value.ValueKind == JsonValueKind.String
            && string.Equals(value.GetString(), expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCodexLimitId(string value)
    {
        return string.Equals(value, "codex", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("codex_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseDocument(string json, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            document = null!;
            return false;
        }
        catch (ArgumentException)
        {
            document = null!;
            return false;
        }
    }

    private static bool TryGetAny(
        JsonElement element,
        IEnumerable<string> names,
        out JsonElement value)
    {
        foreach (var name in names)
        {
            if (TryGet(element, name, out value))
            {
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private enum WindowKind
    {
        Unknown,
        FiveHour,
        Weekly,
    }

    private readonly record struct ParsedSlot(
        bool IsPresent,
        WindowKind Kind,
        RateLimitWindowState State)
    {
        public static ParsedSlot Empty { get; } = new(
            false,
            WindowKind.Unknown,
            RateLimitWindowState.Unknown);
    }
}
