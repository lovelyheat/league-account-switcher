using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeagueAccountSwitcher.Core.Models;
using LeagueAccountSwitcher.Core.Services;
using LeagueAccountSwitcher.Services;
using Microsoft.UI.Xaml.Controls;

namespace LeagueAccountSwitcher.ViewModels;

public sealed partial class AddAccountViewModel(
    AccountCoordinator accounts,
    IRiotClientService riot,
    ILcuService lcu) : ObservableObject
{
    private LcuSnapshot? _live;
    private string? _detectedPuuid;
    private bool _discardConfirmed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCaptureMode), nameof(PrimaryText), nameof(CanSubmit), nameof(ShowDetails))]
    private bool _isSignInMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit), nameof(ShowDetails))]
    private bool _hasSession;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    private bool _isLoginOpen;

    [ObservableProperty] private AccountDetailsViewModel _details = new(new AccountRecord());
    [ObservableProperty] private string _nickname = string.Empty;
    [ObservableProperty] private string _openLoginText = "Open Clean Login";
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private InfoBarSeverity _severity;

    public AccountRecord? SavedAccount { get; private set; }

    public bool IsCaptureMode => !IsSignInMode;
    public bool ShowDetails => IsCaptureMode && HasSession;
    public string PrimaryText => IsSignInMode ? "Capture New Session" : "Save Account";
    public bool CanSubmit => !IsBusy && (IsSignInMode ? IsLoginOpen : HasSession);

    partial void OnIsSignInModeChanged(bool value)
    {
        Message = string.Empty;

        // Re-read only if this dialog changed the session, so typed details otherwise survive a tab change.
        if (!value && IsLoginOpen)
            DetectCommand.Execute(null);
    }

    [RelayCommand]
    private Task DetectAsync() => RunAsync(DetectCoreAsync);

    private async Task DetectCoreAsync()
    {
        Show("Looking for a signed-in Riot session…");
        (_live, _detectedPuuid, HasSession) = (null, null, false);

        if (!riot.HasActiveRiotSession())
        {
            Show("No saved Riot sign-in found. Sign in to Riot Client with \"Stay signed in\" checked, or choose Sign In to New Account.", InfoBarSeverity.Warning);
            return;
        }

        _detectedPuuid = await riot.TryReadActiveSessionPuuidAsync();

        LcuSnapshot? live;
        try
        {
            var path = accounts.Settings.LeagueClientPathOverride;
            live = await Task.Run(() => lcu.TryFetchCurrentAccountAsync(path));
        }
        catch (Exception)
        {
            // Pre-fill is optional; capturing only needs the session file.
            live = null;
        }

        if (live is not null && (string.IsNullOrEmpty(_detectedPuuid)
            || string.Equals(live.Puuid, _detectedPuuid, StringComparison.OrdinalIgnoreCase)))
        {
            _live = live;
        }

        var existing = accounts.FindByPuuid(_detectedPuuid ?? _live?.Puuid);
        var regionHint = await riot.TryReadActiveSessionRegionAsync();

        Details = new AccountDetailsViewModel(new AccountRecord
        {
            Alias = existing?.Alias,
            GameName = _live?.GameName ?? existing?.GameName ?? string.Empty,
            TagLine = _live?.TagLine ?? existing?.TagLine ?? string.Empty,
            Region = Helpers.Regions.Known(_live?.Region) ?? Helpers.Regions.Known(existing?.Region) ?? regionHint ?? "Unknown",
            SummonerLevel = _live?.SummonerLevel ?? existing?.SummonerLevel,
            RankedSolo = (_live is not null && AccountCoordinator.HasKnownRank(_live) ? _live.RankedSolo : existing?.RankedSolo)
                ?? RankedSoloInfo.Unranked,
        });
        HasSession = true;

        if (existing is not null)
            Show($"{existing.DisplayName} is already saved. Saving updates its session and details.");
        else if (_live is not null)
            Show($"Found {_live.GameName}#{_live.TagLine} in League Client.", InfoBarSeverity.Success);
        else
            Show("Session found. Level and rank fill in automatically the next time League Client opens.", InfoBarSeverity.Success);
    }

    [RelayCommand]
    private Task OpenLoginAsync() => RunAsync(async () =>
    {
        if (!_discardConfirmed && await accounts.HasUnsavedSessionAsync())
        {
            _discardConfirmed = true;
            OpenLoginText = "Continue Without Saving";
            Show("The account signed in now isn't saved and would be lost. Capture it first, or continue without saving.", InfoBarSeverity.Warning);
            return;
        }

        IsLoginOpen = false;
        _discardConfirmed = false;
        Show("Opening a clean Riot Client login…");
        await accounts.PrepareFreshLoginAsync(new Progress<string>(text =>
        {
            if (IsBusy && !IsLoginOpen)
                Show(text);
        }));

        IsLoginOpen = true;
        OpenLoginText = "Open Clean Login Again";
        Show("Riot Client is open. Sign in with \"Stay signed in\" checked, then select Capture New Session.", InfoBarSeverity.Success);
    });

    public async Task<bool> SaveAsync()
    {
        if (!CanSubmit)
            return false;

        await RunAsync(async () =>
        {
            if (IsSignInMode)
            {
                await DetectCoreAsync();
                if (!HasSession)
                {
                    Show("Not signed in yet. Finish signing in to Riot Client with \"Stay signed in\" checked, then try again.", InfoBarSeverity.Warning);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(Nickname))
                    Details.Alias = Nickname;
            }
            else if (!string.Equals(await riot.TryReadActiveSessionPuuidAsync(), _detectedPuuid, StringComparison.OrdinalIgnoreCase))
            {
                await DetectCoreAsync();
                Show("The signed-in account changed. Check the details, then save again.", InfoBarSeverity.Warning);
                return;
            }

            SavedAccount = await accounts.CaptureAsync(_live, Details.ApplyTo);
        });

        return SavedAccount is not null;
    }

    private async Task RunAsync(Func<Task> action)
    {
        IsBusy = true;
        try { await action(); }
        catch (Exception ex) { Show(ex.Message, InfoBarSeverity.Error); }
        finally { IsBusy = false; }
    }

    private void Show(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        Severity = severity;
        Message = message;
    }
}
