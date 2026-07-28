namespace Codex.TaskbarStatus.Core;

public static class CodexTaskDeepLink
{
    public static bool TryCreate(string? sessionId, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var escapedSessionId = Uri.EscapeDataString(sessionId.Trim());
        return Uri.TryCreate(
            $"codex://threads/{escapedSessionId}",
            UriKind.Absolute,
            out uri);
    }
}
