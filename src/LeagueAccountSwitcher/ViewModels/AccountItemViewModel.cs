using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeagueAccountSwitcher.Core.Models;
using LeagueAccountSwitcher.Core.Services;

namespace LeagueAccountSwitcher.ViewModels;

public sealed partial class AccountItemViewModel : ObservableObject
{
    private readonly IAssetCacheService _assets;
    private int? _iconId;

    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _tag = string.Empty;
    [ObservableProperty] private string _subtitle = string.Empty;
    [ObservableProperty] private string _initials = string.Empty;
    [ObservableProperty] private string _region = string.Empty;
    [ObservableProperty] private string _level = string.Empty;
    [ObservableProperty] private string _tier = "UNRANKED";
    [ObservableProperty] private string _rank = string.Empty;
    [ObservableProperty] private string _winLoss = string.Empty;
    [ObservableProperty] private string _syncedText = string.Empty;
    // Empty, never null: x:Bind skips a function with a null argument, leaving a recycled card showing another account's icon.
    [ObservableProperty] private string _avatarPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionText), nameof(ActionHint), nameof(IsRunning))]
    private bool _isActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionText), nameof(ActionHint), nameof(IsRunning))]
    private bool _isLeagueRunning;

    public AccountItemViewModel(AccountRecord record, MainViewModel owner, IAssetCacheService assets)
    {
        Record = record;
        Owner = owner;
        _assets = assets;
        Update();
    }

    public AccountRecord Record { get; }
    public MainViewModel Owner { get; }

    public bool IsRunning => IsActive && IsLeagueRunning;
    public string ActionText => !IsActive ? "Switch" : IsLeagueRunning ? "Active" : "Launch";

    public string ActionHint => !IsActive ? "Switch to this account and launch"
        : IsLeagueRunning ? "League is running on this account. Select to restart it."
        : "Launch with this account";

    public void Update()
    {
        var hasAlias = !string.IsNullOrWhiteSpace(Record.Alias);
        var hasName = !string.IsNullOrWhiteSpace(Record.GameName);

        Title = hasAlias ? Record.Alias! : hasName ? Record.GameName : Record.RiotId;
        Tag = !hasAlias && hasName && !string.IsNullOrWhiteSpace(Record.TagLine) ? $"#{Record.TagLine}" : string.Empty;
        Subtitle = hasAlias && hasName ? Record.RiotId : string.Empty;
        Initials = hasAlias || hasName ? StringInfo.GetNextTextElement(Title.Trim()).ToUpperInvariant() : string.Empty;

        Region = Helpers.Regions.Known(Record.Region) ?? "—";
        Level = Record.SummonerLevel is > 0 and var level ? $"Lv. {level}" : "Lv. —";

        Tier = Record.RankedSolo.NormalizedTier;
        Rank = Record.RankedSolo.FormattedRank;
        WinLoss = Record.RankedSolo.FormattedRecord;
        SyncedText = Record.LastSyncedAt is { } at
            ? $"Ranked Solo/Duo, synced {at.LocalDateTime:g}"
            : "Ranked Solo/Duo, not synced yet";

        _ = LoadAvatarAsync();
    }

    private async Task LoadAvatarAsync()
    {
        var id = Record.ProfileIconId;
        if (id is null || (id == _iconId && AvatarPath.Length > 0))
            return;

        _iconId = id;
        try
        {
            var path = await _assets.GetProfileIconPathAsync(id);
            if (id == _iconId)
                AvatarPath = path ?? string.Empty;
        }
        catch (Exception ex)
        {
            // The monogram stays; the next update retries.
            Debug.WriteLine(ex);
        }
    }

    [RelayCommand] private Task SwitchAsync() => Owner.SwitchAsync(this);
    [RelayCommand] private Task RecaptureAsync() => Owner.RecaptureAsync(this);
    [RelayCommand] private Task RefreshAsync() => Owner.RefreshViaApiAsync([Record]);
    [RelayCommand] private Task EditAsync() => Owner.EditAsync(this);
    [RelayCommand] private Task RemoveAsync() => Owner.RemoveAsync(this);
}
