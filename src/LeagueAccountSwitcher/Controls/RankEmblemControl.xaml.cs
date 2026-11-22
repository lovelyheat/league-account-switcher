using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace LeagueAccountSwitcher.Controls;

public sealed partial class RankEmblemControl : UserControl
{
    private static readonly Dictionary<string, BitmapImage> Cache = new(StringComparer.OrdinalIgnoreCase);

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
        var fileName = NormalizeFileName(Tier);
        if (!Cache.TryGetValue(fileName, out var image))
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Ranks", $"{fileName}.png");
            if (!File.Exists(path))
            {
                EmblemImage.Source = null;
                return;
            }

            image = new BitmapImage(new Uri(path));
            Cache[fileName] = image;
        }

        EmblemImage.Source = image;
    }

    private static string NormalizeFileName(string? tier) =>
        tier?.Trim().ToUpperInvariant() switch
        {
            "IRON" => "iron",
            "BRONZE" => "bronze",
            "SILVER" => "silver",
            "GOLD" => "gold",
            "PLATINUM" => "platinum",
            "EMERALD" => "emerald",
            "DIAMOND" => "diamond",
            "MASTER" => "master",
            "GRANDMASTER" => "grandmaster",
            "CHALLENGER" => "challenger",
            _ => "unranked",
        };
}
