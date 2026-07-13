using System.Text.Json;
using Codex.TaskbarStatus.Core;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WidBar.SDK;

namespace Codex.TaskbarStatus.ExtensionApp;

public sealed class MainPlugin : WidgetPluginBase, IConfigurableWidgetPlugin
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly CodexStatusReader _statusReader = new();
    private readonly RequestSpinnerAnimator _spinnerAnimator = new();
    private WidgetSettings _settings = new();
    private CodexStatusSnapshot _snapshot = new();
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
        _settings = WidgetSettings.FromJson(context.SettingsJson);
        _snapshot = _statusReader.Read();
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
        var draft = WidgetSettings.FromJson(context.SettingsJson);
        var root = new Grid
        {
            MinHeight = 520,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var cards = new Grid
        {
            ColumnSpacing = 14,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
        };
        cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var displayedOptions = new StackPanel { Spacing = 6 };
        AddToggle(displayedOptions, "Status / activity", draft.ShowActivity, value => draft.ShowActivity = value, draft, context);
        AddToggle(displayedOptions, "Changed files", draft.ShowFiles, value => draft.ShowFiles = value, draft, context);
        AddToggle(displayedOptions, "Subagents", draft.ShowAgents, value => draft.ShowAgents = value, draft, context);
        AddToggle(displayedOptions, "Elapsed time", draft.ShowElapsed, value => draft.ShowElapsed = value, draft, context);

        var behaviorOptions = new StackPanel { Spacing = 6 };
        AddToggle(behaviorOptions, "Animated spinner", draft.ShowPulse, value => draft.ShowPulse = value, draft, context);
        AddToggle(behaviorOptions, "Compact mode", draft.Compact, value => draft.Compact = value, draft, context);
        AddToggle(behaviorOptions, "Hide when idle", draft.HideWhenIdle, value => draft.HideWhenIdle = value, draft, context);

        var displayedCard = CreateSettingsCard("Displayed items", displayedOptions);
        var behaviorCard = CreateSettingsCard("Animation and behavior", behaviorOptions);
        Grid.SetColumn(behaviorCard, 1);
        cards.Children.Add(displayedCard);
        cards.Children.Add(behaviorCard);
        root.Children.Add(cards);

        var attributionLink = new HyperlinkButton
        {
            Content = "Spinners by Eronred/expo-agent-spinners (MIT)",
            NavigateUri = new Uri("https://github.com/Eronred/expo-agent-spinners"),
            FontSize = 12,
            Opacity = 0.72,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 18, 0, 4),
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
        _settings = WidgetSettings.FromJson(settingsJson);
        RenderPreview();
        SyncSpinnerTimer();
        Context?.RequestPreviewRefresh();
    }

    public override ValueTask DisposeAsync()
    {
        _refreshTimer?.Stop();
        _refreshTimer = null;
        _spinnerTimer?.Stop();
        _spinnerTimer = null;
        _spinnerTimerRunning = false;
        _spinnerText = null;
        _previewRoot = null;
        _flyoutRoot = null;
        return ValueTask.CompletedTask;
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

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var hasContent = false;

        var presentation = PreviewPresentationFactory.Create(
            _settings.ShowActivity,
            _settings.ShowFiles,
            _settings.ShowAgents,
            _settings.ShowElapsed,
            _settings.ShowPulse,
            IsActive(_snapshot.Status),
            _settings.Compact,
            _snapshot.FilesChangedCount,
            _snapshot.TotalSubagents);

        if (presentation.ShowActivity || presentation.ShowSpinner)
        {
            var activitySegment = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
            };

            if (presentation.ShowActivity)
            {
                activitySegment.Children.Add(CreateText(
                    _snapshot.Activity,
                    presentation.ActivityMaxWidth,
                    _snapshot.Status == "running" ? Colors.White : Color("#FFD1D6DC")));
            }

            if (presentation.ShowSpinner)
            {
                activitySegment.Children.Add(CreateSpinner());
            }

            AddSegment(row, activitySegment, ref hasContent);
        }

        if (presentation.FilesText is not null)
        {
            AddSegment(row, CreateText(presentation.FilesText, presentation.FilesMaxWidth), ref hasContent);
        }

        if (presentation.SubagentsText is not null)
        {
            AddSegment(row, CreateText(presentation.SubagentsText, presentation.SubagentsMaxWidth), ref hasContent);
        }

        if (presentation.ShowElapsed)
        {
            AddSegment(row, CreateText(FormatElapsed(_snapshot.Elapsed(DateTimeOffset.UtcNow)), 60), ref hasContent);
        }

        if (!hasContent)
        {
            row.Children.Add(CreateText("Codex", 60));
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
            570);
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
    }

    private static TextBlock CreateText(string text, double maxWidth, Windows.UI.Color? color = null)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = new SolidColorBrush(color ?? Color("#FFE4E7EB")),
            MaxWidth = maxWidth,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private Border CreateSpinner()
    {
        var frame = CurrentSpinnerFrame(DateTimeOffset.UtcNow)
            ?? throw new InvalidOperationException("An active request must have a spinner frame.");
        _spinnerText = new TextBlock
        {
            Text = frame.Text,
            FontSize = 14,
            FontFamily = new FontFamily("Cascadia Mono"),
            Foreground = new SolidColorBrush(StatusColor(_snapshot.Status)),
            TextAlignment = TextAlignment.Left,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var container = new Border
        {
            Width = SpinnerContainerWidth(frame.Definition),
            Height = 18,
            Child = _spinnerText,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(container, $"Spinner {frame.Definition.Name}");
        return container;
    }

    private static double SpinnerContainerWidth(AgentSpinnerDefinition definition)
    {
        var characterCount = definition.Frames.Max(frame =>
            new System.Globalization.StringInfo(frame).LengthInTextElements);
        return Math.Clamp((characterCount * 8.5) + 2, 12, 48);
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
        WidgetSettings draft,
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
        Grid.SetColumn(toggle, 1);
        row.Children.Add(toggle);

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

    private static void AddSegment(StackPanel row, UIElement element, ref bool hasContent)
    {
        if (hasContent)
        {
            row.Children.Add(new Border
            {
                Width = 1,
                Height = 16,
                Background = Brush("#553C424A"),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        row.Children.Add(element);
        hasContent = true;
    }

    private static Windows.UI.Color StatusColor(string? status)
    {
        return status?.ToLowerInvariant() switch
        {
            "running" => Color("#FF3B9EFF"),
            "waiting" => Color("#FFF2B84B"),
            "completed" => Color("#FF54D38A"),
            "error" or "aborted" => Color("#FFFF6B72"),
            _ => Color("#FF98A2AD"),
        };
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

    private sealed class WidgetSettings
    {
        public bool ShowActivity { get; set; } = true;
        public bool ShowFiles { get; set; } = true;
        public bool ShowAgents { get; set; } = true;
        public bool ShowElapsed { get; set; } = true;
        public bool ShowPulse { get; set; } = true;
        public bool Compact { get; set; }
        public bool HideWhenIdle { get; set; }

        public static WidgetSettings FromJson(string? json)
        {
            try
            {
                return string.IsNullOrWhiteSpace(json)
                    ? new WidgetSettings()
                    : JsonSerializer.Deserialize<WidgetSettings>(json, JsonOptions) ?? new WidgetSettings();
            }
            catch (JsonException)
            {
                return new WidgetSettings();
            }
        }

        public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
    }
}
