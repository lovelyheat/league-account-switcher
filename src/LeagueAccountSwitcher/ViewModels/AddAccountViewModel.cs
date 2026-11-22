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
    private string? _confirmedDiscardPuuid;
    private bool _matchCloseConfirmed;

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
        ResetLoginConfirmations();

        // Re-read only if this dialog changed the session, so typed details otherwise survive a tab change.
        if (!value && IsLoginOpen)
            DetectCommand.Execute(null);
    }

    [RelayCommand]
    private Task DetectAsync() => RunAsync(() => DetectCoreAsync(includeRegionHint: !IsSignInMode));

    private async Task DetectCoreAsync(bool includeRegionHint)
    {
        Show("Looking for a signed-in Riot session…");
        (_live, _detectedPuuid, HasSession) = (null, null, false);
        ResetLoginConfirmations();

        if (!await Task.Run(riot.HasActiveRiotSession))
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
        // Never silently adopt the machine's global RiotClientSettings region in Sign-In mode; only offer as an editable hint in Capture mode.
        var regionHint = includeRegionHint ? await riot.TryReadActiveSessionRegionAsync() : null;

        Details = new AccountDetailsViewModel(new AccountRecord
        {
            Alias = existing?.Alias,
            GameName = NonEmpty(_live?.GameName) ?? NonEmpty(existing?.GameName) ?? string.Empty,
            TagLine = NonEmpty(_live?.TagLine) ?? NonEmpty(existing?.TagLine) ?? string.Empty,
            Region = Helpers.Regions.Known(_live?.Region)
                ?? Helpers.Regions.Known(existing?.Region)
                ?? Helpers.Regions.Known(regionHint)
                ?? "Unknown",
            SummonerLevel = _live?.SummonerLevel is > 0 ? _live.SummonerLevel : existing?.SummonerLevel,
            RankedSolo = _live?.RankedSolo ?? existing?.RankedSolo ?? RankedSoloInfo.Unranked,
            HasRankData = _live?.RankedSolo is not null || (existing?.HasRankData ?? false),
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
        var inMatch = await Task.Run(riot.IsMatchRunning);
        if (inMatch && !_matchCloseConfirmed)
        {
            _matchCloseConfirmed = true;
            OpenLoginText = "Close Game and Open Login";
            Show("A League of Legends match is running. Continuing closes the game and may incur a leaver penalty. Select again to confirm.", InfoBarSeverity.Warning);
            return;
        }

        var hasUnsaved = await accounts.HasUnsavedSessionAsync();
        var allowDiscard = false;
        string? confirmedDiscardPuuid = null;
        if (hasUnsaved)
        {
            var liveKey = await riot.TryReadActiveSessionPuuidAsync() ?? string.Empty;
            if (!string.Equals(_confirmedDiscardPuuid, liveKey, StringComparison.OrdinalIgnoreCase))
            {
                _confirmedDiscardPuuid = liveKey;
                OpenLoginText = inMatch ? "Close Game & Continue Without Saving" : "Continue Without Saving";
                Show("The account signed in now isn't saved and would be lost. Capture it first, or select again to continue without saving.", InfoBarSeverity.Warning);
                return;
            }

            allowDiscard = true;
            confirmedDiscardPuuid = liveKey;
        }

        var allowCloseMatch = inMatch && _matchCloseConfirmed;
        IsLoginOpen = false;
        ResetLoginConfirmations();
        Show("Opening a clean Riot Client login…");
        await accounts.PrepareFreshLoginAsync(
            new Progress<string>(text =>
            {
                if (IsBusy && !IsLoginOpen)
                    Show(text);
            }),
            allowCloseRunningMatch: allowCloseMatch,
            allowDiscardUnsavedSession: allowDiscard,
            confirmedDiscardPuuid: confirmedDiscardPuuid);

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
                await DetectCoreAsync(includeRegionHint: false);
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
                await DetectCoreAsync(includeRegionHint: true);
                Show("The signed-in account changed. Check the details, then save again.", InfoBarSeverity.Warning);
                return;
            }

            SavedAccount = await accounts.CaptureAsync(_live, _detectedPuuid, Details.ApplyTo);
        });

        return SavedAccount is not null;
    }

    private void ResetLoginConfirmations()
    {
        _confirmedDiscardPuuid = null;
        _matchCloseConfirmed = false;
        OpenLoginText = IsLoginOpen ? "Open Clean Login Again" : "Open Clean Login";
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

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
