using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Codex.TaskbarStatus.Standalone.Hosting;

/// <summary>
/// Owns the standalone application's notification-area icon and its native
/// message window. The service must be created and disposed on the UI thread.
/// </summary>
internal sealed class TrayIconService : IDisposable
{
    private const uint CallbackMessage = WmApp + 1;
    private const uint RetryTimerId = 1;
    private const uint RetryIntervalMilliseconds = 2_000;

    private const uint WmApp = 0x8000;
    private const uint WmTimer = 0x0113;
    private const uint WmNull = 0x0000;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmLButtonDoubleClick = 0x0203;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmContextMenu = 0x007B;
    private const uint NinSelect = 0x0400;
    private const uint NinKeySelect = 0x0401;

    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifShowTip = 0x00000080;

    private const uint NimAdd = 0x00000000;
    private const uint NimDelete = 0x00000002;
    private const uint NimSetVersion = 0x00000004;
    private const uint NotifyIconVersion4 = 4;

    private const uint MfString = 0x00000000;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmNoNotify = 0x0080;
    private const uint TpmReturnCommand = 0x0100;

    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WsPopup = unchecked((int)0x80000000);
    private const int IdiApplication = 32512;

    private const uint MenuOpenSettings = 1;
    private const uint MenuShowDetails = 2;
    private const uint MenuRestart = 3;
    private const uint MenuExit = 4;

    private readonly Action _openSettings;
    private readonly Action _showDetails;
    private readonly Action _restart;
    private readonly Action _exit;
    private readonly WindowProcedure _windowProcedure;
    private readonly string _windowClassName;
    private readonly nint _module;
    private readonly uint _taskbarCreatedMessage;

    private nint _window;
    private nint _icon;
    private bool _ownsIcon;
    private bool _iconAdded;
    private bool _usesVersion4;
    private bool _classRegistered;
    private bool _disposed;

