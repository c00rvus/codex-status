using System.Text.Json;
using System.Text.RegularExpressions;

namespace Codex.TaskbarStatus.Core;

public sealed class HookEventProcessor
{
    private static readonly Regex PatchFileHeader = new(
        @"^\*{3}\s+(?:(?:Add|Update|Delete) File:|Move to:)\s*(?<path>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private readonly StatusFileStore _store;
    private readonly TimeProvider _timeProvider;

    public HookEventProcessor(StatusFileStore? store = null, TimeProvider? timeProvider = null)
    {
        _store = store ?? new StatusFileStore();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<CodexExecutionState> ProcessAsync(
        string? hookJson,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(hookJson))
        {
            return await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(hookJson);
        }
        catch (JsonException)
        {
            return await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
            }

            var root = document.RootElement;
            var eventName = ReadString(root, "hook_event_name", "hookEventName", "event_name", "eventName", "event", "type");
            if (string.IsNullOrWhiteSpace(eventName))
            {
                return await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
            }

            var now = ReadTimestamp(root) ?? _timeProvider.GetUtcNow();
            return await _store.UpdateAsync(
                state => ApplyEvent(state, root, NormalizeName(eventName), now),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static CodexExecutionState ApplyEvent(
        CodexExecutionState state,
        JsonElement root,
        string eventName,
        DateTimeOffset now)
    {
        switch (eventName)
        {
            case "sessionstart":
                ResetExecution(state);
                state.Status = CodexExecutionStatuses.Running;
                state.Activity = "Iniciando sessão";
                state.StartedAtUtc = now;
                break;

            case "userpromptsubmit":
                ResetTurn(state);
                state.Status = CodexExecutionStatuses.Running;
                state.Activity = "Processando solicitação";
                state.StartedAtUtc = now;
                break;

            case "pretooluse":
                state.Status = CodexExecutionStatuses.Running;
                state.WaitingSinceAtUtc = null;
                state.CurrentTool = ReadToolName(root) ?? "ferramenta";
                state.Activity = IsApplyPatch(state.CurrentTool)
                    ? "Aplicando alterações"
                    : $"Executando {state.CurrentTool}";
                state.ToolCount++;
                state.LastToolAtUtc = now;
                AddPatchFiles(state, root, state.CurrentTool);
                break;

            case "permissionrequest":
                state.Status = CodexExecutionStatuses.Waiting;
                state.Activity = "Aguardando permissão";
                state.CurrentTool = ReadToolName(root) ?? state.CurrentTool;
                state.WaitingSinceAtUtc = now;
                break;

            case "posttooluse":
                state.Status = CodexExecutionStatuses.Running;
                state.Activity = "Processando resultado";
                state.WaitingSinceAtUtc = null;
                var completedTool = ReadToolName(root) ?? state.CurrentTool;
                AddPatchFiles(state, root, completedTool);
                state.CurrentTool = completedTool;
                break;

            case "subagentstart":
                state.Status = CodexExecutionStatuses.Running;
                state.Activity = "Executando subagente";
                state.ActiveSubagents++;
                state.TotalSubagents++;
                break;

            case "subagentstop":
                state.Status = CodexExecutionStatuses.Running;
                state.Activity = "Processando resultado do subagente";
                state.ActiveSubagents = Math.Max(0, state.ActiveSubagents - 1);
                break;

            case "stop":
                ApplyStop(state, root);
                state.CurrentTool = null;
                state.ActiveSubagents = 0;
                state.WaitingSinceAtUtc = null;
                state.StoppedAtUtc = now;
                break;
        }

        ApplyMetadata(state, root);
        state.LastUpdatedAtUtc = now;
        return state;
    }

    private static void ApplyStop(CodexExecutionState state, JsonElement root)
    {
        var reason = ReadString(root, "stop_reason", "stopReason", "reason", "outcome", "result", "status");
        var error = ReadString(root, "error", "error_message", "errorMessage");
        var normalizedReason = NormalizeName(reason ?? string.Empty);

        if (!string.IsNullOrWhiteSpace(error)
            || normalizedReason.Contains("error", StringComparison.Ordinal)
            || normalizedReason.Contains("fail", StringComparison.Ordinal))
        {
            state.Status = CodexExecutionStatuses.Error;
            state.Activity = "Erro";
            state.ErrorMessage = error ?? reason;
        }
        else if (normalizedReason.Contains("abort", StringComparison.Ordinal)
            || normalizedReason.Contains("cancel", StringComparison.Ordinal)
            || normalizedReason.Contains("interrupt", StringComparison.Ordinal))
        {
            state.Status = CodexExecutionStatuses.Aborted;
            state.Activity = "Interrompido";
            state.ErrorMessage = null;
        }
        else
        {
            state.Status = CodexExecutionStatuses.Completed;
            state.Activity = "Concluído";
            state.ErrorMessage = null;
        }
    }

    private static void ApplyMetadata(CodexExecutionState state, JsonElement root)
    {
        state.SessionId = ReadString(root, "session_id", "sessionId") ?? state.SessionId;
        state.TurnId = ReadString(root, "turn_id", "turnId") ?? state.TurnId;
        state.TranscriptPath = ReadString(root, "transcript_path", "transcriptPath") ?? state.TranscriptPath;
        state.Cwd = ReadString(root, "cwd", "working_directory", "workingDirectory") ?? state.Cwd;
        state.Model = ReadString(root, "model", "model_name", "modelName") ?? state.Model;
    }

    private static void ResetExecution(CodexExecutionState state)
    {
        state.SessionId = null;
        state.TurnId = null;
        state.TranscriptPath = null;
        state.Cwd = null;
        state.Model = null;
        ResetTurn(state);
    }

    private static void ResetTurn(CodexExecutionState state)
    {
        state.TurnId = null;
        state.CurrentTool = null;
        state.ErrorMessage = null;
        state.ToolCount = 0;
        state.FilesChanged = [];
        state.ActiveSubagents = 0;
        state.TotalSubagents = 0;
        state.LastToolAtUtc = null;
        state.WaitingSinceAtUtc = null;
        state.StoppedAtUtc = null;
    }

    private static void AddPatchFiles(CodexExecutionState state, JsonElement root, string? toolName)
    {
        if (!IsApplyPatch(toolName))
        {
            return;
        }

        var toolInput = FindElement(root, "tool_input", "toolInput", "input", "arguments");
        if (toolInput is null)
        {
            return;
        }

        var files = new HashSet<string>(state.FilesChanged, StringComparer.OrdinalIgnoreCase);
        CollectPatchFiles(toolInput.Value, files);
        state.FilesChanged = files.ToList();
    }

    private static void CollectPatchFiles(JsonElement element, HashSet<string> files)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var normalizedName = NormalizeName(property.Name);
                    if ((normalizedName is "filepath" or "path") && property.Value.ValueKind == JsonValueKind.String)
                    {
                        AddPath(files, property.Value.GetString());
                    }

                    CollectPatchFiles(property.Value, files);
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectPatchFiles(item, files);
                }
                break;

            case JsonValueKind.String:
                var value = element.GetString();
                if (value is null)
                {
                    return;
                }

                foreach (Match match in PatchFileHeader.Matches(value))
                {
                    AddPath(files, match.Groups["path"].Value);
                }
                break;
        }
    }

