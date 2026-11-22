using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LeagueAccountSwitcher.Services;

public sealed class DialogService
{
    private FrameworkElement? _host;
    private bool _isOpen;

    public void Attach(FrameworkElement host) => _host = host;

    /// <summary>WinUI throws on a second open dialog, so a concurrent request reports as cancelled.</summary>
    public async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        if (_isOpen || _host?.XamlRoot is not { } xamlRoot)
            return ContentDialogResult.None;

        _isOpen = true;
        try
        {
            dialog.XamlRoot = xamlRoot;
            // Dialogs live in the popup layer and don't inherit the window's theme.
            dialog.RequestedTheme = _host.RequestedTheme;
            return await dialog.ShowAsync();
        }
        finally
        {
            _isOpen = false;
        }
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText) =>
        await ShowAsync(new ContentDialog
        {
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = confirmText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        }) == ContentDialogResult.Primary;
}
