using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace LeagueAccountSwitcher.Helpers;

public static class Ui
{
    public static bool Not(bool value) => !value;

    public static bool HasText(string? value) => !string.IsNullOrEmpty(value);

    public static Visibility VisibleIfText(string? value) => string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

    public static ImageSource? Avatar(string path) =>
        Uri.TryCreate(path, UriKind.Absolute, out var uri) ? new BitmapImage(uri) { DecodePixelWidth = 96 } : null;

    public static Style ActionStyle(bool isSubtle) =>
        (Style)Application.Current.Resources[isSubtle ? "DefaultButtonStyle" : "AccentButtonStyle"];

    public static async Task CloseOnSuccessAsync(this ContentDialogButtonClickEventArgs args, Func<Task<bool>> action)
    {
        var deferral = args.GetDeferral();
        try { args.Cancel = !await action(); }
        finally { deferral.Complete(); }
    }
}
