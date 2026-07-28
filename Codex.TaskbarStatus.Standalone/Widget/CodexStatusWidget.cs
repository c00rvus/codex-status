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
    private const int MinimumFlyoutHeight = 300;
    private const int MaximumFlyoutHeight = 620;

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
    private readonly ReviewedTaskStore _reviewedTaskStore = new();
    private readonly RequestSpinnerAnimator _spinnerAnimator = new();
    private readonly Dictionary<string, RequestSpinnerAnimator> _flyoutSpinnerAnimators = new();
    private readonly Dictionary<string, TextBlock> _flyoutSpinnerTexts = new();
    private readonly Dictionary<string, TextBlock> _flyoutTimeTexts = new();
    private readonly HashSet<string> _reviewedTaskKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _notifiedAttentionStates = new(StringComparer.Ordinal);
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
    private int _flyoutLogicalHeight = MinimumFlyoutHeight;
    private IWidgetRuntimeContext? _context;

    internal int PreviewLogicalWidth => _previewLogicalWidth;
    internal bool IsPreviewVisible =>
        !_settings.HideWhenIdle ||
        _snapshot.IsActive ||
        _snapshot.RequiresAttention;
    internal int FlyoutWidth => 500;
    internal int FlyoutHeight => _flyoutLogicalHeight;

    internal Task InitializeAsync(IWidgetRuntimeContext context)
    {
        _context = context;
        _settings = CodexWidgetSettings.FromJson(context.SettingsJson);
        _reviewedTaskKeys.UnionWith(_reviewedTaskStore.Read());
        _board = _statusReader.ReadBoard(_reviewedTaskKeys);
        _snapshot = _board.Primary;
        _notifiedAttentionStates.UnionWith(
            _board.Tasks
                .Where(task => task.ShouldNotifyAttention)
                .Select(AttentionStateKey));
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
            Spacing = 12,
            Padding = new Thickness(18, 16, 18, 14),
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
        AddToggle(
            behaviorOptions,
            "Attention notifications",
            draft.ShowAttentionNotifications,
            value => draft.ShowAttentionNotifications = value,
            draft,
            context);
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
            SyncSpinnerTimer();
            return;
        }

        // Refresh synchronously before the popup becomes visible so its first
        // painted frame never shows data left over from the previous opening.
        try
        {
            RefreshVisualState();
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
        _flyoutTimeTexts.Clear();
        _reviewedTaskKeys.Clear();
        _notifiedAttentionStates.Clear();
        _lastPreviewVisualState = null;
        _lastFlyoutVisualState = null;
        _flyoutLogicalHeight = MinimumFlyoutHeight;
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

    private void RefreshVisualState()
    {
        var priorVisibility = IsPreviewVisible;
        var priorWidth = PreviewLogicalWidth;
        var now = DateTimeOffset.UtcNow;

        _board = _statusReader.ReadBoard(_reviewedTaskKeys);
        _snapshot = _board.Primary;
        NotifyAttentionTransitions();
        _rateLimitService.RequestRefresh();
        _rateLimits = _rateLimitService.Current;

        RenderPreview(force: false, now);
        SyncSpinnerTimer();
        if (_flyoutVisible)
        {
            RenderFlyout(now);
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
            _snapshot.Status == CodexExecutionStatuses.Running,
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
                        visualState),
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

    private bool RenderFlyout(DateTimeOffset now)
    {
        if (_flyoutRoot is null)
        {
            return false;
        }

        var visualState = CreateFlyoutVisualState();
        if (_lastFlyoutVisualState is { } previous && previous == visualState)
        {
            UpdateFlyoutTaskTimes(now);
            return false;
        }

        var previousOffset = _flyoutScrollViewer?.VerticalOffset ?? 0;
        _flyoutRoot.Children.Clear();
        _flyoutSpinnerTexts.Clear();
        _flyoutTimeTexts.Clear();

        _flyoutRoot.Children.Add(CreateFlyoutHeader(visualState));
        _flyoutRoot.Children.Add(CreateRateLimitPanel(visualState));

        var attentionTasks = _board.Tasks.Where(task => task.RequiresAttention).ToArray();
        var activeTasks = _board.Tasks
            .Where(task => task.IsActive && !task.RequiresAttention)
            .ToArray();
        var completedTasks = _board.Tasks
            .Where(task => !task.IsActive && !task.RequiresAttention)
            .ToArray();
        var activeKeys = _board.Tasks
            .Where(task => task.Status == CodexExecutionStatuses.Running)
            .Select(task => task.TaskKey)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var staleKey in _flyoutSpinnerAnimators.Keys
                     .Where(key => !activeKeys.Contains(key))
                     .ToArray())
        {
            _flyoutSpinnerAnimators.Remove(staleKey);
        }

        if (attentionTasks.Length > 0)
        {
            _flyoutRoot.Children.Add(CreateSectionHeader("NEEDS ATTENTION", attentionTasks.Length));
            foreach (var task in attentionTasks)
            {
                _flyoutRoot.Children.Add(CreateTaskCard(task, now));
            }
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
        UpdateDesiredFlyoutHeight();
        if (previousOffset > 0 && _flyoutScrollViewer is { } scrollViewer)
        {
            scrollViewer.DispatcherQueue.TryEnqueue(() =>
                scrollViewer.ChangeView(null, previousOffset, null, disableAnimation: true));
        }

        return true;
    }

    private void UpdateDesiredFlyoutHeight()
    {
        if (_flyoutRoot is null)
        {
            return;
        }

        _flyoutRoot.Measure(new Windows.Foundation.Size(
            FlyoutWidth,
            double.PositiveInfinity));
        var measuredHeight = (int)Math.Ceiling(_flyoutRoot.DesiredSize.Height);
        var desiredHeight = Math.Clamp(
            measuredHeight,
            MinimumFlyoutHeight,
            MaximumFlyoutHeight);
        if (_flyoutLogicalHeight == desiredHeight)
        {
            return;
        }

        _flyoutLogicalHeight = desiredHeight;
        if (_flyoutVisible)
        {
            _context?.RequestFlyoutResize(desiredHeight);
        }
    }

    private PreviewVisualState CreatePreviewVisualState(DateTimeOffset now)
    {
        var isProcessing = _snapshot.Status == CodexExecutionStatuses.Running;
        var showSpinner = isProcessing && _settings.ShowPulse;
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
            _settings.ShowActivity ? activity : null,
            showSpinner ? _snapshot.SessionId : null,
            showSpinner ? _snapshot.TurnId : null,
            showSpinner ? _snapshot.StartedAtUtc : null,
            _settings.ShowFiles ? _snapshot.FilesChangedCount : null,
            _settings.ShowAgents ? _snapshot.TotalSubagents : null,
            _settings.ShowElapsed ? FormatElapsed(_snapshot.Elapsed(now)) : null,
            _settings.ShowFiveHourUsage ? _rateLimits.FiveHour : null,
            _settings.ShowWeeklyUsage ? _rateLimits.Weekly : null,
            _board.ActiveCount,
            _board.AttentionCount);
    }

    private FlyoutVisualState CreateFlyoutVisualState()
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
                task.CurrentTool,
                task.ErrorMessage,
                task.StartedAtUtc?.UtcTicks,
                task.WaitingSinceAtUtc?.UtcTicks,
                task.StoppedAtUtc?.UtcTicks,
                !task.IsActive && task.StoppedAtUtc is null
                    ? task.LastUpdatedAtUtc.UtcTicks
                    : 0)));

        return new FlyoutVisualState(
            _settingsVersion,
            _board.ActiveCount,
            _board.RunningCount,
            _board.ReadyCount,
            _board.AttentionCount,
            tasksKey,
            _rateLimits.FiveHour,
            _rateLimits.Weekly,
            _board.UnreadSignalAvailable);
    }

    private readonly record struct PreviewVisualState(
        int SettingsVersion,
        bool ShowSpinner,
        string? Activity,
        string? SessionId,
        string? TurnId,
        DateTimeOffset? SpinnerStartedAtUtc,
        int? FilesChangedCount,
        int? TotalSubagents,
        string? ElapsedText,
        RateLimitWindowState? FiveHour,
        RateLimitWindowState? Weekly,
        int ActiveCount,
        int AttentionCount);

    private readonly record struct FlyoutVisualState(
        int SettingsVersion,
        int ActiveCount,
        int RunningCount,
        int ReadyCount,
        int AttentionCount,
        string TasksKey,
        RateLimitWindowState FiveHour,
        RateLimitWindowState Weekly,
        bool UnreadSignalAvailable);

    private static UIElement CreateFlyoutHeader(FlyoutVisualState state)
    {
        var header = new Grid
        {
            ColumnSpacing = 12,
            Margin = new Thickness(2, 0, 42, 2),
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Image
        {
            Source = new BitmapImage(new Uri("ms-appx:///Assets/CodexStatus.png")),
            Width = 30,
            Height = 30,
            VerticalAlignment = VerticalAlignment.Center,
        };
        header.Children.Add(icon);

        var heading = new StackPanel { Spacing = 1 };
        heading.Children.Add(new TextBlock
        {
            Text = "Codex Status",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI Variable Display"),
        });
        heading.Children.Add(new TextBlock
        {
            Text = FormatFlyoutHeaderSummary(state),
            FontSize = 11.5,
            Foreground = Brush("#FFAAB2BC"),
        });
        Grid.SetColumn(heading, 1);
        header.Children.Add(heading);

        var statusColor = state.AttentionCount > 0
            ? Brush("#FFFFC766")
            : state.ActiveCount > 0
                ? Brush("#FF65B8FF")
                : Brush("#FF8F99A4");
        var status = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        status.Children.Add(new Border
        {
            Width = 6,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = statusColor,
        });
        status.Children.Add(new TextBlock
        {
            Text = state.AttentionCount > 0
                ? $"{state.AttentionCount} ATTENTION"
                : state.ActiveCount > 0
                    ? $"{state.ActiveCount} ACTIVE"
                    : "IDLE",
            FontSize = 10,
            CharacterSpacing = 45,
            FontWeight = FontWeights.SemiBold,
            Foreground = statusColor,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(status, 2);
        header.Children.Add(status);
        return header;
    }

    private static string FormatFlyoutHeaderSummary(FlyoutVisualState state)
    {
        if (state.AttentionCount > 0)
        {
            var summary = FormatCount(
                state.AttentionCount,
                "task needs attention",
                "tasks need attention");
            return state.RunningCount > 0
                ? $"{summary} · {FormatCount(state.RunningCount, "running", "running")}"
                : summary;
        }

        return state.ActiveCount switch
        {
            > 0 when state.ReadyCount > 0 =>
                $"{FormatCount(state.ActiveCount, "active task", "active tasks")} · " +
                $"{FormatCount(state.ReadyCount, "ready", "ready")}",
            > 0 => FormatCount(state.ActiveCount, "active task", "active tasks"),
            _ when state.ReadyCount > 0 =>
                FormatCount(state.ReadyCount, "task ready", "tasks ready"),
            _ => "All caught up",
        };
    }

    private static UIElement CreateRateLimitPanel(FlyoutVisualState state)
    {
        var grid = new Grid
        {
            ColumnSpacing = 18,
            Margin = new Thickness(2, 5, 2, 3),
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });
        grid.Children.Add(CreateRateLimitSummary("5-HOUR", state.FiveHour, 0));
        var separator = new Border
        {
            Width = 1,
            Background = Brush("#285F6974"),
            Margin = new Thickness(0, 1, 0, 1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        Grid.SetColumn(separator, 1);
        grid.Children.Add(separator);
        var weekly = CreateRateLimitSummary("WEEKLY", state.Weekly, 2);
        grid.Children.Add(weekly);
        return grid;
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
        var row = new Grid
        {
            ColumnSpacing = 10,
            Margin = new Thickness(2, 5, 2, -3),
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 10,
            CharacterSpacing = 105,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#FF9DA6B0"),
        };
        row.Children.Add(labelText);
        var rule = new Border
        {
            Height = 1,
            Background = Brush("#245F6974"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(rule, 1);
        row.Children.Add(rule);
        var countText = new TextBlock
        {
            Text = count.ToString(),
            FontSize = 10,
            Foreground = Brush("#FF7F8994"),
        };
        Grid.SetColumn(countText, 2);
        row.Children.Add(countText);
        return row;
    }

    private UIElement CreateTaskCard(CodexStatusSnapshot task, DateTimeOffset now)
    {
        var statusColor = TaskStatusColor(task);
        var content = new StackPanel { Spacing = 6 };

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
        _flyoutTimeTexts[task.TaskKey] = elapsed;
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
        activity.Children.Add(
            task.Status == CodexExecutionStatuses.Running && _settings.ShowPulse
            ? CreateFlyoutTaskSpinner(task)
            : CreateTaskStateGlyph(task, statusColor));
        activity.Children.Add(new TextBlock
        {
            Text = task.RequiresAttention
                ? task.Activity
                : task.IsActive
                    ? task.Activity
                    : "Completed",
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
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        chips.Children.Add(new TextBlock
        {
            Text =
                $"{FormatCount(task.FilesChangedCount, "file", "files")}  ·  " +
                $"{FormatCount(task.TotalSubagents, "subagent", "subagents")}",
            FontSize = 9.5,
            Foreground = Brush("#FF8E98A3"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(chips, 1);
        footer.Children.Add(chips);
        content.Children.Add(footer);

        if (task.RequiresAttention)
        {
            var attentionDetail = !string.IsNullOrWhiteSpace(task.ErrorMessage)
                ? task.ErrorMessage
                : task.Status == CodexExecutionStatuses.Waiting
                    ? "Action required in Codex"
                    : task.Activity;
            content.Children.Add(new TextBlock
            {
                Text = attentionDetail,
                FontSize = 10.5,
                MaxLines = 2,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(statusColor),
                Opacity = 0.82,
                Margin = new Thickness(0, 1, 0, 0),
            });
        }

        if (!task.IsActive)
        {
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 7,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            actions.Children.Add(CreateTaskActionButton(
                "Open",
                "\uE8A7",
                () => OpenTask(task),
                enabled: !string.IsNullOrWhiteSpace(task.SessionId)));
            actions.Children.Add(CreateTaskActionButton(
                "Mark reviewed",
                "\uE73E",
                () => MarkTaskReviewed(task)));
            content.Children.Add(actions);
        }

        var layout = new Grid { ColumnSpacing = 10 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(new Border
        {
            Width = 2,
            CornerRadius = new CornerRadius(1),
            Background = new SolidColorBrush(statusColor),
            VerticalAlignment = VerticalAlignment.Stretch,
            Opacity = 0.92,
        });
        Grid.SetColumn(content, 1);
        layout.Children.Add(content);

        var card = new Border
        {
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(7, 9, 7, 10),
            Child = layout,
            Opacity = task.IsActive ? 1 : 0.9,
        };
        card.PointerEntered += (_, _) => card.Background = Brush("#182F3944");
        card.PointerExited += (_, _) =>
            card.Background = new SolidColorBrush(Colors.Transparent);
        AutomationProperties.SetName(
            card,
            $"{TaskDisplayTitle(task)}, {task.Activity}, {FormatElapsed(task.Elapsed(now))}");
        if (!string.IsNullOrWhiteSpace(task.SessionId))
        {
            AutomationProperties.SetHelpText(card, "Click to open this task in Codex");
            ToolTipService.SetToolTip(card, "Open in Codex");
            card.Tapped += (_, args) =>
            {
                args.Handled = true;
                OpenTask(task);
            };
        }

        return card;
    }

    private static Button CreateTaskActionButton(
        string label,
        string glyph,
        Action action,
        bool enabled = true)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
        };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 11 });
        content.Children.Add(new TextBlock { Text = label, FontSize = 10.5 });

        var button = new Button
        {
            Content = content,
            Padding = new Thickness(7, 3, 7, 3),
            MinHeight = 26,
            IsEnabled = enabled,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Foreground = Brush(label == "Open" ? "#FF68B8FF" : "#FF9CA6B1"),
        };
        button.Tapped += (_, args) => args.Handled = true;
        button.Click += (_, _) => action();
        AutomationProperties.SetName(button, label);
        return button;
    }

    private void OpenTask(CodexStatusSnapshot task)
    {
        if (!string.IsNullOrWhiteSpace(task.SessionId))
        {
            _context?.RequestOpenTask(task.SessionId);
        }
    }

    private void MarkTaskReviewed(CodexStatusSnapshot task)
    {
        try
        {
            _reviewedTaskStore.MarkReviewed(task.TaskKey);
            _reviewedTaskKeys.Add(task.TaskKey);
            _flyoutRoot?.DispatcherQueue.TryEnqueue(() =>
                RefreshVisualState());
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            StandaloneLog.Write("Saving the reviewed task failed", exception);
        }
    }

    private void UpdateFlyoutTaskTimes(DateTimeOffset now)
    {
        foreach (var task in _board.Tasks)
        {
            if (!_flyoutTimeTexts.TryGetValue(task.TaskKey, out var text))
            {
                continue;
            }

            var value = task.IsActive
                ? FormatElapsed(task.Elapsed(now))
                : FormatUpdatedAt(task.StoppedAtUtc ?? task.LastUpdatedAtUtc);
            if (!string.Equals(text.Text, value, StringComparison.Ordinal))
            {
                text.Text = value;
            }
        }
    }

    private void NotifyAttentionTransitions()
    {
        var currentStates = _board.Tasks
            .Where(task => task.ShouldNotifyAttention)
            .ToDictionary(AttentionStateKey, StringComparer.Ordinal);

        if (_settings.ShowAttentionNotifications)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var (stateKey, task) in currentStates)
            {
                if (_notifiedAttentionStates.Contains(stateKey) ||
                    task.LastUpdatedAtUtc == DateTimeOffset.MinValue ||
                    now - task.LastUpdatedAtUtc > TimeSpan.FromMinutes(10))
                {
                    continue;
                }

                _context?.RequestAttentionNotification(new WidgetAttentionNotification(
                    task.TaskKey,
                    task.SessionId,
                    TaskDisplayTitle(task),
                    !string.IsNullOrWhiteSpace(task.ErrorMessage)
                        ? task.ErrorMessage
                        : task.Activity,
                    task.Status == CodexExecutionStatuses.Error));
            }
        }

        _notifiedAttentionStates.Clear();
        _notifiedAttentionStates.UnionWith(currentStates.Keys);
    }

    private static string AttentionStateKey(CodexStatusSnapshot task) =>
        $"{task.TaskKey}\u001f{task.Status}\u001f{task.Activity}";

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
        if (task.RequiresAttention)
        {
            return new FontIcon
            {
                Glyph = task.Status == CodexExecutionStatuses.Error
                    ? "\uEA39"
                    : "\uE7BA",
                FontSize = 12,
                Foreground = new SolidColorBrush(color),
            };
        }

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

    private static UIElement CreateEmptyTasksCard()
    {
        var panel = new StackPanel
        {
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(12, 18, 12, 10),
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

        return panel;
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
        PreviewVisualState visualState)
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
                    visualState.Activity ?? string.Empty,
                    presentation.ActivityMaxWidth,
                    presentation.TextFontSize,
                    _snapshot.Status switch
                    {
                        CodexExecutionStatuses.Running => Colors.White,
                        CodexExecutionStatuses.Waiting => Color("#FFFFD487"),
                        CodexExecutionStatuses.Error => Color("#FFFF8A8A"),
                        _ => Color("#FFD1D6DC"),
                    }));
            }
        }

        if (visualState.ActiveCount > 1 || visualState.AttentionCount > 0)
        {
            segment.Children.Add(CreateTaskCountBadge(
                visualState.ActiveCount,
                visualState.AttentionCount,
                _snapshot.Status == CodexExecutionStatuses.Error));
        }

        return segment;
    }

    private static Border CreateTaskCountBadge(
        int activeCount,
        int attentionCount,
        bool hasError)
    {
        var hasMultipleActive = activeCount > 1;
        var text = hasMultipleActive
            ? activeCount.ToString()
            : attentionCount > 1
                ? $"!{attentionCount}"
                : "!";
        var accent = attentionCount > 0
            ? hasError
                ? Color("#FFFF7474")
                : Color("#FFF4BE5B")
            : Color("#FF5AACFF");
        var badge = new Border
        {
            MinWidth = 18,
            Height = 18,
            Padding = new Thickness(5, 0, 5, 0),
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(
                48,
                accent.R,
                accent.G,
                accent.B)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(
                125,
                accent.R,
                accent.G,
                accent.B)),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(accent),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            VerticalAlignment = VerticalAlignment.Center,
        };

        var accessibleName = hasMultipleActive
            ? $"{activeCount} active tasks"
            : $"{attentionCount} task{(attentionCount == 1 ? string.Empty : "s")} need attention";
        AutomationProperties.SetName(badge, accessibleName);
        ToolTipService.SetToolTip(badge, accessibleName);
        return badge;
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
            _snapshot.Status == CodexExecutionStatuses.Running,
            _snapshot.SessionId,
            _snapshot.TurnId,
            _snapshot.StartedAtUtc,
            nowUtc);

    private void UpdateSpinnerFrame()
    {
        if (!_settings.ShowPulse)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (_snapshot.Status == CodexExecutionStatuses.Running)
        {
            var frame = CurrentSpinnerFrame(now);
            if (_spinnerText is not null && frame is not null &&
                !string.Equals(_spinnerText.Text, frame.Text, StringComparison.Ordinal))
            {
                _spinnerText.Text = frame.Text;
            }
        }

        if (!_flyoutVisible || _flyoutSpinnerTexts.Count == 0)
        {
            return;
        }

        foreach (var task in _board.Tasks.Where(
                     task => task.Status == CodexExecutionStatuses.Running))
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
        var previewIsRunning = _snapshot.Status == CodexExecutionStatuses.Running;
        if (!previewIsRunning)
        {
            _spinnerAnimator.Reset();
        }

        var flyoutHasRunningTask = _flyoutVisible &&
            _board.Tasks.Any(task => task.Status == CodexExecutionStatuses.Running);
        var shouldRun =
            _settings.ShowPulse &&
            _spinnerTimer is not null &&
            (previewIsRunning || flyoutHasRunningTask);
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
