using LeagueAccountSwitcher.Helpers;
using LeagueAccountSwitcher.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace LeagueAccountSwitcher.Views;

public sealed partial class SettingsDialog : ContentDialog
{
    public SettingsDialog(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; }

    private async void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args) => await ViewModel.DetectPathsAsync();

    private async void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args) =>
        await args.CloseOnSuccessAsync(ViewModel.SaveAsync);
}
