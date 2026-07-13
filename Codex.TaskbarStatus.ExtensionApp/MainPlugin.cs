using Codex.TaskbarStatus.Core;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using WidBar.SDK;

namespace Codex.TaskbarStatus.ExtensionApp;

public sealed class MainPlugin : WidgetPluginBase, IConfigurableWidgetPlugin
{
    private static readonly (string Name, string Hex)[] SpinnerColorPresets =
    [
        ("Blue", "#3B9EFF"),
        ("Cyan", "#22D3EE"),
        ("Green", "#34D399"),
        ("Amber", "#FBBF24"),
        ("Purple", "#C084FC"),
    ];

    private readonly CodexStatusReader _statusReader = new();
    private readonly CodexRateLimitService _rateLimitService = new();
    private readonly RequestSpinnerAnimator _spinnerAnimator = new();
    private CodexWidgetSettings _settings = new();
    private CodexStatusSnapshot _snapshot = new();
    private CodexRateLimitSnapshot _rateLimits = CodexRateLimitSnapshot.Unknown;
    private Border? _previewRoot;
    private StackPanel? _flyoutRoot;
    private DispatcherTimer? _refreshTimer;
    private DispatcherTimer? _spinnerTimer;
    private TextBlock? _spinnerText;
    private bool _spinnerTimerRunning;
    private int _previewLogicalWidth = 400;

    public override string Id => "com.wille.codex.taskbarstatus";
    public override string Name => "Codex Status";
    public override string Description => "Local Codex execution status in the taskbar.";
    public override WidgetCategory Category => WidgetCategory.Developer;

    public override int PreviewLogicalWidth => _previewLogicalWidth;
    public override bool IsPreviewVisible => !_settings.HideWhenIdle || IsActive(_snapshot.Status);
    public override int FlyoutWidth => 420;
    public override int FlyoutHeight => 340;
    public override WidgetFlyoutBackdrop FlyoutBackdrop => WidgetFlyoutBackdrop.Acrylic;

    public override Task InitializeAsync(IWidgetContext context)
    {
        _settings = CodexWidgetSettings.FromJson(context.SettingsJson);
        _snapshot = _statusReader.Read();
        _rateLimits = _rateLimitService.Current;
        _rateLimitService.RequestRefresh();
        return base.InitializeAsync(context);
    }

    public override UIElement CreatePreviewContent()
    {
        var initialWidth = _previewLogicalWidth;
        _previewRoot = new Border
        {
            Background = new SolidColorBrush(Colors.Transparent),
            Padding = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
        };

        AutomationProperties.SetName(_previewRoot, "Codex execution status");
        RenderPreview();
        EnsureTimers();

        if (initialWidth != _previewLogicalWidth)
        {
            _previewRoot.DispatcherQueue.TryEnqueue(() => Context?.RequestPreviewRefresh());
        }

        return _previewRoot;
    }

