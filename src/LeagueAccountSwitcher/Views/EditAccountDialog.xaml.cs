using LeagueAccountSwitcher.Helpers;
using LeagueAccountSwitcher.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace LeagueAccountSwitcher.Views;

public sealed partial class EditAccountDialog : ContentDialog
{
    private readonly Func<Task> _save;

    public EditAccountDialog(AccountDetailsViewModel details, Func<Task> save)
    {
        Details = details;
        _save = save;
        InitializeComponent();
    }

    public AccountDetailsViewModel Details { get; }

    private async void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args) =>
        await args.CloseOnSuccessAsync(SaveAsync);

    private async Task<bool> SaveAsync()
    {
        try
        {
            await _save();
            return true;
        }
        catch (Exception ex)
        {
            ErrorBar.Message = $"Couldn't save: {ex.Message}";
            ErrorBar.IsOpen = true;
            return false;
        }
    }
}
