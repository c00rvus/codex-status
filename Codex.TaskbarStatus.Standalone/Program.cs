using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT;

namespace Codex.TaskbarStatus.Standalone;

public static class Program
{
    private const string MutexName = "Local\\CodexTaskbarStatus.Standalone";
    private const string ExplorerRecoveryArgument = "--recover-explorer";
    private const string OpenSettingsSignalName =
        "Local\\CodexTaskbarStatus.Standalone.OpenSettings";
    private const string OpenFlyoutSignalName =
        "Local\\CodexTaskbarStatus.Standalone.OpenFlyout";
    private static Mutex? _singleInstanceMutex;
    private static EventWaitHandle? _openSettingsSignal;
    private static EventWaitHandle? _openFlyoutSignal;
    private static RegisteredWaitHandle? _openSettingsRegistration;
    private static RegisteredWaitHandle? _openFlyoutRegistration;

    internal enum ActivationRequest
    {
        OpenSettings,
        OpenFlyout,
    }

    internal static event Action<ActivationRequest>? ActivationRequested;

    internal static IReadOnlySet<string> Arguments { get; private set; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    [STAThread]
    public static void Main(string[] args)
    {
        Arguments = new HashSet<string>(args, StringComparer.OrdinalIgnoreCase);
        _singleInstanceMutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            if (Arguments.Contains(ExplorerRecoveryArgument))
            {
                if (!WaitForExplorerRecoveryHandoff())
                {
                    ReleaseSingleInstanceResources();
                    return;
                }
            }
            else if (StartupLaunch.IsStartupLaunch(Arguments))
            {
                ReleaseSingleInstanceResources();
                return;
            }
            else
            {
                SignalExistingInstance();
                ReleaseSingleInstanceResources();
                return;
            }
        }

        InitializeActivationSignals();
        try
        {
            ComWrappersSupport.InitializeComWrappers();
            Application.Start(_ =>
            {
                var context = new DispatcherQueueSynchronizationContext(
                    DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });
        }
        finally
        {
            ReleaseSingleInstanceResources();
        }
    }

    internal static void Relaunch()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new InvalidOperationException("The current executable path is unavailable.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(ExplorerRecoveryArgument);

        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The recovery process could not be started.");
    }

    private static bool WaitForExplorerRecoveryHandoff()
    {
        if (!Arguments.Contains(ExplorerRecoveryArgument) || _singleInstanceMutex is null)
        {
            return false;
        }

        try
        {
            return _singleInstanceMutex.WaitOne(TimeSpan.FromSeconds(15));
        }
        catch (AbandonedMutexException)
        {
            // The predecessor exited before explicitly releasing the mutex. The
            // recovery instance owns it now and can safely continue.
            return true;
        }
    }

    private static void InitializeActivationSignals()
    {
        _openSettingsSignal = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            OpenSettingsSignalName);
        _openFlyoutSignal = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            OpenFlyoutSignalName);
        _openSettingsRegistration = RegisterActivationSignal(
            _openSettingsSignal,
            ActivationRequest.OpenSettings);
        _openFlyoutRegistration = RegisterActivationSignal(
            _openFlyoutSignal,
            ActivationRequest.OpenFlyout);
    }

    private static RegisteredWaitHandle RegisterActivationSignal(
        EventWaitHandle signal,
        ActivationRequest request)
    {
        return ThreadPool.RegisterWaitForSingleObject(
            signal,
            (_, timedOut) =>
            {
                if (!timedOut)
                {
                    ActivationRequested?.Invoke(request);
                }
            },
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    private static void SignalExistingInstance()
    {
        var signalName = Arguments.Contains("--open-flyout")
            ? OpenFlyoutSignalName
            : OpenSettingsSignalName;

        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var signal = EventWaitHandle.OpenExisting(signalName);
                signal.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static void ReleaseSingleInstanceResources()
    {
        _openSettingsRegistration?.Unregister(null);
        _openSettingsRegistration = null;
        _openFlyoutRegistration?.Unregister(null);
        _openFlyoutRegistration = null;
        _openSettingsSignal?.Dispose();
        _openSettingsSignal = null;
        _openFlyoutSignal?.Dispose();
        _openFlyoutSignal = null;

        if (_singleInstanceMutex is null)
        {
            return;
        }

        try
        {
            _singleInstanceMutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // It may already have been released for a controlled relaunch.
        }

        _singleInstanceMutex.Dispose();
        _singleInstanceMutex = null;
    }
}
