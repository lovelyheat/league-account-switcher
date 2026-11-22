using System.Text.Json.Serialization;

namespace LeagueAccountSwitcher.Core.Models;

public sealed class AccountRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? Puuid { get; set; }
    public string GameName { get; set; } = string.Empty;
    public string TagLine { get; set; } = string.Empty;
    public string? Alias { get; set; }
    public string Region { get; set; } = "Unknown";
    public int? SummonerLevel { get; set; }
    public int? ProfileIconId { get; set; }
    public RankedSoloInfo RankedSolo { get; set; } = RankedSoloInfo.Unranked;
    public bool HasRankData { get; set; }
    public bool HasSavedSession { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSwitchedAt { get; set; }
    public DateTimeOffset? LastSyncedAt { get; set; }

    [JsonIgnore]
    public string RiotId => string.IsNullOrWhiteSpace(GameName)
        ? "Unknown Account"
        : string.IsNullOrWhiteSpace(TagLine)
            ? GameName
            : $"{GameName}#{TagLine}";

    [JsonIgnore]
    public string DisplayName => !string.IsNullOrWhiteSpace(Alias)
        ? Alias!
        : RiotId;
}
