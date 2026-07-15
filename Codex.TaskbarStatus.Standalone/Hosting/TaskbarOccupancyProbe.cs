using Codex.TaskbarStatus.Core;
using System.Runtime.InteropServices;

namespace Codex.TaskbarStatus.Standalone.Hosting;

internal sealed record TaskbarOccupancySnapshot(
    nint Taskbar,
    NativeMethods.Rect Bounds,
    IReadOnlyList<PixelInterval> OccupiedIntervals);

/// <summary>
/// Reads Windows taskbar button bounds through the native MSAA bridge. This
/// deliberately avoids the managed WPF UI Automation client because loading
/// that framework into the WinUI host destabilizes unpackaged WinUI windows.
/// </summary>
internal sealed class TaskbarOccupancyProbe
{
    private const int ObjectIdClient = -4;
    private const int ChildIdSelf = 0;
    private const int RoleSystemPushButton = 43;
    private const int MaximumDepth = 14;
    private const int MaximumChildrenPerNode = 512;
    private const int MaximumNodesPerScan = 2_048;
    private const int StateSystemInvisible = 0x00008000;
    private const int StateSystemOffscreen = 0x00010000;

    private static readonly Guid AccessibleInterfaceId =
        new("618736E0-3C3D-11CF-810C-00AA00389B71");
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

    private readonly object _syncRoot = new();
    private TaskbarOccupancySnapshot? _snapshot;
    private DateTimeOffset _nextRefreshAt;
    private bool _refreshing;
    private bool _failureLogged;

    internal TaskbarOccupancySnapshot? GetSnapshot(
        nint taskbar,
        NativeMethods.Rect taskbarBounds)
    {
        var now = DateTimeOffset.UtcNow;
        var shouldRefresh = false;
        lock (_syncRoot)
        {
            if (!_refreshing &&
                (now >= _nextRefreshAt ||
                 _snapshot is null ||
                 _snapshot.Taskbar != taskbar ||
                 _snapshot.Bounds != taskbarBounds))
            {
                _refreshing = true;
                _nextRefreshAt = now + RefreshInterval;
                shouldRefresh = true;
            }

            if (!shouldRefresh)
            {
                return IsCurrent(_snapshot, taskbar, taskbarBounds) ? _snapshot : null;
            }
        }

        _ = Task.Run(() => RefreshSnapshot(taskbar, taskbarBounds));
        lock (_syncRoot)
        {
            return IsCurrent(_snapshot, taskbar, taskbarBounds) ? _snapshot : null;
        }
    }

    private void RefreshSnapshot(nint taskbar, NativeMethods.Rect taskbarBounds)
    {
        try
        {
            var intervals = ReadOccupiedIntervals(taskbar, taskbarBounds);
            lock (_syncRoot)
            {
                _snapshot = new TaskbarOccupancySnapshot(
                    taskbar,
                    taskbarBounds,
                    intervals);
                _failureLogged = false;
            }
        }
        catch (Exception exception)
        {
            var shouldLog = false;
            lock (_syncRoot)
            {
                if (!_failureLogged)
                {
                    _failureLogged = true;
                    shouldLog = true;
                }
            }

            if (shouldLog)
            {
                StandaloneLog.Write("Taskbar occupancy scan failed", exception);
            }
        }
        finally
        {
            lock (_syncRoot)
            {
                _refreshing = false;
            }
        }
    }

    private static IReadOnlyList<PixelInterval> ReadOccupiedIntervals(
        nint taskbar,
        NativeMethods.Rect taskbarBounds)
    {
        var interfaceId = AccessibleInterfaceId;
        var result = AccessibleObjectFromWindow(
            taskbar,
            ObjectIdClient,
            ref interfaceId,
            out var accessiblePointer);
        if (result < 0 || accessiblePointer == nint.Zero)
        {
            Marshal.ThrowExceptionForHR(result);
            return Array.Empty<PixelInterval>();
        }

        try
        {
            var root = (IAccessibleNative)Marshal.GetTypedObjectForIUnknown(
                accessiblePointer,
                typeof(IAccessibleNative));
            var intervals = new List<PixelInterval>();
            var remainingNodes = MaximumNodesPerScan;
            WalkAccessibleTree(
                root,
                depth: 0,
                taskbarBounds,
                intervals,
                ref remainingNodes);

            return TaskbarPlacementResolver.MergeIntervals(
                intervals,
                taskbarBounds.Left,
                taskbarBounds.Right);
        }
        finally
        {
            Marshal.Release(accessiblePointer);
        }
    }

