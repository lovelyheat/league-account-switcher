using LeagueAccountSwitcher.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace LeagueAccountSwitcher;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var services = ServiceProviderFactory.Create();
        _window = services.GetRequiredService<MainWindow>();

        // Disposes MainViewModel and ILcuService, which stops background monitoring.
        _window.Closed += (_, _) => services.Dispose();
        _window.Activate();
    }
}
