using Microsoft.UI.Xaml;

namespace Codex.TaskbarStatus.Standalone;

public partial class App : Application
{
    private StandaloneHost? _host;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            StandaloneLog.Write("Unhandled WinUI exception", args.Exception);
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _host = new StandaloneHost(
                openFlyoutOnStart: Program.Arguments.Contains("--open-flyout"),
                openSettingsOnStart: Program.Arguments.Contains("--open-settings"));
            await _host.StartAsync();
        }
        catch (Exception exception)
        {
            StandaloneLog.Write("Standalone host startup failed", exception);
            Exit();
        }
    }
}
