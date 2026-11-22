using System.Runtime.InteropServices;
using LeagueAccountSwitcher.Services;
using LeagueAccountSwitcher.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace LeagueAccountSwitcher;

public sealed partial class MainWindow : Window
{
    // Effective pixels; AppWindow works in physical ones.
    private const int DefaultWidth = 820, DefaultHeight = 640, MinWidth = 640, MinHeight = 480;

    private double _scale;
    private bool _initialized;

    public MainWindow(MainViewModel viewModel, DialogService dialogs)
    {
        ViewModel = viewModel;
        InitializeComponent();

        Title = "League Account Switcher";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        dialogs.Attach(RootGrid);

        _scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        SetMinimumSize();
        CenterAtDefaultSize();

        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Theme))
                ApplyTheme();
        };
    }

    public MainViewModel ViewModel { get; }

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

        // Caption buttons don't follow the content's theme.
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
}
