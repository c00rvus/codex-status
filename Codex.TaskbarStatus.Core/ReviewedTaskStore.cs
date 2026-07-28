using System.Text.Json;

namespace Codex.TaskbarStatus.Core;

/// <summary>
/// Persists completions explicitly dismissed by the user without modifying
/// Codex Desktop's internal unread state.
/// </summary>
public sealed class ReviewedTaskStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;

    public ReviewedTaskStore(
        string? filePath = null,
        TimeProvider? timeProvider = null)
    {
        FilePath = Path.GetFullPath(filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexTaskbarStatus",
            "reviewed-tasks.json"));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string FilePath { get; }

    public IReadOnlySet<string> Read()
    {
        lock (_sync)
        {
            return ReadDocument()
                .Entries
                .Select(entry => entry.TaskKey)
                .ToHashSet(StringComparer.Ordinal);
        }
    }

    public void MarkReviewed(string taskKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskKey);

        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            var document = ReadDocument();
            var retained = document.Entries
                .Where(entry =>
                    now - entry.ReviewedAtUtc <= Retention &&
                    !string.Equals(entry.TaskKey, taskKey, StringComparison.Ordinal))
                .ToList();
            retained.Add(new ReviewedTaskEntry(taskKey, now));
            WriteDocument(new ReviewedTaskDocument(1, retained));
        }
    }

    private ReviewedTaskDocument ReadDocument()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return ReviewedTaskDocument.Empty;
            }

            var document = JsonSerializer.Deserialize<ReviewedTaskDocument>(
                File.ReadAllText(FilePath),
                JsonOptions);
            if (document?.Entries is null)
            {
                return ReviewedTaskDocument.Empty;
            }

            var cutoff = _timeProvider.GetUtcNow() - Retention;
            return document with
            {
                Entries = document.Entries
                    .Where(entry =>
                        !string.IsNullOrWhiteSpace(entry.TaskKey) &&
                        entry.ReviewedAtUtc >= cutoff)
                    .GroupBy(entry => entry.TaskKey, StringComparer.Ordinal)
                    .Select(group => group.MaxBy(entry => entry.ReviewedAtUtc)!)
                    .ToList(),
            };
        }
        catch (IOException)
        {
            return ReviewedTaskDocument.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return ReviewedTaskDocument.Empty;
        }
        catch (JsonException)
        {
            return ReviewedTaskDocument.Empty;
        }
    }

    private void WriteDocument(ReviewedTaskDocument document)
    {
        var directory = Path.GetDirectoryName(FilePath)
            ?? throw new InvalidOperationException("The reviewed-task path has no directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(document, JsonOptions));
            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed record ReviewedTaskDocument(
        int SchemaVersion,
        List<ReviewedTaskEntry> Entries)
    {
        internal static ReviewedTaskDocument Empty { get; } = new(1, []);
    }

    private sealed record ReviewedTaskEntry(
        string TaskKey,
        DateTimeOffset ReviewedAtUtc);
}
