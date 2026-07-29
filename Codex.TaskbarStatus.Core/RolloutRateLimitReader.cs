using System.Text;

namespace Codex.TaskbarStatus.Core;

/// <summary>
/// Reads a bounded tail from recent Codex rollouts without blocking their writer.
/// Rollout files are never modified.
/// </summary>
public sealed class RolloutRateLimitReader : IDisposable
{
    public const int DefaultMaxRecentFiles = 16;
    public const int DefaultTailBytes = 128 * 1024;
    private static readonly TimeSpan DefaultFallbackRescanInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan WatcherUnavailableRescanInterval = TimeSpan.FromSeconds(2);

    private readonly string _sessionsRoot;
    private readonly int _maxRecentFiles;
    private readonly int _tailBytes;
    private readonly TimeSpan _fallbackRescanInterval;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private FileSystemWatcher? _watcher;
    private CodexRateLimitSnapshot _cachedSnapshot = CodexRateLimitSnapshot.Unknown;
    private DateTimeOffset _nextFallbackRescanAtUtc = DateTimeOffset.MinValue;
    private DateTime _rootLastWriteTimeUtc;
    private bool _dirty = true;
    private bool _disposed;

    public RolloutRateLimitReader(
        string? sessionsRoot = null,
        int maxRecentFiles = DefaultMaxRecentFiles,
        int tailBytes = DefaultTailBytes,
        TimeSpan? fallbackRescanInterval = null,
        TimeProvider? timeProvider = null)
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
        _fallbackRescanInterval =
            fallbackRescanInterval ?? DefaultFallbackRescanInterval;
        _timeProvider = timeProvider ?? TimeProvider.System;

        if (_fallbackRescanInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fallbackRescanInterval),
                "The fallback rescan interval cannot be negative.");
        }
    }

    public CodexRateLimitSnapshot ReadLatest()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!Directory.Exists(_sessionsRoot))
            {
                ResetForMissingRoot();
                return CodexRateLimitSnapshot.Unknown;
            }

            try
            {
                var rootLastWriteTimeUtc = Directory.GetLastWriteTimeUtc(_sessionsRoot);
                if (rootLastWriteTimeUtc != _rootLastWriteTimeUtc)
                {
                    _rootLastWriteTimeUtc = rootLastWriteTimeUtc;
                    _dirty = true;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            EnsureWatcher();
            var now = _timeProvider.GetUtcNow();
            if (!_dirty && now < _nextFallbackRescanAtUtc)
            {
                return _cachedSnapshot;
            }

            _cachedSnapshot = ScanLatest();
            _dirty = false;
            FullScanCount++;
            var interval = _watcher is null
                ? MinInterval(
                    _fallbackRescanInterval,
                    WatcherUnavailableRescanInterval)
                : _fallbackRescanInterval;
            _nextFallbackRescanAtUtc = now + interval;
            return _cachedSnapshot;
        }
    }

    internal int FullScanCount { get; private set; }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DisposeWatcher();
        }
    }

    private CodexRateLimitSnapshot ScanLatest()
    {
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

    private void EnsureWatcher()
    {
        if (_watcher is not null)
        {
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(_sessionsRoot, "rollout-*.jsonl")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName
                    | NotifyFilters.DirectoryName
                    | NotifyFilters.LastWrite
                    | NotifyFilters.Size,
                InternalBufferSize = 16 * 1024,
            };
            watcher.Changed += OnRolloutChanged;
            watcher.Created += OnRolloutChanged;
            watcher.Deleted += OnRolloutChanged;
            watcher.Renamed += OnRolloutRenamed;
            watcher.Error += OnWatcherError;
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (ArgumentException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void OnRolloutChanged(object sender, FileSystemEventArgs args)
    {
        lock (_sync)
        {
            if (!_disposed)
            {
                _dirty = true;
            }
        }
    }

    private void OnRolloutRenamed(object sender, RenamedEventArgs args) =>
        OnRolloutChanged(sender, args);

    private void OnWatcherError(object sender, ErrorEventArgs args)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _dirty = true;
            DisposeWatcher();
        }
    }

    private void ResetForMissingRoot()
    {
        DisposeWatcher();
        _cachedSnapshot = CodexRateLimitSnapshot.Unknown;
        _nextFallbackRescanAtUtc = DateTimeOffset.MinValue;
        _rootLastWriteTimeUtc = default;
        _dirty = true;
    }

    private void DisposeWatcher()
    {
        if (_watcher is null)
        {
            return;
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Changed -= OnRolloutChanged;
        _watcher.Created -= OnRolloutChanged;
        _watcher.Deleted -= OnRolloutChanged;
        _watcher.Renamed -= OnRolloutRenamed;
        _watcher.Error -= OnWatcherError;
        _watcher.Dispose();
        _watcher = null;
    }

    private static TimeSpan MinInterval(TimeSpan left, TimeSpan right) =>
        left <= right ? left : right;

    private sealed record RolloutFile(string Path, DateTimeOffset LastWriteAtUtc);
}
