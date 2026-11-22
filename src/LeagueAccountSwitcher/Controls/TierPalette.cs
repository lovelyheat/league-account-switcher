using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace LeagueAccountSwitcher.Controls;

public static class TierPalette
{
    private static readonly AccessibilitySettings Accessibility = new();

    public static Brush TextBrush(string? tier, ElementTheme theme)
    {
        if (Accessibility.HighContrast)
            return (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];

        var (onLight, onDark) = tier switch
        {
            "IRON" => (0x5C5552u, 0xB9AFAAu),
            "BRONZE" => (0x8A4B24u, 0xE0A275u),
            "SILVER" => (0x4F6272u, 0xB8C9D6u),
            "GOLD" => (0x7D5700u, 0xF0C05Au),
            "PLATINUM" => (0x0B6660u, 0x5FD3C8u),
            "EMERALD" => (0x0B6B3Au, 0x4ADE80u),
            "DIAMOND" => (0x2456B8u, 0x8AB4FFu),
            "MASTER" => (0x7B2FB5u, 0xD29BFFu),
            "GRANDMASTER" => (0xB3202Au, 0xFF8F8Fu),
            "CHALLENGER" => (0x05617Au, 0x7DE3F4u),
            _ => (0x5B6472u, 0xA3ADBBu),
        };

        var rgb = theme == ElementTheme.Dark ? onDark : onLight;
        return new SolidColorBrush(ColorHelper.FromArgb(0xFF, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
    }
}
