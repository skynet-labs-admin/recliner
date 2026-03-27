using System.Windows;
using Recliner.Services;

namespace Recliner;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Apply theme before any window loads so StaticResource references
        // in XAML resolve to the correct themed brushes.
        var settings = SettingsService.Load();
        ThemeService.Apply(settings.Theme);
    }
}
