using LeagueAccountSwitcher.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LeagueAccountSwitcher.Controls;

public sealed partial class AccountDetailsEditor : UserControl
{
    public static readonly DependencyProperty DetailsProperty = DependencyProperty.Register(
        nameof(Details), typeof(AccountDetailsViewModel), typeof(AccountDetailsEditor), new PropertyMetadata(null));

    public AccountDetailsEditor() => InitializeComponent();

    public AccountDetailsViewModel Details
    {
        get => (AccountDetailsViewModel)GetValue(DetailsProperty);
        set => SetValue(DetailsProperty, value);
    }
}
