using Codex.TaskbarStatus.Core;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Codex.TaskbarStatus.Standalone.Hosting;

/// <summary>
/// Edits standalone taskbar placement as a draft. The control intentionally
/// uses only WinUI core controls so it remains safe in the unpackaged host.
/// </summary>
internal sealed class TaskbarPlacementSettingsControl : Grid
{
    private const int DefaultMaximumOffsetPx = 100_000;

    private static readonly (TaskbarPlacementMode Mode, string Label)[] PlacementOptions =
    [
        (TaskbarPlacementMode.Automatic, "Automatic (recommended)"),
        (TaskbarPlacementMode.Left, "Left"),
        (TaskbarPlacementMode.Center, "Center"),
        (TaskbarPlacementMode.Right, "Right"),
        (TaskbarPlacementMode.Manual, "Manual"),
    ];

    private readonly ComboBox _monitorSelector;
    private readonly ComboBox _positionSelector;
    private readonly Border _manualOffsetContainer;
    private readonly StackPanel _manualOffsetRow;
    private readonly Slider _manualOffsetSlider;
    private readonly TextBlock _manualOffsetValue;
    private readonly List<TaskbarMonitorOption> _monitors = [];
    private StandalonePlacementDraft _draft;
    private int _maximumManualOffsetPx = DefaultMaximumOffsetPx;
    private bool _updatingControls;

    internal TaskbarPlacementSettingsControl(
        StandalonePlacementDraft draft,
        IReadOnlyList<TaskbarMonitorOption> monitors)
    {
        _draft = draft.Normalize();
        AutomationProperties.SetName(this, "Taskbar placement settings");
        UseLayoutRounding = true;

        _monitorSelector = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 220,
        };
        AutomationProperties.SetName(_monitorSelector, "Taskbar monitor");
        _monitorSelector.SelectionChanged += (_, _) => ApplyMonitorSelection();