    internal TrayIconService(
        Action openSettings,
        Action showDetails,
        Action restart,
        Action exit)
    {
        _openSettings = openSettings ?? throw new ArgumentNullException(nameof(openSettings));
        _showDetails = showDetails ?? throw new ArgumentNullException(nameof(showDetails));
        _restart = restart ?? throw new ArgumentNullException(nameof(restart));
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));

        _windowProcedure = WindowProc;
        _windowClassName = $"CodexTaskbarStatus.TrayIcon.{Guid.NewGuid():N}";
        _module = GetModuleHandle(null);
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        try
        {
            CreateNativeWindow();
            LoadTrayIcon();
            AddIconOrScheduleRetry();
        }
        catch
        {
            // The host deliberately treats the tray icon as optional. Ensure a
            // failed initialization does not leave an orphan native window or
            // registered class behind before the exception reaches the host.
            Dispose();
            throw;
        }
    }

    internal nint WindowHandle => _window;

    internal bool IsIconVisible => _iconAdded;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_window != nint.Zero)
        {
            KillTimer(_window, RetryTimerId);
            DeleteIcon();

            var window = _window;
            _window = nint.Zero;
            DestroyWindow(window);
        }

        if (_classRegistered && _module != nint.Zero)
        {
            UnregisterClass(_windowClassName, _module);
            _classRegistered = false;
        }

        if (_ownsIcon && _icon != nint.Zero)
        {
            DestroyIcon(_icon);
        }

        _icon = nint.Zero;
        _ownsIcon = false;
        GC.SuppressFinalize(this);
    }

    private void CreateNativeWindow()
    {
        var windowClass = new WindowClassEx
        {
            Size = (uint)Marshal.SizeOf<WindowClassEx>(),
            WindowProcedure = _windowProcedure,
            Instance = _module,
            ClassName = _windowClassName,
        };

        if (RegisterClassEx(ref windowClass) == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Could not register the notification-area window class.");
        }

        _classRegistered = true;

        _window = CreateWindowEx(
            WsExToolWindow | WsExNoActivate,
            _windowClassName,
            "Codex Status",
            WsPopup,
            0,
            0,
            0,
            0,
            nint.Zero,
            nint.Zero,
            _module,
            nint.Zero);

        if (_window != nint.Zero)
        {
            return;
        }

        var error = Marshal.GetLastWin32Error();
        UnregisterClass(_windowClassName, _module);
        _classRegistered = false;
        throw new Win32Exception(error, "Could not create the notification-area message window.");
    }

    private void LoadTrayIcon()
    {
        var executablePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            var smallIcons = new nint[1];
            if (ExtractIconEx(executablePath, 0, null, smallIcons, 1) > 0 &&
                smallIcons[0] != nint.Zero)
            {
                _icon = smallIcons[0];
                _ownsIcon = true;
                return;
            }
        }

        _icon = LoadIcon(nint.Zero, (nint)IdiApplication);
        _ownsIcon = false;
    }

    private void AddIconOrScheduleRetry()
    {
        if (_disposed || _window == nint.Zero)
        {
            return;
        }

        // KillTimer can race with a WM_TIMER that was already selected for
        // dispatch. Avoid turning that late message into a duplicate NIM_ADD.
        if (_iconAdded)
        {
            KillTimer(_window, RetryTimerId);
            return;
        }

        if (AddIcon())
        {
            KillTimer(_window, RetryTimerId);
            return;
        }

        SetTimer(_window, RetryTimerId, RetryIntervalMilliseconds, nint.Zero);
    }

    private bool AddIcon()
    {
        var data = CreateIconData();
        if (!ShellNotifyIcon(NimAdd, ref data))
        {
            _iconAdded = false;
            return false;
        }

        _iconAdded = true;

        // Opt into the current callback semantics. If an older shell rejects
        // this request, the icon remains valid and legacy callbacks still work.
        data.TimeoutOrVersion = NotifyIconVersion4;
        _usesVersion4 = ShellNotifyIcon(NimSetVersion, ref data);
        StandaloneLog.Write("Notification-area icon registered.");
        return true;
    }

    private void DeleteIcon()
    {
        if (!_iconAdded || _window == nint.Zero)
        {
            return;
        }

        var data = CreateIconData();
        ShellNotifyIcon(NimDelete, ref data);
        _iconAdded = false;
        _usesVersion4 = false;
    }

    private NotifyIconData CreateIconData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        Window = _window,
        Id = 1,
        Flags = NifMessage | NifIcon | NifTip | NifShowTip,
        CallbackMessage = CallbackMessage,
        Icon = _icon,
        Tip = "Codex Status",
        Info = string.Empty,
        InfoTitle = string.Empty,
    };

    private nint WindowProc(nint window, uint message, nint wParam, nint lParam)
    {
        try
        {
            if (_taskbarCreatedMessage != 0 && message == _taskbarCreatedMessage)
            {
                // Explorer discarded all notification icons when its taskbar
                // was recreated. Its new shell is now ready to accept ours.
                _iconAdded = false;
                _usesVersion4 = false;
                AddIconOrScheduleRetry();
                return nint.Zero;
            }

            if (message == WmTimer && unchecked((nuint)wParam) == RetryTimerId)
            {
                AddIconOrScheduleRetry();
                return nint.Zero;
            }

            if (message == CallbackMessage)
            {
                HandleNotification(
                    unchecked((uint)(long)lParam) & 0xFFFF,
                    wParam);
                return nint.Zero;
            }
        }
        catch (Exception exception)
        {
            StandaloneLog.Write("Notification-area callback failed", exception);
        }

        return DefWindowProc(window, message, wParam, lParam);
    }

    private void HandleNotification(uint notification, nint callbackData)
    {
        switch (notification)
        {
            case WmLButtonUp:
            case WmLButtonDoubleClick:
            case NinSelect:
            case NinKeySelect:
                _openSettings();
                break;

            case WmRButtonUp:
                ShowContextMenu();
                break;

            case WmContextMenu:
                ShowContextMenu(_usesVersion4
                    ? Point.FromPacked(callbackData)
                    : null);
                break;
        }
    }

    private void ShowContextMenu(Point? callbackPosition = null)
    {
        if (_window == nint.Zero)
        {
            return;
        }

        var cursor = callbackPosition ?? default;
        if (callbackPosition is null && !GetCursorPos(out cursor))
        {
            return;
        }

        var menu = CreatePopupMenu();
        if (menu == nint.Zero)
        {
            return;
        }

        try
        {
            AppendMenu(menu, MfString, MenuOpenSettings, "Open settings");
            AppendMenu(menu, MfString, MenuShowDetails, "Show details");
            AppendMenu(menu, MfString, MenuRestart, "Restart");
            AppendMenu(menu, MfSeparator, 0, null);
            AppendMenu(menu, MfString, MenuExit, "Exit");

            SetForegroundWindow(_window);
            var command = TrackPopupMenuEx(
                menu,
                TpmRightButton | TpmNoNotify | TpmReturnCommand,
                cursor.X,
                cursor.Y,
                _window,
                nint.Zero);

            switch (command)
            {
                case MenuOpenSettings:
                    _openSettings();
                    break;
                case MenuShowDetails:
                    _showDetails();
                    break;
                case MenuRestart:
                    _restart();
                    break;
                case MenuExit:
                    _exit();
                    break;
            }

            // Required by the shell so that a click elsewhere dismisses the
            // menu reliably and the next invocation is not swallowed.
            if (_window != nint.Zero)
            {
                PostMessage(_window, WmNull, nint.Zero, nint.Zero);
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        internal uint Size;
        internal uint Style;
        internal WindowProcedure WindowProcedure;
        internal int ClassExtraBytes;
        internal int WindowExtraBytes;
        internal nint Instance;
        internal nint Icon;
        internal nint Cursor;
        internal nint BackgroundBrush;
        internal string? MenuName;
        internal string ClassName;
        internal nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        internal uint Size;
        internal nint Window;
        internal uint Id;
        internal uint Flags;
        internal uint CallbackMessage;
        internal nint Icon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        internal string Tip;

        internal uint State;
        internal uint StateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        internal string Info;

        internal uint TimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        internal string InfoTitle;

        internal uint InfoFlags;
        internal Guid ItemGuid;
        internal nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        internal int X;
        internal int Y;

        internal static Point FromPacked(nint value)
        {
            var packed = unchecked((uint)(long)value);
            return new Point
            {
                X = unchecked((short)(packed & 0xFFFF)),
                Y = unchecked((short)((packed >> 16) & 0xFFFF)),
            };
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClass(string className, nint instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(
        int extendedStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nuint SetTimer(nint window, nuint id, uint interval, nint timerProcedure);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool KillTimer(nint window, nuint id);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(nint menu, uint flags, nuint item, string? text);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint TrackPopupMenuEx(
        nint menu,
        uint flags,
        int x,
        int y,
        nint window,
        nint parameters);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadIcon(nint instance, nint iconName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(
        string file,
        int index,
        [Out] nint[]? largeIcons,
        [Out] nint[]? smallIcons,
        uint iconCount);

    [DllImport(
        "shell32.dll",
        EntryPoint = "Shell_NotifyIconW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
}
