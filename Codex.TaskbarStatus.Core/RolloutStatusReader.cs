using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Codex.TaskbarStatus.Core;

/// <summary>
/// Read-only fallback for Codex Desktop versions that do not emit configured hooks.
/// Rollout files are never changed by this reader.
/// </summary>
public sealed class RolloutStatusReader : IDisposable
{
    private static readonly HashSet<string> SupportedEvents = new(StringComparer.Ordinal)
    {
        "task_started",
        "task_complete",
        "turn_aborted",
        "patch_apply_end",
        "sub_agent_activity",
        "agent_message",
        "user_message",
        "token_count",
    };

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static readonly TimeSpan DefaultFallbackRescanInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WatcherUnavailableRescanInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TransientReadRetryInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan KnownActiveRetention = TimeSpan.FromDays(7);
    private const int ReadBufferSize = 16 * 1024;
    private const int CheckpointSize = 128;
    private const int MaxTaskTitleLength = 120;
    private const int MaxCachedEntries = 96;

    private readonly string _sessionsRoot;
    private readonly TimeSpan _fallbackRescanInterval;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private readonly Dictionary<string, RolloutCacheEntry> _entries = new(PathComparer);
    private readonly Dictionary<string, RolloutPathStamp> _indexedPaths = new(PathComparer);
    private readonly SortedSet<RolloutPathStamp> _orderedPaths =
        new(RolloutPathStampComparer.Instance);
    private readonly HashSet<string> _pendingPathChanges = new(PathComparer);
    private readonly HashSet<string> _contentDirtyPaths = new(PathComparer);
    private FileSystemWatcher? _watcher;
    private string? _selectedPath;
    private DateTimeOffset _nextFallbackRescanAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _nextDirtyContentRetryAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _nextPathMetadataRetryAtUtc = DateTimeOffset.MinValue;
    private bool _pathIndexInitialized;
    private bool _fullRescanRequired = true;
    private bool _latestSelectionDirty = true;
    private bool _disposed;

    public RolloutStatusReader(
        string? sessionsRoot = null,
        TimeSpan? fallbackRescanInterval = null,
        TimeProvider? timeProvider = null)
    {
        _sessionsRoot = sessionsRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex",
            "sessions");
        _fallbackRescanInterval = fallbackRescanInterval ?? DefaultFallbackRescanInterval;
        _timeProvider = timeProvider ?? TimeProvider.System;

