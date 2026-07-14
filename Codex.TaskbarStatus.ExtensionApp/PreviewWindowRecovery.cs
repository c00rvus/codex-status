using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml;

namespace Codex.TaskbarStatus.ExtensionApp;

// WidBar.SDK 1.2 hosts the preview as a child of Explorer's taskbar window.
// During sign-in Windows can reject that cross-process SetParent call while
// Explorer is still settling. The SDK then applies taskbar-relative coordinates
// to a desktop child, which leaves the preview at the top of the monitor. This
// small watchdog only touches our compact child preview and only when its bounds
// do not match the real WidBar overlay slot.
internal sealed class PreviewWindowRecovery
{
    private const int GwlStyle = -16;
    private const long WsChild = 0x40000000L;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private static readonly nint HwndTop = nint.Zero;

    private readonly DispatcherTimer _timer = new()
    {
        Interval = TimeSpan.FromSeconds(1),
    };

    private readonly HashSet<nint> _reportedRecoveries = [];

    public PreviewWindowRecovery()
    {
        _timer.Tick += (_, _) => RecoverEscapedPreviews();
    }

    public void Start()
    {
        _timer.Start();
        RecoverEscapedPreviews();
    }

    private void RecoverEscapedPreviews()
    {
        try
        {
            var taskbars = FindTaskbars();
            if (taskbars.Count == 0)
            {
                return;
            }

            var windows = EnumerateWindows();
            var targets = FindOverlayTargets(windows, taskbars);
            if (targets.Count == 0)
            {
                return;
            }

            foreach (var preview in FindOwnPreviewWindows(windows))
            {
                if (!Native.GetWindowRect(preview, out var previewBounds))
                {
                    continue;
                }

                var target = FindBestTarget(previewBounds, targets);
                if (target is null || previewBounds.NearlyEquals(target.Value.Bounds))
                {
                    continue;
                }

                RecoverPreview(preview, target.Value);
            }
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"[PreviewWindowRecovery] Check failed: {exception.Message}");
        }
    }

    private void RecoverPreview(nint preview, PreviewTarget target)
    {
        Native.SetParent(preview, target.Taskbar);
        var parented = Native.GetParent(preview) == target.Taskbar;
        var x = parented
            ? target.Bounds.Left - target.TaskbarBounds.Left
            : target.Bounds.Left;
        var y = parented
            ? target.Bounds.Top - target.TaskbarBounds.Top
            : target.Bounds.Top;

        Native.SetWindowPos(
            preview,
            HwndTop,
            x,
            y,
            target.Bounds.Width,
            target.Bounds.Height,
            SwpNoActivate | SwpNoOwnerZOrder);

        if (_reportedRecoveries.Add(preview))
        {
            var mode = parented ? "reparented" : "desktop fallback";
            Trace.WriteLine(
                $"[PreviewWindowRecovery] Preview {mode} at " +
                $"[{target.Bounds.Left},{target.Bounds.Top} " +
                $"{target.Bounds.Width}x{target.Bounds.Height}].");
        }
    }

    private static List<PreviewTarget> FindOverlayTargets(
        IReadOnlyCollection<nint> windows,
        IReadOnlyList<TaskbarWindow> taskbars)
    {
        var targets = new List<PreviewTarget>();

        foreach (var overlay in windows.Where(window =>
                     string.Equals(
                         Native.ClassName(window),
                         "WidBarRemotePreviewOverlay",
                         StringComparison.Ordinal)))
        {
            if (!Native.GetWindowRect(overlay, out var overlayBounds))
            {
                continue;
            }

            var parent = Native.GetParent(overlay);
            var ownedTaskbar = taskbars.FirstOrDefault(taskbar => taskbar.Handle == parent);
            if (ownedTaskbar.Handle != nint.Zero)
            {
                var desiredBounds = overlayBounds;
                if (!desiredBounds.Intersects(ownedTaskbar.Bounds))
                {
                    var taskbarRelativeBounds = overlayBounds.Offset(
                        -ownedTaskbar.Bounds.Left,
                        -ownedTaskbar.Bounds.Top);
                    if (taskbarRelativeBounds.Intersects(ownedTaskbar.Bounds))
                    {
                        desiredBounds = taskbarRelativeBounds;
                    }
                }

                if (desiredBounds.Intersects(ownedTaskbar.Bounds))
                {
                    targets.Add(new PreviewTarget(
                        ownedTaskbar.Handle,
                        ownedTaskbar.Bounds,
                        desiredBounds));
                }

                continue;
            }

            var intersectingTaskbar = taskbars.FirstOrDefault(taskbar =>
                overlayBounds.Intersects(taskbar.Bounds));
            if (intersectingTaskbar.Handle != nint.Zero)
            {
                targets.Add(new PreviewTarget(
                    intersectingTaskbar.Handle,
                    intersectingTaskbar.Bounds,
                    overlayBounds));
            }
        }

        return targets;
    }

    private static PreviewTarget? FindBestTarget(
        Native.Rect previewBounds,
        IReadOnlyList<PreviewTarget> targets)
    {
        PreviewTarget? best = null;
        long bestScore = long.MaxValue;

        foreach (var target in targets)
        {
            var wrongBounds = target.Bounds.Offset(
                -target.TaskbarBounds.Left,
                -target.TaskbarBounds.Top);
            var positionDistance = Math.Min(
                previewBounds.PositionDistance(target.Bounds),
                previewBounds.PositionDistance(wrongBounds));
            var sizeDistance =
                Math.Abs(previewBounds.Width - target.Bounds.Width) +
                Math.Abs(previewBounds.Height - target.Bounds.Height);
            var score = (sizeDistance * 10_000L) + positionDistance;

            if (score < bestScore)
            {
                best = target;
                bestScore = score;
            }
        }

        // A different widget may have an overlay with a similar position. Only
        // act when the physical preview dimensions identify the slot precisely.
        return best is not null &&
               Math.Abs(previewBounds.Width - best.Value.Bounds.Width) <= 2 &&
               Math.Abs(previewBounds.Height - best.Value.Bounds.Height) <= 2
            ? best
            : null;
    }

    private static IEnumerable<nint> FindOwnPreviewWindows(
        IReadOnlyCollection<nint> windows)
    {
        var processId = Environment.ProcessId;

        return windows.Where(window =>
        {
            Native.GetWindowThreadProcessId(window, out var windowProcessId);
            if (windowProcessId != processId ||
                !string.Equals(
                    Native.ClassName(window),
                    "WinUIDesktopWin32WindowClass",
                    StringComparison.Ordinal))
            {
                return false;
            }

            var style = Native.GetWindowLongPtr(window, GwlStyle).ToInt64();
            if ((style & WsChild) == 0 ||
                !Native.GetWindowRect(window, out var bounds))
            {
                return false;
            }

            return bounds.Width is >= 80 and <= 800 &&
                   bounds.Height is >= 16 and <= 120;
        });
    }

    private static List<TaskbarWindow> FindTaskbars()
    {
        var taskbars = new List<TaskbarWindow>();
        AddTaskbar(Native.FindWindow("Shell_TrayWnd", null), taskbars);

        nint previous = nint.Zero;
        while (true)
        {
            var secondary = Native.FindWindowEx(
                nint.Zero,
                previous,
                "Shell_SecondaryTrayWnd",
                null);
            if (secondary == nint.Zero)
            {
                break;
            }

            AddTaskbar(secondary, taskbars);
            previous = secondary;
        }

        return taskbars;
    }

    private static void AddTaskbar(nint handle, ICollection<TaskbarWindow> taskbars)
    {
        if (handle != nint.Zero && Native.GetWindowRect(handle, out var bounds))
        {
            taskbars.Add(new TaskbarWindow(handle, bounds));
        }
    }

    private static IReadOnlyCollection<nint> EnumerateWindows()
    {
        var windows = new HashSet<nint>();
        Native.EnumWindows((window, _) =>
        {
            windows.Add(window);
            Native.EnumChildWindows(window, (child, _) =>
            {
                windows.Add(child);
                return true;
            }, nint.Zero);
            return true;
        }, nint.Zero);
        return windows;
    }

    private readonly record struct TaskbarWindow(nint Handle, Native.Rect Bounds);

    private readonly record struct PreviewTarget(
        nint Taskbar,
        Native.Rect TaskbarBounds,
        Native.Rect Bounds);

    private static class Native
    {
        internal delegate bool EnumWindowsCallback(nint window, nint parameter);

        [StructLayout(LayoutKind.Sequential)]
        internal readonly record struct Rect(int Left, int Top, int Right, int Bottom)
        {
            internal int Width => Right - Left;
            internal int Height => Bottom - Top;

            internal bool Intersects(Rect other) =>
                Left < other.Right && Right > other.Left &&
                Top < other.Bottom && Bottom > other.Top;

            internal Rect Offset(int x, int y) =>
                new(Left + x, Top + y, Right + x, Bottom + y);

            internal long PositionDistance(Rect other) =>
                Math.Abs((long)Left - other.Left) +
                Math.Abs((long)Top - other.Top);

            internal bool NearlyEquals(Rect other) =>
                Math.Abs(Left - other.Left) <= 2 &&
                Math.Abs(Top - other.Top) <= 2 &&
                Math.Abs(Right - other.Right) <= 2 &&
                Math.Abs(Bottom - other.Bottom) <= 2;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(
            EnumWindowsCallback callback,
            nint parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumChildWindows(
            nint parent,
            EnumWindowsCallback callback,
            nint parameter);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern nint FindWindow(string className, string? windowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern nint FindWindowEx(
            nint parent,
            nint childAfter,
            string className,
            string? windowName);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(nint window, out Rect bounds);

        [DllImport("user32.dll")]
        internal static extern nint GetParent(nint window);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        internal static extern nint GetWindowLongPtr(nint window, int index);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(
            nint window,
            out int processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(
            nint window,
            StringBuilder className,
            int maxCount);

        [DllImport("user32.dll")]
        internal static extern nint SetParent(nint child, nint newParent);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(
            nint window,
            nint insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        internal static string ClassName(nint window)
        {
            var buffer = new StringBuilder(128);
            return GetClassName(window, buffer, buffer.Capacity) > 0
                ? buffer.ToString()
                : string.Empty;
        }
    }
}
