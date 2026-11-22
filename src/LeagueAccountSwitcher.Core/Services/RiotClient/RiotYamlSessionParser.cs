using LeagueAccountSwitcher.Core.Models;

namespace LeagueAccountSwitcher.Core.Services.RiotClient;

public sealed record RiotSessionInfo(bool HasSession, string? Puuid, string? Region);

// shortcut: reads block-style YAML with single-line scalars only, switch to YamlDotNet if Riot emits flow style.
public static class RiotYamlSessionParser
{
    private const string CookiesPath = "riot-login/persist/session/cookies";

    internal readonly record struct Entry(string ParentPath, int Item, string Key, string Value);

    public static RiotSessionInfo Parse(string? yaml)
    {
        var entries = ReadEntries(yaml);

        var cookies = entries
            .Where(e => e.ParentPath == CookiesPath)
            .GroupBy(e => e.Item)
            .Select(cookie => (
                Name: cookie.FirstOrDefault(e => e.Key == "name").Value,
                Value: cookie.FirstOrDefault(e => e.Key == "value").Value))
            .Where(cookie => !string.IsNullOrWhiteSpace(cookie.Value))
            .ToList();

        var puuid = cookies.FirstOrDefault(c => c.Name == "sub").Value;

        // `tdid` only identifies the device and outlives sign-out, and `sub` without `ssid`
        // cannot authenticate Riot Client. Require a non-empty `ssid` cookie to count as signed in.
        var hasSession = cookies.Any(c => c.Name == "ssid");

        return new RiotSessionInfo(hasSession, puuid, FindRegion(entries));
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

        foreach (var rawLine in yaml.TrimStart('﻿').Split('\n'))
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
