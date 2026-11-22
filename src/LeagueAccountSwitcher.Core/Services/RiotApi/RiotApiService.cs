using System.Net;
using System.Text.Json;
using LeagueAccountSwitcher.Core.Models;
using LeagueAccountSwitcher.Core.Services.Lcu;

namespace LeagueAccountSwitcher.Core.Services.RiotApi;

public sealed class RiotApiService(HttpClient? httpClient = null) : IRiotApiService
{
    private readonly HttpClient _http = httpClient
        ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<AccountRefreshResult?> RefreshAccountAsync(AccountRecord account, string apiKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        var key = apiKey?.Trim();
        if (string.IsNullOrEmpty(key) || key.Any(c => c is < '!' or > '~'))
            throw new ArgumentException("Enter a valid Riot API key.", nameof(apiKey));

        if (LeagueRegion.TryGetMetadata(account.Region) is not { } region)
            return null;

        if (await GetAccountAsync(account, region, key, ct) is not { } riotAccount
            || LcuResponseParser.GetString(riotAccount, "puuid") is not { } apiPuuid)
        {
            return null;
        }

        var platformUrl = $"https://{GetPlatformHost(region)}.api.riotgames.com/lol";
        var escapedPuuid = Uri.EscapeDataString(apiPuuid);

        if (await GetJsonAsync($"{platformUrl}/summoner/v4/summoners/by-puuid/{escapedPuuid}", key, ct) is not { } summoner)
            return null;

        var entries = await GetJsonAsync($"{platformUrl}/league/v4/entries/by-puuid/{escapedPuuid}", key, ct);
        if (entries is null && LcuResponseParser.GetString(summoner, "id") is { } summonerId)
            entries = await GetJsonAsync($"{platformUrl}/league/v4/entries/by-summoner/{Uri.EscapeDataString(summonerId)}", key, ct);

        if (entries is not { ValueKind: JsonValueKind.Array } queues)
            return null;

        var rank = RankedSoloInfo.Unranked;
        foreach (var queue in queues.EnumerateArray())
        {
            if (LcuResponseParser.GetString(queue, "queueType") == LcuResponseParser.SoloQueue)
            {
                rank = LcuResponseParser.CreateRankedSolo(queue, "rank");
                break;
            }
        }

        return new AccountRefreshResult(
            IsClientPuuid(account.Puuid) ? account.Puuid! : apiPuuid,
            LcuResponseParser.GetString(riotAccount, "gameName") ?? account.GameName,
            LcuResponseParser.GetString(riotAccount, "tagLine") ?? account.TagLine,
            LcuResponseParser.GetInt(summoner, "summonerLevel"),
            LcuResponseParser.GetInt(summoner, "profileIconId"),
            rank,
            DateTimeOffset.UtcNow);
    }

    private async Task<JsonElement?> GetAccountAsync(AccountRecord account, RegionMetadata region, string key, CancellationToken ct)
    {
        var route = region.RegionalRoute == "sea" ? "asia" : region.RegionalRoute;
        var baseUrl = $"https://{route}.api.riotgames.com/riot/account/v1/accounts";
        var hasRiotId = !string.IsNullOrWhiteSpace(account.GameName) && !string.IsNullOrWhiteSpace(account.TagLine);

        if (!string.IsNullOrWhiteSpace(account.Puuid) && !IsClientPuuid(account.Puuid))
        {
            try
            {
                return await GetJsonAsync($"{baseUrl}/by-puuid/{Uri.EscapeDataString(account.Puuid)}", key, ct);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.BadRequest && hasRiotId)
            {
            }
        }

        return hasRiotId
            ? await GetJsonAsync($"{baseUrl}/by-riot-id/{Uri.EscapeDataString(account.GameName)}/{Uri.EscapeDataString(account.TagLine)}", key, ct)
            : null;
    }

    private static bool IsClientPuuid(string? puuid) => Guid.TryParse(puuid, out _);

    private static string GetPlatformHost(RegionMetadata region) =>
        region.PlatformId is "ph2" or "th2" ? "sg2" : region.PlatformId;

    private async Task<JsonElement?> GetJsonAsync(string url, string key, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Riot-Token", key);

        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        if (!response.IsSuccessStatusCode)
            throw CreateException(response);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return document.RootElement.Clone();
    }

    private static HttpRequestException CreateException(HttpResponseMessage response)
    {
        var status = response.StatusCode;
        var message = status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                $"The Riot API key is invalid or expired (HTTP {(int)status}). Update it in Settings; development keys expire every 24 hours.",
            HttpStatusCode.TooManyRequests => response.Headers.RetryAfter?.Delta is { } delay
                ? $"The Riot API rate limit was reached (HTTP 429). Try again in {delay.TotalSeconds:0} seconds."
                : "The Riot API rate limit was reached (HTTP 429). Try again shortly.",
            _ => $"The Riot API request failed (HTTP {(int)status}).",
        };

        return new HttpRequestException(message, null, status);
    }
}
