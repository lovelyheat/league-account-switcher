using System.Text.Json.Serialization;

namespace LeagueAccountSwitcher.Core.Models;

public sealed record RankedSoloInfo
{
    public static RankedSoloInfo Unranked { get; } = new();

    public string Tier { get; init; } = "UNRANKED";
    public string Division { get; init; } = string.Empty;
    public int LeaguePoints { get; init; }
    public int Wins { get; init; }
    public int Losses { get; init; }
    public bool IsProvisional { get; init; }

    [JsonIgnore]
    public string NormalizedTier => string.IsNullOrWhiteSpace(Tier) ||
                                    Tier.Equals("NONE", StringComparison.OrdinalIgnoreCase) ||
                                    Tier.Equals("UNRANKED", StringComparison.OrdinalIgnoreCase)
        ? "UNRANKED"
        : Tier.Trim().ToUpperInvariant();

    [JsonIgnore]
    public bool IsRanked => NormalizedTier != "UNRANKED";

    [JsonIgnore]
    public bool IsApexTier => NormalizedTier is "MASTER" or "GRANDMASTER" or "CHALLENGER";

    [JsonIgnore]
    public int TotalGames => Math.Max(0, Wins) + Math.Max(0, Losses);

    [JsonIgnore]
    public double? WinRate => TotalGames > 0
        ? Math.Round((double)Wins / TotalGames * 100.0, 1)
        : null;

    [JsonIgnore]
    public string FormattedTierName => NormalizedTier switch
    {
        "IRON" => "Iron",
        "BRONZE" => "Bronze",
        "SILVER" => "Silver",
        "GOLD" => "Gold",
        "PLATINUM" => "Platinum",
        "EMERALD" => "Emerald",
        "DIAMOND" => "Diamond",
        "MASTER" => "Master",
        "GRANDMASTER" => "Grandmaster",
        "CHALLENGER" => "Challenger",
        _ => "Unranked"
    };

    [JsonIgnore]
    public string FormattedTierAndDivision
    {
        get
        {
            if (!IsRanked)
                return "Unranked";

            if (IsApexTier || string.IsNullOrWhiteSpace(Division) || Division.Equals("NA", StringComparison.OrdinalIgnoreCase))
                return FormattedTierName;

            return $"{FormattedTierName} {Division.Trim().ToUpperInvariant()}";
        }
    }

    [JsonIgnore]
    public string FormattedRank => IsRanked
        ? $"{FormattedTierAndDivision} · {LeaguePoints} LP"
        : "Unranked";

    [JsonIgnore]
    public string FormattedRecord => TotalGames > 0
        ? $"{Wins}W {Losses}L ({Math.Round((double)Wins / TotalGames * 100.0):0}%)"
        : string.Empty;

    [JsonIgnore]
    public int SortScore
    {
        get
        {
            var tierWeight = NormalizedTier switch
            {
                "IRON" => 1,
                "BRONZE" => 2,
                "SILVER" => 3,
                "GOLD" => 4,
                "PLATINUM" => 5,
                "EMERALD" => 6,
                "DIAMOND" => 7,
                "MASTER" => 8,
                "GRANDMASTER" => 9,
                "CHALLENGER" => 10,
                _ => 0
            };

            if (tierWeight == 0)
                return 0;

            var divisionWeight = IsApexTier
                ? 4
                : Division.Trim().ToUpperInvariant() switch
                {
                    "IV" => 1,
                    "III" => 2,
                    "II" => 3,
                    "I" => 4,
                    _ => 0
                };

            return (tierWeight * 100_000) + (divisionWeight * 10_000) + Math.Clamp(LeaguePoints, 0, 9_999);
        }
    }
}
