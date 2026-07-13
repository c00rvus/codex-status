using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WidBar.SDK;
using WidBar.SDK.Hosting;

namespace Codex.TaskbarStatus.ExtensionApp;

// Entry point of the plugin process. The SDK base class takes care of talking
// to WidBar, rendering the taskbar preview and hosting the flyout/settings
// windows. All we do here is hand it our plugin (one instance per active widget).
public partial class App : WidgetHostApplication
{
    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // WidBar.SDK 1.2.0 looks this up directly on Application.Resources.
        // WinUI keeps the stock value inside a theme dictionary, which that
        // lookup cannot reach. Add a direct resource once the XAML runtime is
        // fully initialized, before the SDK starts accepting host messages.
        var settingsHostResources = new ResourceDictionary();
        settingsHostResources.Add(
            "LayerFillColorDefaultBrush",
            new SolidColorBrush(ColorHelper.FromArgb(0x4C, 0x3A, 0x3A, 0x3A)));
        Resources.MergedDictionaries.Add(settingsHostResources);

        base.OnLaunched(args);
    }

    protected override IWidgetPlugin CreatePlugin() => new MainPlugin();
}
