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

    public void RequestPreviewRefresh() => PreviewRefreshRequested?.Invoke();

    public void RequestOpenFlyout() => OpenFlyoutRequested?.Invoke();

    internal void ReplaceSettings(string settingsJson)
    {
        SettingsJson = string.IsNullOrWhiteSpace(settingsJson) ? "{}" : settingsJson;
    }
}
