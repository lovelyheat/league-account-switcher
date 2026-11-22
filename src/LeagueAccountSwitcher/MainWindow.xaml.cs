using System.Runtime.InteropServices;
using LeagueAccountSwitcher.Core.Models;
using LeagueAccountSwitcher.Services;
using LeagueAccountSwitcher.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;

namespace LeagueAccountSwitcher;

public sealed partial class MainWindow : Window
{
    private const int DefaultWidth = 820, DefaultHeight = 640, MinWidth = 640, MinHeight = 480;
    private const int SwRestore = 9;

    private readonly AccountCoordinator _coordinator;
    private readonly TrayIconService _tray;
    private double _scale;
    private bool _initialized;
    private bool _forceClose;

    public MainWindow(MainViewModel viewModel, AccountCoordinator coordinator, DialogService dialogs)
    {
        ViewModel = viewModel;
        _coordinator = coordinator;
        InitializeComponent();

        Title = "League Account Switcher";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        dialogs.Attach(RootGrid);

        var icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(icoPath))
            AppWindow.SetIcon(icoPath);

        var pngPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png");
        if (File.Exists(pngPath))
            TitleBarIcon.Source = new BitmapImage(new Uri(pngPath));

        _scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        SetMinimumSize();
        CenterAtDefaultSize();
        ApplyTheme();

        _tray = new TrayIconService(
            DispatcherQueue,
            icoPath,
            onOpen: RestoreFromTray,
            onSwitchAccount: SwitchFromTray,
            onExit: RequestExit,
            getAccounts: () => _coordinator.Accounts,
            getActiveAccountId: () => _coordinator.Settings.ActiveAccountId,
            canSwitch: () => ViewModel.CanInteract);

        _coordinator.Changed += OnCoordinatorChanged;

        AppWindow.Closing += (_, args) =>
        {
            if (_coordinator.IsOperating)
            {
                args.Cancel = true;
                RestoreFromTray();
                ViewModel.ShowWarning("Wait for the account switch to finish before closing.");
                return;
            }

            if (!_forceClose && _coordinator.Settings.MinimizeToTrayOnClose)
            {
                args.Cancel = true;
                AppWindow.Hide();
            }
        };

        Closed += (_, _) =>
        {
            _coordinator.Changed -= OnCoordinatorChanged;
            _tray.Dispose();
        };

        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Theme))
                ApplyTheme();
        };
    }

    public MainViewModel ViewModel { get; }

    public void RestoreFromTray()
    {
        AppWindow.Show();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (IsIconic(hwnd))
            ShowWindow(hwnd, SwRestore);
        SetForegroundWindow(hwnd);
        Activate();
    }

    private void RequestExit()
    {
        if (_coordinator.IsOperating)
        {
            RestoreFromTray();
            ViewModel.ShowWarning("Wait for the account switch to finish before closing.");
            return;
        }

        _forceClose = true;
        Close();
    }

    private async void SwitchFromTray(AccountRecord account)
    {
        RestoreFromTray();
        await ViewModel.SwitchAccountAsync(account);
    }

    private void OnCoordinatorChanged(object? sender, EventArgs e)
    {
        var active = _coordinator.Active;
        var tip = active is not null
            ? $"League Account Switcher — {active.DisplayName}"
            : "League Account Switcher";
        _tray.UpdateTooltip(tip);
    }

    private async void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
            return;

        _initialized = true;
        Content.XamlRoot.Changed += (root, _) =>
        {
            if (root.RasterizationScale == _scale)
                return;

            _scale = root.RasterizationScale;
            SetMinimumSize();
        };

        await ViewModel.InitializeAsync();
    }

    private void ApplyTheme()
    {
        RootGrid.RequestedTheme = ViewModel.Theme;

        AppWindow.TitleBar.PreferredTheme = ViewModel.Theme switch
        {
            ElementTheme.Dark => TitleBarTheme.Dark,
            ElementTheme.Light => TitleBarTheme.Light,
            _ => TitleBarTheme.UseDefaultAppMode,
        };
    }

    private void SetMinimumSize()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter)
            return;

        presenter.PreferredMinimumWidth = (int)(MinWidth * _scale);
        presenter.PreferredMinimumHeight = (int)(MinHeight * _scale);
    }

    private void CenterAtDefaultSize()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min((int)(DefaultWidth * _scale), area.Width);
        var height = Math.Min((int)(DefaultHeight * _scale), area.Height);

        AppWindow.MoveAndResize(new RectInt32(
            area.X + ((area.Width - width) / 2),
            area.Y + ((area.Height - height) / 2),
            width,
            height));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

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
