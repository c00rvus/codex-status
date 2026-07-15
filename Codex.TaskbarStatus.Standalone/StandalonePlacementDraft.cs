using Codex.TaskbarStatus.Core;

namespace Codex.TaskbarStatus.Standalone;

/// <summary>
/// Editable placement state used by the standalone settings window. This
/// deliberately lives outside <c>CodexWidgetSettings</c> because taskbar
/// placement belongs to the native host rather than the status presentation.
/// </summary>
internal readonly record struct StandalonePlacementDraft(
    int MonitorIndex,
    string MonitorDeviceName,
    TaskbarPlacementMode Mode,
    int ManualOffsetPx)
{
    internal static StandalonePlacementDraft Default { get; } = new(
        StandaloneSettings.DefaultMonitorIndex,
        string.Empty,
        TaskbarPlacementMode.Automatic,
        StandaloneSettings.DefaultAnchorOffsetPx);

    internal StandalonePlacementDraft Normalize(int maximumManualOffsetPx = int.MaxValue)
    {
        var mode = Enum.IsDefined(Mode) ? Mode : TaskbarPlacementMode.Automatic;
        return new StandalonePlacementDraft(
            Math.Max(0, MonitorIndex),
            MonitorDeviceName?.Trim() ?? string.Empty,
            mode,
            Math.Clamp(ManualOffsetPx, 0, Math.Max(0, maximumManualOffsetPx)));
    }
}

/// <summary>
/// One taskbar exposed by the host to the placement selector.
/// </summary>
internal sealed record TaskbarMonitorOption(
    int Index,
    string DisplayName,
    bool IsPrimary = false,
    bool IsAvailable = true,
    string DeviceName = "",
    int WidthPx = 0);