    public override UIElement CreateFlyoutContent()
    {
        _flyoutRoot = new StackPanel
        {
            Spacing = 14,
            Padding = new Thickness(22),
        };

        RenderFlyout();
        EnsureTimers();

        return new ScrollViewer
        {
            Content = _flyoutRoot,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    public UIElement CreateSettingsContent(IWidgetSettingsContext context)
    {
        var draft = CodexWidgetSettings.FromJson(context.SettingsJson);
        var root = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Loaded += (_, _) => ConfigureSettingsWindowMinimumSize(root);

        var cards = new Grid
        {
            ColumnSpacing = 14,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
        };
        cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var displayedOptions = new StackPanel { Spacing = 6 };
        void RenderDisplayedOptions()
        {
            displayedOptions.Children.Clear();
            for (var index = 0; index < draft.IndicatorOrder.Count; index++)
            {
                AddOrderedIndicatorRow(
                    displayedOptions,
                    draft.IndicatorOrder[index],
                    index,
                    draft,
                    context,
                    RenderDisplayedOptions);
            }
        }
        RenderDisplayedOptions();

        var behaviorOptions = new StackPanel { Spacing = 6 };
        AddToggle(behaviorOptions, "Animated spinner", draft.ShowPulse, value => draft.ShowPulse = value, draft, context);
        AddSpinnerColorSelector(behaviorOptions, draft, context);
        AddToggle(behaviorOptions, "Compact mode", draft.Compact, value => draft.Compact = value, draft, context);
        AddToggle(behaviorOptions, "Hide when idle", draft.HideWhenIdle, value => draft.HideWhenIdle = value, draft, context);

        var displayedCard = CreateSettingsCard("Displayed items", displayedOptions);
        var behaviorCard = CreateSettingsCard("Animation and behavior", behaviorOptions);
        Grid.SetColumn(behaviorCard, 1);
        cards.Children.Add(displayedCard);
        cards.Children.Add(behaviorCard);
        root.Children.Add(new ScrollViewer
        {
            Content = cards,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });

        var attributionLink = new HyperlinkButton
        {
            Content = "Spinners by Eronred/expo-agent-spinners (MIT)",
            NavigateUri = new Uri("https://github.com/Eronred/expo-agent-spinners"),
            FontSize = 12,
            Opacity = 0.72,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        AutomationProperties.SetName(
            attributionLink,
            "Open the Eronred expo-agent-spinners repository");
        Grid.SetRow(attributionLink, 1);
        root.Children.Add(attributionLink);

        return root;
    }

    public override void OnSettingsDraftChanged(string settingsJson)
    {
        _settings = CodexWidgetSettings.FromJson(settingsJson);
        RenderPreview();
        SyncSpinnerTimer();
        Context?.RequestPreviewRefresh();
    }

    public override async ValueTask DisposeAsync()
    {
        _refreshTimer?.Stop();
        _refreshTimer = null;
        _spinnerTimer?.Stop();
        _spinnerTimer = null;
        _spinnerTimerRunning = false;
        _spinnerText = null;
        _previewRoot = null;
        _flyoutRoot = null;
        await _rateLimitService.DisposeAsync();
    }

    private void EnsureTimers()
    {
        if (_refreshTimer is null)
        {
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
            _refreshTimer.Tick += (_, _) =>
            {
                var priorVisibility = IsPreviewVisible;
                var priorWidth = PreviewLogicalWidth;
                _snapshot = _statusReader.Read();
                _rateLimitService.RequestRefresh();
                _rateLimits = _rateLimitService.Current;
                RenderPreview();
                SyncSpinnerTimer();
                RenderFlyout();

                if (priorVisibility != IsPreviewVisible || priorWidth != PreviewLogicalWidth)
                {
                    Context?.RequestPreviewRefresh();
                }
            };
            _refreshTimer.Start();
        }

        if (_spinnerTimer is null)
        {
            _spinnerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _spinnerTimer.Tick += (_, _) => UpdateSpinnerFrame();
        }

        SyncSpinnerTimer();
    }

    private void RenderPreview()
    {
        if (_previewRoot is null)
        {
            return;
        }

        _spinnerText = null;

        var presentation = PreviewPresentationFactory.Create(
            _settings,
            IsActive(_snapshot.Status),
            _snapshot.FilesChangedCount,
            _snapshot.TotalSubagents);

        _previewRoot.Padding = new Thickness(
            presentation.HorizontalPadding,
            presentation.VerticalPadding,
            presentation.HorizontalPadding,
            presentation.VerticalPadding);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = presentation.RowSpacing,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var hasContent = false;

        for (var itemIndex = 0; itemIndex < presentation.Items.Count; itemIndex++)
        {
            var item = presentation.Items[itemIndex];
            UIElement? segment;
            if (IsUsageIndicator(item.Kind))
            {
                segment = CreateUsageGroup(presentation, ref itemIndex);
            }
            else
            {
                segment = item.Kind switch
                {
                    PreviewIndicatorKind.Activity => CreateActivitySegment(item, presentation),
                    PreviewIndicatorKind.Files => CreateText(
                        item.Text ?? string.Empty,
                        presentation.FilesMaxWidth,
                        presentation.TextFontSize),
                    PreviewIndicatorKind.Subagents => CreateText(
                        item.Text ?? string.Empty,
                        presentation.SubagentsMaxWidth,
                        presentation.TextFontSize),
                    PreviewIndicatorKind.Elapsed => CreateText(
                        FormatElapsed(_snapshot.Elapsed(DateTimeOffset.UtcNow)),
                        presentation.ElapsedMaxWidth,
                        presentation.TextFontSize),
                    _ => null,
                };
            }

            if (segment is not null)
            {
                AddSegment(row, segment, presentation.SeparatorHeight, ref hasContent);
            }
        }

        if (!hasContent)
        {
            row.Children.Add(CreateText("Codex", 60, presentation.TextFontSize));
        }

        _previewRoot.Child = row;
        row.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));

        var horizontalChrome =
            _previewRoot.Padding.Left +
            _previewRoot.Padding.Right +
            _previewRoot.BorderThickness.Left +
            _previewRoot.BorderThickness.Right;
        _previewLogicalWidth = Math.Clamp(
            (int)Math.Ceiling(row.DesiredSize.Width + horizontalChrome),
            96,
            700);
    }

    private void RenderFlyout()
    {
        if (_flyoutRoot is null)
        {
            return;
        }

        _flyoutRoot.Children.Clear();

        var title = new TextBlock
        {
            Text = "Codex Status",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _flyoutRoot.Children.Add(title);

        _flyoutRoot.Children.Add(new TextBlock
        {
            Text = _snapshot.Activity,
            FontSize = 15,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("#FFD9DEE4"),
        });

        var metrics = new Grid
        {
            ColumnSpacing = 10,
            RowSpacing = 10,
        };
        metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        metrics.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        metrics.Children.Add(CreateMetric("Elapsed", FormatElapsed(_snapshot.Elapsed(DateTimeOffset.UtcNow)), 0, 0));
        metrics.Children.Add(CreateMetric("Files", _snapshot.FilesChangedCount.ToString(), 0, 1));
        metrics.Children.Add(CreateMetric("Subagents", _snapshot.TotalSubagents.ToString(), 0, 2));
        _flyoutRoot.Children.Add(metrics);

        AddDetail(_flyoutRoot, "Project", ProjectName(_snapshot.Cwd));
        AddDetail(_flyoutRoot, "Model", _snapshot.Model);
        AddDetail(_flyoutRoot, "Updated", FormatUpdatedAt(_snapshot.LastUpdatedAtUtc));
        AddDetail(_flyoutRoot, "Source", _snapshot.Source);
        AddDetail(_flyoutRoot, "5-hour limit", FormatRateLimitDetail(_rateLimits.FiveHour));
        AddDetail(_flyoutRoot, "Weekly limit", FormatRateLimitDetail(_rateLimits.Weekly));
        AddDetail(_flyoutRoot, "Usage source", _rateLimitService.Source);
    }

    private static TextBlock CreateText(
        string text,
        double maxWidth,
        double fontSize,
        Windows.UI.Color? color = null)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            Foreground = new SolidColorBrush(color ?? Color("#FFE4E7EB")),
            MaxWidth = maxWidth,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private StackPanel CreateActivitySegment(
        PreviewIndicatorPresentation item,
        PreviewPresentation presentation)
    {
        var segment = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = presentation.LeadingSpacing,
            VerticalAlignment = VerticalAlignment.Center,
        };

        foreach (var leadingItem in item.LeadingItems)
        {
            if (leadingItem == PreviewLeadingItem.Spinner)
            {
                segment.Children.Add(CreateSpinner(presentation));
            }
            else
            {
                segment.Children.Add(CreateText(
                    _snapshot.Activity,
                    presentation.ActivityMaxWidth,
                    presentation.TextFontSize,
                    _snapshot.Status == "running" ? Colors.White : Color("#FFD1D6DC")));
            }
        }

        return segment;
    }

    private StackPanel CreateUsageGroup(
        PreviewPresentation presentation,
        ref int itemIndex)
    {
        var group = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = Math.Max(1, presentation.UsageSpacing - 1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        while (itemIndex < presentation.Items.Count &&
               IsUsageIndicator(presentation.Items[itemIndex].Kind))
        {
            var kind = presentation.Items[itemIndex].Kind;
            group.Children.Add(kind switch
            {
                PreviewIndicatorKind.FiveHourUsage => CreateUsageIndicator(
                    "5h",
                    "5-hour Codex limit",
                    _rateLimits.FiveHour,
                    presentation),
                PreviewIndicatorKind.WeeklyUsage => CreateUsageIndicator(
                    "w",
                    "Weekly Codex limit",
                    _rateLimits.Weekly,
                    presentation),
                _ => throw new InvalidOperationException("Unexpected usage indicator."),
            });
            itemIndex++;
        }

        itemIndex--;
        AutomationProperties.SetName(group, "Codex usage limits");
        return group;
    }

    private static StackPanel CreateUsageIndicator(
        string shortLabel,
        string accessibleLabel,
        RateLimitWindowState state,
        PreviewPresentation presentation)
    {
        var available = state.Availability == RateLimitAvailability.Available &&
            state.RemainingPercent is { };
        var remaining = available
            ? Math.Clamp(state.RemainingPercent!.Value, 0d, 100d)
            : 0d;
        var color = state.Availability switch
        {
            RateLimitAvailability.Available when remaining <= 20d => Color("#FFFF6B6B"),
            RateLimitAvailability.Available when remaining <= 50d => Color("#FFF5B942"),
            RateLimitAvailability.Available => Color("#FF45D483"),
            _ => Color("#FF7D8792"),
        };

        var fill = new Border
        {
            Width = Math.Max(0d, (presentation.BatteryWidth - 4d) * remaining / 100d),
            Height = Math.Max(1d, presentation.BatteryHeight - 4d),
            Margin = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(color),
            CornerRadius = new CornerRadius(1),
        };
        var batteryBody = new Border
        {
            Width = presentation.BatteryWidth,
            Height = presentation.BatteryHeight,
            BorderBrush = new SolidColorBrush(color),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Child = fill,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var terminal = new Border
        {
            Width = presentation.BatteryTerminalWidth,
            Height = presentation.BatteryTerminalHeight,
            Background = new SolidColorBrush(color),
            CornerRadius = new CornerRadius(0, 1, 1, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var battery = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        battery.Children.Add(batteryBody);
        battery.Children.Add(terminal);

        var roundedRemaining = (int)Math.Round(remaining, MidpointRounding.AwayFromZero);
        var valueText = state.Availability switch
        {
            RateLimitAvailability.Available => $"{shortLabel} {roundedRemaining}%",
            RateLimitAvailability.Disabled => $"{shortLabel} —",
            _ => $"{shortLabel} ?",
        };
        var text = CreateText(
            valueText,
            presentation.UsageMaxWidth,
            presentation.TextFontSize,
            state.Availability == RateLimitAvailability.Available
                ? Color("#FFE4E7EB")
                : Color("#FF9AA3AD"));

        var segment = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = presentation.UsageSpacing,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = state.Availability switch
            {
                RateLimitAvailability.Disabled => 0.42,
                RateLimitAvailability.Unknown => 0.62,
                _ => 1,
            },
        };
        segment.Children.Add(battery);
        segment.Children.Add(text);

        var automationName = state.Availability switch
        {
            RateLimitAvailability.Available =>
                $"{accessibleLabel}: {roundedRemaining} percent remaining" +
                FormatResetForAutomation(state.ResetsAtUtc),
            RateLimitAvailability.Disabled => $"{accessibleLabel}: disabled",
            _ => $"{accessibleLabel}: unavailable",
        };
        AutomationProperties.SetName(segment, automationName);
        return segment;
    }

    private static bool IsUsageIndicator(PreviewIndicatorKind kind) =>
        kind is PreviewIndicatorKind.FiveHourUsage or PreviewIndicatorKind.WeeklyUsage;

    private static string FormatResetForAutomation(DateTimeOffset? resetsAtUtc) =>
        resetsAtUtc is { } reset
            ? $", resets {reset.ToLocalTime():g}"
            : string.Empty;

    private Border CreateSpinner(PreviewPresentation presentation)
    {
        var frame = CurrentSpinnerFrame(DateTimeOffset.UtcNow)
            ?? throw new InvalidOperationException("An active request must have a spinner frame.");
        _spinnerText = new TextBlock
        {
            Text = frame.Text,
            FontSize = presentation.SpinnerFontSize,
            FontFamily = new FontFamily("Cascadia Mono"),
            Foreground = new SolidColorBrush(Color(_settings.SpinnerColor)),
            TextAlignment = TextAlignment.Left,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var container = new Border
        {
            Width = SpinnerContainerWidth(frame.Definition, presentation.SpinnerFontSize),
            Height = presentation.SpinnerHeight,
            Child = _spinnerText,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(container, $"Spinner {frame.Definition.Name}");
        return container;
    }

    private static double SpinnerContainerWidth(
        AgentSpinnerDefinition definition,
        double fontSize)
    {
        var characterCount = definition.Frames.Max(frame =>
            new System.Globalization.StringInfo(frame).LengthInTextElements);
        return Math.Clamp((characterCount * fontSize * 0.61) + 2, 12, 48);
    }

    private AgentSpinnerFrame? CurrentSpinnerFrame(DateTimeOffset nowUtc) =>
        _spinnerAnimator.GetFrame(
            IsActive(_snapshot.Status),
            _snapshot.SessionId,
            _snapshot.TurnId,
            _snapshot.StartedAtUtc,
            nowUtc);

    private void UpdateSpinnerFrame()
    {
        if (_spinnerText is null || !_settings.ShowPulse || !IsActive(_snapshot.Status))
        {
            return;
        }

        var frame = CurrentSpinnerFrame(DateTimeOffset.UtcNow);
        if (frame is not null && !string.Equals(_spinnerText.Text, frame.Text, StringComparison.Ordinal))
        {
            _spinnerText.Text = frame.Text;
        }
    }

    private void SyncSpinnerTimer()
    {
        var isActive = IsActive(_snapshot.Status);
        if (!isActive)
        {
            _spinnerAnimator.Reset();
        }

        var shouldRun = isActive && _settings.ShowPulse && _spinnerTimer is not null;
        if (shouldRun && !_spinnerTimerRunning)
        {
            _spinnerTimer!.Start();
            _spinnerTimerRunning = true;
        }
        else if (!shouldRun && _spinnerTimerRunning)
        {
            _spinnerTimer?.Stop();
            _spinnerTimerRunning = false;
        }
    }

    private static Border CreateMetric(string label, string value, int row, int column)
    {
        var panel = new StackPanel { Spacing = 3 };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Opacity = 0.62,
        });
        panel.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
        });

        var card = new Border
        {
            Background = Brush("#241B2026"),
            BorderBrush = Brush("#403C434C"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Child = panel,
        };
        Grid.SetRow(card, row);
        Grid.SetColumn(card, column);
        return card;
    }

    private static void AddDetail(Panel panel, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock
        {
            Text = label,
            Opacity = 0.58,
            FontSize = 12,
        });
        var valueText = new TextBlock
        {
            Text = value,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(valueText, 1);
        grid.Children.Add(valueText);
        panel.Children.Add(grid);
    }

    private static void AddToggle(
        Panel panel,
        string header,
        bool initialValue,
        Action<bool> apply,
        CodexWidgetSettings draft,
        IWidgetSettingsContext context)
    {
        var toggle = new ToggleSwitch
        {
            IsOn = initialValue,
            OnContent = string.Empty,
            OffContent = string.Empty,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(toggle, header);
        toggle.Toggled += (_, _) =>
        {
            apply(toggle.IsOn);
            context.SaveSettings(draft.ToJson());
        };

        AddSettingRow(panel, header, toggle);
    }

    private static void AddOrderedIndicatorRow(
        Panel panel,
        PreviewIndicatorKind indicator,
        int index,
        CodexWidgetSettings draft,
        IWidgetSettingsContext context,
        Action rerender)
    {
        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var toggle = new ToggleSwitch
        {
            IsOn = GetIndicatorVisibility(draft, indicator),
            OnContent = string.Empty,
            OffContent = string.Empty,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var label = IndicatorLabel(indicator);
        AutomationProperties.SetName(toggle, $"Show {label}");
        toggle.Toggled += (_, _) =>
        {
            SetIndicatorVisibility(draft, indicator, toggle.IsOn);
            context.SaveSettings(draft.ToJson());
        };

        var earlier = CreateOrderButton("↑", $"Move {label} earlier", index > 0);
        var later = CreateOrderButton(
            "↓",
            $"Move {label} later",
            index < draft.IndicatorOrder.Count - 1);
        earlier.Click += (_, _) =>
        {
            if (draft.MoveIndicator(indicator, -1))
            {
                context.SaveSettings(draft.ToJson());
                rerender();
            }
        };
        later.Click += (_, _) =>
        {
            if (draft.MoveIndicator(indicator, 1))
            {
                context.SaveSettings(draft.ToJson());
                rerender();
            }
        };

        controls.Children.Add(toggle);
        controls.Children.Add(earlier);
        controls.Children.Add(later);
        AddSettingRow(panel, label, controls);
    }

    private static Button CreateOrderButton(string content, string automationName, bool enabled)
    {
        var button = new Button
        {
            Content = content,
            Width = 28,
            Height = 28,
            MinWidth = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            FontSize = 14,
            IsEnabled = enabled,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(button, automationName);
        return button;
    }

    private static string IndicatorLabel(PreviewIndicatorKind indicator) => indicator switch
    {
        PreviewIndicatorKind.Activity => "Status / activity",
        PreviewIndicatorKind.Files => "Changed files",
        PreviewIndicatorKind.Subagents => "Subagents",
        PreviewIndicatorKind.Elapsed => "Elapsed time",
        PreviewIndicatorKind.FiveHourUsage => "5-hour usage",
        PreviewIndicatorKind.WeeklyUsage => "Weekly usage",
        _ => indicator.ToString(),
    };

    private static bool GetIndicatorVisibility(
        CodexWidgetSettings settings,
        PreviewIndicatorKind indicator) => indicator switch
    {
        PreviewIndicatorKind.Activity => settings.ShowActivity,
        PreviewIndicatorKind.Files => settings.ShowFiles,
        PreviewIndicatorKind.Subagents => settings.ShowAgents,
        PreviewIndicatorKind.Elapsed => settings.ShowElapsed,
        PreviewIndicatorKind.FiveHourUsage => settings.ShowFiveHourUsage,
        PreviewIndicatorKind.WeeklyUsage => settings.ShowWeeklyUsage,
        _ => false,
    };

    private static void SetIndicatorVisibility(
        CodexWidgetSettings settings,
        PreviewIndicatorKind indicator,
        bool visible)
    {
        switch (indicator)
        {
            case PreviewIndicatorKind.Activity:
                settings.ShowActivity = visible;
                break;
            case PreviewIndicatorKind.Files:
                settings.ShowFiles = visible;
                break;
            case PreviewIndicatorKind.Subagents:
                settings.ShowAgents = visible;
                break;
            case PreviewIndicatorKind.Elapsed:
                settings.ShowElapsed = visible;
                break;
            case PreviewIndicatorKind.FiveHourUsage:
                settings.ShowFiveHourUsage = visible;
                break;
            case PreviewIndicatorKind.WeeklyUsage:
                settings.ShowWeeklyUsage = visible;
                break;
        }
    }

    private static void AddSpinnerColorSelector(
        Panel panel,
        CodexWidgetSettings draft,
        IWidgetSettingsContext context)
    {
        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var presetButtons = new List<(string Hex, ToggleButton Button)>();
        foreach (var preset in SpinnerColorPresets)
        {
            var swatch = new Border
            {
                Width = 14,
                Height = 14,
                Background = new SolidColorBrush(Color(preset.Hex)),
                BorderBrush = Brush("#80FFFFFF"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
            };
            var button = new ToggleButton
            {
                Content = swatch,
                Width = 24,
                Height = 24,
                MinWidth = 0,
                Padding = new Thickness(2),
                CornerRadius = new CornerRadius(5),
                IsChecked = string.Equals(
                    draft.SpinnerColor,
                    preset.Hex,
                    StringComparison.OrdinalIgnoreCase),
                VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetName(button, $"{preset.Name} spinner color, {preset.Hex}");
            presetButtons.Add((preset.Hex, button));
            controls.Children.Add(button);
        }

        var customInput = new TextBox
        {
            Text = draft.SpinnerColor,
            Width = 80,
            MinWidth = 0,
            MaxLength = 7,
            FontSize = 11,
            Padding = new Thickness(5, 2, 5, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(customInput, "Custom spinner color in #RRGGBB format");
        AutomationProperties.SetHelpText(customInput, "Enter a hexadecimal color such as #3B9EFF");
        controls.Children.Add(customInput);

        void ApplyColor(string value, bool save)
        {
            if (!CodexWidgetSettings.TryNormalizeSpinnerColor(value, out var normalized))
            {
                return;
            }

            var changed = !string.Equals(
                draft.SpinnerColor,
                normalized,
                StringComparison.OrdinalIgnoreCase);
            draft.SpinnerColor = normalized;
            customInput.Text = normalized;
            foreach (var presetButton in presetButtons)
            {
                presetButton.Button.IsChecked = string.Equals(
                    presetButton.Hex,
                    normalized,
                    StringComparison.OrdinalIgnoreCase);
            }

            if (save && changed)
            {
                context.SaveSettings(draft.ToJson());
            }
        }

        void CommitCustomColor()
        {
            if (CodexWidgetSettings.TryNormalizeSpinnerColor(customInput.Text, out var normalized))
            {
                ApplyColor(normalized, save: true);
            }
            else
            {
                customInput.Text = draft.SpinnerColor;
            }
        }

        foreach (var presetButton in presetButtons)
        {
            presetButton.Button.Click += (_, _) => ApplyColor(presetButton.Hex, save: true);
        }
        customInput.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Enter)
            {
                CommitCustomColor();
                args.Handled = true;
            }
        };
        customInput.LostFocus += (_, _) => CommitCustomColor();

        AddSettingRow(panel, "Spinner color", controls);
    }

    private static void AddSettingRow(Panel panel, string header, FrameworkElement control)
    {
        var row = new Grid
        {
            MinHeight = 44,
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock
        {
            Text = header,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(control, 1);
        row.Children.Add(control);

        panel.Children.Add(new Border
        {
            Background = Brush("#121B2026"),
            BorderBrush = Brush("#303C434C"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 4, 8, 4),
            Child = row,
        });
    }

    private static void ConfigureSettingsWindowMinimumSize(FrameworkElement root)
    {
        if (root.XamlRoot is null)
        {
            return;
        }

        var appWindow = AppWindow.GetFromWindowId(
            root.XamlRoot.ContentIslandEnvironment.AppWindowId);
        if (appWindow?.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 800;
            presenter.PreferredMinimumHeight = 480;
        }
    }

    private static Border CreateSettingsCard(string title, UIElement content)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
        });
        panel.Children.Add(content);

        return new Border
        {
            Background = Brush("#161B2026"),
            BorderBrush = Brush("#403C434C"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Child = panel,
        };
    }

    private static void AddSegment(
        StackPanel row,
        UIElement element,
        double separatorHeight,
        ref bool hasContent)
    {
        if (hasContent)
        {
            row.Children.Add(new Border
            {
                Width = 1,
                Height = separatorHeight,
                Background = Brush("#553C424A"),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        row.Children.Add(element);
        hasContent = true;
    }

    private static bool IsActive(string? status) =>
        status is "running" or "waiting";

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalHours >= 1)
        {
            return $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        }

        return $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

    private static string FormatUpdatedAt(DateTimeOffset? timestamp) =>
        timestamp?.ToLocalTime().ToString("HH:mm:ss") ?? "—";

    private static string FormatRateLimitDetail(RateLimitWindowState state)
    {
        if (state.Availability == RateLimitAvailability.Disabled)
        {
            return "Disabled";
        }

        if (state.Availability != RateLimitAvailability.Available ||
            state.RemainingPercent is not { } remaining)
        {
            return "Unavailable";
        }

        var value = $"{Math.Round(remaining, MidpointRounding.AwayFromZero):0}% remaining";
        return state.ResetsAtUtc is { } reset
            ? $"{value} · resets {reset.ToLocalTime():ddd HH:mm}"
            : value;
    }

    private static string ProjectName(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd))
        {
            return "—";
        }

        return Path.GetFileName(cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    private static SolidColorBrush Brush(string hex) => new(Color(hex));

    private static Windows.UI.Color Color(string hex)
    {
        var value = hex.TrimStart('#');
        var argb = value.Length == 8
            ? Convert.ToUInt32(value, 16)
            : 0xFF000000 | Convert.ToUInt32(value, 16);
        return Windows.UI.Color.FromArgb(
            (byte)(argb >> 24),
            (byte)(argb >> 16),
            (byte)(argb >> 8),
            (byte)argb);
    }

}
