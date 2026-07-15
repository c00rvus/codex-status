using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Codex.TaskbarStatus.Standalone.Hosting;

internal sealed class TaskbarPreviewWindow
{
    private readonly Window _window;
    private readonly Grid _root;
    private readonly Border _hoverSurface;
    private readonly nint _windowHandle;
    private bool _bridgeIsTransparent;

    internal TaskbarPreviewWindow(
        UIElement content,
        Action onInvoked,
        Action onOpenSettings,
        Action onExit)
    {
        if (content is FrameworkElement element)
        {
            element.HorizontalAlignment = HorizontalAlignment.Stretch;
            element.VerticalAlignment = VerticalAlignment.Stretch;
            element.Width = double.NaN;
            element.Height = double.NaN;
            element.MinWidth = 0;
            element.MinHeight = 0;
        }

        _hoverSurface = new Border
        {
            IsHitTestVisible = false,
            Margin = new Thickness(4),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Colors.Transparent),
        };

        _root = new Grid
        {
            Background = new SolidColorBrush(Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            RequestedTheme = ElementTheme.Dark,
        };
        _root.Children.Add(content);
        _root.Children.Add(_hoverSurface);
        _root.PointerEntered += OnPointerEntered;
        _root.PointerExited += OnPointerExited;
        _root.Tapped += (_, args) =>
        {
            onInvoked();
            args.Handled = true;
        };
        _root.RightTapped += (_, args) =>
        {
            var menu = new MenuFlyout();
            var settingsItem = new MenuFlyoutItem
            {
                Text = "Settings",
                Icon = new FontIcon { Glyph = "\uE713" },
            };
            settingsItem.Click += (_, _) => onOpenSettings();
            var exitItem = new MenuFlyoutItem
            {
                Text = "Exit Codex Status",
                Icon = new FontIcon { Glyph = "\uE8BB" },
            };
            exitItem.Click += (_, _) => onExit();
            menu.Items.Add(settingsItem);
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(exitItem);
            menu.ShowAt(_root, args.GetPosition(_root));
            args.Handled = true;
        };
        _root.Loaded += (_, _) => ApplyBridgeTransparency();

        _window = new Window
        {
            ExtendsContentIntoTitleBar = true,
            Content = _root,
            SystemBackdrop = new TransparentBackdrop(),
        };
        _windowHandle = NativeMethods.GetHandle(_window);
        NativeMethods.ShowWindow(_windowHandle, NativeMethods.SwHide);
        ApplyTransparentChrome();
    }

    internal nint WindowHandle => _windowHandle;

    internal uint Dpi
    {
        get
        {
            var dpi = NativeMethods.GetDpiForWindow(_windowHandle);
            return dpi == 0 ? 96u : dpi;
        }
    }

    internal NativeMethods.Rect ScreenBounds
    {
        get
        {
            return NativeMethods.GetWindowRect(_windowHandle, out var bounds)
                ? bounds
                : default;
        }
    }

    internal bool AttachAndShow(
        nint taskbar,
        NativeMethods.Rect taskbarBounds,
        NativeMethods.Rect slotBounds)
    {
        if (taskbar == nint.Zero ||
            !NativeMethods.IsWindow(taskbar) ||
            taskbarBounds.IsEmpty ||
            slotBounds.IsEmpty)
        {
            Hide();
            return false;
        }

        ApplyChildStyle();
        if (NativeMethods.GetParent(_windowHandle) != taskbar)
        {
            NativeMethods.SetParent(_windowHandle, taskbar);
        }

        if (NativeMethods.GetParent(_windowHandle) != taskbar)
        {
            Hide();
            StandaloneLog.Write(
                $"Taskbar reparent failed: preview=0x{_windowHandle:X}, taskbar=0x{taskbar:X}");
            return false;
        }

        var relativeX = slotBounds.Left - taskbarBounds.Left;
        var relativeY = slotBounds.Top - taskbarBounds.Top;
        var positioned = NativeMethods.SetWindowPos(
            _windowHandle,
            NativeMethods.HwndTop,
            relativeX,
            relativeY,
            slotBounds.Width,
            slotBounds.Height,
            NativeMethods.SwpNoActivate |
            NativeMethods.SwpFrameChanged |
            NativeMethods.SwpShowWindow);
        if (!positioned)
        {
            Hide();
            StandaloneLog.Write(
                $"Taskbar positioning failed: preview=0x{_windowHandle:X}, taskbar=0x{taskbar:X}");
            return false;
        }

        ApplyBridgeTransparency();
        NativeMethods.ShowWindow(_windowHandle, NativeMethods.SwShowNoActivate);
        return true;
    }

    internal void Hide()
    {
        NativeMethods.ShowWindow(_windowHandle, NativeMethods.SwHide);
    }

    private void ApplyTransparentChrome()
    {
        var noBackdrop = NativeMethods.DwmsbtNone;
        NativeMethods.DwmSetWindowAttribute(
            _windowHandle,
            NativeMethods.DwmwaSystemBackdropType,
            ref noBackdrop,
            sizeof(int));
        var noBorder = NativeMethods.DwmColorNone;
        NativeMethods.DwmSetWindowAttribute(
            _windowHandle,
            NativeMethods.DwmwaBorderColor,
            ref noBorder,
            sizeof(uint));
        ApplyChildStyle();
    }

    private void ApplyChildStyle()
    {
        const int previewStyle =
            NativeMethods.WsChild |
            NativeMethods.WsVisible |
            NativeMethods.WsClipSiblings;
        if (NativeMethods.GetWindowLong(_windowHandle, NativeMethods.GwlStyle) != previewStyle)
        {
            NativeMethods.SetWindowLong(
                _windowHandle,
                NativeMethods.GwlStyle,
                previewStyle);
        }

        if (NativeMethods.GetWindowLong(_windowHandle, NativeMethods.GwlExStyle) !=
            NativeMethods.WsExNoActivate)
        {
            NativeMethods.SetWindowLong(
                _windowHandle,
                NativeMethods.GwlExStyle,
                NativeMethods.WsExNoActivate);
        }
    }

    private void ApplyBridgeTransparency()
    {
        if (_bridgeIsTransparent)
        {
            return;
        }

        var bridge = NativeMethods.FindContentBridge(_windowHandle);
        if (bridge == nint.Zero)
        {
            return;
        }

        var style = NativeMethods.GetWindowLong(bridge, NativeMethods.GwlExStyle);
        NativeMethods.SetWindowLong(
            bridge,
            NativeMethods.GwlExStyle,
            style | NativeMethods.WsExLayered);
        _bridgeIsTransparent = NativeMethods.SetLayeredWindowAttributes(
            bridge,
            colorKey: 0,
            alpha: 0,
            NativeMethods.LwaColorKey);
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs args)
    {
        _hoverSurface.Background = new SolidColorBrush(
            Windows.UI.Color.FromArgb(12, 255, 255, 255));
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs args)
    {
        _hoverSurface.Background = new SolidColorBrush(Colors.Transparent);
    }
}
