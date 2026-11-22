namespace LeagueAccountSwitcher.Core.Models;

public sealed record RegionMetadata(
    string DisplayCode,
    string PlatformId,
    string RegionalRoute,
    string DisplayName);

public static class LeagueRegion
{
    private static readonly RegionMetadata[] Regions =
    [
        new("EUW", "euw1", "europe", "EU West"),
        new("EUNE", "eun1", "europe", "EU Nordic & East"),
        new("NA", "na1", "americas", "North America"),
        new("KR", "kr", "asia", "Korea"),
        new("OCE", "oc1", "sea", "Oceania"),
        new("BR", "br1", "americas", "Brazil"),
        new("JP", "jp1", "asia", "Japan"),
        new("LAN", "la1", "americas", "Latin America North"),
        new("LAS", "la2", "americas", "Latin America South"),
        new("TR", "tr1", "europe", "Turkey"),
        new("RU", "ru", "europe", "Russia"),
        new("ME", "me1", "europe", "Middle East"),
        new("PH", "ph2", "sea", "Philippines"),
        new("SG", "sg2", "sea", "Singapore"),
        new("TH", "th2", "sea", "Thailand"),
        new("TW", "tw2", "sea", "Taiwan"),
        new("VN", "vn2", "sea", "Vietnam"),
    ];

    public static IReadOnlyList<RegionMetadata> All => Regions;

    public static string Normalize(string? rawRegion)
    {
        if (string.IsNullOrWhiteSpace(rawRegion))
            return "Unknown";

        var cleaned = rawRegion.Trim().ToUpperInvariant();
        return cleaned switch
        {
            "EUW" or "EUW1" => "EUW",
            "EUNE" or "EUN1" or "EUN" => "EUNE",
            "NA" or "NA1" => "NA",
            "KR" or "KR1" => "KR",
            "OCE" or "OC1" or "OC" => "OCE",
            "BR" or "BR1" => "BR",
            "JP" or "JP1" => "JP",
            "LAN" or "LA1" => "LAN",
            "LAS" or "LA2" => "LAS",
            "TR" or "TR1" => "TR",
            "RU" or "RU1" => "RU",
            "ME" or "ME1" => "ME",
            "PH" or "PH2" => "PH",
            "SG" or "SG2" => "SG",
            "TH" or "TH2" => "TH",
            "TW" or "TW2" => "TW",
            "VN" or "VN2" => "VN",
            "PBE" or "PBE1" => "PBE",
            _ => cleaned
        };
    }

    public static RegionMetadata? TryGetMetadata(string? rawRegion)
    {
        var normalized = Normalize(rawRegion);
        return Regions.FirstOrDefault(r => string.Equals(r.DisplayCode, normalized, StringComparison.OrdinalIgnoreCase));
    }
}
