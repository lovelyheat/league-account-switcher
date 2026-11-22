using System.Runtime.InteropServices;
using LeagueAccountSwitcher.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace LeagueAccountSwitcher;

public partial class App : Application
{
    private const string InstanceKey = "LeagueAccountSwitcher.Main";

    private MainWindow? _window;
    private AppInstance? _instance;

    public App()
    {
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Prevent multiple instances from overwriting each other's accounts.json or racing Riot switches.
        var instance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!instance.IsCurrent)
        {
            var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
            await instance.RedirectActivationToAsync(activationArgs);
            Exit();
            return;
        }

        _instance = instance;
        _instance.Activated += OnInstanceActivated;

        var services = ServiceProviderFactory.Create();
        _window = services.GetRequiredService<MainWindow>();

        // Disposes MainViewModel and ILcuService, which stops background monitoring.
        _window.Closed += (_, _) =>
        {
            if (_instance is not null)
                _instance.Activated -= OnInstanceActivated;
            services.Dispose();
        };
        _window.Activate();
    }

    private void OnInstanceActivated(object? sender, AppActivationArguments e)
    {
        _window?.DispatcherQueue.TryEnqueue(() =>
        {
            if (_window is null)
                return;

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            if (IsIconic(hwnd))
                ShowWindow(hwnd, SwRestore);
            SetForegroundWindow(hwnd);
            _window.Activate();
        });
    }

    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
