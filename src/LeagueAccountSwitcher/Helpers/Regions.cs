using LeagueAccountSwitcher.Core.Models;

namespace LeagueAccountSwitcher.Helpers;

public static class Regions
{
    public static string? Known(string? raw)
    {
        var region = LeagueRegion.Normalize(raw);
        return region.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ? null : region;
    }
}
