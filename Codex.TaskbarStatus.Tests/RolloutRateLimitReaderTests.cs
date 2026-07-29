using Codex.TaskbarStatus.Core;
using System.Text.Json;

namespace Codex.TaskbarStatus.Tests;

public sealed class RolloutRateLimitReaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"CodexRateLimits-{Guid.NewGuid():N}");

    [Fact]
    public async Task ReadLatest_NewerNullDoesNotEraseValidSnapshotButWeeklyOnlyDoes()
    {
        Directory.CreateDirectory(_directory);
        await WriteRolloutAsync(
            "rollout-old-dual.jsonl",
            new DateTime(2026, 7, 13, 10, 0, 0, DateTimeKind.Utc),
            DualEvent("2026-07-13T10:00:00Z", fiveHourUsed: 20, weeklyUsed: 30));
        await WriteRolloutAsync(
            "rollout-newer-null.jsonl",
            new DateTime(2026, 7, 13, 10, 10, 0, DateTimeKind.Utc),
            """{"timestamp":"2026-07-13T10:10:00Z","type":"event_msg","payload":{"type":"token_count","rate_limits":null}}""");

        using var reader = new RolloutRateLimitReader(_directory);
        var afterNull = reader.ReadLatest();

        Assert.Equal(RateLimitAvailability.Available, afterNull.FiveHour.Availability);
        Assert.Equal(20d, afterNull.FiveHour.UsedPercent);

        await WriteRolloutAsync(
            "rollout-newest-weekly.jsonl",
            new DateTime(2026, 7, 13, 10, 20, 0, DateTimeKind.Utc),
            WeeklyOnlyEvent("2026-07-13T10:20:00Z", weeklyUsed: 40));

        var afterWeeklyOnly = reader.ReadLatest();

        Assert.Equal(RateLimitAvailability.Disabled, afterWeeklyOnly.FiveHour.Availability);
        Assert.Null(afterWeeklyOnly.FiveHour.UsedPercent);
        Assert.Equal(40d, afterWeeklyOnly.Weekly.UsedPercent);
    }

    [Fact]
    public async Task ReadLatest_UsesEventTimestampAndAllowsUsageToDropAfterReset()
    {
        Directory.CreateDirectory(_directory);
        await WriteRolloutAsync(
            "rollout-newer-file.jsonl",
            new DateTime(2026, 7, 13, 12, 30, 0, DateTimeKind.Utc),
            DualEvent("2026-07-13T11:00:00Z", fiveHourUsed: 99, weeklyUsed: 80));
        await WriteRolloutAsync(
            "rollout-older-file.jsonl",
            new DateTime(2026, 7, 13, 12, 0, 0, DateTimeKind.Utc),
            DualEvent("2026-07-13T12:00:00Z", fiveHourUsed: 2, weeklyUsed: 3));

        using var reader = new RolloutRateLimitReader(_directory);
        var snapshot = reader.ReadLatest();

        Assert.Equal(2d, snapshot.FiveHour.UsedPercent);
        Assert.Equal(3d, snapshot.Weekly.UsedPercent);
        Assert.Equal(DateTimeOffset.Parse("2026-07-13T12:00:00Z"), snapshot.ObservedAtUtc);
    }

    [Fact]
    public async Task ReadLatest_ReadsOpenFileAndToleratesPartialTrailingLineWithoutWriting()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "rollout-active.jsonl");
        await File.WriteAllTextAsync(
            path,
            DualEvent("2026-07-13T12:00:00Z", fiveHourUsed: 10, weeklyUsed: 20)
            + Environment.NewLine
            + "{\"timestamp\":\"2026-07-13T12:00:01Z\"");
        var before = await File.ReadAllTextAsync(path);

        CodexRateLimitSnapshot snapshot;
        using var reader = new RolloutRateLimitReader(_directory);
        await using (var writer = new FileStream(
                         path,
                         FileMode.Open,
                         FileAccess.ReadWrite,
                         FileShare.ReadWrite | FileShare.Delete))
        {
            snapshot = reader.ReadLatest();
        }

        Assert.Equal(10d, snapshot.FiveHour.UsedPercent);
        Assert.Equal(20d, snapshot.Weekly.UsedPercent);
        Assert.Equal(before, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ReadLatest_BoundsRecentFilesAndTailBytes()
    {
        Directory.CreateDirectory(_directory);
        await WriteRolloutAsync(
            "rollout-old-valid.jsonl",
            new DateTime(2026, 7, 13, 10, 0, 0, DateTimeKind.Utc),
            DualEvent("2026-07-13T10:00:00Z", fiveHourUsed: 10, weeklyUsed: 20));
        await WriteRolloutAsync(
            "rollout-new-null.jsonl",
            new DateTime(2026, 7, 13, 11, 0, 0, DateTimeKind.Utc),
            """{"type":"event_msg","payload":{"type":"token_count","rate_limits":null}}""");

        using var fileBoundedReader = new RolloutRateLimitReader(
            _directory,
            maxRecentFiles: 1,
            tailBytes: 4096);
        var boundedByFiles = fileBoundedReader.ReadLatest();

        Assert.Equal(CodexRateLimitSnapshot.Unknown, boundedByFiles);

        var tailPath = Path.Combine(_directory, "rollout-new-tail.jsonl");
        await File.WriteAllTextAsync(
            tailPath,
            DualEvent("2026-07-13T12:00:00Z", fiveHourUsed: 30, weeklyUsed: 40)
            + Environment.NewLine
            + new string('x', 8192));
        File.SetLastWriteTimeUtc(
            tailPath,
            new DateTime(2026, 7, 13, 12, 0, 0, DateTimeKind.Utc));

        using var tailBoundedReader = new RolloutRateLimitReader(
            _directory,
            maxRecentFiles: 1,
            tailBytes: 1024);
        var boundedByTail = tailBoundedReader.ReadLatest();

        Assert.Equal(CodexRateLimitSnapshot.Unknown, boundedByTail);
    }

    [Fact]
    public void ReadLatest_MissingDirectoryReturnsUnknown()
    {
        using var reader = new RolloutRateLimitReader(_directory);
        var snapshot = reader.ReadLatest();

        Assert.Equal(CodexRateLimitSnapshot.Unknown, snapshot);
    }

    [Fact]
    public async Task ReadLatest_ReusesCachedSnapshotWithoutAnotherFullScan()
    {
        Directory.CreateDirectory(_directory);
        await WriteRolloutAsync(
            "rollout-cached.jsonl",
            new DateTime(2026, 7, 13, 13, 0, 0, DateTimeKind.Utc),
            DualEvent("2026-07-13T13:00:00Z", fiveHourUsed: 12, weeklyUsed: 34));

        using var reader = new RolloutRateLimitReader(
            _directory,
            fallbackRescanInterval: TimeSpan.FromHours(1));
        var first = reader.ReadLatest();
        var second = reader.ReadLatest();

        Assert.Same(first, second);
        Assert.Equal(1, reader.FullScanCount);
    }

    [Fact]
    public async Task ReadLatest_WatcherRefreshesAnAppendedRateLimitEvent()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "rollout-watched.jsonl");
        await File.WriteAllTextAsync(
            path,
            DualEvent("2026-07-13T13:10:00Z", fiveHourUsed: 10, weeklyUsed: 20)
            + Environment.NewLine);

        using var reader = new RolloutRateLimitReader(
            _directory,
            fallbackRescanInterval: TimeSpan.FromHours(1));
        Assert.Equal(10d, reader.ReadLatest().FiveHour.UsedPercent);

        await File.AppendAllTextAsync(
            path,
            DualEvent("2026-07-13T13:11:00Z", fiveHourUsed: 15, weeklyUsed: 25)
            + Environment.NewLine);

        var expiresAt = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        CodexRateLimitSnapshot refreshed;
        do
        {
            refreshed = reader.ReadLatest();
            if (refreshed.FiveHour.UsedPercent == 15d)
            {
                break;
            }

            await Task.Delay(25);
        }
        while (DateTime.UtcNow < expiresAt);

        Assert.Equal(15d, refreshed.FiveHour.UsedPercent);
        Assert.Equal(25d, refreshed.Weekly.UsedPercent);
        Assert.Equal(2, reader.FullScanCount);
        Assert.Same(refreshed, reader.ReadLatest());
        Assert.Equal(2, reader.FullScanCount);
    }

    private async Task WriteRolloutAsync(
        string name,
        DateTime lastWriteAtUtc,
        params string[] lines)
    {
        var path = Path.Combine(_directory, name);
        await File.WriteAllLinesAsync(path, lines);
        File.SetLastWriteTimeUtc(path, lastWriteAtUtc);
    }

    private static string DualEvent(
        string timestamp,
        double fiveHourUsed,
        double weeklyUsed)
    {
        return JsonSerializer.Serialize(new
        {
            timestamp,
            type = "event_msg",
            payload = new
            {
                type = "token_count",
                rate_limits = new
                {
                    limit_id = "codex",
                    primary = new
                    {
                        used_percent = fiveHourUsed,
                        window_minutes = 300,
                        resets_at = 1784050000,
                    },
                    secondary = new
                    {
                        used_percent = weeklyUsed,
                        window_minutes = 10080,
                        resets_at = 1784488811,
                    },
                },
            },
        });
    }

    private static string WeeklyOnlyEvent(string timestamp, double weeklyUsed)
    {
        return JsonSerializer.Serialize(new
        {
            timestamp,
            type = "event_msg",
            payload = new
            {
                type = "token_count",
                rate_limits = new
                {
                    limit_id = "codex",
                    primary = new
                    {
                        used_percent = weeklyUsed,
                        window_minutes = 10080,
                        resets_at = 1784488811,
                    },
                    secondary = (object?)null,
                },
            },
        });
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
