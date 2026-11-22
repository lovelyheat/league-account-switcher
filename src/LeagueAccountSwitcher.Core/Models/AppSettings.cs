namespace LeagueAccountSwitcher.Core.Models;

public enum LaunchTarget
{
    LeagueOfLegends = 0,
    RiotClientOnly = 1
}

public enum AppThemePreference
{
    System = 0,
    Dark = 1,
    Light = 2
}

public sealed class AppSettings
{
    public string? RiotClientPathOverride { get; set; }
    public string? LeagueClientPathOverride { get; set; }
    public LaunchTarget LaunchTarget { get; set; } = LaunchTarget.LeagueOfLegends;
    public AppThemePreference Theme { get; set; } = AppThemePreference.System;
    public bool AutoSyncWithLcu { get; set; } = true;
    public bool MinimizeToTrayOnClose { get; set; }
    public string? ActiveAccountId { get; set; }
    public string? ProtectedRiotApiKey { get; set; }
}
