using System.Runtime.InteropServices;
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
        // Enforce single instance across ALL executable paths (e.g., Debug vs Release or copied folders) per Windows user session.
        var mutex = new Mutex(initiallyOwned: true, name: SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            try
            {
                var existing = AppInstance.FindOrRegisterForKey(InstanceKey);
                if (!existing.IsCurrent)
                    await existing.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
            }
            catch
            {
                // Best-effort focus handoff when launched from a different executable path.
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

        // Disposes MainViewModel and ILcuService, which stops background monitoring.
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
