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

        _alias = source.Alias ?? string.Empty;
        _gameName = source.GameName;
        _tagLine = source.TagLine;
        _region = Helpers.Regions.Known(source.Region) ?? "Unknown";
        _level = source.SummonerLevel ?? double.NaN;
        _tier = rank.FormattedTierName;
        _division = Divisions.FirstOrDefault(d => d.Equals(rank.Division, StringComparison.OrdinalIgnoreCase));
        _leaguePoints = rank.LeaguePoints;
        _wins = rank.Wins;
        _losses = rank.Losses;
    }

    public bool IsRanked => Tier is not (null or "Unranked");
    public bool HasDivision => IsRanked && Tier is not ("Master" or "Grandmaster" or "Challenger");

    partial void OnTierChanged(string? value)
    {
        if (HasDivision)
            Division ??= "IV";
    }

    public void ApplyTo(AccountRecord target)
    {
        var name = GameName.Trim();
        var tag = TagLine.Trim().TrimStart('#');

        // A full "Name#Tag" pasted into the name box.
        if (name.IndexOf('#') is >= 0 and var hash)
            (name, tag) = (name[..hash].Trim(), name[(hash + 1)..].Trim());

        target.Alias = string.IsNullOrWhiteSpace(Alias) ? null : Alias.Trim();
        target.GameName = name;
        target.TagLine = tag;
        target.Region = Region ?? target.Region;
        target.SummonerLevel = ToCount(Level) is > 0 and var level ? level : null;
        target.RankedSolo = target.RankedSolo with
        {
            Tier = IsRanked ? Tier!.ToUpperInvariant() : "UNRANKED",
            Division = HasDivision ? Division ?? "IV" : string.Empty,
            LeaguePoints = IsRanked ? ToCount(LeaguePoints) : 0,
            Wins = ToCount(Wins),
            Losses = ToCount(Losses),
        };
    }

    // NumberBox reports an empty box as NaN.
    private static int ToCount(double value) => double.IsFinite(value) ? (int)Math.Clamp(value, 0, 99_999) : 0;
}
