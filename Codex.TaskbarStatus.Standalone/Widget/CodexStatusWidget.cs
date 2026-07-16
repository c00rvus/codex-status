using Codex.TaskbarStatus.Core;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Codex.TaskbarStatus.Standalone.Widget;

internal sealed class CodexStatusWidget : IAsyncDisposable
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
    private readonly Dictionary<string, RequestSpinnerAnimator> _flyoutSpinnerAnimators = new();
    private readonly Dictionary<string, TextBlock> _flyoutSpinnerTexts = new();
    private CodexWidgetSettings _settings = new();
    private CodexStatusBoardSnapshot _board = new();
    private CodexStatusSnapshot _snapshot = new();
    private CodexRateLimitSnapshot _rateLimits = CodexRateLimitSnapshot.Unknown;
    private Border? _previewRoot;
    private StackPanel? _flyoutRoot;
    private ScrollViewer? _flyoutScrollViewer;
    private DispatcherTimer? _refreshTimer;
    private DispatcherTimer? _spinnerTimer;
    private TextBlock? _spinnerText;
    private bool _spinnerTimerRunning;
    private bool _flyoutVisible;
    private int _settingsVersion;
    private PreviewVisualState? _lastPreviewVisualState;
    private FlyoutVisualState? _lastFlyoutVisualState;
    private int _previewLogicalWidth = 400;
    private IWidgetRuntimeContext? _context;

    internal int PreviewLogicalWidth => _previewLogicalWidth;
    internal bool IsPreviewVisible => !_settings.HideWhenIdle || IsActive(_snapshot.Status);
    internal int FlyoutWidth => 500;
    internal int FlyoutHeight => 430;

    internal Task InitializeAsync(IWidgetRuntimeContext context)
    {
        _context = context;
        _settings = CodexWidgetSettings.FromJson(context.SettingsJson);
        _board = _statusReader.ReadBoard();
        _snapshot = _board.Primary;
        _rateLimits = _rateLimitService.Current;
        _rateLimitService.RequestRefresh();
        return Task.CompletedTask;
    }

    internal UIElement CreatePreviewContent()
    {
        var initialWidth = _previewLogicalWidth;
        _lastPreviewVisualState = null;
        _previewRoot = new Border
        {
            Background = new SolidColorBrush(Colors.Transparent),
            Padding = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
        };

        AutomationProperties.SetName(_previewRoot, "Codex execution status");
        RenderPreview(force: true, DateTimeOffset.UtcNow);
        EnsureTimers();

        if (initialWidth != _previewLogicalWidth)
        {
            _previewRoot.DispatcherQueue.TryEnqueue(() => _context?.RequestPreviewRefresh());
        }

        return _previewRoot;
    }

    internal UIElement CreateFlyoutContent()
    {
        _lastFlyoutVisualState = null;
        _flyoutRoot = new StackPanel
        {
            Spacing = 16,
            Padding = new Thickness(18),
        };

        EnsureTimers();

        _flyoutScrollViewer = new ScrollViewer
        {
            Content = _flyoutRoot,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        return _flyoutScrollViewer;
    }

    internal UIElement CreateSettingsContent(IWidgetSettingsContext context)
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

    internal void OnSettingsDraftChanged(string settingsJson)
    {
        _settings = CodexWidgetSettings.FromJson(settingsJson);
        _settingsVersion++;
        RenderPreview(force: true, DateTimeOffset.UtcNow);
        SyncSpinnerTimer();
        _context?.RequestPreviewRefresh();
    }

    internal void OnFlyoutVisibilityChanged(bool isVisible)
    {
        _flyoutVisible = isVisible;
        if (!isVisible)
        {
            return;
        }

        // Refresh synchronously before the popup becomes visible so its first
        // painted frame never shows data left over from the previous opening.
        try
        {
            RefreshVisualState(forceFlyout: true);
        }
        catch (Exception exception)
        {
            StandaloneLog.Write("Details flyout rendering failed", exception);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _refreshTimer?.Stop();
        _refreshTimer = null;
        _spinnerTimer?.Stop();
        _spinnerTimer = null;
        _spinnerTimerRunning = false;
        _flyoutVisible = false;
        _spinnerText = null;
        _previewRoot = null;
        _flyoutRoot = null;
        _flyoutScrollViewer = null;
        _flyoutSpinnerTexts.Clear();
        _flyoutSpinnerAnimators.Clear();
        _lastPreviewVisualState = null;
        _lastFlyoutVisualState = null;
        _statusReader.Dispose();
        await _rateLimitService.DisposeAsync();
    }

    private void EnsureTimers()
    {
        if (_refreshTimer is null)
        {
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _refreshTimer.Tick += (_, _) => RefreshVisualState();
            _refreshTimer.Start();
        }

        if (_spinnerTimer is null)
        {
            _spinnerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _spinnerTimer.Tick += (_, _) => UpdateSpinnerFrame();
        }

        SyncSpinnerTimer();
    }

    private void RefreshVisualState(bool forceFlyout = false)
    {
        var priorVisibility = IsPreviewVisible;
        var priorWidth = PreviewLogicalWidth;
        var now = DateTimeOffset.UtcNow;

        _board = _statusReader.ReadBoard();
        _snapshot = _board.Primary;
        _rateLimitService.RequestRefresh();
        _rateLimits = _rateLimitService.Current;

        RenderPreview(force: false, now);
        SyncSpinnerTimer();
        if (_flyoutVisible)
        {
            RenderFlyout(forceFlyout, now);
        }

        if (priorVisibility != IsPreviewVisible || priorWidth != PreviewLogicalWidth)
        {
            _context?.RequestPreviewRefresh();
        }
    }

    private bool RenderPreview(bool force, DateTimeOffset now)
    {
        if (_previewRoot is null)
        {
            return false;
        }

        var visualState = CreatePreviewVisualState(now);
        if (!force && _lastPreviewVisualState is { } previous && previous == visualState)
        {
            return false;
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
                    PreviewIndicatorKind.Activity => CreateActivitySegment(
                        item,
                        presentation,
                        visualState.Activity),
                    PreviewIndicatorKind.Files => CreateText(
                        item.Text ?? string.Empty,
                        presentation.FilesMaxWidth,
                        presentation.TextFontSize),
                    PreviewIndicatorKind.Subagents => CreateText(
                        item.Text ?? string.Empty,
                        presentation.SubagentsMaxWidth,
                        presentation.TextFontSize),
                    PreviewIndicatorKind.Elapsed => CreateText(
                        visualState.ElapsedText ?? string.Empty,
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
        _lastPreviewVisualState = visualState;
        return true;
    }

    private bool RenderFlyout(bool force, DateTimeOffset now)
    {
        if (_flyoutRoot is null)
        {
            return false;
        }

        var visualState = CreateFlyoutVisualState(now);
        if (!force && _lastFlyoutVisualState is { } previous && previous == visualState)
        {
            return false;
        }

        var previousOffset = _flyoutScrollViewer?.VerticalOffset ?? 0;
        _flyoutRoot.Children.Clear();
        _flyoutSpinnerTexts.Clear();

        _flyoutRoot.Children.Add(CreateFlyoutHeader(visualState));
        _flyoutRoot.Children.Add(CreateRateLimitPanel(visualState));

        var activeTasks = _board.Tasks.Where(task => task.IsActive).ToArray();
        var completedTasks = _board.Tasks.Where(task => !task.IsActive).ToArray();
        var activeKeys = activeTasks.Select(task => task.TaskKey).ToHashSet(StringComparer.Ordinal);
        foreach (var staleKey in _flyoutSpinnerAnimators.Keys
                     .Where(key => !activeKeys.Contains(key))
                     .ToArray())
        {
            _flyoutSpinnerAnimators.Remove(staleKey);
        }

        if (activeTasks.Length > 0)
        {
            _flyoutRoot.Children.Add(CreateSectionHeader("RUNNING", activeTasks.Length));
            foreach (var task in activeTasks)
            {
                _flyoutRoot.Children.Add(CreateTaskCard(task, now));
            }
        }

        if (completedTasks.Length > 0)
        {
            _flyoutRoot.Children.Add(CreateSectionHeader("READY TO REVIEW", completedTasks.Length));
            foreach (var task in completedTasks)
            {
                _flyoutRoot.Children.Add(CreateTaskCard(task, now));
            }
        }

        if (_board.Tasks.Count == 0)
        {
            _flyoutRoot.Children.Add(CreateEmptyTasksCard());
        }

        _lastFlyoutVisualState = visualState;
        if (previousOffset > 0 && _flyoutScrollViewer is { } scrollViewer)
        {
            scrollViewer.DispatcherQueue.TryEnqueue(() =>
                scrollViewer.ChangeView(null, previousOffset, null, disableAnimation: true));
        }

        return true;
    }

    private PreviewVisualState CreatePreviewVisualState(DateTimeOffset now)
    {
        var isActive = IsActive(_snapshot.Status);
        var showSpinner = isActive && _settings.ShowPulse;
        var activity = _settings.Compact
            ? CompactActivityLabel.Resolve(
                _snapshot.Status,
                RequestSpinnerAnimator.CreateRequestKey(
                    _snapshot.SessionId,
                    _snapshot.TurnId,
                    _snapshot.StartedAtUtc),
                _snapshot.StartedAtUtc,
                now)
            : _snapshot.Activity;
        return new PreviewVisualState(
            _settingsVersion,
            showSpinner,
            _settings.ShowActivity && _snapshot.Status == "running",
            _settings.ShowActivity ? activity : null,
            showSpinner ? _snapshot.SessionId : null,
            showSpinner ? _snapshot.TurnId : null,
            showSpinner ? _snapshot.StartedAtUtc : null,
            _settings.ShowFiles ? _snapshot.FilesChangedCount : null,
            _settings.ShowAgents ? _snapshot.TotalSubagents : null,
            _settings.ShowElapsed ? FormatElapsed(_snapshot.Elapsed(now)) : null,
            _settings.ShowFiveHourUsage ? _rateLimits.FiveHour : null,
            _settings.ShowWeeklyUsage ? _rateLimits.Weekly : null);
    }

    private FlyoutVisualState CreateFlyoutVisualState(DateTimeOffset now)
    {
        var tasksKey = string.Join(
            '\u001f',
            _board.Tasks.Select(task => string.Join(
                '\u001e',
                task.TaskKey,
                task.Status,
                task.Activity,
                task.TaskTitle,
                task.Cwd,
                task.FilesChangedCount,
                task.TotalSubagents,
                FormatElapsed(task.Elapsed(now)),
                task.LastUpdatedAtUtc.UtcTicks)));

        return new FlyoutVisualState(
            _settingsVersion,
            _board.ActiveCount,
            _board.ReadyCount,
            tasksKey,
            _rateLimits.FiveHour,
            _rateLimits.Weekly,
            _board.UnreadSignalAvailable);
    }

    private readonly record struct PreviewVisualState(
        int SettingsVersion,
        bool ShowSpinner,
        bool ActivityUsesRunningColor,
        string? Activity,
        string? SessionId,
        string? TurnId,
        DateTimeOffset? SpinnerStartedAtUtc,
        int? FilesChangedCount,
        int? TotalSubagents,
        string? ElapsedText,
        RateLimitWindowState? FiveHour,
        RateLimitWindowState? Weekly);

    private readonly record struct FlyoutVisualState(
        int SettingsVersion,
        int ActiveCount,
        int ReadyCount,
        string TasksKey,
        RateLimitWindowState FiveHour,
        RateLimitWindowState Weekly,
        bool UnreadSignalAvailable);

    private static UIElement CreateFlyoutHeader(FlyoutVisualState state)
    {
        var header = new Grid { ColumnSpacing = 12, Margin = new Thickness(2, 0, 42, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(9),
            Child = new Image
            {
                Source = new BitmapImage(new Uri("ms-appx:///Assets/CodexStatus.png")),
                Width = 28,
                Height = 28,
            },
            VerticalAlignment = VerticalAlignment.Center,
        };
        header.Children.Add(icon);

        var heading = new StackPanel { Spacing = 1 };
        heading.Children.Add(new TextBlock
        {
            Text = "Codex Status",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
        });
        heading.Children.Add(new TextBlock
        {
            Text = state.ActiveCount switch
            {
                > 0 when state.ReadyCount > 0 =>
                    $"{FormatCount(state.ActiveCount, "active task", "active tasks")} · " +
                    $"{FormatCount(state.ReadyCount, "ready", "ready")}",
                > 0 => FormatCount(state.ActiveCount, "active task", "active tasks"),
                _ when state.ReadyCount > 0 => FormatCount(state.ReadyCount, "task ready", "tasks ready"),
                _ => "All caught up",
            },
            FontSize = 11.5,
            Foreground = Brush("#FFAAB2BC"),
        });
        Grid.SetColumn(heading, 1);
        header.Children.Add(heading);

        var badgeText = state.ActiveCount > 0 ? $"{state.ActiveCount} active" : "Idle";
        var badge = new Border
        {
            Padding = new Thickness(9, 4, 9, 4),
            CornerRadius = new CornerRadius(10),
            Background = Brush(state.ActiveCount > 0 ? "#293B9EFF" : "#1E8A949F"),
            BorderBrush = Brush(state.ActiveCount > 0 ? "#703B9EFF" : "#358A949F"),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = badgeText,
                FontSize = 10.5,
                Foreground = Brush(state.ActiveCount > 0 ? "#FF8CCAFF" : "#FFB2BAC3"),
            },
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(badge, 2);
        header.Children.Add(badge);
        return header;
    }

    private static UIElement CreateRateLimitPanel(FlyoutVisualState state)
    {
        var grid = new Grid { ColumnSpacing = 18 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(CreateRateLimitSummary("5-HOUR", state.FiveHour, 0));
        grid.Children.Add(CreateRateLimitSummary("WEEKLY", state.Weekly, 1));

        return new Border
        {
            Background = Brush("#4223282E"),
            BorderBrush = Brush("#4A5A626C"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 11, 14, 12),
            Child = grid,
        };
    }

    private static UIElement CreateRateLimitSummary(
        string label,
        RateLimitWindowState state,
        int column)
    {
        var remaining = state.Availability == RateLimitAvailability.Available
            ? Math.Clamp(state.RemainingPercent ?? 0, 0, 100)
            : 0;
        var accent = RateLimitColor(state);
        var panel = new StackPanel { Spacing = 5 };

        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 10,
            CharacterSpacing = 70,
            Foreground = Brush("#FF9FA8B2"),
        });
        var value = new TextBlock
        {
            Text = state.Availability switch
            {
                RateLimitAvailability.Available => $"{Math.Round(remaining):0}% left",
                RateLimitAvailability.Disabled => "Disabled",
                _ => "Unavailable",
            },
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(accent),
        };
        Grid.SetColumn(value, 1);
        titleRow.Children.Add(value);
        panel.Children.Add(titleRow);

        var progress = new Grid { Height = 4 };
        progress.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(Math.Max(0.001, remaining), GridUnitType.Star),
        });
        progress.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(Math.Max(0.001, 100 - remaining), GridUnitType.Star),
        });
        var progressTrack = new Border
        {
            Background = Brush("#4A4C535C"),
            CornerRadius = new CornerRadius(2),
        };
        Grid.SetColumnSpan(progressTrack, 2);
        progress.Children.Add(progressTrack);
        var progressFill = new Border
        {
            Background = new SolidColorBrush(accent),
            CornerRadius = new CornerRadius(2),
            Opacity = state.Availability == RateLimitAvailability.Available ? 1 : 0.35,
        };
        progress.Children.Add(progressFill);
        panel.Children.Add(progress);
        panel.Children.Add(new TextBlock
        {
            Text = state.ResetsAtUtc is { } reset
                ? $"Resets {reset.ToLocalTime():ddd HH:mm}"
                : state.Availability == RateLimitAvailability.Disabled
                    ? "Not enabled for this account"
                    : "Reset time unavailable",
            FontSize = 10,
            Foreground = Brush("#FF89929D"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        Grid.SetColumn(panel, column);
        return panel;
    }

    private static UIElement CreateSectionHeader(string label, int count)
    {
        var row = new Grid { Margin = new Thickness(2, 2, 2, -5) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 10.5,
            CharacterSpacing = 90,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#FF9DA6B0"),
        });
        var countText = new TextBlock
        {
            Text = count.ToString(),
            FontSize = 10.5,
            Foreground = Brush("#FF7F8994"),
        };
        Grid.SetColumn(countText, 1);
        row.Children.Add(countText);
        return row;
    }

    private UIElement CreateTaskCard(CodexStatusSnapshot task, DateTimeOffset now)
    {
        var statusColor = TaskStatusColor(task);
        var content = new StackPanel { Spacing = 8 };

        var top = new Grid { ColumnSpacing = 12 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = new StackPanel { Spacing = 2 };
        heading.Children.Add(new TextBlock
        {
            Text = TaskDisplayTitle(task),
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            MaxLines = 2,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Brush("#FFF0F3F6"),
        });
        heading.Children.Add(new TextBlock
        {
            Text = ProjectName(task.Cwd),
            FontSize = 10.5,
            Foreground = Brush("#FF929CA7"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        top.Children.Add(heading);
        var elapsed = new TextBlock
        {
            Text = task.IsActive
                ? FormatElapsed(task.Elapsed(now))
                : FormatUpdatedAt(task.StoppedAtUtc ?? task.LastUpdatedAtUtc),
            FontSize = 11,
            FontFamily = new FontFamily("Cascadia Mono"),
            Foreground = Brush("#FFA9B2BC"),
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetColumn(elapsed, 1);
        top.Children.Add(elapsed);
        content.Children.Add(top);

        var footer = new Grid { ColumnSpacing = 10 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var activity = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            VerticalAlignment = VerticalAlignment.Center,
        };
        activity.Children.Add(task.IsActive && _settings.ShowPulse
            ? CreateFlyoutTaskSpinner(task)
            : CreateTaskStateGlyph(task, statusColor));
        activity.Children.Add(new TextBlock
        {
            Text = task.IsActive ? task.Activity : "Completed",
            FontSize = 11.5,
            Foreground = new SolidColorBrush(statusColor),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 230,
            VerticalAlignment = VerticalAlignment.Center,
        });
        footer.Children.Add(activity);

        var chips = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        chips.Children.Add(CreateTaskChip(FormatCount(task.FilesChangedCount, "file", "files")));
        chips.Children.Add(CreateTaskChip(FormatCount(task.TotalSubagents, "subagent", "subagents")));
        Grid.SetColumn(chips, 1);
        footer.Children.Add(chips);
        content.Children.Add(footer);

        var layout = new Grid { ColumnSpacing = 11 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(new Border
        {
            Width = 3,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(statusColor),
            VerticalAlignment = VerticalAlignment.Stretch,
        });
        Grid.SetColumn(content, 1);
        layout.Children.Add(content);

        var card = new Border
        {
            Background = Brush(task.IsActive ? "#4A23282E" : "#38242A2F"),
            BorderBrush = Brush(task.IsActive ? "#4F626B76" : "#3A59616B"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(11),
            Child = layout,
            Opacity = task.IsActive ? 1 : 0.9,
        };
        AutomationProperties.SetName(
            card,
            $"{TaskDisplayTitle(task)}, {task.Activity}, {FormatElapsed(task.Elapsed(now))}");
        return card;
    }

    private Border CreateFlyoutTaskSpinner(CodexStatusSnapshot task)
    {
        if (!_flyoutSpinnerAnimators.TryGetValue(task.TaskKey, out var animator))
        {
            animator = new RequestSpinnerAnimator();
            _flyoutSpinnerAnimators.Add(task.TaskKey, animator);
        }

        var frame = animator.GetFrame(
            true,
            task.SessionId,
            task.TurnId,
            task.StartedAtUtc,
            DateTimeOffset.UtcNow)!;
        var text = new TextBlock
        {
            Text = frame.Text,
            FontSize = 12,
            FontFamily = new FontFamily("Cascadia Mono"),
            Foreground = new SolidColorBrush(Color(_settings.SpinnerColor)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _flyoutSpinnerTexts[task.TaskKey] = text;
        return new Border
        {
            Width = Math.Clamp(SpinnerContainerWidth(frame.Definition, 12), 14, 42),
            Height = 16,
            Child = text,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private static UIElement CreateTaskStateGlyph(
        CodexStatusSnapshot task,
        Windows.UI.Color color)
    {
        if (!task.IsActive)
        {
            return new FontIcon
            {
                Glyph = "\uE73E",
                FontSize = 11,
                Foreground = new SolidColorBrush(color),
            };
        }

        return new Border
        {
            Width = 7,
            Height = 7,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(color),
        };
    }

    private static Border CreateTaskChip(string text) => new()
    {
        Padding = new Thickness(6, 2, 6, 2),
        CornerRadius = new CornerRadius(7),
        Background = Brush("#35454C55"),
        Child = new TextBlock
        {
            Text = text,
            FontSize = 9.5,
            Foreground = Brush("#FFAAB2BC"),
        },
    };

    private static UIElement CreateEmptyTasksCard()
    {
        var panel = new StackPanel
        {
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        panel.Children.Add(new FontIcon
        {
            Glyph = "\uE930",
            FontSize = 20,
            Foreground = Brush("#FF6EBE91"),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "No active tasks",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Completed chats disappear after you open them in Codex.",
            FontSize = 10.5,
            Foreground = Brush("#FF929BA5"),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });

        return new Border
        {
            Background = Brush("#30242A30"),
            BorderBrush = Brush("#3A59616B"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(18),
            Child = panel,
        };
    }

    private static string TaskDisplayTitle(CodexStatusSnapshot task)
    {
        if (!string.IsNullOrWhiteSpace(task.TaskTitle))
        {
            return task.TaskTitle;
        }

        var project = ProjectName(task.Cwd);
        return project == "—" ? "Codex task" : $"Task in {project}";
    }

    private static Windows.UI.Color TaskStatusColor(CodexStatusSnapshot task) => task.Status switch
    {
        CodexExecutionStatuses.Waiting => Color("#FFF4BE5B"),
        CodexExecutionStatuses.Completed => Color("#FF69D899"),
        CodexExecutionStatuses.Error => Color("#FFFF7474"),
        CodexExecutionStatuses.Aborted => Color("#FF9EA7B1"),
        _ => Color("#FF5AACFF"),
    };

    private static Windows.UI.Color RateLimitColor(RateLimitWindowState state)
    {
        if (state.Availability != RateLimitAvailability.Available)
        {
            return Color("#FF818B96");
        }

        return state.RemainingPercent switch
        {
            <= 20 => Color("#FFFF7474"),
            <= 50 => Color("#FFF4BE5B"),
            _ => Color("#FF69D899"),
        };
    }

    private static string FormatCount(int count, string singular, string plural) =>
        $"{count} {(count == 1 ? singular : plural)}";

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
        PreviewPresentation presentation,
        string? activityText)
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
                    activityText ?? string.Empty,
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
        if (!_settings.ShowPulse || !IsActive(_snapshot.Status))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var frame = CurrentSpinnerFrame(now);
        if (_spinnerText is not null && frame is not null &&
            !string.Equals(_spinnerText.Text, frame.Text, StringComparison.Ordinal))
        {
            _spinnerText.Text = frame.Text;
        }

        if (!_flyoutVisible || _flyoutSpinnerTexts.Count == 0)
        {
            return;
        }

        foreach (var task in _board.Tasks.Where(task => task.IsActive))
        {
            if (!_flyoutSpinnerTexts.TryGetValue(task.TaskKey, out var text) ||
                !_flyoutSpinnerAnimators.TryGetValue(task.TaskKey, out var animator))
            {
                continue;
            }

            var taskFrame = animator.GetFrame(
                true,
                task.SessionId,
                task.TurnId,
                task.StartedAtUtc,
                now);
            if (taskFrame is not null &&
                !string.Equals(text.Text, taskFrame.Text, StringComparison.Ordinal))
            {
                text.Text = taskFrame.Text;
            }
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