    private static void WalkAccessibleTree(
        IAccessibleNative accessible,
        int depth,
        NativeMethods.Rect taskbarBounds,
        ICollection<PixelInterval> intervals,
        ref int remainingNodes)
    {
        if (depth > MaximumDepth || remainingNodes-- <= 0)
        {
            return;
        }

        if (IsOwnedByCurrentProcess(accessible))
        {
            return;
        }

        TryAddCandidate(accessible, ChildIdSelf, taskbarBounds, intervals);

        int childCount;
        try
        {
            childCount = accessible.accChildCount;
        }
        catch (COMException)
        {
            return;
        }
        catch (InvalidCastException)
        {
            return;
        }

        if (childCount <= 0 || childCount > MaximumChildrenPerNode)
        {
            return;
        }

        var children = new object[childCount];
        int obtained;
        try
        {
            var result = AccessibleChildren(
                accessible,
                0,
                childCount,
                children,
                out obtained);
            if (result < 0)
            {
                return;
            }
        }
        catch (COMException)
        {
            return;
        }

        for (var index = 0; index < Math.Min(obtained, children.Length); index++)
        {
            if (remainingNodes <= 0)
            {
                return;
            }

            var child = children[index];
            if (child is int childId)
            {
                remainingNodes--;
                TryAddCandidate(accessible, childId, taskbarBounds, intervals);
                continue;
            }

            if (child is null)
            {
                continue;
            }

            try
            {
                WalkAccessibleTree(
                    AsAccessible(child),
                    depth + 1,
                    taskbarBounds,
                    intervals,
                    ref remainingNodes);
            }
            catch (COMException)
            {
                // The taskbar can rebuild an accessibility peer mid-scan.
            }
            catch (InvalidCastException)
            {
                // Ignore non-accessible VARIANT children from third-party shells.
            }
        }
    }

