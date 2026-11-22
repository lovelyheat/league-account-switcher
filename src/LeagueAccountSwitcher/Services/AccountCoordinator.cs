using LeagueAccountSwitcher.Core.Models;
using LeagueAccountSwitcher.Core.Services;
using LeagueAccountSwitcher.Helpers;

namespace LeagueAccountSwitcher.Services;

/// <summary>
/// Owns the saved accounts and the active-account pointer. Call from the UI thread.
/// The pointer decides which snapshot the live session is backed up over, so when in doubt it is cleared, never guessed.
/// </summary>
public sealed class AccountCoordinator(IAccountStore store, IRiotClientService riot)
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private List<AccountRecord> _accounts = [];

    public event EventHandler? Changed;

    public IReadOnlyList<AccountRecord> Accounts => _accounts;
    public AppSettings Settings { get; private set; } = new();

    /// <summary>True while the session file is being replaced; observed identities are unreliable until it ends.</summary>
    public bool IsOperating { get; private set; }

    public AccountRecord? Active => _accounts.Find(a => a.Id == Settings.ActiveAccountId);

    public async Task LoadAsync()
    {
        var settings = await store.LoadSettingsAsync();
        var accounts = (await store.LoadAccountsAsync()).ToList();
        foreach (var account in accounts)
        {
            account.RankedSolo ??= RankedSoloInfo.Unranked;
            account.HasSavedSession = store.HasSessionSnapshot(account.Id);
        }

        (Settings, _accounts) = (settings, accounts);
        NotifyChanged();
    }

    public AccountRecord? FindByPuuid(string? puuid) => string.IsNullOrEmpty(puuid)
        ? null
        : _accounts.Find(a => string.Equals(a.Puuid, puuid, StringComparison.OrdinalIgnoreCase));

    /// <summary>The saved account that owns the live session file, or null if that can't be established.</summary>
    public async Task<AccountRecord?> ResolveSessionOwnerAsync()
    {
        if (!riot.HasActiveRiotSession())
            return null;

        var puuid = await riot.TryReadActiveSessionPuuidAsync();
        return string.IsNullOrEmpty(puuid) ? Active : FindByPuuid(puuid);
    }

    public async Task<bool> HasUnsavedSessionAsync() =>
        riot.HasActiveRiotSession() && await ResolveSessionOwnerAsync() is null;

    /// <summary>Follows sign-ins made directly in Riot Client.</summary>
    public async Task FollowLiveSessionAsync()
    {
        if (IsOperating)
            return;

        var puuid = await riot.TryReadActiveSessionPuuidAsync();
        if (!IsOperating && !string.IsNullOrEmpty(puuid))
            await SetActiveAsync(FindByPuuid(puuid));
    }

    public async Task SwitchAsync(AccountRecord target, IProgress<string>? progress)
    {
        if (!store.HasSessionSnapshot(target.Id))
            throw new InvalidOperationException("No saved session for this account. Sign in to it, then choose Re-capture Current Session.");

        await ReplaceSessionAsync(currentId => riot.SwitchToAccountAsync(
            target, currentId, Settings.LaunchTarget, Settings.RiotClientPathOverride, progress), target);
    }

    public Task PrepareFreshLoginAsync(IProgress<string>? progress) =>
        ReplaceSessionAsync(currentId => riot.PrepareFreshLoginAsync(
            currentId, Settings.RiotClientPathOverride, progress), next: null);

    private async Task ReplaceSessionAsync(Func<string?, Task> replace, AccountRecord? next)
    {
        IsOperating = true;
        try
        {
            var previous = await ResolveSessionOwnerAsync();

            // Cleared first: if this fails halfway, a later backup must not write an unknown session over the previous account.
            Settings.ActiveAccountId = null;
            await SaveSettingsAsync();

            await Task.Run(() => replace(previous?.Id));

            if (previous is not null)
                previous.HasSavedSession = store.HasSessionSnapshot(previous.Id);

            if (next is not null)
            {
                Settings.ActiveAccountId = next.Id;
                next.LastSwitchedAt = DateTimeOffset.UtcNow;

                // The file is now exactly this account's snapshot, so its identity can be adopted.
                var puuid = await riot.TryReadActiveSessionPuuidAsync();
                if (string.IsNullOrEmpty(next.Puuid) && !string.IsNullOrEmpty(puuid) && FindByPuuid(puuid) is null)
                    next.Puuid = puuid;

                await SaveSettingsAsync();
            }

            await SaveAccountsAsync();
        }
        finally
        {
            IsOperating = false;
            NotifyChanged();
        }
    }

    public async Task LaunchAsync(AccountRecord account)
    {
        var (target, path) = (Settings.LaunchTarget, Settings.RiotClientPathOverride);
        await Task.Run(() => riot.LaunchClientAsync(target, path));

        account.LastSwitchedAt = DateTimeOffset.UtcNow;
        await SaveAccountsAsync();
        NotifyChanged();
    }

    /// <summary>Saves the live session as a new account, or over the saved account it belongs to.</summary>
    public async Task<AccountRecord> CaptureAsync(LcuSnapshot? live, Action<AccountRecord> applyDetails)
    {
        var bytes = await CaptureBytesAsync();
        var puuid = await riot.TryReadActiveSessionPuuidAsync() ?? live?.Puuid;

        var entered = new AccountRecord();
        applyDetails(entered);

        var account = FindByPuuid(puuid)
            ?? _accounts.Find(a => string.IsNullOrEmpty(a.Puuid) && entered.GameName.Length > 0
                && string.Equals(a.RiotId, entered.RiotId, StringComparison.OrdinalIgnoreCase));
        var isNew = account is null;
        account ??= new AccountRecord();

        await store.SaveSessionSnapshotAsync(account.Id, bytes);

        if (isNew)
            _accounts.Add(account);
        if (live is not null)
            ApplyLive(account, live);
        applyDetails(account);
        if (!string.IsNullOrEmpty(puuid))
            account.Puuid = puuid;

        await MarkCapturedAsync(account);
        return account;
    }

    public async Task RecaptureAsync(AccountRecord account)
    {
        var bytes = await CaptureBytesAsync();
        var puuid = await riot.TryReadActiveSessionPuuidAsync();
        if (!string.IsNullOrEmpty(puuid))
        {
            var owner = FindByPuuid(puuid);
            var differentPuuid = !string.IsNullOrEmpty(account.Puuid)
                && !string.Equals(account.Puuid, puuid, StringComparison.OrdinalIgnoreCase);

            if (differentPuuid || (owner is not null && owner != account))
                throw new InvalidOperationException($"Riot Client is signed in to a different account. Sign in to {account.DisplayName} first.");
        }

        await store.SaveSessionSnapshotAsync(account.Id, bytes);
        if (!string.IsNullOrEmpty(puuid))
            account.Puuid = puuid;

        await MarkCapturedAsync(account);
    }

    private async Task<byte[]> CaptureBytesAsync()
    {
        if (!riot.HasActiveRiotSession())
            throw new InvalidOperationException("No saved Riot sign-in found. Sign in to Riot Client with \"Stay signed in\" checked.");

        return await riot.CaptureCurrentSessionBytesAsync();
    }

    private async Task MarkCapturedAsync(AccountRecord account)
    {
        account.HasSavedSession = true;
        account.LastSwitchedAt = DateTimeOffset.UtcNow;
        await SaveAccountsAsync();

        Settings.ActiveAccountId = account.Id;
        await SaveSettingsAsync();
        NotifyChanged();
    }

    /// <returns>The matching account (null if unsaved) and whether anything visible changed.</returns>
    public async Task<(AccountRecord? Account, bool Updated)> ApplySnapshotAsync(LcuSnapshot live)
    {
        if (string.IsNullOrEmpty(live.Puuid))
            return (null, false);

        var account = FindByPuuid(live.Puuid);

        // An account captured before its PUUID was known can only be claimed while it is the active one.
        if (account is null && Active is { } active && string.IsNullOrEmpty(active.Puuid)
            && (active.GameName.Length == 0
                || string.Equals(active.RiotId, $"{live.GameName}#{live.TagLine}", StringComparison.OrdinalIgnoreCase)))
        {
            account = active;
        }

        var updated = false;
        if (account is not null)
        {
            var learnedPuuid = string.IsNullOrEmpty(account.Puuid);
            if (learnedPuuid)
                account.Puuid = live.Puuid;

            updated = ApplyLive(account, live) || learnedPuuid;
            if (updated)
            {
                await SaveAccountsAsync();
                NotifyChanged();
            }
        }

        if (!IsOperating)
            await SetActiveAsync(account);

        return (account, updated);
    }

    public async Task ApplyRefreshAsync(AccountRecord account, AccountRefreshResult result)
    {
        // result.Puuid is deliberately ignored: Riot API PUUIDs are encrypted per key and never match the client's.
        ApplyStats(account, result.GameName, result.TagLine, result.SummonerLevel, result.ProfileIconId, result.RankedSolo, result.FetchedAt);
        await SaveAccountsAsync();
        NotifyChanged();
    }

    private static bool ApplyLive(AccountRecord account, LcuSnapshot live)
    {
        var region = Regions.Known(live.Region);

        var rank = HasKnownRank(live) ? live.RankedSolo : account.RankedSolo;

        var changed = ApplyStats(account, live.GameName, live.TagLine, live.SummonerLevel, live.ProfileIconId, rank, live.FetchedAt);
        if (region is null || region == account.Region)
            return changed;

        account.Region = region;
        return true;
    }

    /// <summary>A fetch during client start-up reports placeholders: no region, and Unranked in place of the real rank.</summary>
    public static bool HasKnownRank(LcuSnapshot live) =>
        live.RankedSolo.IsRanked || Regions.Known(live.Region) is not null;

    private static bool ApplyStats(AccountRecord a, string name, string tag, int level, int icon, RankedSoloInfo rank, DateTimeOffset at)
    {
        var before = (a.GameName, a.TagLine, a.SummonerLevel, a.ProfileIconId, a.RankedSolo);

        if (!string.IsNullOrWhiteSpace(name))
            (a.GameName, a.TagLine) = (name, tag);
        if (level > 0)
            a.SummonerLevel = level;
        if (icon >= 0)
            a.ProfileIconId = icon;
        a.RankedSolo = rank;
        a.LastSyncedAt = at;

        return before != (a.GameName, a.TagLine, a.SummonerLevel, a.ProfileIconId, a.RankedSolo);
    }

    public async Task EditAsync(AccountRecord account, Action<AccountRecord> edit)
    {
        edit(account);
        await SaveAccountsAsync();
        NotifyChanged();
    }

    public async Task RemoveAsync(AccountRecord account)
    {
        var index = _accounts.IndexOf(account);
        if (index < 0)
            return;

        _accounts.RemoveAt(index);
        try
        {
            await SaveAccountsAsync();
        }
        catch
        {
            _accounts.Insert(index, account);
            throw;
        }

        if (Settings.ActiveAccountId == account.Id)
        {
            Settings.ActiveAccountId = null;
            await SaveSettingsAsync();
        }

        NotifyChanged();
        await store.DeleteSessionSnapshotAsync(account.Id);
    }

    public async Task UpdateSettingsAsync(Action<AppSettings> update)
    {
        update(Settings);
        await SaveSettingsAsync();
        NotifyChanged();
    }

    private async Task SetActiveAsync(AccountRecord? account)
    {
        if (Settings.ActiveAccountId == account?.Id)
            return;

        Settings.ActiveAccountId = account?.Id;
        NotifyChanged();
        await SaveSettingsAsync();
    }

    private Task SaveAccountsAsync() => SerializedAsync(() => store.SaveAccountsAsync(_accounts.ToArray()));

    private Task SaveSettingsAsync() => SerializedAsync(() => store.SaveSettingsAsync(Settings));

    private async Task SerializedAsync(Func<Task> save)
    {
        await _saveGate.WaitAsync();
        try { await save(); }
        finally { _saveGate.Release(); }
    }

    private void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
