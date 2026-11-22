using LeagueAccountSwitcher.Core.Models;

namespace LeagueAccountSwitcher.Helpers;

public static class Regions
{
    // LeagueRegion.Normalize turns the stored "Unknown" into "UNKNOWN", so compare without case.
    public static string? Known(string? raw)
    {
        var region = LeagueRegion.Normalize(raw);
        return region.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ? null : region;
    }
}