        if (_fallbackRescanInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fallbackRescanInterval),
                "The fallback rescan interval cannot be negative.");
        }
    }

    /// <summary>
    /// Reports whether a filesystem notification or the safety interval says
    /// that cached rollout state should be refreshed.
    /// </summary>
    public bool HasPendingChanges
    {
        get
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var now = _timeProvider.GetUtcNow();
                return !_pathIndexInitialized
                    || _fullRescanRequired
                    || (_pendingPathChanges.Count > 0
                        && now >= _nextPathMetadataRetryAtUtc)
                    || (_contentDirtyPaths.Count > 0
                        && now >= _nextDirtyContentRetryAtUtc)
                    || now >= _nextFallbackRescanAtUtc;
            }
        }
    }

    internal int FullRescanCount { get; private set; }

    internal int PathMetadataReadCount { get; private set; }

    internal int ContentRefreshCount { get; private set; }

    public CodexExecutionState? ReadLatest()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!Directory.Exists(_sessionsRoot))
            {
                ResetForMissingRoot();
                return null;
            }

            EnsureWatcher();
            RefreshPathIndex(_timeProvider.GetUtcNow());
            if (!_latestSelectionDirty
                && _selectedPath is not null
                && _entries.TryGetValue(_selectedPath, out var selectedEntry))
            {
                ContentRefreshCount++;
                if (TryRefreshEntry(selectedEntry))
                {
                    CompleteEntryRefresh(selectedEntry);
                    if (selectedEntry.Eligible && selectedEntry.State is not null)
                    {
                        return selectedEntry.State;
                    }
                }
                else
                {
                    _contentDirtyPaths.Add(selectedEntry.Path);
                    ScheduleDirtyContentRetry();
                }

                _latestSelectionDirty = true;
            }

            return ReadLatestFromIndex();
        }
    }

    public IReadOnlyList<CodexExecutionState> ReadRecent(
        IReadOnlySet<string>? prioritySessionIds = null,
        int recentPathLimit = 32)
    {
        if (recentPathLimit < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(recentPathLimit),
                "The recent rollout path limit cannot be negative.");
        }

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!Directory.Exists(_sessionsRoot))
            {
                ResetForMissingRoot();
                return [];
            }

            EnsureWatcher();
            RefreshPathIndex(_timeProvider.GetUtcNow());
            return ReadRecentCore(prioritySessionIds, recentPathLimit);
        }
    }

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
            ClearEntries();
            ClearPathIndex();
        }
    }

    private CodexExecutionState? ReadLatestFromIndex()
    {
        foreach (var candidate in _orderedPaths)
        {
            if (!_entries.TryGetValue(candidate.Path, out var entry))
            {
                entry = new RolloutCacheEntry(candidate.Path);
                _entries.Add(candidate.Path, entry);
            }

            var forceValidation = _contentDirtyPaths.Contains(candidate.Path);
            if (!entry.Initialized || forceValidation)
            {
                ContentRefreshCount++;
                if (!TryRefreshEntry(entry, forceValidation))
                {
                    continue;
                }

                CompleteEntryRefresh(entry);
            }

            if (entry.Eligible && entry.State is not null)
            {
                _selectedPath = candidate.Path;
                _latestSelectionDirty = false;
                return entry.State;
            }
        }

        _selectedPath = null;
        _latestSelectionDirty = false;
        return null;
    }

    private IReadOnlyList<CodexExecutionState> ReadRecentCore(
        IReadOnlySet<string>? prioritySessionIds,
        int recentPathLimit)
    {
        HashSet<string> priorityIds = prioritySessionIds is null
            ? []
            : prioritySessionIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidates = new Dictionary<string, RolloutPathStamp>(PathComparer);
        var recentCount = 0;
        foreach (var item in _orderedPaths)
        {
            if (recentCount >= recentPathLimit)
            {
                break;
            }

            candidates.Add(item.Path, item);
            recentCount++;
        }

        if (priorityIds.Count > 0)
        {
            foreach (var item in _orderedPaths)
            {
                var fileName = Path.GetFileNameWithoutExtension(item.Path);
                if (priorityIds.Any(id => fileName.EndsWith(id, StringComparison.OrdinalIgnoreCase)))
                {
                    candidates[item.Path] = item;
                }
            }
        }

        // A changed rollout may keep an old timestamp (for example after a
        // restore or overwrite). Include notified paths even when they sit
        // outside the recency window so an active task cannot become stale.
        foreach (var dirtyPath in _contentDirtyPaths)
        {
            if (_indexedPaths.TryGetValue(dirtyPath, out var dirtyStamp))
            {
                candidates[dirtyPath] = dirtyStamp;
            }
        }

        // Once an active session has been observed, keep tailing it even if a
        // burst of newer rollouts pushes its original file outside the limit.
        var activeCutoff = _timeProvider.GetUtcNow() - KnownActiveRetention;
        foreach (var entry in _entries.Values)
        {
            if (IsActiveEntry(entry, activeCutoff)
                && _indexedPaths.TryGetValue(entry.Path, out var activeStamp))
            {
                candidates[entry.Path] = activeStamp;
            }
        }

        var statesBySession = new Dictionary<string, (CodexExecutionState State, string Path)>(
            StringComparer.OrdinalIgnoreCase);
        var dirtyRetryDue =
            _timeProvider.GetUtcNow() >= _nextDirtyContentRetryAtUtc;

        foreach (var candidate in candidates.Values
                     .OrderByDescending(item => item.LastWriteTimeUtc)
                     .ThenBy(item => item.Path, PathComparer))
        {
            if (!_entries.TryGetValue(candidate.Path, out var entry))
            {
                entry = new RolloutCacheEntry(candidate.Path);
                _entries.Add(candidate.Path, entry);
            }

            var contentDirty = _contentDirtyPaths.Contains(candidate.Path);
            if ((!entry.Initialized || contentDirty)
                && (!contentDirty || dirtyRetryDue))
            {
                ContentRefreshCount++;
                if (!TryRefreshEntry(entry, contentDirty))
                {
                    _contentDirtyPaths.Add(candidate.Path);
                    ScheduleDirtyContentRetry();
                    if (!entry.Initialized)
                    {
                        continue;
                    }
                }
                else
                {
                    CompleteEntryRefresh(entry);
                }
            }

            if (!entry.Initialized || !entry.Eligible || entry.State is null)
            {
                continue;
            }

            var sessionKey = string.IsNullOrWhiteSpace(entry.State.SessionId)
                ? $"path:{entry.Path}"
                : entry.State.SessionId;
            if (!statesBySession.TryGetValue(sessionKey, out var existing)
                || entry.State.LastUpdatedAtUtc > existing.State.LastUpdatedAtUtc)
            {
                statesBySession[sessionKey] = (entry.State, entry.Path);
            }
        }

        if (_contentDirtyPaths.Count == 0)
        {
            _nextDirtyContentRetryAtUtc = DateTimeOffset.MinValue;
        }

        var result = statesBySession.Values
            .OrderByDescending(item => item.State.LastUpdatedAtUtc)
            .ThenBy(item => item.State.SessionId, StringComparer.OrdinalIgnoreCase)
            .Select(item => CloneState(item.State))
            .ToArray();
        TrimEntryCache(candidates.Keys);
        return result;
    }

    private static bool IsActiveEntry(
        RolloutCacheEntry entry,
        DateTimeOffset activeCutoff) =>
        entry.Initialized
        && entry.State is { StoppedAtUtc: null } state
        && state.LastUpdatedAtUtc >= activeCutoff
        && state.Status is CodexExecutionStatuses.Running
            or CodexExecutionStatuses.Waiting
            or CodexExecutionStatuses.Error;

    private void RefreshPathIndex(DateTimeOffset now)
    {
        if (!_pathIndexInitialized
            || _fullRescanRequired
            || now >= _nextFallbackRescanAtUtc)
        {
            FullRescanPathIndex(now);
            return;
        }

        if (_pendingPathChanges.Count == 0
            || now < _nextPathMetadataRetryAtUtc)
        {
            return;
        }

        var changedPaths = _pendingPathChanges.ToArray();
        _pendingPathChanges.Clear();
        var retryNeeded = false;
        foreach (var path in changedPaths)
        {
            if (!IsRolloutPath(path))
            {
                RemoveIndexedPath(path);
                continue;
            }

            switch (ReadPathStamp(path, out var stamp))
            {
                case PathStampReadResult.Found:
                    UpsertIndexedPath(stamp, forceContentDirty: true);
                    break;
                case PathStampReadResult.Missing:
                    RemoveIndexedPath(path);
                    break;
                case PathStampReadResult.Retry:
                    _pendingPathChanges.Add(path);
                    retryNeeded = true;
                    break;
            }
        }

        _nextPathMetadataRetryAtUtc = retryNeeded
            ? now + TransientReadRetryInterval
            : DateTimeOffset.MinValue;
    }

    private void FullRescanPathIndex(DateTimeOffset now)
    {
        FullRescanCount++;
        var wasInitialized = _pathIndexInitialized;
        var explicitlyChangedPaths =
            _pendingPathChanges.ToHashSet(PathComparer);
        var presentPaths = new HashSet<string>(PathComparer);
        var enumerationCompleted = false;
        var metadataReadsCompleted = true;

        try
        {
            foreach (var path in Directory.EnumerateFiles(
                         _sessionsRoot,
                         "rollout-*.jsonl",
                         SearchOption.AllDirectories))
            {
                var readResult = ReadPathStamp(path, out var stamp);
                if (readResult == PathStampReadResult.Missing)
                {
                    continue;
                }

                presentPaths.Add(path);
                if (readResult == PathStampReadResult.Retry)
                {
                    metadataReadsCompleted = false;
                    _pendingPathChanges.Add(path);
                    continue;
                }

                UpsertIndexedPath(
                    stamp,
                    forceContentDirty: explicitlyChangedPaths.Contains(path),
                    markNewContentDirty: wasInitialized);
            }

            enumerationCompleted = true;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        if (enumerationCompleted)
        {
            foreach (var stalePath in _indexedPaths.Keys
                         .Where(path => !presentPaths.Contains(path))
                         .ToArray())
            {
                RemoveIndexedPath(stalePath);
            }
        }

        if (enumerationCompleted && metadataReadsCompleted)
        {
            // Notifications delivered while the scan holds _sync are queued
            // by FileSystemWatcher and run after this method releases it.
            _pendingPathChanges.Clear();
            _nextPathMetadataRetryAtUtc = DateTimeOffset.MinValue;
        }
        else if (_pendingPathChanges.Count > 0)
        {
            _nextPathMetadataRetryAtUtc =
                now + TransientReadRetryInterval;
        }

        _pathIndexInitialized = true;
        _fullRescanRequired = false;
        var nextInterval = enumerationCompleted
            ? EffectiveFallbackRescanInterval()
            : MinInterval(
                _fallbackRescanInterval,
                WatcherUnavailableRescanInterval);
        _nextFallbackRescanAtUtc = now + nextInterval;
    }

    private TimeSpan EffectiveFallbackRescanInterval()
    {
        if (_watcher is not null
            || _fallbackRescanInterval <= WatcherUnavailableRescanInterval)
        {
            return _fallbackRescanInterval;
        }

        return WatcherUnavailableRescanInterval;
    }

    private static TimeSpan MinInterval(TimeSpan left, TimeSpan right) =>
        left <= right ? left : right;

    private static bool IsRolloutPath(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.StartsWith("rollout-", StringComparison.OrdinalIgnoreCase)
            && fileName.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);
    }

    private PathStampReadResult ReadPathStamp(string path, out RolloutPathStamp stamp)
    {
        PathMetadataReadCount++;
        try
        {
            var fileInfo = new FileInfo(path);
            fileInfo.Refresh();
            if (!fileInfo.Exists)
            {
                stamp = default!;
                return PathStampReadResult.Missing;
            }

            stamp = new RolloutPathStamp(
                path,
                fileInfo.LastWriteTimeUtc,
                fileInfo.CreationTimeUtc,
                fileInfo.Length);
            return PathStampReadResult.Found;
        }
        catch (IOException)
        {
            stamp = default!;
            return PathStampReadResult.Retry;
        }
        catch (UnauthorizedAccessException)
        {
            stamp = default!;
            return PathStampReadResult.Retry;
        }
    }

    private void UpsertIndexedPath(
        RolloutPathStamp stamp,
        bool forceContentDirty,
        bool markNewContentDirty = true)
    {
        if (_indexedPaths.TryGetValue(stamp.Path, out var previous))
        {
            var metadataChanged = previous.LastWriteTimeUtc != stamp.LastWriteTimeUtc
                || previous.CreationTimeUtc != stamp.CreationTimeUtc
                || previous.Length != stamp.Length;
            if (metadataChanged)
            {
                _orderedPaths.Remove(previous);
                _orderedPaths.Add(stamp);
                _indexedPaths[stamp.Path] = stamp;
                _latestSelectionDirty = true;
            }

            if (forceContentDirty || metadataChanged)
            {
                _contentDirtyPaths.Add(stamp.Path);
                _nextDirtyContentRetryAtUtc = DateTimeOffset.MinValue;
                _latestSelectionDirty = true;
            }

            return;
        }

        _indexedPaths.Add(stamp.Path, stamp);
        _orderedPaths.Add(stamp);
        _latestSelectionDirty = true;
        if (markNewContentDirty)
        {
            _contentDirtyPaths.Add(stamp.Path);
            _nextDirtyContentRetryAtUtc = DateTimeOffset.MinValue;
        }
    }

    private void RemoveIndexedPath(string path)
    {
        if (_indexedPaths.Remove(path, out var stamp))
        {
            _orderedPaths.Remove(stamp);
            _latestSelectionDirty = true;
        }

        _pendingPathChanges.Remove(path);
        if (_pendingPathChanges.Count == 0)
        {
            _nextPathMetadataRetryAtUtc = DateTimeOffset.MinValue;
        }
        _contentDirtyPaths.Remove(path);
        if (_contentDirtyPaths.Count == 0)
        {
            _nextDirtyContentRetryAtUtc = DateTimeOffset.MinValue;
        }
        if (_entries.Remove(path, out var entry))
        {
            entry.Dispose();
        }

        if (PathComparer.Equals(_selectedPath, path))
        {
            _selectedPath = null;
        }
    }

    private void TrimEntryCache(IEnumerable<string> protectedPaths)
    {
        if (_entries.Count <= MaxCachedEntries)
        {
            return;
        }

        var protectedSet = protectedPaths.ToHashSet(PathComparer);
        var removeCount = _entries.Count - MaxCachedEntries;
        var removablePaths = _entries.Keys
            .Where(path => !protectedSet.Contains(path))
            .OrderBy(path => _indexedPaths.TryGetValue(path, out var stamp)
                ? stamp.LastWriteTimeUtc
                : DateTime.MinValue)
            .Take(removeCount)
            .ToArray();

        foreach (var path in removablePaths)
        {
            if (_entries.Remove(path, out var entry))
            {
                entry.Dispose();
            }
        }
    }

    private void ScheduleDirtyContentRetry()
    {
        _nextDirtyContentRetryAtUtc =
            _timeProvider.GetUtcNow() + TransientReadRetryInterval;
    }

    private void CompleteEntryRefresh(RolloutCacheEntry entry)
    {
        if (entry.HasUnreadBytes)
        {
            _contentDirtyPaths.Add(entry.Path);
            _latestSelectionDirty = true;
            ScheduleDirtyContentRetry();
            return;
        }

        _contentDirtyPaths.Remove(entry.Path);
    }

    private void QueuePathChange(string path)
    {
        _pendingPathChanges.Add(path);
        _nextPathMetadataRetryAtUtc = DateTimeOffset.MinValue;
    }

    private static CodexExecutionState CloneState(CodexExecutionState state)
    {
        return state with
        {
            FilesChanged = [.. state.FilesChanged],
        };
    }

    private static bool TryRefreshEntry(
        RolloutCacheEntry entry,
        bool forceValidation = false)
    {
        entry.HasUnreadBytes = false;
        try
        {
            var fileInfo = new FileInfo(entry.Path);
            fileInfo.Refresh();
            if (!fileInfo.Exists)
            {
                return false;
            }

            var currentLength = fileInfo.Length;
            var currentLastWriteTimeUtc = fileInfo.LastWriteTimeUtc;
            var currentCreationTimeUtc = fileInfo.CreationTimeUtc;

            if (!forceValidation
                && entry.Initialized
                && currentLength == entry.BytesRead
                && currentLastWriteTimeUtc == entry.LastWriteTimeUtc
                && currentCreationTimeUtc == entry.CreationTimeUtc)
            {
                return true;
            }

            // Codex keeps the active rollout open for writing. Use the sharing
            // mode expected by a log tailer so this reader never blocks it.
            using var stream = new FileStream(
                entry.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: ReadBufferSize,
                FileOptions.SequentialScan);

            var targetLength = stream.Length;
            var mustReset = !entry.Initialized
                || targetLength < entry.BytesRead
                || currentCreationTimeUtc != entry.CreationTimeUtc
                || (targetLength == entry.BytesRead && currentLastWriteTimeUtc != entry.LastWriteTimeUtc)
                || !CheckpointMatches(stream, entry);

            if (mustReset)
            {
                entry.Reset();
            }

            // Make the current metadata available as the timestamp fallback
            // while appended records are being applied.
            entry.CreationTimeUtc = currentCreationTimeUtc;
            entry.LastWriteTimeUtc = currentLastWriteTimeUtc;
            ReadAppendedBytes(stream, targetLength, entry);
            UpdateCheckpoint(stream, entry);

            // The writer can append between the first FileInfo refresh and
            // stream.Length. Refresh metadata after consuming the snapshot so
            // a newer timestamp paired with the bytes just read cannot look
            // like a same-length replacement on the next poll.
            fileInfo.Refresh();
            if (fileInfo.Exists)
            {
                entry.CreationTimeUtc = fileInfo.CreationTimeUtc;
                entry.LastWriteTimeUtc = fileInfo.LastWriteTimeUtc;
                entry.HasUnreadBytes = fileInfo.Length > entry.BytesRead;
            }

            entry.Initialized = true;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void ReadAppendedBytes(
        FileStream stream,
        long targetLength,
        RolloutCacheEntry entry)
    {
        if (entry.BytesRead >= targetLength)
        {
            return;
        }

        stream.Position = entry.BytesRead;
        var buffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);

        try
        {
            var remaining = targetLength - entry.BytesRead;
            while (remaining > 0)
            {
                var requested = (int)Math.Min(buffer.Length, remaining);
                var count = stream.Read(buffer, 0, requested);
                if (count == 0)
                {
                    break;
                }

                ProcessBytes(entry, buffer.AsSpan(0, count));
                entry.BytesRead += count;
                remaining -= count;

                if (entry.Rejected)
                {
                    // session_meta is the first record in a rollout. Once it
                    // identifies a subagent, skip the rest of what can be a
                    // very large file without parsing each JSONL record.
                    entry.PartialLine.SetLength(0);
                    entry.BytesRead = targetLength;
                    break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ProcessBytes(RolloutCacheEntry entry, ReadOnlySpan<byte> bytes)
    {
        var segmentStart = 0;
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] != (byte)'\n')
            {
                continue;
            }

            var segment = bytes[segmentStart..index];
            if (entry.PartialLine.Length == 0)
            {
                ProcessLine(entry, segment);
            }
            else
            {
                entry.PartialLine.Write(segment);
                ProcessBufferedLine(entry);
                entry.PartialLine.SetLength(0);
            }

            segmentStart = index + 1;
            if (entry.Rejected)
            {
                return;
            }
        }

        if (segmentStart < bytes.Length)
        {
            entry.PartialLine.Write(bytes[segmentStart..]);
        }
    }

    private static void ProcessBufferedLine(RolloutCacheEntry entry)
    {
        if (!entry.PartialLine.TryGetBuffer(out var buffer))
        {
            return;
        }

        ProcessLine(entry, buffer.AsSpan(0, checked((int)entry.PartialLine.Length)));
    }

    private static void ProcessLine(RolloutCacheEntry entry, ReadOnlySpan<byte> utf8Line)
    {
        if (utf8Line.Length > 0 && utf8Line[^1] == (byte)'\r')
        {
            utf8Line = utf8Line[..^1];
        }

        if (utf8Line.IsEmpty || entry.Rejected)
        {
            return;
        }

        JsonDocument document;
        try
        {
            var jsonReader = new Utf8JsonReader(utf8Line, isFinalBlock: true, state: default);
            document = JsonDocument.ParseValue(ref jsonReader);
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGet(root, "type", out var rowType)
                || rowType.ValueKind != JsonValueKind.String
                || !TryGet(root, "payload", out var payload)
                || payload.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var timestamp = ReadTimestamp(root);
            var rowTypeName = rowType.GetString();
            if (rowTypeName == "session_meta")
            {
                var threadSource = ReadString(payload, "thread_source");
                var originator = ReadString(payload, "originator");
                var parentThreadId = ReadString(payload, "parent_thread_id");
                entry.Eligible = string.IsNullOrWhiteSpace(parentThreadId)
                    && (string.Equals(threadSource, "user", StringComparison.OrdinalIgnoreCase)
                    || (string.IsNullOrWhiteSpace(threadSource)
                        && string.Equals(originator, "Codex Desktop", StringComparison.OrdinalIgnoreCase)));

                if (!entry.Eligible)
                {
                    entry.Rejected = true;
                    entry.State = null;
                    return;
                }

                // Codex may emit session_meta again immediately after a
                // task_started event when a persisted Desktop thread is resumed.
                // Refresh only metadata so the active turn is preserved.
                entry.State ??= new CodexExecutionState();
                entry.State.SessionId = ReadString(payload, "id")
                    ?? ReadString(payload, "session_id")
                    ?? entry.State.SessionId;
                entry.State.TranscriptPath = entry.Path;
                entry.State.Cwd = ReadString(payload, "cwd") ?? entry.State.Cwd;
                entry.State.Model = ReadString(payload, "model")
                    ?? ReadString(payload, "model_provider")
                    ?? entry.State.Model;
                entry.State.LastUpdatedAtUtc = timestamp ?? entry.LastWriteTimeUtc;
                return;
            }

            if (!entry.Eligible || entry.State is null)
            {
                return;
            }

            if (rowTypeName == "response_item")
            {
                ApplyResponseItem(
                    entry,
                    payload,
                    timestamp ?? entry.State.LastUpdatedAtUtc);
                return;
            }

            if (rowTypeName != "event_msg")
            {
                return;
            }

            var eventType = ReadString(payload, "type");
            if (eventType is null || !SupportedEvents.Contains(eventType))
            {
                return;
            }

            ApplyEvent(
                entry.State,
                payload,
                eventType,
                timestamp ?? entry.State.LastUpdatedAtUtc);

            if (eventType is "task_started" or "task_complete" or "turn_aborted")
            {
                entry.PendingInputCallIds.Clear();
            }
        }
    }

    private static void ApplyResponseItem(
        RolloutCacheEntry entry,
        JsonElement payload,
        DateTimeOffset timestamp)
    {
        var state = entry.State;
        if (state is null)
        {
            return;
        }

        var itemType = ReadString(payload, "type");
        var callId = ReadString(payload, "call_id");
        if (itemType == "function_call" &&
            string.Equals(
                ReadString(payload, "name"),
                "request_user_input",
                StringComparison.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(callId))
            {
                entry.PendingInputCallIds.Add(callId);
            }

            state.Status = CodexExecutionStatuses.Waiting;
            state.Activity = CodexActivityLabels.WaitingForInput;
            state.CurrentTool = "request_user_input";
            state.WaitingSinceAtUtc = timestamp;
            state.LastUpdatedAtUtc = timestamp;
            return;
        }

        if (itemType != "function_call_output" ||
            string.IsNullOrWhiteSpace(callId) ||
            !entry.PendingInputCallIds.Remove(callId))
        {
            return;
        }

        if (entry.PendingInputCallIds.Count == 0 &&
            state.Status == CodexExecutionStatuses.Waiting &&
            string.Equals(
                state.CurrentTool,
                "request_user_input",
                StringComparison.Ordinal))
        {
            state.Status = CodexExecutionStatuses.Running;
            state.Activity = CodexActivityLabels.ProcessingRequest;
            state.CurrentTool = null;
            state.WaitingSinceAtUtc = null;
        }

        state.LastUpdatedAtUtc = timestamp;
    }

    private static bool CheckpointMatches(FileStream stream, RolloutCacheEntry entry)
    {
        if (!entry.Initialized || entry.Checkpoint.Length == 0)
        {
            return true;
        }

        if (stream.Length < entry.CheckpointOffset + entry.Checkpoint.Length)
        {
            return false;
        }

        stream.Position = entry.CheckpointOffset;
        Span<byte> actual = stackalloc byte[CheckpointSize];
        var expected = entry.Checkpoint.AsSpan();
        var totalRead = 0;

        while (totalRead < expected.Length)
        {
            var count = stream.Read(actual[totalRead..expected.Length]);
            if (count == 0)
            {
                return false;
            }

            totalRead += count;
        }

        return actual[..expected.Length].SequenceEqual(expected);
    }

    private static void UpdateCheckpoint(FileStream stream, RolloutCacheEntry entry)
    {
        var count = (int)Math.Min(CheckpointSize, entry.BytesRead);
        if (count == 0)
        {
            entry.Checkpoint = [];
            entry.CheckpointOffset = 0;
            return;
        }

        entry.CheckpointOffset = entry.BytesRead - count;
        entry.Checkpoint = new byte[count];
        stream.Position = entry.CheckpointOffset;

        var totalRead = 0;
        while (totalRead < count)
        {
            var read = stream.Read(entry.Checkpoint, totalRead, count - totalRead);
            if (read == 0)
            {
                entry.Checkpoint = entry.Checkpoint[..totalRead];
                break;
            }

            totalRead += read;
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
                InternalBufferSize = 64 * 1024,
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
            // Unsupported or temporarily unavailable watcher configuration.
            // The periodic rescan remains active as a correctness fallback.
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
            if (_disposed)
            {
                return;
            }

            // Keep callbacks constant-time and coalesce bursts from a single
            // append. Metadata and content are read on the next consumer pass.
            QueuePathChange(args.FullPath);
        }
    }

    private void OnRolloutRenamed(object sender, RenamedEventArgs args)
    {
        lock (_sync)
        {
            if (!_disposed)
            {
                QueuePathChange(args.OldFullPath);
                QueuePathChange(args.FullPath);
            }
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs args)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            // Buffer overflow or watcher failure: force a full scan now and
            // recreate the watcher so subsequent changes are signalled again.
            _fullRescanRequired = true;
            _latestSelectionDirty = true;
            DisposeWatcher();
        }
    }

    private void ResetForMissingRoot()
    {
        DisposeWatcher();
        ClearEntries();
        ClearPathIndex();
        _fullRescanRequired = true;
        _latestSelectionDirty = true;
        _pathIndexInitialized = false;
        _selectedPath = null;
        _nextFallbackRescanAtUtc = DateTimeOffset.MinValue;
        _nextDirtyContentRetryAtUtc = DateTimeOffset.MinValue;
        _nextPathMetadataRetryAtUtc = DateTimeOffset.MinValue;
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

    private void ClearEntries()
    {
        foreach (var entry in _entries.Values)
        {
            entry.Dispose();
        }

        _entries.Clear();
    }

    private void ClearPathIndex()
    {
        _indexedPaths.Clear();
        _orderedPaths.Clear();
        _pendingPathChanges.Clear();
        _contentDirtyPaths.Clear();
        _selectedPath = null;
        _latestSelectionDirty = true;
        _nextDirtyContentRetryAtUtc = DateTimeOffset.MinValue;
        _nextPathMetadataRetryAtUtc = DateTimeOffset.MinValue;
    }

    private static void ApplyEvent(
        CodexExecutionState state,
        JsonElement payload,
        string eventType,
        DateTimeOffset timestamp)
    {
        switch (eventType)
        {
            case "task_started":
                state.Status = CodexExecutionStatuses.Running;
                state.Activity = CodexActivityLabels.Running;
                state.TurnId = ReadString(payload, "turn_id") ?? state.TurnId;
                state.TaskTitle = null;
                state.StartedAtUtc = timestamp;
                state.StoppedAtUtc = null;
                state.ErrorMessage = null;
                state.CurrentTool = null;
                state.WaitingSinceAtUtc = null;
                state.ToolCount = 0;
                state.FilesChanged = [];
                state.ActiveSubagents = 0;
                state.TotalSubagents = 0;
                break;

            case "task_complete":
                state.Status = CodexExecutionStatuses.Completed;
                state.Activity = CodexActivityLabels.Completed;
                state.TurnId = ReadString(payload, "turn_id") ?? state.TurnId;
                state.StoppedAtUtc = timestamp;
                state.ActiveSubagents = 0;
                state.CurrentTool = null;
                state.WaitingSinceAtUtc = null;
                break;

            case "turn_aborted":
                state.Status = CodexExecutionStatuses.Aborted;
                state.Activity = CodexActivityLabels.Interrupted;
                state.TurnId = ReadString(payload, "turn_id") ?? state.TurnId;
                state.StoppedAtUtc = timestamp;
                state.ActiveSubagents = 0;
                state.CurrentTool = null;
                state.WaitingSinceAtUtc = null;
                break;

            case "patch_apply_end":
                state.ToolCount++;
                state.LastToolAtUtc = timestamp;
                if (ReadBoolean(payload, "success") is false)
                {
                    state.Status = CodexExecutionStatuses.Error;
                    state.Activity = CodexActivityLabels.FailedToApplyChanges;
                    state.ErrorMessage = ReadString(payload, "stderr");
                }
                else
                {
                    state.Status = CodexExecutionStatuses.Running;
                    state.Activity = CodexActivityLabels.ApplyingChanges;
                }

                if (TryGet(payload, "changes", out var changes) && changes.ValueKind == JsonValueKind.Object)
                {
                    var files = new HashSet<string>(state.FilesChanged, StringComparer.OrdinalIgnoreCase);
                    foreach (var change in changes.EnumerateObject())
                    {
                        files.Add(change.Name);
                    }

                    state.FilesChanged = files.ToList();
                }
                break;

            case "sub_agent_activity":
                var kind = ReadString(payload, "kind");
                if (kind == "started")
                {
                    state.ActiveSubagents++;
                    state.TotalSubagents++;
                    state.Activity = CodexActivityLabels.RunningSubagent;
                }
                else if (kind is "completed" or "stopped" or "failed")
                {
                    state.ActiveSubagents = Math.Max(0, state.ActiveSubagents - 1);
                    state.Activity = CodexActivityLabels.ProcessingSubagentResult;
                }
                break;

            case "agent_message":
                if (state.Status == CodexExecutionStatuses.Running)
                {
                    state.Activity = CodexActivityLabels.GeneratingResponse;
                }
                break;

            case "user_message":
                state.TaskTitle = NormalizeTaskTitle(ReadString(payload, "message"));
                break;

            case "token_count":
                ApplyTokens(state, payload);
                break;
        }

        state.LastUpdatedAtUtc = timestamp;
    }

    private static string? NormalizeTaskTitle(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        const string requestMarker = "## My request for Codex:";
        var requestMarkerIndex = message.LastIndexOf(
            requestMarker,
            StringComparison.OrdinalIgnoreCase);
        if (requestMarkerIndex >= 0)
        {
            message = message[(requestMarkerIndex + requestMarker.Length)..];
        }

        message = message.TrimStart();
        while (message.StartsWith('#'))
        {
            message = message[1..].TrimStart();
        }

        var title = new StringBuilder(Math.Min(message.Length, MaxTaskTitleLength));
        var pendingSpace = false;
        foreach (var character in message)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = title.Length > 0;
                continue;
            }

            if (pendingSpace && title.Length < MaxTaskTitleLength)
            {
                if (title.Length + 1 >= MaxTaskTitleLength)
                {
                    break;
                }

                title.Append(' ');
            }

            pendingSpace = false;
            if (title.Length >= MaxTaskTitleLength)
            {
                break;
            }

            title.Append(character);
        }

        return title.ToString();
    }

    private static void ApplyTokens(CodexExecutionState state, JsonElement payload)
    {
        if (!TryGet(payload, "info", out var info) || info.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        state.ModelContextWindow = ReadInt64(info, "model_context_window") ?? state.ModelContextWindow;
        if (!TryGet(info, "total_token_usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        state.InputTokens = ReadInt64(usage, "input_tokens") ?? state.InputTokens;
        state.OutputTokens = ReadInt64(usage, "output_tokens") ?? state.OutputTokens;
        state.TotalTokens = ReadInt64(usage, "total_tokens") ?? state.TotalTokens;
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement root)
    {
        var raw = ReadString(root, "timestamp");
        return DateTimeOffset.TryParse(raw, out var value) ? value.ToUniversalTime() : null;
    }

    private static string? ReadString(JsonElement element, string name)
    {
        return TryGet(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool? ReadBoolean(JsonElement element, string name)
    {
        if (!TryGet(element, name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static long? ReadInt64(JsonElement element, string name)
    {
        return TryGet(element, name, out var value) && value.TryGetInt64(out var number)
            ? number
            : null;
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

    private sealed record RolloutPathStamp(
        string Path,
        DateTime LastWriteTimeUtc,
        DateTime CreationTimeUtc,
        long Length);

    private enum PathStampReadResult
    {
        Found,
        Missing,
        Retry,
    }

    private sealed class RolloutPathStampComparer : IComparer<RolloutPathStamp>
    {
        public static RolloutPathStampComparer Instance { get; } = new();

        public int Compare(RolloutPathStamp? left, RolloutPathStamp? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return 1;
            }

            if (right is null)
            {
                return -1;
            }

            var timestampComparison = right.LastWriteTimeUtc.CompareTo(left.LastWriteTimeUtc);
            return timestampComparison != 0
                ? timestampComparison
                : PathComparer.Compare(left.Path, right.Path);
        }
    }

    private sealed class RolloutCacheEntry : IDisposable
    {
        public RolloutCacheEntry(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public MemoryStream PartialLine { get; } = new();

        public HashSet<string> PendingInputCallIds { get; } =
            new(StringComparer.Ordinal);

        public CodexExecutionState? State { get; set; }

        public long BytesRead { get; set; }

        public bool Initialized { get; set; }

        public bool Eligible { get; set; }

        public bool Rejected { get; set; }

        public DateTime CreationTimeUtc { get; set; }

        public DateTime LastWriteTimeUtc { get; set; }

        public long CheckpointOffset { get; set; }

        public byte[] Checkpoint { get; set; } = [];

        public bool HasUnreadBytes { get; set; }

        public void Reset()
        {
            State = null;
            BytesRead = 0;
            Eligible = false;
            Rejected = false;
            PartialLine.SetLength(0);
            PendingInputCallIds.Clear();
            CheckpointOffset = 0;
            Checkpoint = [];
            HasUnreadBytes = false;
        }

        public void Dispose()
        {
            PartialLine.Dispose();
        }
    }
}
