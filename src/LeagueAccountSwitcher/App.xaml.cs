using LeagueAccountSwitcher.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace LeagueAccountSwitcher;

public partial class App : Application
{
    private const string InstanceKey = "LeagueAccountSwitcher.Main";
    private const string SingleInstanceMutexName = @"Local\LeagueAccountSwitcher.SingleInstance.v1";

    private MainWindow? _window;
    private AppInstance? _instance;
    private Mutex? _singleInstanceMutex;

    public App()
    {
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        Mutex mutex;
        bool createdNew;
        try
        {
            mutex = new Mutex(initiallyOwned: true, name: SingleInstanceMutexName, out createdNew);
        }
        catch (UnauthorizedAccessException)
        {
            Exit();
            return;
        }

        if (!createdNew)
        {
            mutex.Dispose();
            try
            {
                var existing = AppInstance.GetInstances()
                    .FirstOrDefault(i => i.Key == InstanceKey && !i.IsCurrent);
                if (existing is not null)
                    await existing.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
            }
            catch
            {
            }

            Exit();
            return;
        }

        _singleInstanceMutex = mutex;

        var instance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!instance.IsCurrent)
        {
            await instance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
            ReleaseMutex();
            Exit();
            return;
        }

        _instance = instance;
        _instance.Activated += OnInstanceActivated;

        var services = ServiceProviderFactory.Create();
        _window = services.GetRequiredService<MainWindow>();

        _window.Closed += (_, _) =>
        {
            if (_instance is not null)
                _instance.Activated -= OnInstanceActivated;
            services.Dispose();
            ReleaseMutex();
        };
        _window.Activate();
    }

    private void ReleaseMutex()
    {
        if (_singleInstanceMutex is null)
            return;

        try { _singleInstanceMutex.ReleaseMutex(); }
        catch (ApplicationException) { }
        _singleInstanceMutex.Dispose();
        _singleInstanceMutex = null;
    }

    private void OnInstanceActivated(object? sender, AppActivationArguments e)
    {
        _window?.DispatcherQueue.TryEnqueue(() => _window?.RestoreFromTray());
    }
}
