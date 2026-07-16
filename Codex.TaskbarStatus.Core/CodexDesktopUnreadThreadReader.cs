using System.Collections.Frozen;
using System.Text.Json;

namespace Codex.TaskbarStatus.Core;

public sealed record CodexDesktopUnreadState(
    bool IsAvailable,
    IReadOnlySet<string> ThreadIds);

/// <summary>
/// Reads Codex Desktop's persisted unread-thread state without modifying it.
/// </summary>
public sealed class CodexDesktopUnreadThreadReader
{
    private const string PersistedAtomStateProperty = "electron-persisted-atom-state";
    private const string UnreadThreadsProperty = "unread-thread-ids-by-host-v1";
    private const string LocalHostProperty = "local";

    private static readonly IReadOnlySet<string> EmptyThreadIds =
        Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal);

    private static readonly CodexDesktopUnreadState UnavailableState =
        new(false, EmptyThreadIds);

    private readonly string _path;
    private readonly object _sync = new();
    private CodexDesktopUnreadState? _cachedState;
    private CodexDesktopUnreadState? _lastGoodState;
    private long _cachedLength;
    private DateTime _cachedLastWriteTimeUtc;

    public CodexDesktopUnreadThreadReader(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex",
            ".codex-global-state.json");

        if (string.IsNullOrWhiteSpace(_path))
        {
            throw new ArgumentException("The Codex Desktop state path cannot be empty.", nameof(path));
        }
    }

    public CodexDesktopUnreadState Read()
    {
        lock (_sync)
        {
            var fileInfo = new FileInfo(_path);
            try
            {
                fileInfo.Refresh();
                if (!fileInfo.Exists)
                {
                    ClearCache();
                    return UnavailableState;
                }

                if (_cachedState is not null
                    && fileInfo.Length == _cachedLength
                    && fileInfo.LastWriteTimeUtc == _cachedLastWriteTimeUtc)
                {
                    return _cachedState;
                }

                var state = ReadState();
                Cache(state, fileInfo.Length, fileInfo.LastWriteTimeUtc);
                if (state.IsAvailable)
                {
                    _lastGoodState = state;
                }

                return state;
            }
            catch (IOException)
            {
                return _lastGoodState ?? UnavailableState;
            }
            catch (UnauthorizedAccessException)
            {
                return _lastGoodState ?? UnavailableState;
            }
            catch (JsonException)
            {
                return _lastGoodState ?? UnavailableState;
            }
        }
    }

    private CodexDesktopUnreadState ReadState()
    {
        using var stream = new FileStream(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var document = JsonDocument.Parse(stream);

        if (!TryGetObjectProperty(document.RootElement, PersistedAtomStateProperty, out var atomState)
            || !TryGetObjectProperty(atomState, UnreadThreadsProperty, out var unreadByHost)
            || !unreadByHost.TryGetProperty(LocalHostProperty, out var localThreads)
            || localThreads.ValueKind != JsonValueKind.Array)
        {
            return UnavailableState;
        }

        var threadIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in localThreads.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                return UnavailableState;
            }

            var threadId = item.GetString();
            if (!string.IsNullOrWhiteSpace(threadId))
            {
                threadIds.Add(threadId);
            }
        }

        return new CodexDesktopUnreadState(
            true,
            threadIds.ToFrozenSet(StringComparer.Ordinal));
    }

    private static bool TryGetObjectProperty(
        JsonElement parent,
        string propertyName,
        out JsonElement value)
    {
        value = default;
        return parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(propertyName, out value)
            && value.ValueKind == JsonValueKind.Object;
    }

    private void Cache(CodexDesktopUnreadState state, long length, DateTime lastWriteTimeUtc)
    {
        _cachedState = state;
        _cachedLength = length;
        _cachedLastWriteTimeUtc = lastWriteTimeUtc;
    }

    private void ClearCache()
    {
        _cachedState = null;
        _cachedLength = 0;
        _cachedLastWriteTimeUtc = default;
        _lastGoodState = null;
    }
}