    private static IAccessibleNative AsAccessible(object value)
    {
        if (value is IAccessibleNative accessible)
        {
            return accessible;
        }

        var unknown = Marshal.GetIUnknownForObject(value);
        try
        {
            return (IAccessibleNative)Marshal.GetTypedObjectForIUnknown(
                unknown,
                typeof(IAccessibleNative));
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    private static void TryAddCandidate(
        IAccessibleNative accessible,
        object child,
        NativeMethods.Rect taskbarBounds,
        ICollection<PixelInterval> intervals)
    {
        try
        {
            var role = Convert.ToInt32(accessible.get_accRole(child));
            if (role != RoleSystemPushButton)
            {
                return;
            }

            var state = Convert.ToInt32(accessible.get_accState(child));
            if ((state & (StateSystemInvisible | StateSystemOffscreen)) != 0)
            {
                return;
            }

            accessible.accLocation(
                out var left,
                out var top,
                out var width,
                out var height,
                child);
            if (width <= 0 || height <= 0)
            {
                return;
            }

            var right = (long)left + width;
            var bottom = (long)top + height;
            var verticalIntersection =
                Math.Min((long)taskbarBounds.Bottom, bottom) -
                Math.Max((long)taskbarBounds.Top, top);
            if (verticalIntersection < Math.Min(8, taskbarBounds.Height / 3d))
            {
                return;
            }

            var clippedLeft = Math.Max(taskbarBounds.Left, left);
            var clippedRight = Math.Min((long)taskbarBounds.Right, right);
            if (clippedRight > clippedLeft)
            {
                intervals.Add(new PixelInterval(
                    clippedLeft,
                    checked((int)clippedRight)));
            }
        }
        catch (COMException)
        {
            // Individual peers may disappear while Explorer updates the bar.
        }
        catch (InvalidCastException)
        {
            // Providers are allowed to expose a string role/state instead.
        }
        catch (FormatException)
        {
            // Treat malformed third-party provider data as non-interactive.
        }
        catch (OverflowException)
        {
            // Ignore invalid provider geometry.
        }
    }

    private static bool IsOwnedByCurrentProcess(IAccessibleNative accessible)
    {
        try
        {
            if (WindowFromAccessibleObject(accessible, out var window) < 0 ||
                window == nint.Zero)
            {
                return false;
            }

            _ = GetWindowThreadProcessId(window, out var processId);
            return processId == Environment.ProcessId;
        }
        catch (COMException)
        {
            return false;
        }
    }

    private static bool IsCurrent(
        TaskbarOccupancySnapshot? snapshot,
        nint taskbar,
        NativeMethods.Rect bounds) =>
        snapshot is not null &&
        snapshot.Taskbar == taskbar &&
        snapshot.Bounds == bounds;

    [ComImport]
    [Guid("618736E0-3C3D-11CF-810C-00AA00389B71")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IAccessibleNative
    {
        [DispId(-5000)]
        object accParent
        {
            [return: MarshalAs(UnmanagedType.IDispatch)]
            get;
        }

        [DispId(-5001)]
        int accChildCount { get; }

        [DispId(-5002)]
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object get_accChild([In, MarshalAs(UnmanagedType.Struct)] object varChild);

        [DispId(-5003)]
        [return: MarshalAs(UnmanagedType.BStr)]
        string get_accName([In, Optional, MarshalAs(UnmanagedType.Struct)] object varChild);

        [DispId(-5004)]
        [return: MarshalAs(UnmanagedType.BStr)]
        string get_accValue([In, Optional, MarshalAs(UnmanagedType.Struct)] object varChild);

        [DispId(-5005)]
        [return: MarshalAs(UnmanagedType.BStr)]
        string get_accDescription([In, Optional, MarshalAs(UnmanagedType.Struct)] object varChild);

        [DispId(-5006)]
        [return: MarshalAs(UnmanagedType.Struct)]
        object get_accRole([In, Optional, MarshalAs(UnmanagedType.Struct)] object varChild);

        [DispId(-5007)]
        [return: MarshalAs(UnmanagedType.Struct)]
        object get_accState([In, Optional, MarshalAs(UnmanagedType.Struct)] object varChild);

        [DispId(-5008)]
        [return: MarshalAs(UnmanagedType.BStr)]
        string get_accHelp([In, Optional, MarshalAs(UnmanagedType.Struct)] object varChild);

        [DispId(-5009)]
        int get_accHelpTopic(
            [Out, MarshalAs(UnmanagedType.BStr)] out string helpFile,
            [In, Optional, MarshalAs(UnmanagedType.Struct)] object varChild);

        [DispId(-5010)]
        [return: MarshalAs(UnmanagedType.BStr)]
        string get_accKeyboardShortcut(
            [In, Optional, MarshalAs(UnmanagedType.Struct)] object varChild);

        [DispId(-5011)]
        object accFocus
        {
            [return: MarshalAs(UnmanagedType.Struct)]
            get;
        }

        [DispId(-5012)]
        object accSelection
        {
            [return: MarshalAs(UnmanagedType.Struct)]
            get;
        }

        [DispId(-5013)]
        [return: MarshalAs(UnmanagedType.BStr)]
        string get_accDefaultAction(
            [In, Optional, MarshalAs(UnmanagedType.Struct)] object varChild);

        [DispId(-5014)]
        void accSelect(
            [In] int flagsSelect,
            [In, Optional, MarshalAs(UnmanagedType.Struct)] object varChild);

        [DispId(-5015)]
        void accLocation(
            [Out] out int left,
            [Out] out int top,
            [Out] out int width,
            [Out] out int height,
            [In, Optional, MarshalAs(UnmanagedType.Struct)] object varChild);

        [DispId(-5016)]
        [return: MarshalAs(UnmanagedType.Struct)]
        object accNavigate(
            [In] int navigationDirection,
            [In, Optional, MarshalAs(UnmanagedType.Struct)] object start);

        [DispId(-5017)]
        [return: MarshalAs(UnmanagedType.Struct)]
        object accHitTest([In] int left, [In] int top);

        [DispId(-5018)]
        void accDoDefaultAction(
            [In, Optional, MarshalAs(UnmanagedType.Struct)] object varChild);

        [DispId(-5003)]
        void set_accName(
            [In, Optional, MarshalAs(UnmanagedType.Struct)] object varChild,
            [In, MarshalAs(UnmanagedType.BStr)] string name);

        [DispId(-5004)]
        void set_accValue(
            [In, Optional, MarshalAs(UnmanagedType.Struct)] object varChild,
            [In, MarshalAs(UnmanagedType.BStr)] string value);
    }

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(
        nint window,
        int objectId,
        ref Guid interfaceId,
        out nint accessible);

    [DllImport("oleacc.dll")]
    private static extern int AccessibleChildren(
        [MarshalAs(UnmanagedType.Interface)] IAccessibleNative container,
        int childStart,
        int childCount,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] object[] children,
        out int obtained);

    [DllImport("oleacc.dll")]
    private static extern int WindowFromAccessibleObject(
        [MarshalAs(UnmanagedType.Interface)] IAccessibleNative accessible,
        out nint window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        nint window,
        out int processId);
}
