using LeagueAccountSwitcher.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LeagueAccountSwitcher.Controls;

public sealed partial class AccountCard : UserControl
{
    public static readonly DependencyProperty AccountProperty = DependencyProperty.Register(
        nameof(Account), typeof(AccountItemViewModel), typeof(AccountCard), new PropertyMetadata(null));

    public AccountCard()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => Bindings.Update();
    }

    public AccountItemViewModel Account
    {
        get => (AccountItemViewModel)GetValue(AccountProperty);
        set => SetValue(AccountProperty, value);
    }
}
