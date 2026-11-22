using CommunityToolkit.Mvvm.ComponentModel;
using LeagueAccountSwitcher.Core.Models;
using LeagueAccountSwitcher.Core.Services;
using LeagueAccountSwitcher.Core.Services.RiotClient;
using LeagueAccountSwitcher.Services;

namespace LeagueAccountSwitcher.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AccountCoordinator _accounts;
    private readonly IAccountStore _store;
    private readonly IRiotClientService _riot;
    private readonly string _originalKey;

    [ObservableProperty] private string _detectedRiotPath = "Detecting…";
    [ObservableProperty] private string _detectedLeaguePath = "Detecting…";
    [ObservableProperty] private string _riotPath;
    [ObservableProperty] private string _leaguePath;
    [ObservableProperty] private int _launchIndex;
    [ObservableProperty] private int _themeIndex;
    [ObservableProperty] private bool _autoSync;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private string _apiKey;
    [ObservableProperty] private string _error = string.Empty;

    public SettingsViewModel(AccountCoordinator accounts, IAccountStore store, IRiotClientService riot)
    {
        (_accounts, _store, _riot) = (accounts, store, riot);

        var settings = accounts.Settings;
        _riotPath = settings.RiotClientPathOverride ?? string.Empty;
        _leaguePath = settings.LeagueClientPathOverride ?? string.Empty;
        _launchIndex = (int)settings.LaunchTarget;
        _themeIndex = (int)settings.Theme;
        _autoSync = settings.AutoSyncWithLcu;
        _minimizeToTray = settings.MinimizeToTrayOnClose;
        _originalKey = _apiKey = store.UnprotectSecret(settings.ProtectedRiotApiKey) ?? string.Empty;
    }

    public async Task DetectPathsAsync()
    {
        DetectedRiotPath = await DetectAsync(() => _riot.DetectRiotClientPath());
        DetectedLeaguePath = await DetectAsync(() => _riot.DetectLeagueInstallPath());
    }

    private static async Task<string> DetectAsync(Func<string?> detect)
    {
        try { return await Task.Run(detect) ?? "Not found. Enter the path below."; }
        catch (Exception ex) { return $"Detection failed: {ex.Message}"; }
    }

    public async Task<bool> SaveAsync()
    {
        Error = string.Empty;
        try
        {
            var riotPath = CleanPath(RiotPath);
            var leaguePath = CleanPath(LeaguePath);

            if (riotPath is not null && RiotClientPathDiscovery.ResolveRiotClientOverride(riotPath) is null)
            {
                Error = "Riot Client override must point to RiotClientServices.exe or the folder containing it on a local drive.";
                return false;
            }

            if (leaguePath is not null && RiotClientPathDiscovery.ResolveLeagueInstallOverride(leaguePath) is null)
            {
                Error = "League Client override must point to LeagueClient.exe or the League of Legends folder containing it on a local drive.";
                return false;
            }

            var key = ApiKey.Trim();
            var protectedKey = key == _originalKey ? _accounts.Settings.ProtectedRiotApiKey
                : key.Length == 0 ? null
                : _store.ProtectSecret(key);

            await _accounts.UpdateSettingsAsync(settings =>
            {
                settings.RiotClientPathOverride = riotPath;
                settings.LeagueClientPathOverride = leaguePath;
                settings.LaunchTarget = (LaunchTarget)LaunchIndex;
                settings.Theme = (AppThemePreference)ThemeIndex;
                settings.AutoSyncWithLcu = AutoSync;
                settings.MinimizeToTrayOnClose = MinimizeToTray;
                settings.ProtectedRiotApiKey = protectedKey;
            });
            return true;
        }
        catch (Exception ex)
        {
            Error = $"Couldn't save settings: {ex.Message}";
            return false;
        }
    }

    // Explorer's "Copy as path" adds quotes.
    private static string? CleanPath(string value) => value.Trim().Trim('"') is { Length: > 0 } path ? path : null;
}
