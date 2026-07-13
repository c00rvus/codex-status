using Codex.TaskbarStatus.Core;

namespace Codex.TaskbarStatus.Tests;

public sealed class CodexRateLimitsTests
{
    [Fact]
    public void TryParseAppServerResponse_ParsesJsonRpcCamelCaseAndRemainingPercent()
    {
        const string json = """
            {
              "id": 7,
              "result": {
                "rateLimits": {
                  "limitId": "codex",
                  "primary": {
                    "usedPercent": 25.5,
                    "windowDurationMins": 300,
                    "resetsAt": 1782699511
                  },
                  "secondary": {
                    "usedPercent": 61,
                    "windowDurationMins": 10080,
                    "resetsAt": 1782993954
                  },
                  "planType": "prolite"
                }
              }
            }
            """;

        var before = DateTimeOffset.UtcNow;
        var parsed = CodexRateLimitParser.TryParseAppServerResponse(json, out var snapshot);
        var after = DateTimeOffset.UtcNow;

        Assert.True(parsed);
        Assert.Equal(RateLimitAvailability.Available, snapshot.FiveHour.Availability);
        Assert.Equal(25.5, snapshot.FiveHour.UsedPercent);
        Assert.Equal(74.5, snapshot.FiveHour.RemainingPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1782699511), snapshot.FiveHour.ResetsAtUtc);
        Assert.Equal(RateLimitAvailability.Available, snapshot.Weekly.Availability);
        Assert.Equal(39d, snapshot.Weekly.RemainingPercent);
        Assert.Equal("codex", snapshot.LimitId);
        Assert.Equal("prolite", snapshot.PlanType);
        Assert.InRange(snapshot.ObservedAtUtc!.Value, before, after);
    }

    [Fact]
    public void TryParseAppServerResponse_PrefersCodexEntryFromByLimitId()
    {
        const string json = """
            {
              "rateLimits": {
                "limitId": "codex",
                "primary": { "usedPercent": 90, "windowDurationMins": 300 }
              },
              "rateLimitsByLimitId": {
                "premium": {
                  "limitId": "premium",
                  "primary": null,
                  "secondary": null
                },
                "codex": {
                  "limitId": "codex",
                  "primary": { "usedPercent": 12, "windowDurationMins": 300 },
                  "secondary": { "usedPercent": 34, "windowDurationMins": 10080 }
                }
              }
            }
            """;

        var parsed = CodexRateLimitParser.TryParseAppServerResponse(json, out var snapshot);

        Assert.True(parsed);
        Assert.Equal(12d, snapshot.FiveHour.UsedPercent);
        Assert.Equal(34d, snapshot.Weekly.UsedPercent);
    }

    [Fact]
    public void TryParseAppServerResponse_WeeklyOnlyMarksFiveHourDisabled()
    {
        const string json = """
            {
              "rateLimits": {
                "limitId": "codex",
                "primary": {
                  "usedPercent": 73,
                  "windowDurationMins": 10080,
                  "resetsAt": 1784488811
                },
                "secondary": null
              }
            }
            """;

        var parsed = CodexRateLimitParser.TryParseAppServerResponse(json, out var snapshot);

        Assert.True(parsed);
        Assert.Equal(RateLimitAvailability.Disabled, snapshot.FiveHour.Availability);
        Assert.Null(snapshot.FiveHour.UsedPercent);
        Assert.Null(snapshot.FiveHour.RemainingPercent);
        Assert.Equal(RateLimitAvailability.Available, snapshot.Weekly.Availability);
        Assert.Equal(27d, snapshot.Weekly.RemainingPercent);
    }

    [Fact]
    public void TryParseRolloutEvent_ClassifiesByDurationWhenSlotsAreSwapped()
    {
        const string json = """
            {
              "timestamp": "2026-07-13T20:00:00Z",
              "type": "event_msg",
              "payload": {
                "type": "token_count",
                "rate_limits": {
                  "limit_id": "codex_bengalfox",
                  "primary": {
                    "used_percent": 40,
                    "window_minutes": 10079,
                    "resets_at": 1784488811
                  },
                  "secondary": {
                    "used_percent": 10,
                    "window_minutes": 299,
                    "resets_at": 1784050000
                  },
                  "plan_type": "prolite"
                }
              }
            }
            """;

        var parsed = CodexRateLimitParser.TryParseRolloutEvent(json, out var snapshot);

        Assert.True(parsed);
        Assert.Equal(10d, snapshot.FiveHour.UsedPercent);
        Assert.Equal(40d, snapshot.Weekly.UsedPercent);
        Assert.Equal(DateTimeOffset.Parse("2026-07-13T20:00:00Z"), snapshot.ObservedAtUtc);
        Assert.Equal("codex_bengalfox", snapshot.LimitId);
    }

    [Theory]
    [InlineData("""{"type":"response_item","payload":{"type":"token_count","rate_limits":{"primary":{"used_percent":10,"window_minutes":300}}}}""")]
    [InlineData("""{"type":"event_msg","payload":{"type":"agent_message","rate_limits":{"primary":{"used_percent":10,"window_minutes":300}}}}""")]
    [InlineData("""{"type":"event_msg","payload":{"type":"token_count","rate_limits":null}}""")]
    [InlineData("not json")]
    public void TryParseRolloutEvent_RejectsNonTokenCountShapesAndNull(string json)
    {
        Assert.False(CodexRateLimitParser.TryParseRolloutEvent(json, out var snapshot));
        Assert.Equal(CodexRateLimitSnapshot.Unknown, snapshot);
    }

    [Fact]
    public void TryParseRolloutEvent_InvalidFiveHourPercentDoesNotPoisonWeekly()
    {
        const string json = """
            {
              "type": "event_msg",
              "payload": {
                "type": "token_count",
                "rate_limits": {
                  "limit_id": "codex",
                  "primary": { "used_percent": -1, "window_minutes": 300 },
                  "secondary": { "used_percent": 35, "window_minutes": 10080 }
                }
              }
            }
            """;

        var parsed = CodexRateLimitParser.TryParseRolloutEvent(
            json,
            DateTimeOffset.Parse("2026-07-13T20:00:00Z"),
            out var snapshot);

        Assert.True(parsed);
        Assert.Equal(RateLimitAvailability.Unknown, snapshot.FiveHour.Availability);
        Assert.Equal(RateLimitAvailability.Available, snapshot.Weekly.Availability);
        Assert.Equal(35d, snapshot.Weekly.UsedPercent);
    }

    [Fact]
    public void TryParseRolloutEvent_UnknownWindowPreventsDisabledInference()
    {
        const string json = """
            {
              "type": "event_msg",
              "payload": {
                "type": "token_count",
                "rate_limits": {
                  "limit_id": "codex",
                  "primary": { "used_percent": 35, "window_minutes": 10080 },
                  "secondary": { "used_percent": 5, "window_minutes": 1440 }
                }
              }
            }
            """;

        Assert.True(CodexRateLimitParser.TryParseRolloutEvent(json, out var snapshot));
        Assert.Equal(RateLimitAvailability.Unknown, snapshot.FiveHour.Availability);
        Assert.Equal(RateLimitAvailability.Available, snapshot.Weekly.Availability);
    }

    [Fact]
    public void TryParseRolloutEvent_AcceptsPercentBoundariesAndIgnoresInvalidReset()
    {
        const string json = """
            {
              "type": "event_msg",
              "payload": {
                "type": "token_count",
                "rate_limits": {
                  "limit_id": "codex",
                  "primary": {
                    "used_percent": 0,
                    "window_minutes": 300,
                    "resets_at": 253402300800
                  },
                  "secondary": { "used_percent": 100, "window_minutes": 10080 }
                }
              }
            }
            """;

        Assert.True(CodexRateLimitParser.TryParseRolloutEvent(json, out var snapshot));
        Assert.Equal(100d, snapshot.FiveHour.RemainingPercent);
        Assert.Null(snapshot.FiveHour.ResetsAtUtc);
        Assert.Equal(0d, snapshot.Weekly.RemainingPercent);
    }

    [Fact]
    public void IsStaleAt_UsesResetOrWindowFallback()
    {
        var observedAt = DateTimeOffset.Parse("2026-07-13T10:00:00Z");
        var reset = observedAt.AddHours(2);
        var withReset = new RateLimitWindowState(
            RateLimitAvailability.Available,
            10,
            reset);
        var withoutReset = withReset with { ResetsAtUtc = null };

        Assert.False(withReset.IsStaleAt(reset.AddTicks(-1), observedAt, TimeSpan.FromHours(5)));
        Assert.True(withReset.IsStaleAt(reset, observedAt, TimeSpan.FromHours(5)));
        Assert.False(withoutReset.IsStaleAt(observedAt.AddHours(5).AddTicks(-1), observedAt, TimeSpan.FromHours(5)));
        Assert.True(withoutReset.IsStaleAt(observedAt.AddHours(5), observedAt, TimeSpan.FromHours(5)));
    }
}
