using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;
using Windows.UI.Composition;

namespace Codex.TaskbarStatus.Standalone.Hosting;

// A WinUI top-level window otherwise paints an opaque composition surface.
// Supplying a fully transparent system-backdrop brush lets the taskbar remain
// visible behind the live XAML preview after the window becomes a child HWND.
internal sealed class TransparentBackdrop : SystemBackdrop
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        internal int Size;
        internal int ThreadType;
        internal int ApartmentType;
    }

    private static readonly object Sync = new();
    private static Windows.UI.Composition.Compositor? _compositor;
    private static nint _dispatcherQueueController;
    private Windows.UI.Composition.CompositionColorBrush? _brush;

    protected override void OnTargetConnected(
        Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop connectedTarget,
        XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        _brush = GetCompositor().CreateColorBrush(Color.FromArgb(0, 0, 0, 0));
        connectedTarget.SystemBackdrop = _brush;
    }

    protected override void OnTargetDisconnected(
        Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        var systemBackdrop = disconnectedTarget.SystemBackdrop;
        disconnectedTarget.SystemBackdrop = null;
        systemBackdrop?.Dispose();
        _brush?.Dispose();
        _brush = null;
    }

    private static Windows.UI.Composition.Compositor GetCompositor()
    {
        if (_compositor is not null)
        {
            return _compositor;
        }

        lock (Sync)
        {
            if (_compositor is null)
            {
                EnsureDispatcherQueue();
                _compositor = new Windows.UI.Composition.Compositor();
            }
        }

        return _compositor;
    }

    private static void EnsureDispatcherQueue()
    {
        if (Windows.System.DispatcherQueue.GetForCurrentThread() is not null ||
            _dispatcherQueueController != nint.Zero)
        {
            return;
        }

        var options = new DispatcherQueueOptions
        {
            Size = Marshal.SizeOf<DispatcherQueueOptions>(),
            ThreadType = 2,
            ApartmentType = 2,
        };
        CreateDispatcherQueueController(options, out _dispatcherQueueController);
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(
        DispatcherQueueOptions options,
        out nint dispatcherQueueController);
}
