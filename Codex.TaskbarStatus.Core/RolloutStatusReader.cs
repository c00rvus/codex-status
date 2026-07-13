using System.Text.Json;

namespace Codex.TaskbarStatus.Core;

/// <summary>
/// Read-only fallback for Codex Desktop versions that do not emit configured hooks.
/// Rollout files are never changed by this reader.
/// </summary>
public sealed class RolloutStatusReader
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

    private readonly string _sessionsRoot;

    public RolloutStatusReader(string? sessionsRoot = null)
    {
        _sessionsRoot = sessionsRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex",
            "sessions");
    }

    public CodexExecutionState? ReadLatest()
    {
        try
        {
            if (!Directory.Exists(_sessionsRoot))
            {
                return null;
            }

            foreach (var path in Directory.EnumerateFiles(_sessionsRoot, "rollout-*.jsonl", SearchOption.AllDirectories)
                         .OrderByDescending(File.GetLastWriteTimeUtc))
            {
                var state = ReadEligibleRollout(path);
                if (state is not null)
                {
                    return state;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static CodexExecutionState? ReadEligibleRollout(string path)
    {
        CodexExecutionState? state = null;
        var eligible = false;

        try
        {
            // Codex keeps the active rollout open for writing. File.ReadLines opens
            // with FileShare.Read only, so it cannot read that live file and the
            // fallback would silently select an older, already completed session.
            // Match the sharing mode used by log tailers so the active rollout can
            // be observed without ever blocking or modifying the Codex writer.
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            using var reader = new StreamReader(stream);

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    continue;
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
                        continue;
                    }

                    var timestamp = ReadTimestamp(root);
                    if (rowType.GetString() == "session_meta")
                    {
                        var threadSource = ReadString(payload, "thread_source");
                        var originator = ReadString(payload, "originator");
                        eligible = string.Equals(threadSource, "user", StringComparison.OrdinalIgnoreCase)
                            || (string.IsNullOrWhiteSpace(threadSource)
                                && string.Equals(originator, "Codex Desktop", StringComparison.OrdinalIgnoreCase));
                        if (!eligible)
                        {
                            return null;
                        }

                        // Codex may emit session_meta again immediately after a
                        // task_started event when a persisted Desktop thread is
                        // resumed. Refresh only the metadata; replacing the whole
                        // state here would erase the active turn and its start time.
                        state ??= new CodexExecutionState();
                        state.SessionId = ReadString(payload, "id")
                            ?? ReadString(payload, "session_id")
                            ?? state.SessionId;
                        state.TranscriptPath = path;
                        state.Cwd = ReadString(payload, "cwd") ?? state.Cwd;
                        state.Model = ReadString(payload, "model")
                            ?? ReadString(payload, "model_provider")
                            ?? state.Model;
                        state.LastUpdatedAtUtc = timestamp ?? File.GetLastWriteTimeUtc(path);
                        continue;
                    }

                    if (!eligible || state is null || rowType.GetString() != "event_msg")
                    {
                        continue;
                    }

                    var eventType = ReadString(payload, "type");
                    if (eventType is null || !SupportedEvents.Contains(eventType))
                    {
                        continue;
                    }

                    ApplyEvent(state, payload, eventType, timestamp ?? state.LastUpdatedAtUtc);
                }
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        return eligible ? state : null;
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
}
