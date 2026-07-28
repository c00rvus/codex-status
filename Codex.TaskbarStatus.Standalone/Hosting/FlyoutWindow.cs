using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Codex.TaskbarStatus.Standalone.Hosting;

internal sealed class FlyoutWindow : IDisposable
{
    private readonly Window _window;
    private readonly nint _windowHandle;
    private readonly nint _previewWindowHandle;
    private readonly int _logicalWidth;
    private int _logicalHeight;
    private readonly OutsideClickMonitor _outsideClickMonitor;
    private readonly Action<bool> _visibilityChanged;
    private NativeMethods.Rect _previewBounds;
    private uint _previewDpi = 96;

    internal FlyoutWindow(
        UIElement pluginContent,
        int logicalWidth,
        int logicalHeight,
        nint previewWindowHandle,
        Action openSettings,
        Action<bool> visibilityChanged)
    {
        _logicalWidth = logicalWidth;
        _logicalHeight = logicalHeight;
        _previewWindowHandle = previewWindowHandle;
        _visibilityChanged = visibilityChanged;

        var card = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(222, 18, 21, 26)),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(14),
            Child = pluginContent,
        };
        var root = new Grid
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            RequestedTheme = ElementTheme.Dark,
        };
        root.Children.Add(card);

        var settingsButton = new Button
        {
            Content = new FontIcon { Glyph = "\uE713", FontSize = 13 },
            Width = 34,
            Height = 34,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 12, 12, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            IsTabStop = false,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        settingsButton.Click += (_, _) => openSettings();
        root.Children.Add(settingsButton);

        _window = new Window
        {
            Title = "Codex Status",
            ExtendsContentIntoTitleBar = true,
            Content = root,
        };
        _window.Activated += OnWindowActivated;
        try
        {
            if (DesktopAcrylicController.IsSupported())
            {
                _window.SystemBackdrop = new DesktopAcrylicBackdrop();
            }
        }
        catch
        {
            _window.SystemBackdrop = null;
        }

        _windowHandle = NativeMethods.GetHandle(_window);
        _outsideClickMonitor = new OutsideClickMonitor(OnMouseButtonDown);
        var appWindow = NativeMethods.GetAppWindow(_window);
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }

        NativeMethods.SetWindowLong(
            _windowHandle,
            NativeMethods.GwlStyle,
            NativeMethods.WsPopup);
        NativeMethods.SetWindowLong(
            _windowHandle,
            NativeMethods.GwlExStyle,
            NativeMethods.WsExToolWindow);
        NativeMethods.ShowWindow(_windowHandle, NativeMethods.SwHide);
    }

    internal bool IsVisible => NativeMethods.IsWindowVisible(_windowHandle);

    internal void Toggle(NativeMethods.Rect previewBounds, uint previewDpi)
    {
        if (IsVisible)
        {
            Hide();
        }
        else
        {
            Show(previewBounds, previewDpi);
        }
    }

    internal void Show(NativeMethods.Rect previewBounds, uint previewDpi)
    {
        _previewBounds = previewBounds;
        _previewDpi = previewDpi == 0 ? 96u : previewDpi;
        // Render first: the widget measures its current content and can update
        // the logical height before the hidden window is positioned.
        _visibilityChanged(true);
        var placement = ResizeAndPosition();
        _window.Activate();
        NativeMethods.SetForegroundWindow(_windowHandle);
        try
        {
            _outsideClickMonitor.Start();
        }
        catch (Exception exception)
        {
            // Window deactivation remains as a best-effort fallback when a
            // policy or another process prevents installing the mouse hook.
            StandaloneLog.Write("Outside-click monitor startup failed", exception);
        }
        StandaloneLog.Write(
            $"Details flyout shown at {placement.X},{placement.Y} " +
            $"{placement.Width}x{placement.Height}.");
    }

    internal void UpdateLogicalHeight(int logicalHeight)
    {
        var normalized = Math.Clamp(logicalHeight, 300, 620);
        if (_logicalHeight == normalized)
        {
            return;
        }

        _logicalHeight = normalized;
        if (IsVisible)
        {
            var placement = ResizeAndPosition();
            StandaloneLog.Write(
                $"Details flyout resized to {placement.Width}x{placement.Height}.");
        }
    }

    internal void Hide()
    {
        var wasVisible = IsVisible;
        _outsideClickMonitor.Stop();
        NativeMethods.ShowWindow(_windowHandle, NativeMethods.SwHide);
        if (wasVisible)
        {
            _visibilityChanged(false);
        }
    }

    public void Dispose()
    {
        Hide();
        _outsideClickMonitor.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnMouseButtonDown(NativeMethods.Point cursor)
    {
        if (!IsVisible || IsCursorOverPreview(cursor))
        {
            return;
        }

        if (NativeMethods.GetWindowRect(_windowHandle, out var flyoutBounds) &&
            flyoutBounds.Contains(cursor))
        {
            return;
        }

        // Let the current input message finish before hiding the WinUI window.
        // This keeps the original click available to the application below it.
        _window.DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsVisible)
            {
                return;
            }

            StandaloneLog.Write("Details flyout dismissed after an outside click.");
            Hide();
        });
    }

    private bool IsCursorOverPreview(NativeMethods.Point cursor) =>
        NativeMethods.GetWindowRect(_previewWindowHandle, out var currentBounds)
            ? currentBounds.Contains(cursor)
            : _previewBounds.Contains(cursor);

    private (int X, int Y, int Width, int Height) ResizeAndPosition()
    {
        var workArea = NativeMethods.GetMonitorWorkArea(_previewBounds);
        var width = (int)Math.Ceiling(_logicalWidth * _previewDpi / 96d);
        var requestedHeight = (int)Math.Ceiling(_logicalHeight * _previewDpi / 96d);
        var maximumPhysicalHeight = Math.Max(240, workArea.Bottom - workArea.Top - 16);
        var height = Math.Min(requestedHeight, maximumPhysicalHeight);
        var maximumX = Math.Max(workArea.Left, workArea.Right - width);
        var maximumY = Math.Max(workArea.Top, workArea.Bottom - height);
        var x = Math.Clamp(_previewBounds.Left, workArea.Left, maximumX);
        var spaceAbove = _previewBounds.Top - workArea.Top;
        var requestedY = spaceAbove >= height + 8
            ? _previewBounds.Top - height - 8
            : _previewBounds.Bottom + 8;
        var y = Math.Clamp(requestedY, workArea.Top, maximumY);
        var appWindow = NativeMethods.GetAppWindow(_window);
        appWindow.Resize(new SizeInt32(width, height));
        appWindow.Move(new PointInt32(x, y));
        return (x, y, width, height);
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated || !IsVisible)
        {
            return;
        }

        // The taskbar preview is deliberately WS_EX_NOACTIVATE. If Windows still
        // reports a transient deactivation while the pointer is over it, leave the
        // flyout visible so the preview's tap handler remains the single toggle.
        if (NativeMethods.GetCursorPos(out var cursor) && IsCursorOverPreview(cursor))
        {
            return;
        }

        StandaloneLog.Write("Details flyout dismissed after losing focus.");
        Hide();
    }
}
