namespace Codex.TaskbarStatus.Standalone.Hosting;

internal sealed class TrayIconActivationFilter
{
    internal const uint WmLButtonUp = 0x0202;
    internal const uint WmLButtonDoubleClick = 0x0203;
    internal const uint WmRButtonUp = 0x0205;
    internal const uint WmContextMenu = 0x007B;
    internal const uint NinSelect = 0x0400;
    internal const uint NinKeySelect = 0x0401;

    private const long DuplicateActivationWindowMilliseconds = 250;
    private long? _lastSettingsActivation;

    internal bool ShouldOpenSettings(
        uint notification,
        bool usesVersion4,
        long timestampMilliseconds)
    {
        // Version 4 still reports normal mouse messages. NIN_SELECT and
        // NIN_KEYSELECT add the accessible keyboard activation paths.
        var isActivation = notification == WmLButtonUp ||
            (usesVersion4 && notification is NinSelect or NinKeySelect);
        if (!isActivation)
        {
            return false;
        }

        if (_lastSettingsActivation is long previousTimestamp)
        {
            var elapsed = timestampMilliseconds - previousTimestamp;
            if (elapsed >= 0 && elapsed < DuplicateActivationWindowMilliseconds)
            {
                return false;
            }
        }

        _lastSettingsActivation = timestampMilliseconds;
        return true;
    }

    internal static bool ShouldShowContextMenu(uint notification, bool usesVersion4) =>
        usesVersion4
            ? notification == WmContextMenu
            : notification == WmRButtonUp;
}