        _positionSelector = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 220,
        };
        AutomationProperties.SetName(_positionSelector, "Widget position");
        foreach (var option in PlacementOptions)
        {
            _positionSelector.Items.Add(option.Label);
        }
        _positionSelector.SelectionChanged += (_, _) => ApplyPositionSelection();

        _manualOffsetValue = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.76,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _manualOffsetSlider = new Slider
        {
            Minimum = 0,
            Maximum = _maximumManualOffsetPx,
            SmallChange = 8,
            StepFrequency = 1,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(
            _manualOffsetSlider,
            "Manual offset from the left edge, in pixels");
        _manualOffsetSlider.ValueChanged += (_, _) => ApplyManualOffset();

        var manualHeader = new Grid();
        manualHeader.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });
        manualHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        manualHeader.Children.Add(new TextBlock
        {
            Text = "Offset from left edge",
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(_manualOffsetValue, 1);
        manualHeader.Children.Add(_manualOffsetValue);

        _manualOffsetRow = new StackPanel { Spacing = 4 };
        _manualOffsetRow.Children.Add(manualHeader);
        _manualOffsetRow.Children.Add(_manualOffsetSlider);

        var fields = new Grid { ColumnSpacing = 12 };
        fields.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });
        fields.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });
        var monitorField = CreateField("Monitor", _monitorSelector);
        var positionField = CreateField("Position", _positionSelector);
        Grid.SetColumn(positionField, 1);
        fields.Children.Add(monitorField);
        fields.Children.Add(positionField);

        var content = new StackPanel { Spacing = 14 };
        content.Children.Add(new TextBlock
        {
            Text = "Taskbar placement",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
        });
        content.Children.Add(fields);
        _manualOffsetContainer = new Border
        {
            Background = new SolidColorBrush(ColorHelper.FromArgb(24, 27, 32, 38)),
            BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(56, 60, 67, 76)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 6),
            Child = _manualOffsetRow,
        };
        content.Children.Add(_manualOffsetContainer);

        Children.Add(new Border
        {
            Background = new SolidColorBrush(ColorHelper.FromArgb(22, 27, 32, 38)),
            BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(64, 60, 67, 76)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            VerticalAlignment = VerticalAlignment.Top,
            Child = content,
        });

        SetMonitors(monitors);
        LoadDraft(_draft);
    }

    internal event Action<StandalonePlacementDraft>? DraftChanged;

    internal StandalonePlacementDraft Draft => _draft;

    internal void SetMonitors(IReadOnlyList<TaskbarMonitorOption> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);

        var wasUpdating = _updatingControls;
        _updatingControls = true;
        try
        {
            _monitors.Clear();
            _monitors.AddRange(monitors
                .Where(monitor => monitor.Index >= 0)
                .DistinctBy(
                    monitor => string.IsNullOrWhiteSpace(monitor.DeviceName)
                        ? $"index:{monitor.Index}"
                        : monitor.DeviceName,
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(monitor => monitor.Index));
            if (_monitors.Count == 0)
            {
                _monitors.Add(new TaskbarMonitorOption(
                    0,
                    "Primary monitor",
                    IsPrimary: true));
            }

            if (!_monitors.Any(monitor => MonitorMatches(monitor, _draft)))
            {
                _monitors.Add(new TaskbarMonitorOption(
                    _draft.MonitorIndex,
                    $"Monitor {_draft.MonitorIndex + 1}",
                    IsAvailable: false,
                    DeviceName: _draft.MonitorDeviceName));
            }

            _monitorSelector.Items.Clear();
            foreach (var monitor in _monitors)
            {
                var label = monitor.DisplayName;
                if (!monitor.IsAvailable)
                {
                    label += " (disconnected)";
                }
                else if (monitor.IsPrimary &&
                         !label.Contains("primary", StringComparison.OrdinalIgnoreCase))
                {
                    label += " (primary)";
                }

                _monitorSelector.Items.Add(new ComboBoxItem
                {
                    Content = label,
                    IsEnabled = monitor.IsAvailable,
                });
            }

            SelectMonitorAndApplyLimit();
        }
        finally
        {
            _updatingControls = wasUpdating;
        }
    }

    internal void LoadDraft(StandalonePlacementDraft draft)
    {
        var wasUpdating = _updatingControls;
        _updatingControls = true;
        try
        {
            _draft = draft.Normalize();
            SelectMonitorAndApplyLimit();
            _positionSelector.SelectedIndex = Array.FindIndex(
                PlacementOptions,
                option => option.Mode == _draft.Mode);
            if (_positionSelector.SelectedIndex < 0)
            {
                _positionSelector.SelectedIndex = 0;
            }
            SynchronizeManualOffset();
            UpdateManualVisibility();
        }
        finally
        {
            _updatingControls = wasUpdating;
        }
    }

    internal void SetManualOffsetMaximum(int maximumPx)
    {
        var previous = _draft;
        var wasUpdating = _updatingControls;
        _updatingControls = true;
        try
        {
            ApplyMaximum(maximumPx);
        }
        finally
        {
            _updatingControls = wasUpdating;
        }

        if (!wasUpdating && _draft != previous)
        {
            DraftChanged?.Invoke(_draft);
        }
    }

    private static StackPanel CreateField(string label, Control control)
    {
        var field = new StackPanel { Spacing = 5 };
        field.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Opacity = 0.78,
        });
        field.Children.Add(control);
        return field;
    }

    private void SelectMonitorAndApplyLimit()
    {
        var selectedIndex = _monitors.FindIndex(monitor => MonitorMatches(monitor, _draft));
        if (selectedIndex < 0)
        {
            selectedIndex = _monitors.FindIndex(monitor => monitor.IsAvailable);
        }
        selectedIndex = Math.Max(0, selectedIndex);
        _monitorSelector.SelectedIndex = selectedIndex;

        if (selectedIndex < _monitors.Count && _monitors[selectedIndex].IsAvailable)
        {
            var monitor = _monitors[selectedIndex];
            _draft = _draft with
            {
                MonitorIndex = monitor.Index,
                MonitorDeviceName = monitor.DeviceName?.Trim() ?? string.Empty,
            };
            ApplyMaximum(GetMaximumOffset(monitor));
        }
        else
        {
            ApplyMaximum(DefaultMaximumOffsetPx);
        }
    }

    private void ApplyMonitorSelection()
    {
        if (_updatingControls ||
            _monitorSelector.SelectedIndex < 0 ||
            _monitorSelector.SelectedIndex >= _monitors.Count)
        {
            return;
        }

        var monitor = _monitors[_monitorSelector.SelectedIndex];
        if (!monitor.IsAvailable)
        {
            return;
        }

        var previous = _draft;
        var wasUpdating = _updatingControls;
        _updatingControls = true;
        try
        {
            _draft = _draft with
            {
                MonitorIndex = monitor.Index,
                MonitorDeviceName = monitor.DeviceName?.Trim() ?? string.Empty,
            };
            ApplyMaximum(GetMaximumOffset(monitor));
        }
        finally
        {
            _updatingControls = wasUpdating;
        }

        if (_draft != previous)
        {
            DraftChanged?.Invoke(_draft);
        }
    }

    private void ApplyPositionSelection()
    {
        if (_updatingControls ||
            _positionSelector.SelectedIndex < 0 ||
            _positionSelector.SelectedIndex >= PlacementOptions.Length)
        {
            return;
        }

        var mode = PlacementOptions[_positionSelector.SelectedIndex].Mode;
        if (mode != _draft.Mode)
        {
            _draft = _draft with { Mode = mode };
            DraftChanged?.Invoke(_draft);
        }
        UpdateManualVisibility();
    }

    private void ApplyManualOffset()
    {
        if (_updatingControls)
        {
            return;
        }

        var offset = Math.Clamp(
            (int)Math.Round(_manualOffsetSlider.Value),
            0,
            _maximumManualOffsetPx);
        _manualOffsetValue.Text = $"{offset:N0} px";
        if (offset == _draft.ManualOffsetPx)
        {
            return;
        }

        _draft = _draft with { ManualOffsetPx = offset };
        DraftChanged?.Invoke(_draft);
    }

    private void ApplyMaximum(int maximumPx)
    {
        _maximumManualOffsetPx = Math.Max(0, maximumPx);
        _draft = _draft.Normalize(_maximumManualOffsetPx);
        _manualOffsetSlider.Maximum = _maximumManualOffsetPx;
        SynchronizeManualOffset();
    }

    private void SynchronizeManualOffset()
    {
        _manualOffsetSlider.Value = _draft.ManualOffsetPx;
        _manualOffsetValue.Text = $"{_draft.ManualOffsetPx:N0} px";
    }

    private void UpdateManualVisibility()
    {
        _manualOffsetContainer.Visibility = _draft.Mode == TaskbarPlacementMode.Manual
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static bool MonitorMatches(
        TaskbarMonitorOption monitor,
        StandalonePlacementDraft draft) =>
        !string.IsNullOrWhiteSpace(draft.MonitorDeviceName)
            ? string.Equals(
                monitor.DeviceName,
                draft.MonitorDeviceName,
                StringComparison.OrdinalIgnoreCase)
            : monitor.Index == draft.MonitorIndex;

    private static int GetMaximumOffset(TaskbarMonitorOption monitor) =>
        monitor is { IsAvailable: true, WidthPx: > 0 }
            ? monitor.WidthPx
            : DefaultMaximumOffsetPx;
}
