using System.Net.Http.Headers;
using System.Net.Security;
using System.Text;
using System.Text.Json;
using LeagueAccountSwitcher.Core.Models;
using LeagueAccountSwitcher.Core.Services.Lcu;

namespace LeagueAccountSwitcher.Core.Services.RiotClient;

/// <summary>
/// Reads the currently signed-in account metadata from Riot Client's local loopback API via its lockfile.
/// </summary>
internal static class RiotClientLocalApi
{
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        ServerCertificateCustomValidationCallback = (request, _, _, errors) =>
            errors == SslPolicyErrors.None || request.RequestUri?.Host is "127.0.0.1" or "localhost",
    })
    {
        Timeout = TimeSpan.FromSeconds(3),
    };

    public static async Task<RiotClientLiveAccount?> TryFetchAsync(string localAppData, CancellationToken ct)
    {
        var lockfilePath = Path.Combine(localAppData, "Riot Games", "Riot Client", "Config", "lockfile");
        var lockfileText = RiotClientPathDiscovery.ReadTextOrNull(lockfilePath);
        if (lockfileText is null || !LcuConnectionInfo.TryParseLockfile(lockfileText, out var connection))
            return null;

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{connection.Protocol}://127.0.0.1:{connection.Port}/player-session-lifecycle/v1/session");
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"riot:{connection.AuthToken}")));

            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return TryParseSessionJson(body);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    internal static RiotClientLiveAccount? TryParseSessionJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var puuid = GetString(root, "puuid");
            if (string.IsNullOrWhiteSpace(puuid))
                return null;

            var gameName = string.Empty;
            var tagLine = string.Empty;
            if (TryGetObject(root, "riotID", out var riotId))
            {
                gameName = GetString(riotId, "gameName") ?? string.Empty;
                tagLine = GetString(riotId, "tagLine") ?? string.Empty;
            }

            var region = "Unknown";
            int? summonerLevel = null;
            int? profileIconId = null;

            if (TryGetObject(root, "userInfo", out var userInfo))
            {
                if (string.IsNullOrWhiteSpace(gameName) && TryGetObject(userInfo, "acct", out var acct))
                {
                    gameName = GetString(acct, "game_name") ?? gameName;
                    tagLine = GetString(acct, "tag_line") ?? tagLine;
                }

                string? rawRegion = null;
                if (TryGetObject(userInfo, "region", out var regionObj))
                    rawRegion = GetString(regionObj, "id") ?? GetString(regionObj, "tag");
                if (string.IsNullOrWhiteSpace(rawRegion) && TryGetObject(userInfo, "lol", out var lolObj))
                    rawRegion = GetString(lolObj, "cpid") ?? GetString(lolObj, "pid");
                rawRegion ??= GetString(userInfo, "original_platform_id");

                if (!string.IsNullOrWhiteSpace(rawRegion))
                    region = LeagueRegion.Normalize(rawRegion);

                if (TryGetObject(userInfo, "lol_account", out var lolAccount))
                {
                    if (TryGetInt(lolAccount, "summoner_level", out var lvl) && lvl > 0)
                        summonerLevel = lvl;
                    if (TryGetInt(lolAccount, "profile_icon", out var icon) && icon >= 0)
                        profileIconId = icon;
                }
            }

            var loginState = GetString(root, "loginState");
            string? persistedPuuid = null;
            if (TryGetObject(root, "persistence", out var persistence))
                persistedPuuid = GetString(persistence, "puuid");

            var hasOfflineAccess = false;
            if (TryGetObject(root, "accessToken", out var accessToken)
                && accessToken.TryGetProperty("scopes", out var scopes)
                && scopes.ValueKind == JsonValueKind.Array)
            {
                foreach (var scope in scopes.EnumerateArray())
                {
                    if (scope.ValueKind == JsonValueKind.String
                        && string.Equals(scope.GetString(), "offline_access", StringComparison.OrdinalIgnoreCase))
                    {
                        hasOfflineAccess = true;
                        break;
                    }
                }
            }

            return new RiotClientLiveAccount(
                puuid,
                gameName,
                tagLine,
                region,
                summonerLevel,
                profileIconId,
                loginState,
                persistedPuuid,
                hasOfflineAccess);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetObject(JsonElement parent, string name, out JsonElement value) =>
        parent.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object;

    private static string? GetString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static bool TryGetInt(JsonElement parent, string name, out int value)
    {
        if (parent.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out value))
            return true;

        value = 0;
        return false;
    }
}
