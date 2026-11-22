using System.Text.Json;

namespace LeagueAccountSwitcher.Core.Services.RiotClient;

public static class RiotClientPathDiscovery
{
    private const string RiotClientExe = "RiotClientServices.exe";
    private const string LeagueClientExe = "LeagueClient.exe";

    public static IReadOnlyList<string> DefaultSearchRoots { get; } = [@"C:\", @"D:\", @"E:\", @"F:\"];

    public static IReadOnlyList<string> ParseRiotClientPaths(string? installsJson) =>
        ParseInstalls(installsJson, root =>
        {
            var candidates = new List<string?>();
            foreach (var key in new[] { "rc_default", "rc_live", "rc_beta" })
            {
                if (root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                    candidates.Add(value.GetString());
            }

            if (root.TryGetProperty("patchlines", out var patchlines) && patchlines.ValueKind == JsonValueKind.Object)
            {
                candidates.AddRange(patchlines.EnumerateObject()
                    .Where(p => p.Value.ValueKind == JsonValueKind.String)
                    .Select(p => p.Value.GetString()));
            }

            return candidates
                .Select(NormalizePath)
                .OfType<string>()
                .Where(p => Path.GetFileName(p).Equals(RiotClientExe, StringComparison.OrdinalIgnoreCase));
        });

    public static IReadOnlyList<string> ParseLeagueInstallDirectories(string? installsJson) =>
        ParseInstalls(installsJson, root =>
        {
            if (!root.TryGetProperty("associated_client", out var clients) || clients.ValueKind != JsonValueKind.Object)
                return [];

            return clients.EnumerateObject()
                .Select(p => NormalizePath(p.Name))
                .OfType<string>()
                .Where(p => p.Contains("League of Legends", StringComparison.OrdinalIgnoreCase) &&
                            !p.Contains("(PBE)", StringComparison.OrdinalIgnoreCase));
        });

    public static string? ParseProductInstallPath(string? productSettingsYaml) =>
        NormalizePath(RiotYamlSessionParser.ReadEntries(productSettingsYaml)
            .FirstOrDefault(e => e.Key == "product_install_full_path").Value);

    public static string? ResolveRiotClientOverride(string? overridePath)
    {
        if (NormalizePath(overridePath) is not { } custom)
            return null;

        var candidate = Path.GetFileName(custom).Equals(RiotClientExe, StringComparison.OrdinalIgnoreCase)
            ? custom
            : Path.Combine(custom, RiotClientExe);
        return File.Exists(candidate) ? candidate : null;
    }

    public static string? ResolveLeagueInstallOverride(string? overridePath)
    {
        if (NormalizePath(overridePath) is not { } custom)
            return null;

        var candidateDir = custom.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileName(custom).Equals(LeagueClientExe, StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(custom)
                : null
            : custom;

        return candidateDir is not null && File.Exists(Path.Combine(candidateDir, LeagueClientExe))
            ? candidateDir
            : null;
    }

    public static string? FindRiotClient(string? overridePath, string programDataDirectory, IEnumerable<string> searchRoots)
    {
        var candidates = new List<string>();
        if (NormalizePath(overridePath) is { } custom)
        {
            candidates.Add(Path.GetFileName(custom).Equals(RiotClientExe, StringComparison.OrdinalIgnoreCase)
                ? custom
                : Path.Combine(custom, RiotClientExe));
        }

        candidates.AddRange(ParseRiotClientPaths(ReadTextOrNull(GetInstallsPath(programDataDirectory))));
        candidates.AddRange(searchRoots.Select(root => Path.Combine(root, "Riot Games", "Riot Client", RiotClientExe)));

        return candidates.FirstOrDefault(File.Exists);
    }

    public static string? FindLeagueInstall(string? overridePath, string programDataDirectory, IEnumerable<string> searchRoots)
    {
        var candidates = new List<string?>();
        if (NormalizePath(overridePath) is { } custom)
        {
            candidates.Add(custom.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(custom)
                : custom);
        }

        var metadataPath = Path.Combine(programDataDirectory, "Riot Games", "Metadata",
            "league_of_legends.live", "league_of_legends.live.product_settings.yaml");
        candidates.Add(ParseProductInstallPath(ReadTextOrNull(metadataPath)));
        candidates.AddRange(ParseLeagueInstallDirectories(ReadTextOrNull(GetInstallsPath(programDataDirectory))));
        candidates.AddRange(searchRoots.Select(root => Path.Combine(root, "Riot Games", "League of Legends")));

        return candidates.OfType<string>().FirstOrDefault(dir => File.Exists(Path.Combine(dir, LeagueClientExe)));
    }

    internal static string? ReadTextOrNull(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string GetInstallsPath(string programDataDirectory) =>
        Path.Combine(programDataDirectory, "Riot Games", "RiotClientInstalls.json");

    private static IReadOnlyList<string> ParseInstalls(string? json, Func<JsonElement, IEnumerable<string>> select)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? select(document.RootElement).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? NormalizePath(string? raw)
    {
        var path = raw?.Trim().Trim('"').Replace('/', '\\');

        if (string.IsNullOrEmpty(path) || path.Contains('\0') || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(path))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (IOException)
        {
            return null;
        }
    }
}
