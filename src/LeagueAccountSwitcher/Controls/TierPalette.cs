using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace LeagueAccountSwitcher.Controls;

internal sealed record TierLook(uint Light, uint Main, uint Deep, uint TextOnLight, uint TextOnDark, string Symbol);

public static class TierPalette
{
    private static readonly AccessibilitySettings Accessibility = new();

    internal static TierLook Get(string? tier) => tier switch
    {
        "IRON" => new(0xC9C1BD, 0x847A76, 0x4A4240, 0x5C5552, 0xB9AFAA,
            "M24,28 L32,35 L40,28 V33 L32,40 L24,33 Z"),
        "BRONZE" => new(0xF2BC8F, 0xB86F3F, 0x663619, 0x8A4B24, 0xE0A275,
            "M24,25 L32,32 L40,25 V30 L32,37 L24,30 Z M24,34 L32,41 L40,34 V39 L32,46 L24,39 Z"),
        "SILVER" => new(0xEAF3F8, 0x91A7B8, 0x435868, 0x4F6272, 0xB8C9D6,
            "M32,23 L40,34 L32,45 L24,34 Z"),
        "GOLD" => new(0xFFE7A0, 0xDCA42F, 0x7A5208, 0x7D5700, 0xF0C05A,
            "M32,22 L36,30 L43,34 L36,38 L32,46 L28,38 L21,34 L28,30 Z"),
        "PLATINUM" => new(0xA8F5EA, 0x22A99E, 0x0C5752, 0x0B6660, 0x5FD3C8,
            "M32,23 L42,41 H22 Z"),
        "EMERALD" => new(0xA7F7C4, 0x1FBE68, 0x08592F, 0x0B6B3A, 0x4ADE80,
            "M32,22 L41,28 V40 L32,46 L23,40 V28 Z"),
        "DIAMOND" => new(0xC4DEFF, 0x3D7BEA, 0x173C8F, 0x2456B8, 0x8AB4FF,
            "M26,25 H38 L43,31 L32,46 L21,31 Z"),
        "MASTER" => new(0xECC8FF, 0xA24EDB, 0x521C7C, 0x7B2FB5, 0xD29BFF,
            "M32,21 L35,31 L44,34 L35,37 L32,47 L29,37 L20,34 L29,31 Z"),
        "GRANDMASTER" => new(0xFFC1B5, 0xD8343F, 0x6E0F18, 0xB3202A, 0xFF8F8F,
            "M32,20 L37,29 L34,45 L32,48 L30,45 L27,29 Z M21,28 L26,32 L27,41 Z M43,28 L38,32 L37,41 Z"),
        "CHALLENGER" => new(0x8BEEFF, 0xEDBB45, 0x0B4F6B, 0x05617A, 0x7DE3F4,
            "M32,22 L35.2,30.3 L44,31 L37.2,36.2 L39.4,45 L32,40 L24.6,45 L26.8,36.2 L20,31 L28.8,30.3 Z"),
        _ => new(0xB4BDCA, 0x6C7788, 0x3B4452, 0x5B6472, 0xA3ADBB,
            "M25,32 H39 V36 H25 Z"),
    };

    public static Brush TextBrush(string? tier, ElementTheme theme)
    {
        if (Accessibility.HighContrast)
            return (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];

        var look = Get(tier);
        return new SolidColorBrush(ToColor(theme == ElementTheme.Dark ? look.TextOnDark : look.TextOnLight));
    }

    internal static Color ToColor(uint rgb) =>
        ColorHelper.FromArgb(0xFF, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
