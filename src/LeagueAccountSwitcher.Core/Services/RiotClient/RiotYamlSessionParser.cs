using System.Text;
using System.Text.Json;
using LeagueAccountSwitcher.Core.Models;

namespace LeagueAccountSwitcher.Core.Services.RiotClient;

public sealed record RiotSessionInfo(bool HasSession, string? Puuid, string? Region);

// shortcut: reads block-style YAML with single-line scalars only, switch to YamlDotNet if Riot emits flow style.
public static class RiotYamlSessionParser
{
    private const string LegacyCookiesPath = "riot-login/persist/session/cookies";
    private const string PslAuthorizationParent = "psl/authorization";
    private const string PslRiotClientPath = "psl/authorization/riot-client";

    internal readonly record struct Entry(string ParentPath, int Item, string Key, string Value);

    public static RiotSessionInfo Parse(string? yaml)
    {
        var entries = ReadEntries(yaml);

        var legacyCookies = entries
            .Where(e => e.ParentPath == LegacyCookiesPath)
            .GroupBy(e => e.Item)
            .Select(cookie => (
                Name: cookie.FirstOrDefault(e => e.Key == "name").Value,
                Value: cookie.FirstOrDefault(e => e.Key == "value").Value))
            .Where(cookie => !string.IsNullOrWhiteSpace(cookie.Value))
            .ToList();

        var legacyPuuid = legacyCookies.FirstOrDefault(c => c.Name == "sub").Value;
        var hasLegacySession = legacyCookies.Any(c => c.Name == "ssid");

        // Modern Riot Client (2025+) stores persistent login tokens under `psl.authorization.riot-client`.
        // When signed out or not persisted, Riot writes `riot-client: null` (which ReadScalar turns into empty string).
        var pslEntries = entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Value) && IsPslAuthorizationEntry(e))
            .ToList();
        var hasPslSession = pslEntries.Count > 0;

        var puuid = legacyPuuid ?? ExtractPslPuuid(pslEntries);
        var hasSession = hasLegacySession || hasPslSession;

        return new RiotSessionInfo(hasSession, puuid, FindRegion(entries));
    }

    private static bool IsPslAuthorizationEntry(Entry entry) =>
        (entry.ParentPath == PslAuthorizationParent && entry.Key == "riot-client")
        || entry.ParentPath == PslRiotClientPath
        || entry.ParentPath.StartsWith(PslRiotClientPath + "/", StringComparison.Ordinal);

    private static string? ExtractPslPuuid(List<Entry> pslEntries)
    {
        foreach (var entry in pslEntries)
        {
            if (entry.Key is "sub" or "puuid" or "subject" && Guid.TryParse(entry.Value, out _))
                return entry.Value;
        }

        foreach (var entry in pslEntries)
        {
            if (TryExtractJwtSub(entry.Value) is { } jwtSub)
                return jwtSub;
        }

        return null;
    }

    private static string? TryExtractJwtSub(string token)
    {
        var firstDot = token.IndexOf('.');
        if (firstDot <= 0)
            return null;

        var secondDot = token.IndexOf('.', firstDot + 1);
        if (secondDot <= firstDot + 1 || token.IndexOf('.', secondDot + 1) >= 0)
            return null;

        try
        {
            var payloadSegment = token[(firstDot + 1)..secondDot]
                .Replace('-', '+')
                .Replace('_', '/');
            var padding = (4 - (payloadSegment.Length % 4)) % 4;
            if (padding > 0)
                payloadSegment += new string('=', padding);

            var bytes = Convert.FromBase64String(payloadSegment);
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("sub", out var sub)
                && sub.ValueKind == JsonValueKind.String
                && sub.GetString() is { Length: > 0 } subValue
                && Guid.TryParse(subValue, out _))
            {
                return subValue;
            }
        }
        catch
        {
            // Opaque or encrypted token; not a plaintext JWT.
        }

        return null;
    }

    private static string? FindRegion(List<Entry> entries) =>
        entries.Where(e => e.Key == "region")
            .Concat(entries.Where(e => e.Key == "affinity"))
            .Select(e => LeagueRegion.Normalize(e.Value))
            // Other Riot products use shard names such as "eu", which are not League regions.
            .FirstOrDefault(region => region == "PBE" || LeagueRegion.TryGetMetadata(region) is not null);

    internal static List<Entry> ReadEntries(string? yaml)
    {
        var entries = new List<Entry>();
        if (string.IsNullOrWhiteSpace(yaml))
            return entries;

        var parents = new List<(int Indent, string Key)>();
        var item = 0;

        foreach (var rawLine in yaml.TrimStart('\uFEFF').Split('\n'))
        {
            var line = rawLine.TrimEnd();
            var content = line.TrimStart();
            if (content.Length == 0 || content.StartsWith('#'))
                continue;

            var indent = line.Length - content.Length;
            if (content == "-" || content.StartsWith("- ", StringComparison.Ordinal))
            {
                // A list may share its parent key's indentation, so only deeper keys are closed here.
                parents.RemoveAll(p => p.Indent > indent);
                item++;

                content = content[1..].TrimStart();
                if (content.Length == 0)
                    continue;

                indent = line.Length - content.Length;
            }

            var separator = content.EndsWith(':') && !content.Contains(": ", StringComparison.Ordinal)
                ? content.Length - 1
                : content.IndexOf(": ", StringComparison.Ordinal);
            if (separator < 0)
                continue;

            parents.RemoveAll(p => p.Indent >= indent);
            var key = content[..separator].Trim().Trim('"', '\'');
            var rawValue = content[(separator + 1)..].Trim();

            if (rawValue.Length == 0 || rawValue.StartsWith('#'))
                parents.Add((indent, key));
            else
                entries.Add(new Entry(string.Join('/', parents.Select(p => p.Key)), item, key, ReadScalar(rawValue)));
        }

        return entries;
    }

    private static string ReadScalar(string raw)
    {
        if (raw[0] is '"' or '\'')
        {
            var end = raw.IndexOf(raw[0], 1);
            return end < 0 ? string.Empty : raw[1..end];
        }

        var comment = raw.IndexOf(" #", StringComparison.Ordinal);
        var value = comment < 0 ? raw : raw[..comment].TrimEnd();
        return value is "~" or "null" or "Null" or "NULL" or "{}" or "[]" ? string.Empty : value;
    }
}
