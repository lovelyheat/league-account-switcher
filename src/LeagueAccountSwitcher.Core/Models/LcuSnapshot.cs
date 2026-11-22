namespace LeagueAccountSwitcher.Core.Models;

public sealed record LcuSnapshot(
    string Puuid,
    string GameName,
    string TagLine,
    int SummonerLevel,
    int ProfileIconId,
    string Region,
    RankedSoloInfo? RankedSolo,
    DateTimeOffset FetchedAt);

public sealed record AccountRefreshResult(
    string Puuid,
    string GameName,
    string TagLine,
    int SummonerLevel,
    int ProfileIconId,
    RankedSoloInfo RankedSolo,
    DateTimeOffset FetchedAt);

public sealed record CapturedSession(
    byte[] Bytes,
    string? Puuid,
    string? Region);
