using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace LeagueAccountSwitcher.Controls;

public sealed partial class RankEmblemControl : UserControl
{
    public static readonly DependencyProperty TierProperty = DependencyProperty.Register(
        nameof(Tier),
        typeof(string),
        typeof(RankEmblemControl),
        new PropertyMetadata("UNRANKED", (d, _) => ((RankEmblemControl)d).Render()));

    public RankEmblemControl()
    {
        InitializeComponent();
        Render();
    }

    public string Tier
    {
        get => (string)GetValue(TierProperty);
        set => SetValue(TierProperty, value);
    }

    private void Render()
    {
        var look = TierPalette.Get(Tier);
        var light = new SolidColorBrush(TierPalette.ToColor(look.Light));
        var main = new SolidColorBrush(TierPalette.ToColor(look.Main));
        var deep = new SolidColorBrush(TierPalette.ToColor(look.Deep));

        (Shield.Fill, Shield.Stroke, ShieldFacet.Fill) = (main, light, deep);
        (Field.Fill, Field.Stroke) = (deep, main);
        (SmallWings.Fill, SmallWings.Stroke) = (main, deep);
        (LargeWings.Fill, LargeWings.Stroke) = (main, deep);
        (Crown.Fill, Crown.Stroke) = (main, light);
        Rays.Fill = light;
        Sigil.Fill = light;

        // A Geometry can't be shared between elements, so parse one per emblem.
        Sigil.Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), look.Symbol);

        Show(SmallWings, Tier is "SILVER" or "GOLD");
        Show(LargeWings, Tier is "PLATINUM" or "EMERALD" or "DIAMOND" or "MASTER" or "GRANDMASTER" or "CHALLENGER");
        Show(Crown, Tier is "MASTER" or "GRANDMASTER" or "CHALLENGER");
        Show(Rays, Tier is "CHALLENGER");
    }

    private static void Show(UIElement part, bool visible) =>
        part.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
}
