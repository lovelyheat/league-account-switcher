using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeagueAccountSwitcher.Core.Models;
using LeagueAccountSwitcher.Core.Services;
using LeagueAccountSwitcher.Services;
using LeagueAccountSwitcher.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LeagueAccountSwitcher.ViewModels;

public enum AccountSort
{
    LastUsed = 0,
    Rank = 1,
    Level = 2,
    Name = 3,
}

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private const string AllRegions = "All Regions";

    private readonly AccountCoordinator _accounts;
    private readonly DialogService _dialogs;
    private readonly IAccountStore _store;
    private readonly IRiotClientService _riot;
    private readonly ILcuService _lcu;
    private readonly IAssetCacheService _assets;
    private readonly IRiotApiService _api;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _pollTimer;
    private readonly DispatcherQueueTimer _bannerTimer;
    private readonly Dictionary<AccountRecord, AccountItemViewModel> _items = [];
    private bool _isPolling;
    private bool _leagueRunning;
    private bool _hasSession;
    private bool _isMonitoring;
    private volatile bool _disposed;

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string? _selectedRegion = AllRegions;
    [ObservableProperty] private int _sortIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInteract), nameof(CanRefreshApi))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInteract), nameof(CanRefreshApi))]
    private bool _isLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRefreshApi))]
    private bool _hasApiKey;

    [ObservableProperty] private bool _showEmptyState;
    [ObservableProperty] private bool _showNoMatches;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _connectionText = "Waiting for League Client";
    [ObservableProperty] private string _countText = string.Empty;
    [ObservableProperty] private ElementTheme _theme;

    [ObservableProperty] private bool _isBannerOpen;
    [ObservableProperty] private string _bannerMessage = string.Empty;
    [ObservableProperty] private InfoBarSeverity _bannerSeverity;

    public MainViewModel(
        AccountCoordinator accounts,
        DialogService dialogs,
        IAccountStore store,
        IRiotClientService riot,
        ILcuService lcu,
        IAssetCacheService assets,
        IRiotApiService api)
    {
        (_accounts, _dialogs, _store, _riot, _lcu, _assets, _api) = (accounts, dialogs, store, riot, lcu, assets, api);

        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _pollTimer = _dispatcher.CreateTimer();
        _pollTimer.Interval = TimeSpan.FromSeconds(3);
        _pollTimer.Tick += OnPollTick;
        _bannerTimer = _dispatcher.CreateTimer();
        _bannerTimer.Interval = TimeSpan.FromSeconds(6);
        _bannerTimer.IsRepeating = false;
        _bannerTimer.Tick += (_, _) => IsBannerOpen = false;

        _accounts.Changed += OnAccountsChanged;
        _lcu.ActiveAccountSynced += OnLcuSynced;
        _lcu.ConnectionStateChanged += OnLcuConnectionChanged;
    }

    public ObservableCollection<AccountItemViewModel> Accounts { get; } = [];
    public ObservableCollection<string> Regions { get; } = [AllRegions];

    public bool CanInteract => IsLoaded && !IsBusy;
    public bool CanRefreshApi => CanInteract && HasApiKey;

    public void ShowWarning(string message) => Notify(message, InfoBarSeverity.Warning);

    partial void OnSearchTextChanged(string value) => ApplyView();
    partial void OnSelectedRegionChanged(string? value) => ApplyView();
    partial void OnSortIndexChanged(int value) => ApplyView();

    public async Task InitializeAsync()
    {
        try
        {
            await _accounts.LoadAsync();
        }
        catch (Exception ex)
        {
            Notify($"Couldn't load saved accounts, so changes are disabled: {ex.Message}", InfoBarSeverity.Error);
            return;
        }

        IsLoaded = true;
        ApplyView();
        try
        {
            ApplyMonitoring();
        }
        catch (Exception ex)
        {
            Notify($"Couldn't start League Client sync: {ex.Message}", InfoBarSeverity.Error);
        }

        _pollTimer.Start();
        await PollAsync();
    }

    private void ApplyMonitoring()
    {
        var wanted = _accounts.Settings.AutoSyncWithLcu;

        if (wanted && !_isMonitoring)
            _lcu.StartMonitoring(() => _accounts.Settings.LeagueClientPathOverride);
        else if (!wanted && _isMonitoring)
            _lcu.StopMonitoring();
        _isMonitoring = wanted;

        SetConnected(_lcu.IsConnected);
    }

    private void SetConnected(bool connected)
    {
        IsConnected = connected && _accounts.Settings.AutoSyncWithLcu;
        ConnectionText = IsConnected ? "League Client connected"
            : _accounts.Settings.AutoSyncWithLcu ? "Waiting for League Client"
            : "Auto-sync is off";
    }

    private void OnLcuConnectionChanged(object? sender, bool connected) => _dispatcher.TryEnqueue(async () =>
    {
        if (_disposed)
            return;

        SetConnected(connected);
        await PollAsync();
    });

    private void OnLcuSynced(object? sender, LcuSnapshot live) => _dispatcher.TryEnqueue(async () =>
    {
        if (_disposed || !IsLoaded || !_accounts.Settings.AutoSyncWithLcu)
            return;

        try
        {
            var (account, updated) = await _accounts.ApplySnapshotAsync(live);
            if (updated && !IsBusy && account is not null)
            {
                var summary = account.HasRankData ? account.RankedSolo.FormattedRank : account.DisplayName;
                Notify($"Synced {summary} from League Client", InfoBarSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            Notify($"Couldn't save League Client data: {ex.Message}", InfoBarSeverity.Error);
        }
    });

    private async void OnPollTick(DispatcherQueueTimer sender, object args) => await PollAsync();

    private async Task PollAsync()
    {
        if (_isPolling || _disposed || !IsLoaded)
            return;

        _isPolling = true;
        try
        {
            var state = await Task.Run(() => (Running: _riot.IsLeagueClientRunning(), HasSession: _riot.HasActiveRiotSession()));
            if (_disposed)
                return;

            (_leagueRunning, _hasSession) = state;
            foreach (var item in _items.Values)
                UpdateLiveState(item);

            await _accounts.FollowLiveSessionAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
        finally
        {
            _isPolling = false;
        }
    }

    private void OnAccountsChanged(object? sender, EventArgs e)
    {
        if (_disposed)
            return;

        var settings = _accounts.Settings;
        Theme = settings.Theme switch
        {
            AppThemePreference.Dark => ElementTheme.Dark,
            AppThemePreference.Light => ElementTheme.Light,
            _ => ElementTheme.Default,
        };
        HasApiKey = !string.IsNullOrEmpty(settings.ProtectedRiotApiKey);

        foreach (var removed in _items.Keys.Except(_accounts.Accounts).ToList())
            _items.Remove(removed);

        foreach (var record in _accounts.Accounts)
        {
            if (_items.TryGetValue(record, out var item))
                item.Update();
            else
                _items[record] = item = new AccountItemViewModel(record, this, _assets);

            UpdateLiveState(item);
        }

        UpdateRegions();
        ApplyView();
    }

    private void UpdateLiveState(AccountItemViewModel item)
    {
        item.IsActive = _hasSession && item.Record.Id == _accounts.Settings.ActiveAccountId;
        item.IsLeagueRunning = _leagueRunning;
    }

    private void UpdateRegions()
    {
        string[] wanted =
        [
            AllRegions,
            .. _accounts.Accounts.Select(a => Helpers.Regions.Known(a.Region)).OfType<string>().Distinct().Order(),
        ];

        if (!wanted.Contains(SelectedRegion))
            SelectedRegion = AllRegions;

        Reconcile(Regions, wanted);
    }

    private void ApplyView()
    {
        var query = SearchText.Trim();
        var region = SelectedRegion ?? AllRegions;

        var matches = _items.Values.Where(item =>
        {
            var a = item.Record;
            return (region == AllRegions || Helpers.Regions.Known(a.Region) == region)
                && (query.Length == 0
                    || a.RiotId.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                    || (a.Alias?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false)
                    || a.Region.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                    || (LeagueRegion.TryGetMetadata(a.Region)?.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false));
        });

        var ordered = ((AccountSort)SortIndex switch
        {
            AccountSort.Rank => matches.OrderByDescending(i => i.Record.RankedSolo.SortScore),
            AccountSort.Level => matches.OrderByDescending(i => i.Record.SummonerLevel ?? 0),
            AccountSort.Name => matches.OrderBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase),
            _ => matches.OrderByDescending(i => i.Record.LastSwitchedAt ?? i.Record.CreatedAt),
        }).ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();

        Reconcile(Accounts, ordered);

        ShowEmptyState = IsLoaded && _items.Count == 0;
        ShowNoMatches = _items.Count > 0 && ordered.Length == 0;
        var total = $"{_items.Count} {(_items.Count == 1 ? "account" : "accounts")}";
        CountText = ordered.Length == _items.Count ? total : $"{ordered.Length} of {total}";
    }

    private static void Reconcile<T>(ObservableCollection<T> current, IReadOnlyList<T> wanted)
    {
        for (var i = current.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(current[i]))
                current.RemoveAt(i);
        }

        for (var i = 0; i < wanted.Count; i++)
        {
            var from = current.IndexOf(wanted[i]);
            if (from < 0)
                current.Insert(i, wanted[i]);
            else if (from != i)
                current.Move(from, i);
        }
    }

    [RelayCommand]
    private void ClearFilters()
    {
        SearchText = string.Empty;
        SelectedRegion = AllRegions;
    }

    [RelayCommand]
    private async Task AddAccountAsync()
    {
        var model = new AddAccountViewModel(_accounts, _riot, _lcu);
        await _dialogs.ShowAsync(new AddAccountDialog(model));
        await PollAsync();
        if (model.SavedAccount is { } saved)
            Notify($"Saved {saved.DisplayName}", InfoBarSeverity.Success);
    }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        try
        {
            var model = new SettingsViewModel(_accounts, _store, _riot);
            if (await _dialogs.ShowAsync(new SettingsDialog(model)) != ContentDialogResult.Primary)
                return;

            ApplyMonitoring();
            Notify("Settings saved", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Notify($"Settings problem: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    [RelayCommand]
    private Task SyncAsync() => RunAsync("Reading League Client…", "Sync failed", async () =>
    {
        var path = _accounts.Settings.LeagueClientPathOverride;
        var live = await Task.Run(() => _lcu.TryFetchCurrentAccountAsync(path));
        if (live is null)
        {
            Notify("League Client isn't available. Open League and sign in, then sync again.", InfoBarSeverity.Warning);
            return;
        }

        var (account, _) = await _accounts.ApplySnapshotAsync(live);
        if (account is null)
        {
            Notify($"{live.GameName}#{live.TagLine} isn't saved yet. Select Add Account to save it.", InfoBarSeverity.Warning);
        }
        else if (live.RankedSolo is not null)
        {
            Notify($"Synced {account.RankedSolo.FormattedRank} from League Client", InfoBarSeverity.Success);
        }
        else
        {
            Notify($"Synced {account.DisplayName} from League Client (ranked stats unavailable yet)", InfoBarSeverity.Success);
        }
    });

    [RelayCommand]
    private Task RefreshAllAsync() => RefreshViaApiAsync([.. _accounts.Accounts]);

    public Task RefreshViaApiAsync(IReadOnlyList<AccountRecord> targets) => RunAsync("Refreshing via Riot API…", "Refresh failed", async () =>
    {
        var key = _store.UnprotectSecret(_accounts.Settings.ProtectedRiotApiKey);
        if (string.IsNullOrEmpty(key))
        {
            Notify("Add a Riot API key in Settings first.", InfoBarSeverity.Warning);
            return;
        }

        var refreshed = 0;
        string? problem = null;
        foreach (var account in targets)
        {
            Notify($"Refreshing {account.DisplayName}…");
            try
            {
                if (await Task.Run(() => _api.RefreshAccountAsync(account, key)) is { } result)
                {
                    await _accounts.ApplyRefreshAsync(account, result);
                    refreshed++;
                }
                else
                {
                    problem = $"no data for {account.DisplayName}; check the Riot ID, region and API key";
                }
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                problem = ex.Message;
                break;
            }
            catch (Exception ex)
            {
                problem = $"{account.DisplayName}: {ex.Message}";
            }
        }

        if (problem is null)
            Notify(targets.Count == 1 ? $"Refreshed {targets[0].DisplayName}" : $"Refreshed {refreshed} accounts", InfoBarSeverity.Success);
        else
            Notify($"Refreshed {refreshed} of {targets.Count} ({problem})", InfoBarSeverity.Warning);
    });

    public Task SwitchAccountAsync(AccountRecord account) =>
        _items.TryGetValue(account, out var item) ? SwitchAsync(item) : Task.CompletedTask;

    public async Task SwitchAsync(AccountItemViewModel item)
    {
        var account = item.Record;
        var launchOnly = false;

        await RunAsync(null, "Switch failed", async () =>
        {
            var owner = await _accounts.ResolveSessionOwnerAsync();
            var running = await Task.Run(_riot.IsLeagueClientRunning);
            var inMatch = await Task.Run(_riot.IsMatchRunning);

            var allowCloseMatch = false;
            var allowDiscardUnsaved = false;
            string? confirmedDiscardPuuid = null;

            launchOnly = owner == account && !running;
            if (!launchOnly)
            {
                if (inMatch)
                {
                    if (!await _dialogs.ConfirmAsync("A match is running",
                            "Continuing closes the game, and you may be penalized for leaving.", "Close game and continue"))
                        return;
                    allowCloseMatch = true;
                }

                if (owner is null && await Task.Run(_riot.HasActiveRiotSession))
                {
                    var unsavedPuuid = await _riot.TryReadActiveSessionPuuidAsync() ?? string.Empty;
                    if (!await _dialogs.ConfirmAsync("Current sign-in isn't saved",
                            "The account signed in now will be replaced and you'll need to sign in to it again. To keep it, cancel and use Add Account first.", "Switch anyway"))
                        return;
                    allowDiscardUnsaved = true;
                    confirmedDiscardPuuid = unsavedPuuid;
                }

                if (owner == account && !inMatch && !await _dialogs.ConfirmAsync("Restart League?",
                        $"League is already running on {account.DisplayName}. This closes and relaunches it.", "Restart"))
                    return;
            }

            if (launchOnly)
            {
                Notify($"Launching {account.RiotId}…");
                await _accounts.LaunchAsync(account);
                Notify($"Launched {account.RiotId}", InfoBarSeverity.Success);
            }
            else
            {
                Notify($"Switching to {account.RiotId}…");
                await _accounts.SwitchAsync(
                    account,
                    new Progress<string>(text =>
                    {
                        if (_accounts.IsOperating)
                            Notify(text);
                    }),
                    allowCloseRunningMatch: allowCloseMatch,
                    allowDiscardUnsavedSession: allowDiscardUnsaved,
                    confirmedDiscardPuuid: confirmedDiscardPuuid);
                Notify($"Switched to {account.RiotId}", InfoBarSeverity.Success);
            }
        });

        await PollAsync();
    }

    public async Task RecaptureAsync(AccountItemViewModel item)
    {
        if (!await _dialogs.ConfirmAsync("Re-capture current session?",
                $"This replaces the saved sign-in for {item.Record.DisplayName}. Make sure Riot Client is signed in to that account now.",
                "Re-capture"))
            return;

        await RunAsync("Capturing session…", "Re-capture failed", async () =>
        {
            await _accounts.RecaptureAsync(item.Record);
            Notify($"Re-captured {item.Record.DisplayName}", InfoBarSeverity.Success);
        });
    }

    public async Task EditAsync(AccountItemViewModel item)
    {
        var details = new AccountDetailsViewModel(item.Record);
        await _dialogs.ShowAsync(new EditAccountDialog(details, () => _accounts.EditAsync(item.Record, details.ApplyChangesTo)));
    }

    public async Task RemoveAsync(AccountItemViewModel item)
    {
        var name = item.Record.DisplayName;
        if (!await _dialogs.ConfirmAsync($"Remove {name}?",
                "This deletes its saved session and details from this PC. The Riot account itself is not affected.",
                "Remove"))
            return;

        await RunAsync("Removing account…", "Remove failed", async () =>
        {
            await _accounts.RemoveAsync(item.Record);
            Notify($"Removed {name}", InfoBarSeverity.Success);
        });
    }

    private async Task RunAsync(string? progress, string failure, Func<Task> action)
    {
        if (!CanInteract)
            return;

        IsBusy = true;
        try
        {
            if (progress is not null)
                Notify(progress);
            await action();
        }
        catch (Exception ex)
        {
            Notify($"{failure}: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Notify(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        _bannerTimer.Stop();
        BannerSeverity = severity;
        BannerMessage = message;
        IsBannerOpen = true;
        if (severity == InfoBarSeverity.Success)
            _bannerTimer.Start();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _pollTimer.Stop();
        _bannerTimer.Stop();
        _accounts.Changed -= OnAccountsChanged;
        _lcu.ActiveAccountSynced -= OnLcuSynced;
        _lcu.ConnectionStateChanged -= OnLcuConnectionChanged;
    }
}
