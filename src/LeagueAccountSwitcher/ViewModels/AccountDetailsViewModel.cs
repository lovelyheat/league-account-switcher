using CommunityToolkit.Mvvm.ComponentModel;
using LeagueAccountSwitcher.Core.Models;

namespace LeagueAccountSwitcher.ViewModels;

/// <summary>Editable account fields, shared by the add and edit dialogs.</summary>
public sealed partial class AccountDetailsViewModel : ObservableObject
{
    public static IReadOnlyList<string> Regions { get; } = ["Unknown", .. LeagueRegion.All.Select(r => r.DisplayCode), "PBE"];

    public static IReadOnlyList<string> Tiers { get; } =
        ["Unranked", "Iron", "Bronze", "Silver", "Gold", "Platinum", "Emerald", "Diamond", "Master", "Grandmaster", "Challenger"];

    public static IReadOnlyList<string> Divisions { get; } = ["IV", "III", "II", "I"];

    private readonly string _initialAlias;
    private readonly string _initialGameName;
    private readonly string _initialTagLine;
    private readonly string _initialRegion;
    private readonly double _initialLevel;
    private readonly string _initialTier;
    private readonly string? _initialDivision;
    private readonly double _initialLeaguePoints;
    private readonly double _initialWins;
    private readonly double _initialLosses;

    [ObservableProperty] private string _alias;
    [ObservableProperty] private string _gameName;
    [ObservableProperty] private string _tagLine;
    [ObservableProperty] private string? _region;
    [ObservableProperty] private double _level;
    [ObservableProperty] private double _leaguePoints;
    [ObservableProperty] private double _wins;
    [ObservableProperty] private double _losses;
    [ObservableProperty] private string? _division;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRanked), nameof(HasDivision))]
    private string? _tier;

    public AccountDetailsViewModel(AccountRecord source)
    {
        var rank = source.RankedSolo;

        _initialAlias = _alias = source.Alias ?? string.Empty;
        _initialGameName = _gameName = source.GameName;
        _initialTagLine = _tagLine = source.TagLine;
        _initialRegion = _region = Helpers.Regions.Known(source.Region) ?? "Unknown";
        _initialLevel = _level = source.SummonerLevel ?? double.NaN;
        _initialTier = _tier = rank.FormattedTierName;
        _initialDivision = _division = Divisions.FirstOrDefault(d => d.Equals(rank.Division, StringComparison.OrdinalIgnoreCase));
        _initialLeaguePoints = _leaguePoints = rank.LeaguePoints;
        _initialWins = _wins = rank.Wins;
        _initialLosses = _losses = rank.Losses;
    }

    public bool IsRanked => Tier is not (null or "Unranked");
    public bool HasDivision => IsRanked && Tier is not ("Master" or "Grandmaster" or "Challenger");

    partial void OnTierChanged(string? value)
    {
        if (HasDivision)
            Division ??= "IV";
    }

    /// <summary>Applies all fields (used when creating or capturing an account).</summary>
    public void ApplyTo(AccountRecord target)
    {
        var (name, tag) = ParseRiotId();

        target.Alias = string.IsNullOrWhiteSpace(Alias) ? null : Alias.Trim();
        if (!string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(target.GameName))
        {
            target.GameName = name;
            target.TagLine = tag;
        }

        if (Helpers.Regions.Known(Region) is { } knownRegion)
            target.Region = knownRegion;
        else if (string.IsNullOrWhiteSpace(target.Region))
            target.Region = "Unknown";

        if (ToCount(Level) is > 0 and var level)
            target.SummonerLevel = level;

        var rankChanged = HasRankChanged();
        if (rankChanged || IsRanked || ToCount(Wins) > 0 || ToCount(Losses) > 0)
        {
            ApplyRankTo(target);
            target.HasRankData = true;
        }
    }

    /// <summary>Applies only the fields modified by the user so concurrent LCU syncs aren't overwritten.</summary>
    public void ApplyChangesTo(AccountRecord target)
    {
        if (!string.Equals(Alias, _initialAlias, StringComparison.Ordinal))
            target.Alias = string.IsNullOrWhiteSpace(Alias) ? null : Alias.Trim();

        if (!string.Equals(GameName, _initialGameName, StringComparison.Ordinal)
            || !string.Equals(TagLine, _initialTagLine, StringComparison.Ordinal))
        {
            var (name, tag) = ParseRiotId();
            target.GameName = name;
            target.TagLine = tag;
        }

        if (!string.Equals(Region, _initialRegion, StringComparison.Ordinal) && Region is not null)
            target.Region = Region;

        if (!SameNumber(Level, _initialLevel))
            target.SummonerLevel = ToCount(Level) is > 0 and var level ? level : null;

        if (HasRankChanged())
        {
            ApplyRankTo(target);
            target.HasRankData = true;
        }
    }

    private (string Name, string Tag) ParseRiotId()
    {
        var name = GameName.Trim();
        var tag = TagLine.Trim().TrimStart('#');

        // A full "Name#Tag" pasted into the name box.
        if (name.IndexOf('#') is >= 0 and var hash)
            (name, tag) = (name[..hash].Trim(), name[(hash + 1)..].Trim());

        return (name, tag);
    }

    private bool HasRankChanged() =>
        !string.Equals(Tier, _initialTier, StringComparison.Ordinal)
        || !string.Equals(Division, _initialDivision, StringComparison.Ordinal)
        || !SameNumber(LeaguePoints, _initialLeaguePoints)
        || !SameNumber(Wins, _initialWins)
        || !SameNumber(Losses, _initialLosses);

    private void ApplyRankTo(AccountRecord target)
    {
        target.RankedSolo = target.RankedSolo with
        {
            Tier = IsRanked ? Tier!.ToUpperInvariant() : "UNRANKED",
            Division = HasDivision ? Division ?? "IV" : string.Empty,
            LeaguePoints = IsRanked ? ToCount(LeaguePoints) : 0,
            Wins = ToCount(Wins),
            Losses = ToCount(Losses),
        };
    }

    private static bool SameNumber(double a, double b) =>
        (double.IsNaN(a) && double.IsNaN(b)) || Math.Abs(a - b) < 0.0001;

    // NumberBox reports an empty box as NaN.
    private static int ToCount(double value) => double.IsFinite(value) ? (int)Math.Clamp(value, 0, 99_999) : 0;
}
