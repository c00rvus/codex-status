using Codex.TaskbarStatus.Standalone.Widget;

namespace Codex.TaskbarStatus.Standalone.Hosting;

internal sealed class StandaloneWidgetContext : IWidgetRuntimeContext
{
    internal StandaloneWidgetContext(string settingsJson)
    {
        SettingsJson = string.IsNullOrWhiteSpace(settingsJson) ? "{}" : settingsJson;
    }

    public string SettingsJson { get; private set; }

    internal event Action? PreviewRefreshRequested;

    internal event Action? OpenFlyoutRequested;

    internal event Action<int>? FlyoutResizeRequested;

    internal event Action<string>? OpenTaskRequested;

    internal event Action<WidgetAttentionNotification>? AttentionNotificationRequested;

    public void RequestPreviewRefresh() => PreviewRefreshRequested?.Invoke();

    public void RequestOpenFlyout() => OpenFlyoutRequested?.Invoke();

    public void RequestFlyoutResize(int logicalHeight) =>
        FlyoutResizeRequested?.Invoke(logicalHeight);

    public void RequestOpenTask(string sessionId) => OpenTaskRequested?.Invoke(sessionId);

    public void RequestAttentionNotification(WidgetAttentionNotification notification) =>
        AttentionNotificationRequested?.Invoke(notification);

    internal void ReplaceSettings(string settingsJson)
    {
        SettingsJson = string.IsNullOrWhiteSpace(settingsJson) ? "{}" : settingsJson;
    }
}
