using System.Configuration;
using System.Data;
using System.Windows;

namespace SnapGlyph.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        StartupRegistration.MigrateLegacyRegistration();
        ForegroundTracker.Initialize();
        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        if (e.Args.Any(argument => string.Equals(
                argument,
                "--autostart",
                StringComparison.OrdinalIgnoreCase)))
        {
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.Opacity = 0;
            window.Show();
            window.Hide();
            window.Opacity = 1;
            return;
        }

        window.Show();
    }
}
