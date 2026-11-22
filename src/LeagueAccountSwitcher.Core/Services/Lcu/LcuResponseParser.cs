using System.Text.Json;
using LeagueAccountSwitcher.Core.Models;

namespace LeagueAccountSwitcher.Core.Services.Lcu;

public sealed record LcuSummoner(string Puuid, string GameName, string TagLine, int SummonerLevel, int ProfileIconId);

public static class LcuResponseParser
{
    internal const string SoloQueue = "RANKED_SOLO_5x5";

    /// <summary>Returns null unless the response carries a puuid.</summary>
    public static LcuSummoner? ParseSummoner(string? json)
    {
        if (ParseObject(json) is not { } root || GetString(root, "puuid") is not { } puuid)
            return null;

        return new LcuSummoner(
            puuid,
            GetString(root, "gameName") ?? GetString(root, "displayName") ?? string.Empty,
            GetString(root, "tagLine") ?? string.Empty,
            GetInt(root, "summonerLevel"),
            GetInt(root, "profileIconId"));
    }

    /// <summary>
    /// Returns null when the response is not a ranked-stats payload (rank unknown), and an unranked value when it is
    /// one without a Solo/Duo rank.
    /// </summary>
    public static RankedSoloInfo? ParseRankedSolo(string? json)
    {
        if (ParseObject(json) is not { } root)
            return null;

        var hasMap = root.TryGetProperty("queueMap", out var map) && map.ValueKind == JsonValueKind.Object;
        if (hasMap && map.TryGetProperty(SoloQueue, out var solo) && solo.ValueKind == JsonValueKind.Object)
            return CreateRankedSolo(solo, "division");

        var hasList = root.TryGetProperty("queues", out var queues) && queues.ValueKind == JsonValueKind.Array;
        if (hasList)
        {
            foreach (var queue in queues.EnumerateArray())
            {
                if (GetString(queue, "queueType") == SoloQueue)
                    return CreateRankedSolo(queue, "division");
            }
        }

        return hasMap || hasList ? RankedSoloInfo.Unranked : null;
    }

    /// <summary>Returns the normalized region, or <c>Unknown</c>.</summary>
    public static string ParseRegion(string? json) =>
        LeagueRegion.Normalize(ParseObject(json) is { } root
            ? GetString(root, "region") ?? GetString(root, "webRegion")
            : null);

    // League-V4 has the same queue shape, except that it calls the division "rank".
    internal static RankedSoloInfo CreateRankedSolo(JsonElement queue, string divisionProperty)
    {
        var info = new RankedSoloInfo
        {
            Tier = GetString(queue, "tier") ?? string.Empty,
            Wins = GetInt(queue, "wins"),
            Losses = GetInt(queue, "losses"),
            IsProvisional = queue.TryGetProperty("isProvisional", out var provisional) && provisional.ValueKind == JsonValueKind.True,
        };

        if (!info.IsRanked)
            return info with { Tier = "UNRANKED" };

        var division = GetString(queue, divisionProperty)?.ToUpperInvariant();
        return info with
        {
            Tier = info.NormalizedTier,
            Division = info.IsApexTier || division is null or "NA" ? string.Empty : division,
            LeaguePoints = GetInt(queue, "leaguePoints"),
        };
    }

    internal static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { } text
        && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

    internal static int GetInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : 0;

    private static JsonElement? ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
