namespace Codex.TaskbarStatus.Standalone.Widget;

internal sealed record WidgetAttentionNotification(
    string TaskKey,
    string? SessionId,
    string Title,
    string Message,
    bool IsError);

internal interface IWidgetRuntimeContext
{
    string SettingsJson { get; }

    void RequestPreviewRefresh();

    void RequestOpenFlyout();

    void RequestFlyoutResize(int logicalHeight);

    void RequestOpenTask(string sessionId);

    void RequestAttentionNotification(WidgetAttentionNotification notification);
}

internal interface IWidgetSettingsContext
{
    string SettingsJson { get; }

    bool StartWithWindows { get; }

    void SaveSettings(string settingsJson);

    void SetStartWithWindows(bool enabled);

    void RequestPreviewRefresh();
}
