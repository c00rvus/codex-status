using Codex.TaskbarStatus.Standalone.Widget;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;

namespace Codex.TaskbarStatus.Standalone.Hosting;

internal sealed class SettingsWindow
{
    private sealed class DraftContext : IWidgetSettingsContext
    {
        private readonly Action<string> _onDraftChanged;

        internal DraftContext(string settingsJson, Action<string> onDraftChanged)
        {
            SettingsJson = string.IsNullOrWhiteSpace(settingsJson) ? "{}" : settingsJson;
            _onDraftChanged = onDraftChanged;
        }

        public string SettingsJson { get; private set; }

        public void SaveSettings(string settingsJson)
        {
            SettingsJson = string.IsNullOrWhiteSpace(settingsJson) ? "{}" : settingsJson;
            _onDraftChanged(SettingsJson);
        }

        public void RequestPreviewRefresh() => _onDraftChanged(SettingsJson);
    }

    private readonly Window _window;
    private readonly DraftContext _context;
    private readonly TaskbarPlacementSettingsControl _placementControl;
    private readonly Action _onCancelled;
    private readonly Action _onClosed;
    private bool _saved;

    internal SettingsWindow(
        CodexStatusWidget widget,
        string settingsJson,
        StandalonePlacementDraft placementDraft,
        IReadOnlyList<TaskbarMonitorOption> monitors,
        Action<string> onDraftChanged,
        Action<StandalonePlacementDraft> onPlacementDraftChanged,
        Action<string, StandalonePlacementDraft> onSave,
        Action onCancelled,
        Action onClosed)
    {
        _onCancelled = onCancelled;
        _onClosed = onClosed;
        _context = new DraftContext(settingsJson, onDraftChanged);
        var settingsContent = widget.CreateSettingsContent(_context)
            ?? throw new InvalidOperationException("The widget did not provide settings content.");
        _placementControl = new TaskbarPlacementSettingsControl(
            placementDraft,
            monitors);
        _placementControl.DraftChanged += onPlacementDraftChanged;

        var titlePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        titlePanel.Children.Add(new Image
        {
            Source = new BitmapImage(new Uri("ms-appx:///Assets/CodexStatus.png")),
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
        });
        titlePanel.Children.Add(new TextBlock
        {
            Text = "Codex Status",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var titleBar = new Grid
        {
            Height = 48,
            Padding = new Thickness(16, 0, 0, 0),
            Background = new SolidColorBrush(Colors.Transparent),
        };
        titleBar.Children.Add(titlePanel);

        _window = new Window
        {
            Title = "Codex Status",
            ExtendsContentIntoTitleBar = true,
        };

        var save = new Button
        {
            Content = "Save",
            MinWidth = 120,
        };
        if (Application.Current.Resources.TryGetValue(
                "AccentButtonStyle",
                out var accentResource) &&
            accentResource is Style accentStyle)
        {
            save.Style = accentStyle;
        }
        save.Click += (_, _) =>
        {
            onSave(_context.SettingsJson, _placementControl.Draft);
            _saved = true;
            _window.Close();
        };

        var cancel = new Button
        {
            Content = "Cancel",
            MinWidth = 120,
        };
        cancel.Click += (_, _) => _window.Close();

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(24, 12, 24, 20),
        };
        actions.Children.Add(save);
        actions.Children.Add(cancel);

        var root = new Grid
        {
            RequestedTheme = ElementTheme.Dark,
            Background = new SolidColorBrush(Colors.Transparent),
        };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var surface = new Border
        {
            Background = new SolidColorBrush(ColorHelper.FromArgb(255, 32, 32, 32)),
            CornerRadius = new CornerRadius(8, 8, 0, 0),
        };
        var contentHost = new Border
        {
            Padding = new Thickness(24, 16, 24, 8),
            Child = CreateSettingsTabs(settingsContent, _placementControl),
        };

        Grid.SetRow(titleBar, 0);
        Grid.SetRow(surface, 1);
        Grid.SetRowSpan(surface, 2);
        Grid.SetRow(contentHost, 1);
        Grid.SetRow(actions, 2);
        root.Children.Add(titleBar);
        root.Children.Add(surface);
        root.Children.Add(contentHost);
        root.Children.Add(actions);

        _window.Content = root;
        _window.SetTitleBar(titleBar);
        try
        {
            if (MicaController.IsSupported())
            {
                _window.SystemBackdrop = new MicaBackdrop();
            }
        }
        catch
        {
            _window.SystemBackdrop = null;
            root.Background = new SolidColorBrush(ColorHelper.FromArgb(255, 32, 32, 32));
        }

        var appWindow = NativeMethods.GetAppWindow(_window);
        var iconPath = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "CodexStatus.ico");
        if (File.Exists(iconPath))
        {
            appWindow.SetIcon(iconPath);
        }

        appWindow.Resize(new SizeInt32(840, 560));
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.PreferredMinimumWidth = 800;
            presenter.PreferredMinimumHeight = 480;
        }

