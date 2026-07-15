using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace Codex.TaskbarStatus.Standalone.Hosting;

internal readonly record struct TaskbarWindowInfo(
    int Index,
    nint Window,
    NativeMethods.Rect TaskbarBounds,
    NativeMethods.Rect MonitorBounds,
    string DeviceName,
    bool IsPrimary);

internal static class NativeMethods
{
    internal delegate bool EnumWindowCallback(nint window, nint parameter);

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Rect(int Left, int Top, int Right, int Bottom)
    {
        internal int Width => Right - Left;
        internal int Height => Bottom - Top;
        internal bool IsEmpty => Width <= 0 || Height <= 0;
        internal bool Contains(Point point) =>
            point.X >= Left &&
            point.X < Right &&
            point.Y >= Top &&
            point.Y < Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Point(int X, int Y);

    internal const int GwlStyle = -16;
    internal const int GwlExStyle = -20;

    internal const int WsVisible = 0x10000000;
    internal const int WsChild = 0x40000000;
    internal const int WsClipSiblings = 0x04000000;
    internal const int WsPopup = unchecked((int)0x80000000);

    internal const int WsExNoActivate = 0x08000000;
    internal const int WsExLayered = 0x00080000;
    internal const int WsExToolWindow = 0x00000080;

    internal const int SwHide = 0;
    internal const int SwShowNoActivate = 4;
    internal const int SwShow = 5;

    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoMove = 0x0002;
    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpFrameChanged = 0x0020;
    internal const uint SwpShowWindow = 0x0040;

    internal const uint LwaColorKey = 0x00000001;
    internal const uint DwmwaBorderColor = 34;
    internal const uint DwmwaSystemBackdropType = 38;
    internal const int DwmsbtNone = 1;
    internal const uint DwmColorNone = 0xFFFFFFFE;

    internal static readonly nint HwndTop = nint.Zero;

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        internal int Size;
        internal Rect Monitor;
        internal Rect WorkArea;
        internal uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        internal string DeviceName;
    }

    internal static nint GetHandle(Window window) => WindowNative.GetWindowHandle(window);

    internal static AppWindow GetAppWindow(Window window) => AppWindow.GetFromWindowId(
        Win32Interop.GetWindowIdFromWindow(GetHandle(window)));

    internal static Rect GetMonitorWorkArea(Rect anchor)
    {
        var monitor = MonitorFromRect(ref anchor, MonitorDefaultToNearest);
        if (monitor == nint.Zero)
        {
            return anchor;
        }

        var info = CreateMonitorInfo();
        return GetMonitorInfo(monitor, ref info) ? info.WorkArea : anchor;
    }

    internal static IReadOnlyList<TaskbarWindowInfo> GetTaskbars()
    {
        var taskbars = new List<TaskbarWindowInfo>();
        AddTaskbar(taskbars, FindWindow("Shell_TrayWnd", null), 0);

        var current = nint.Zero;
        for (var index = 1; index < 32; index++)
        {
            current = FindWindowEx(
                nint.Zero,
                current,
                "Shell_SecondaryTrayWnd",
                null);
            if (current == nint.Zero)
            {
                break;
            }

            AddTaskbar(taskbars, current, index);
        }

        return taskbars;
    }

    internal static nint FindTaskbar(string? deviceName, int monitorIndex)
    {
        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            var match = GetTaskbars().FirstOrDefault(taskbar =>
                string.Equals(
                    taskbar.DeviceName,
                    deviceName,
                    StringComparison.OrdinalIgnoreCase));
            if (match.Window != nint.Zero)
            {
                return match.Window;
            }

            return nint.Zero;
        }

        return FindTaskbar(monitorIndex);
    }

    internal static string GetClass(nint window)
    {
        var value = new StringBuilder(256);
        return GetClassName(window, value, value.Capacity) > 0
            ? value.ToString()
            : string.Empty;
    }

    internal static nint FindContentBridge(nint parent)
    {
        string[] knownClasses =
        [
            "Microsoft.UI.Content.DesktopChildSiteBridge",
            "Microsoft.UI.Content.ContentWindowSiteBridge",
            "Microsoft.UI.Input.DesktopWindowLiftedContentBridge",
        ];

        foreach (var className in knownClasses)
        {
            var bridge = FindWindowEx(parent, nint.Zero, className, null);
            if (bridge != nint.Zero)
            {
                return bridge;
            }
        }

        var discovered = nint.Zero;
        EnumChildWindows(parent, (window, _) =>
        {
            var className = GetClass(window);
            if (className.Contains("SiteBridge", StringComparison.Ordinal) ||
                className.Contains("ContentBridge", StringComparison.Ordinal))
            {
                discovered = window;
                return false;
            }

            return true;
        }, nint.Zero);
        return discovered;
    }

    internal static nint FindTaskbar(int monitorIndex)
    {
        if (monitorIndex <= 0)
        {
            return FindWindow("Shell_TrayWnd", null);
        }

        var current = nint.Zero;
        for (var index = 1; index <= monitorIndex; index++)
        {
            current = FindWindowEx(nint.Zero, current, "Shell_SecondaryTrayWnd", null);
            if (current == nint.Zero)
            {
                break;
            }
        }

        return current;
    }

    private static void AddTaskbar(
        ICollection<TaskbarWindowInfo> taskbars,
        nint window,
        int index)
    {
        if (window == nint.Zero ||
            !GetWindowRect(window, out var taskbarBounds) ||
            taskbarBounds.IsEmpty)
        {
            return;
        }

        var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        var info = CreateMonitorInfo();
        if (monitor == nint.Zero || !GetMonitorInfo(monitor, ref info))
        {
            info.Monitor = taskbarBounds;
            info.DeviceName = string.Empty;
        }

        taskbars.Add(new TaskbarWindowInfo(
            index,
            window,
            taskbarBounds,
            info.Monitor,
            info.DeviceName ?? string.Empty,
            (info.Flags & 1) != 0));
    }

    private static MonitorInfoEx CreateMonitorInfo() => new()
    {
        Size = Marshal.SizeOf<MonitorInfoEx>(),
        DeviceName = string.Empty,
    };

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetWindowLong(nint window, int index);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int SetWindowLong(nint window, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetParent(nint child, nint newParent);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint GetParent(nint child);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint window, out Rect bounds);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromRect(
        ref Rect rectangle,
        uint flags);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(
        nint window,
        uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(
        nint monitor,
        ref MonitorInfoEx info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint FindWindow(string className, string? title);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint FindWindowEx(
        nint parent,
        nint childAfter,
        string className,
        string? title);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumChildWindows(
        nint parent,
        EnumWindowCallback callback,
        nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetLayeredWindowAttributes(
        nint window,
        uint colorKey,
        byte alpha,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint window);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(
        nint window,
        uint attribute,
        ref int value,
        int valueSize);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(
        nint window,
        uint attribute,
        ref uint value,
        int valueSize);
}