    private static void AddPath(HashSet<string> files, string? path)
    {
        path = path?.Trim().Trim('"', '\'');
        if (!string.IsNullOrWhiteSpace(path) && path != "/dev/null")
        {
            files.Add(path);
        }
    }

    private static bool IsApplyPatch(string? toolName) =>
        !string.IsNullOrWhiteSpace(toolName)
        && NormalizeName(toolName).Contains("applypatch", StringComparison.Ordinal);

    private static string? ReadToolName(JsonElement root)
    {
        var direct = ReadString(root, "tool_name", "toolName");
        if (direct is not null)
        {
            return direct;
        }

        var tool = FindElement(root, "tool");
        return tool is { ValueKind: JsonValueKind.Object }
            ? ReadScalarProperty(tool.Value, "name", "tool_name", "toolName")
            : null;
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement root)
    {
        var value = ReadString(root, "timestamp", "timestamp_utc", "timestampUtc", "created_at", "createdAt");
        return DateTimeOffset.TryParse(value, out var timestamp) ? timestamp.ToUniversalTime() : null;
    }

    private static string? ReadString(JsonElement root, params string[] names)
    {
        var direct = ReadScalarProperty(root, names);
        if (direct is not null)
        {
            return direct;
        }

        foreach (var containerName in new[] { "payload", "data", "context", "session" })
        {
            var container = FindDirectElement(root, containerName);
            if (container is { ValueKind: JsonValueKind.Object })
            {
                var nested = ReadScalarProperty(container.Value, names);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static string? ReadScalarProperty(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var normalizedNames = names.Select(NormalizeName).ToHashSet(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!normalizedNames.Contains(NormalizeName(property.Name)))
            {
                continue;
            }

            return property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            };
        }

        return null;
    }

    private static JsonElement? FindElement(JsonElement root, params string[] names)
    {
        var direct = FindDirectElement(root, names);
        if (direct is not null)
        {
            return direct;
        }

        foreach (var containerName in new[] { "payload", "data", "context" })
        {
            var container = FindDirectElement(root, containerName);
            if (container is { ValueKind: JsonValueKind.Object })
            {
                var nested = FindDirectElement(container.Value, names);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static JsonElement? FindDirectElement(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var normalizedNames = names.Select(NormalizeName).ToHashSet(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (normalizedNames.Contains(NormalizeName(property.Name)))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static string NormalizeName(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
