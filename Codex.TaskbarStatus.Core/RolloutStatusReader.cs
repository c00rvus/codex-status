using System.Buffers;
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
        "token_count",
    };

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static readonly TimeSpan DefaultFallbackRescanInterval = TimeSpan.FromSeconds(5);
    private const int ReadBufferSize = 16 * 1024;
    private const int CheckpointSize = 128;

    private readonly string _sessionsRoot;
    private readonly TimeSpan _fallbackRescanInterval;
    private readonly object _sync = new();
    private readonly Dictionary<string, RolloutCacheEntry> _entries = new(PathComparer);
    private FileSystemWatcher? _watcher;
    private string? _selectedPath;
    private DateTimeOffset _nextFallbackRescanAtUtc = DateTimeOffset.MinValue;
    private bool _rescanRequired = true;
    private bool _disposed;

    public RolloutStatusReader(
        string? sessionsRoot = null,
        TimeSpan? fallbackRescanInterval = null)
    {
        _sessionsRoot = sessionsRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex",
            "sessions");
        _fallbackRescanInterval = fallbackRescanInterval ?? DefaultFallbackRescanInterval;

        if (_fallbackRescanInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fallbackRescanInterval),
                "The fallback rescan interval cannot be negative.");
        }
    }

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

            var now = DateTimeOffset.UtcNow;
            if (!_rescanRequired
                && now < _nextFallbackRescanAtUtc
                && _selectedPath is not null
                && _entries.TryGetValue(_selectedPath, out var selectedEntry))
            {
                if (TryRefreshEntry(selectedEntry))
                {
                    if (selectedEntry.Eligible)
                    {
                        return selectedEntry.State;
                    }

                    // An active file can only stop being eligible if it was
                    // replaced or truncated. Find the next eligible rollout.
                    _rescanRequired = true;
                }
                else
                {
                    _rescanRequired = true;
                }
            }

            return Rescan(now);
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
        }
    }

    private CodexExecutionState? Rescan(DateTimeOffset now)
    {
        var paths = new List<(string Path, DateTime LastWriteTimeUtc)>();

        try
        {
            foreach (var path in Directory.EnumerateFiles(
                         _sessionsRoot,
                         "rollout-*.jsonl",
                         SearchOption.AllDirectories))
            {
                try
                {
                    paths.Add((path, File.GetLastWriteTimeUtc(path)));
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

        var presentPaths = new HashSet<string>(paths.Select(item => item.Path), PathComparer);
        foreach (var stalePath in _entries.Keys.Where(path => !presentPaths.Contains(path)).ToArray())
        {
            _entries[stalePath].Dispose();
            _entries.Remove(stalePath);
        }

        CodexExecutionState? selectedState = null;
        string? selectedPath = null;

        foreach (var candidate in paths.OrderByDescending(item => item.LastWriteTimeUtc))
        {
            if (!_entries.TryGetValue(candidate.Path, out var entry))
            {
                entry = new RolloutCacheEntry(candidate.Path);
                _entries.Add(candidate.Path, entry);
            }

            if (!TryRefreshEntry(entry) || !entry.Eligible || entry.State is null)
            {
                continue;
            }

            selectedPath = candidate.Path;
            selectedState = entry.State;
            break;
        }

        _selectedPath = selectedPath;
        _rescanRequired = false;
        _nextFallbackRescanAtUtc = now + _fallbackRescanInterval;
        return selectedState;
    }

    private static bool TryRefreshEntry(RolloutCacheEntry entry)
    {
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

            if (entry.Initialized
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
            if (rowType.GetString() == "session_meta")
            {
                var threadSource = ReadString(payload, "thread_source");
                var originator = ReadString(payload, "originator");
                entry.Eligible = string.Equals(threadSource, "user", StringComparison.OrdinalIgnoreCase)
                    || (string.IsNullOrWhiteSpace(threadSource)
                        && string.Equals(originator, "Codex Desktop", StringComparison.OrdinalIgnoreCase));

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

            if (!entry.Eligible || entry.State is null || rowType.GetString() != "event_msg")
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
        }
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

            // Appends to the selected file are detected by its cached length.
            // Any other path may be a newer eligible session and needs ordering.
            if (args.ChangeType is WatcherChangeTypes.Created or WatcherChangeTypes.Deleted
                || _selectedPath is null
                || !PathComparer.Equals(args.FullPath, _selectedPath))
            {
                _rescanRequired = true;
            }
        }
    }

    private void OnRolloutRenamed(object sender, RenamedEventArgs args)
    {
        lock (_sync)
        {
            if (!_disposed)
            {
                _rescanRequired = true;
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
            _rescanRequired = true;
            DisposeWatcher();
        }
    }

    private void ResetForMissingRoot()
    {
        DisposeWatcher();
        ClearEntries();
        _selectedPath = null;
        _rescanRequired = true;
        _nextFallbackRescanAtUtc = DateTimeOffset.MinValue;
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
                state.StartedAtUtc = timestamp;
                state.StoppedAtUtc = null;
                state.ErrorMessage = null;
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
                break;

            case "turn_aborted":
                state.Status = CodexExecutionStatuses.Aborted;
                state.Activity = CodexActivityLabels.Interrupted;
                state.TurnId = ReadString(payload, "turn_id") ?? state.TurnId;
                state.StoppedAtUtc = timestamp;
                state.ActiveSubagents = 0;
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

            case "token_count":
                ApplyTokens(state, payload);
                break;
        }

        state.LastUpdatedAtUtc = timestamp;
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

    private sealed class RolloutCacheEntry : IDisposable
    {
        public RolloutCacheEntry(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public MemoryStream PartialLine { get; } = new();

        public CodexExecutionState? State { get; set; }

        public long BytesRead { get; set; }

        public bool Initialized { get; set; }

        public bool Eligible { get; set; }

        public bool Rejected { get; set; }

        public DateTime CreationTimeUtc { get; set; }

        public DateTime LastWriteTimeUtc { get; set; }

        public long CheckpointOffset { get; set; }

        public byte[] Checkpoint { get; set; } = [];

        public void Reset()
        {
            State = null;
            BytesRead = 0;
            Eligible = false;
            Rejected = false;
            PartialLine.SetLength(0);
            CheckpointOffset = 0;
            Checkpoint = [];
        }

        public void Dispose()
        {
            PartialLine.Dispose();
        }
    }
}
