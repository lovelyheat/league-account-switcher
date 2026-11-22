using LeagueAccountSwitcher.Helpers;
using LeagueAccountSwitcher.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace LeagueAccountSwitcher.Views;

public sealed partial class AddAccountDialog : ContentDialog
{
    public AddAccountDialog(AddAccountViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public AddAccountViewModel ViewModel { get; }

    private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args) => ViewModel.DetectCommand.Execute(null);

    private void OnModeChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) =>
        ViewModel.IsSignInMode = sender.SelectedItem == SignInTab;

    private async void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args) =>
        await args.CloseOnSuccessAsync(ViewModel.SaveAsync);

    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args) =>
        args.Cancel |= ViewModel.IsBusy;
}
