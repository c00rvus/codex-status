using System.Text;

namespace Codex.TaskbarStatus.Core;

/// <summary>
/// Reads a bounded tail from recent Codex rollouts without blocking their writer.
/// Rollout files are never modified.
/// </summary>
public sealed class RolloutRateLimitReader
{
    public const int DefaultMaxRecentFiles = 16;
    public const int DefaultTailBytes = 128 * 1024;

    private readonly string _sessionsRoot;
    private readonly int _maxRecentFiles;
    private readonly int _tailBytes;

    public RolloutRateLimitReader(
        string? sessionsRoot = null,
        int maxRecentFiles = DefaultMaxRecentFiles,
        int tailBytes = DefaultTailBytes)
    {
        if (maxRecentFiles <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRecentFiles));
        }

        if (tailBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tailBytes));
        }

        _sessionsRoot = sessionsRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex",
            "sessions");
        _maxRecentFiles = maxRecentFiles;
        _tailBytes = tailBytes;
    }

    public CodexRateLimitSnapshot ReadLatest()
    {
        if (!Directory.Exists(_sessionsRoot))
        {
            return CodexRateLimitSnapshot.Unknown;
        }

        CodexRateLimitSnapshot? latest = null;

        foreach (var file in EnumerateRecentFiles())
        {
            try
            {
                foreach (var line in ReadTailLines(file.Path))
                {
                    if (!CodexRateLimitParser.TryParseRolloutEvent(
                            line,
                            file.LastWriteAtUtc,
                            out var candidate))
                    {
                        continue;
                    }

                    if (latest?.ObservedAtUtc is null
                        || candidate.ObservedAtUtc > latest.ObservedAtUtc)
                    {
                        latest = candidate;
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return latest ?? CodexRateLimitSnapshot.Unknown;
    }

    private IReadOnlyList<RolloutFile> EnumerateRecentFiles()
    {
        var files = new List<RolloutFile>();

        try
        {
            foreach (var path in Directory.EnumerateFiles(
                         _sessionsRoot,
                         "rollout-*.jsonl",
                         SearchOption.AllDirectories))
            {
                try
                {
                    files.Add(new RolloutFile(
                        path,
                        new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero)));
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return files
            .OrderByDescending(file => file.LastWriteAtUtc)
            .Take(_maxRecentFiles)
            .ToList();
    }

    private IEnumerable<string> ReadTailLines(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.SequentialScan);

        var lengthAtOpen = stream.Length;
        var bytesToRead = (int)Math.Min(lengthAtOpen, _tailBytes);
        var start = lengthAtOpen - bytesToRead;
        stream.Seek(start, SeekOrigin.Begin);

        var buffer = new byte[bytesToRead];
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        var text = Encoding.UTF8.GetString(buffer, 0, totalRead);
        var lines = text.Split('\n');
        var firstCompleteLine = start > 0 ? 1 : 0;

        for (var index = firstCompleteLine; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r');
            if (index == 0)
            {
                line = line.TrimStart('\uFEFF');
            }

            if (!string.IsNullOrWhiteSpace(line))
            {
                yield return line;
            }
        }
    }

    private sealed record RolloutFile(string Path, DateTimeOffset LastWriteAtUtc);
}
