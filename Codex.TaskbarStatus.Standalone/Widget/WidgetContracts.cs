namespace Codex.TaskbarStatus.Standalone.Widget;

internal interface IWidgetRuntimeContext
{
    string SettingsJson { get; }

    void RequestPreviewRefresh();

    void RequestOpenFlyout();
}

internal interface IWidgetSettingsContext
{
    string SettingsJson { get; }

    void SaveSettings(string settingsJson);

    void RequestPreviewRefresh();
}
