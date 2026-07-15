using Codex.TaskbarStatus.Core;
using Microsoft.UI.Xaml;

namespace Codex.TaskbarStatus.Standalone.Hosting;

internal sealed class TaskbarHostService
{
    private readonly TaskbarPreviewWindow _preview;
    private readonly Func<int> _previewLogicalWidth;
    private readonly Func<bool> _previewVisible;
    private readonly Func<StandaloneSettings> _settings;
    private readonly Action _restartRequested;
    private readonly DispatcherTimer _watchdog;
    private readonly TaskbarOccupancyProbe _occupancyProbe = new();
    private nint _lastTaskbar;
    private PixelRectangle _lastSlot;
    private PixelRectangle _lastSuccessfulSlot;
    private nint _lastSuccessfulTaskbar;
    private bool _lastAttached;
    private bool _restartSignalled;

    internal TaskbarHostService(
        TaskbarPreviewWindow preview,
        Func<int> previewLogicalWidth,
        Func<bool> previewVisible,
        Func<StandaloneSettings> settings,
        Action restartRequested)
    {
        _preview = preview;
        _previewLogicalWidth = previewLogicalWidth;
        _previewVisible = previewVisible;
        _settings = settings;
        _restartRequested = restartRequested;
        _watchdog = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _watchdog.Tick += (_, _) => Refresh();
    }

    internal PixelRectangle CurrentSlot => _lastSlot;

    internal void Start()
    {
        Refresh();
        _watchdog.Start();
    }

    internal void Stop()
    {
        _watchdog.Stop();
    }

    internal void Refresh()
    {
        try
        {
            if (!NativeMethods.IsWindow(_preview.WindowHandle))
            {
                if (!_restartSignalled)
                {
                    _restartSignalled = true;
                    StandaloneLog.Write(
                        "The taskbar destroyed the preview HWND; requesting a controlled relaunch.");
                    _restartRequested();
                }
                return;
            }

            var settings = _settings();
            var taskbar = NativeMethods.FindTaskbar(
                settings.MonitorDeviceName,
                settings.MonitorIndex);
            if (taskbar == nint.Zero)
            {
                taskbar = NativeMethods.FindTaskbar(0);
            }

            if (taskbar == nint.Zero ||
                !NativeMethods.GetWindowRect(taskbar, out var taskbarBounds) ||
                taskbarBounds.IsEmpty ||
                taskbarBounds.Height < 16 ||
                taskbarBounds.Height > 200 ||
                !_previewVisible())
            {
                _preview.Hide();
                RecordAttachment(taskbar, default, attached: false);
                return;
            }

            var dpi = NativeMethods.GetDpiForWindow(taskbar);
            if (dpi == 0)
            {
                dpi = 96;
            }

            var desiredWidth = Math.Max(
                1,
                (int)Math.Ceiling(_previewLogicalWidth() * dpi / 96d));
            if (_lastAttached &&
                _lastTaskbar == taskbar &&
                Math.Abs(desiredWidth - _lastSlot.Width) <= 4)
            {
                desiredWidth = _lastSlot.Width;
            }
            var taskbarRectangle = new PixelRectangle(
                taskbarBounds.Left,
                taskbarBounds.Top,
                taskbarBounds.Width,
                taskbarBounds.Height);
            var occupancy = _occupancyProbe.GetSnapshot(taskbar, taskbarBounds);
            if (occupancy is null)
            {
                if (_lastAttached &&
                    _lastTaskbar == taskbar &&
                    _lastSuccessfulTaskbar == taskbar &&
                    _lastSuccessfulSlot.Width > 0)
                {
                    var previousBounds = new NativeMethods.Rect(
                        _lastSuccessfulSlot.X,
                        _lastSuccessfulSlot.Y,
                        _lastSuccessfulSlot.X + _lastSuccessfulSlot.Width,
                        _lastSuccessfulSlot.Y + _lastSuccessfulSlot.Height);
                    var stillAttached = _preview.AttachAndShow(
                        taskbar,
                        taskbarBounds,
                        previousBounds);
                    RecordAttachment(taskbar, _lastSuccessfulSlot, stillAttached);
                }
                else
                {
                    _preview.Hide();
                    RecordAttachment(taskbar, default, attached: false);
                }
                return;
            }

            var preferredOffset = settings.PlacementMode == TaskbarPlacementMode.Automatic &&
                                  _lastSuccessfulSlot.Width > 0 &&
                                  _lastSuccessfulTaskbar == taskbar
                ? Math.Max(0, _lastSuccessfulSlot.X - taskbarBounds.Left)
                : settings.AnchorOffsetPx;
            var slot = TaskbarPlacementResolver.Calculate(
                taskbarRectangle,
                desiredWidth,
                new TaskbarSlotMargins(10, 0, 10, 0),
                settings.PlacementMode,
                preferredOffset,
                occupancy.OccupiedIntervals,
                occupiedSpacingPx: Math.Max(6, (int)Math.Ceiling(8 * dpi / 96d)));
            if (slot is null)
            {
                _preview.Hide();
                RecordAttachment(taskbar, default, attached: false);
                return;
            }

            var slotBounds = new NativeMethods.Rect(
                slot.Value.X,
                slot.Value.Y,
                slot.Value.X + slot.Value.Width,
                slot.Value.Y + slot.Value.Height);
            var attached = _preview.AttachAndShow(taskbar, taskbarBounds, slotBounds);
            RecordAttachment(taskbar, slot.Value, attached);
        }
        catch (Exception exception)
        {
            _preview.Hide();
            StandaloneLog.Write("Taskbar watchdog failed", exception);
        }
    }

    private void RecordAttachment(nint taskbar, PixelRectangle slot, bool attached)
    {
        if (_lastTaskbar == taskbar && _lastSlot == slot && _lastAttached == attached)
        {
            return;
        }

        var isMeasurementJitter =
            _lastTaskbar == taskbar &&
            _lastAttached == attached &&
            _lastSlot.X == slot.X &&
            _lastSlot.Y == slot.Y &&
            _lastSlot.Height == slot.Height &&
            Math.Abs(_lastSlot.Width - slot.Width) <= 4;

        _lastTaskbar = taskbar;
        _lastSlot = slot;
        _lastAttached = attached;
        if (attached && slot.Width > 0)
        {
            _lastSuccessfulSlot = slot;
            _lastSuccessfulTaskbar = taskbar;
        }
        if (isMeasurementJitter)
        {
            return;
        }

        StandaloneLog.Write(
            $"Taskbar state: attached={attached}, taskbar=0x{taskbar:X}, " +
            $"preview=0x{_preview.WindowHandle:X}, slot={slot.X},{slot.Y} {slot.Width}x{slot.Height}");
    }
}