        var appTitleBar = appWindow.TitleBar;
        appTitleBar.ButtonBackgroundColor = Colors.Transparent;
        appTitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        appTitleBar.ButtonForegroundColor = Colors.White;
        appTitleBar.ButtonInactiveForegroundColor = ColorHelper.FromArgb(255, 160, 160, 160);
        _window.Closed += (_, _) =>
        {
            if (!_saved)
            {
                _onCancelled();
            }
            _onClosed();
        };
    }

    internal void ShowCentered()
    {
        var appWindow = NativeMethods.GetAppWindow(_window);
        var displayArea = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
        var x = displayArea.WorkArea.X + (displayArea.WorkArea.Width - appWindow.Size.Width) / 2;
        var y = displayArea.WorkArea.Y + (displayArea.WorkArea.Height - appWindow.Size.Height) / 2;
        appWindow.Move(new PointInt32(
            Math.Max(displayArea.WorkArea.X, x),
            Math.Max(displayArea.WorkArea.Y, y)));
        _window.Activate();
        NativeMethods.SetForegroundWindow(NativeMethods.GetHandle(_window));
    }

    internal void BringToFront()
    {
        _window.Activate();
        NativeMethods.SetForegroundWindow(NativeMethods.GetHandle(_window));
    }

    private static Grid CreateSettingsTabs(
        UIElement widgetSettings,
        UIElement taskbarSettings)
    {
        var widgetHost = new Grid();
        widgetHost.Children.Add(widgetSettings);
        var taskbarHost = new ScrollViewer
        {
            Content = taskbarSettings,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed,
        };

        var widgetButton = new Button
        {
            Content = "Widget",
            MinWidth = 120,
        };
        var taskbarButton = new Button
        {
            Content = "Taskbar",
            MinWidth = 120,
        };
        var navigation = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 0, 0, 10),
        };
        navigation.Children.Add(widgetButton);
        navigation.Children.Add(taskbarButton);

        void SelectPage(bool taskbarSelected)
        {
            widgetHost.Visibility = taskbarSelected
                ? Visibility.Collapsed
                : Visibility.Visible;
            taskbarHost.Visibility = taskbarSelected
                ? Visibility.Visible
                : Visibility.Collapsed;
            widgetButton.Opacity = taskbarSelected ? 0.68 : 1;
            taskbarButton.Opacity = taskbarSelected ? 1 : 0.68;
        }

        widgetButton.Click += (_, _) => SelectPage(taskbarSelected: false);
        taskbarButton.Click += (_, _) => SelectPage(taskbarSelected: true);

        var content = new Grid();
        content.Children.Add(widgetHost);
        content.Children.Add(taskbarHost);
        var root = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
            },
        };
        Grid.SetRow(content, 1);
        root.Children.Add(navigation);
        root.Children.Add(content);
        SelectPage(taskbarSelected: false);
        return root;
    }
}
