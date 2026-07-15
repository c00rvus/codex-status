using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Codex.TaskbarStatus.Standalone.Hosting;

/// <summary>
/// Observes mouse button presses system-wide while the details flyout is
/// visible. A low-level mouse hook is used because taskbar and desktop windows
/// frequently do not activate, so WinUI deactivation alone cannot reliably
/// detect a click outside the flyout.
/// </summary>
internal sealed class OutsideClickMonitor : IDisposable
{
    private const int WhMouseLowLevel = 14;
    private const int ErrorInvalidHookHandle = 1404;
    private const uint WmLeftButtonDown = 0x0201;
    private const uint WmRightButtonDown = 0x0204;
    private const uint WmMiddleButtonDown = 0x0207;
    private const uint WmXButtonDown = 0x020B;

    private readonly Action<NativeMethods.Point> _onMouseButtonDown;
    private readonly LowLevelMouseProcedure _procedure;
    private nint _hook;
    private bool _disposed;

    internal OutsideClickMonitor(Action<NativeMethods.Point> onMouseButtonDown)
    {
        _onMouseButtonDown = onMouseButtonDown ??
            throw new ArgumentNullException(nameof(onMouseButtonDown));
        _procedure = HookProcedure;
    }

    internal bool IsRunning => _hook != nint.Zero;

    internal void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_hook != nint.Zero)
        {
            return;
        }

        _hook = SetWindowsHookEx(
            WhMouseLowLevel,
            _procedure,
            GetModuleHandle(null),
            0);
        if (_hook == nint.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not start the outside-click monitor.");
        }
    }

    internal void Stop()
    {
        if (_hook == nint.Zero)
        {
            return;
        }

        var hook = _hook;
        if (UnhookWindowsHookEx(hook))
        {
            _hook = nint.Zero;
            return;
        }

        var error = Marshal.GetLastWin32Error();
        if (error == ErrorInvalidHookHandle)
        {
            _hook = nint.Zero;
        }

        StandaloneLog.Write(
            "Outside-click monitor cleanup failed",
            new Win32Exception(error));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private nint HookProcedure(int code, nint message, nint data)
    {
        if (code >= 0 && IsMouseButtonDown(unchecked((uint)(long)message)))
        {
            try
            {
                var hookData = Marshal.PtrToStructure<LowLevelMouseHookData>(data);
                _onMouseButtonDown(hookData.Position);
            }
            catch (Exception exception)
            {
                StandaloneLog.Write("Outside-click monitor callback failed", exception);
            }
        }

        return CallNextHookEx(_hook, code, message, data);
    }

    private static bool IsMouseButtonDown(uint message) =>
        message is WmLeftButtonDown or
            WmRightButtonDown or
            WmMiddleButtonDown or
            WmXButtonDown;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint LowLevelMouseProcedure(
        int code,
        nint message,
        nint data);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct LowLevelMouseHookData
    {
        internal readonly NativeMethods.Point Position;
        internal readonly uint MouseData;
        internal readonly uint Flags;
        internal readonly uint Time;
        internal readonly nuint ExtraInfo;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(
        int hook,
        LowLevelMouseProcedure procedure,
        nint module,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(
        nint hook,
        int code,
        nint message,
        nint data);
}
