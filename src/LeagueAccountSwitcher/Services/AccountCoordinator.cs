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
    private static readonly TimeSpan SyncTimestampSaveInterval = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> _lastPersistedSync = [];
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
            if (account.RankedSolo.IsRanked || account.RankedSolo.TotalGames > 0)
                account.HasRankData = true;
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
        if (!await Task.Run(riot.HasActiveRiotSession))
            return null;

        var puuid = await riot.TryReadActiveSessionPuuidAsync();
        if (!string.IsNullOrEmpty(puuid))
            return FindByPuuid(puuid);

        // Only fall back to the active pointer if that account itself has no known client PUUID.
        return Active is { } active && !Guid.TryParse(active.Puuid, out _) ? active : null;
    }

    public async Task<bool> HasUnsavedSessionAsync() =>
        await Task.Run(riot.HasActiveRiotSession) && await ResolveSessionOwnerAsync() is null;

    /// <summary>Follows sign-ins made directly in Riot Client.</summary>
    public async Task FollowLiveSessionAsync()
    {
        if (IsOperating)
            return;

        var puuid = await riot.TryReadActiveSessionPuuidAsync();
        if (!IsOperating && !string.IsNullOrEmpty(puuid))
            await SetActiveAsync(FindByPuuid(puuid));
    }

    public async Task SwitchAsync(
        AccountRecord target,
        IProgress<string>? progress,
        bool allowCloseRunningMatch = false,
        bool allowDiscardUnsavedSession = false,
        string? confirmedDiscardPuuid = null)
    {
        if (!store.HasSessionSnapshot(target.Id))
            throw new InvalidOperationException("No saved session for this account. Sign in to it, then choose Re-capture Current Session.");

        await ReplaceSessionAsync(currentId => riot.SwitchToAccountAsync(
            target,
            currentId,
            Settings.LaunchTarget,
            Settings.RiotClientPathOverride,
            progress,
            allowCloseRunningMatch: allowCloseRunningMatch,
            allowDiscardUnsavedSession: allowDiscardUnsavedSession,
            confirmedDiscardPuuid: confirmedDiscardPuuid), target);
    }

    public Task PrepareFreshLoginAsync(
        IProgress<string>? progress,
        bool allowCloseRunningMatch = false,
        bool allowDiscardUnsavedSession = false,
        string? confirmedDiscardPuuid = null) =>
        ReplaceSessionAsync(currentId => riot.PrepareFreshLoginAsync(
            currentId,
            Settings.RiotClientPathOverride,
            progress,
            allowCloseRunningMatch: allowCloseRunningMatch,
            allowDiscardUnsavedSession: allowDiscardUnsavedSession,
            confirmedDiscardPuuid: confirmedDiscardPuuid), next: null);

    private async Task ReplaceSessionAsync(Func<string?, Task> replace, AccountRecord? next)
    {
        IsOperating = true;
        try
        {
            var previous = await ResolveSessionOwnerAsync();
            var previousActiveId = Settings.ActiveAccountId;
            var preLiveBytes = await TryCaptureBytesBestEffortAsync();

            // Cleared first: if this fails halfway, a later backup must not write an unknown session over the previous account.
            Settings.ActiveAccountId = null;
            await SaveSettingsAsync();

            try
            {
                await Task.Run(() => replace(previous?.Id));
            }
            catch
            {
                // If the operation aborted before altering the live session file, restore the previous active pointer.
                if (previousActiveId is not null && preLiveBytes is not null)
                {
                    var currentLiveBytes = await TryCaptureBytesBestEffortAsync();
                    if (currentLiveBytes is not null && currentLiveBytes.AsSpan().SequenceEqual(preLiveBytes))
                    {
                        Settings.ActiveAccountId = previousActiveId;
                        await SaveSettingsAsync();
                    }
                }

                throw;
            }

            if (previous is not null)
                previous.HasSavedSession = store.HasSessionSnapshot(previous.Id);

            if (next is not null)
            {
                Settings.ActiveAccountId = next.Id;
                next.LastSwitchedAt = DateTimeOffset.UtcNow;

                // Post-launch identity adoption is best-effort: a transient startup lock must not fail a completed switch.
                try
                {
                    var puuid = await riot.TryReadActiveSessionPuuidAsync();
                    if (string.IsNullOrEmpty(next.Puuid) && !string.IsNullOrEmpty(puuid) && FindByPuuid(puuid) is null)
                        next.Puuid = puuid;
                }
                catch (IOException)
                {
                }

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

    private async Task<byte[]?> TryCaptureBytesBestEffortAsync()
    {
        try { return (await riot.CaptureCurrentSessionAsync()).Bytes; }
        catch { return null; }
    }

    public async Task LaunchAsync(AccountRecord account)
    {
        var (target, path) = (Settings.LaunchTarget, Settings.RiotClientPathOverride);
        await Task.Run(() => riot.LaunchClientAsync(target, path));

        account.LastSwitchedAt = DateTimeOffset.UtcNow;
        await SaveAccountsAsync();
        NotifyChanged();
    }

    /// <summary>Saves the live session as a new account, or over the saved account it belongs to, using one atomic read.</summary>
    public async Task<AccountRecord> CaptureAsync(LcuSnapshot? live, string? expectedPuuid, Action<AccountRecord> applyDetails)
    {
        var captured = await riot.CaptureCurrentSessionAsync();
        var sessionPuuid = captured.Puuid;

        if (!string.IsNullOrEmpty(expectedPuuid)
            && !string.Equals(expectedPuuid, sessionPuuid, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The signed-in Riot account changed before saving. Check the details and try again.");
        }

        // Only trust LCU data when the captured session bytes prove it belongs to the same account.
        var matchingLive = live is not null && !string.IsNullOrEmpty(sessionPuuid)
            && string.Equals(live.Puuid, sessionPuuid, StringComparison.OrdinalIgnoreCase)
            ? live
            : null;

        var entered = new AccountRecord();
        applyDetails(entered);

        // Fail closed: never match an account that has a known client PUUID unless the captured bytes prove that exact PUUID.
        var account = FindByPuuid(sessionPuuid)
            ?? _accounts.Find(a => !Guid.TryParse(a.Puuid, out _) && entered.GameName.Length > 0
                && string.Equals(a.RiotId, entered.RiotId, StringComparison.OrdinalIgnoreCase));
        var isNew = account is null;
        account ??= new AccountRecord();

        await store.SaveSessionSnapshotAsync(account.Id, captured.Bytes);

        if (isNew)
            _accounts.Add(account);
        if (matchingLive is not null)
        {
            ApplyLive(account, matchingLive);
        }
        else if (await riot.TryReadLiveRiotClientAccountAsync() is { } rc
            && !string.IsNullOrEmpty(sessionPuuid)
            && string.Equals(rc.Puuid, sessionPuuid, StringComparison.OrdinalIgnoreCase))
        {
            if (rc.ProfileIconId is >= 0)
                account.ProfileIconId = rc.ProfileIconId;
            if (rc.SummonerLevel is > 0 && account.SummonerLevel is null)
                account.SummonerLevel = rc.SummonerLevel;
            if (Regions.Known(rc.Region) is { } rcRegion && Regions.Known(account.Region) is null)
                account.Region = rcRegion;
        }
        applyDetails(account);
        if (!string.IsNullOrEmpty(sessionPuuid))
            account.Puuid = sessionPuuid;

        await MarkCapturedAsync(account);
        return account;
    }

    public async Task RecaptureAsync(AccountRecord account)
    {
        var captured = await riot.CaptureCurrentSessionAsync();
        var puuid = captured.Puuid;

        var accountHasClientPuuid = Guid.TryParse(account.Puuid, out _);
        if (accountHasClientPuuid)
        {
            if (string.IsNullOrEmpty(puuid) || !string.Equals(account.Puuid, puuid, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Riot Client is signed in to a different account (or identity could not be verified). Sign in to {account.DisplayName} first.");
        }
        else if (!string.IsNullOrEmpty(puuid))
        {
            var owner = FindByPuuid(puuid);
            if (owner is not null && owner != account)
                throw new InvalidOperationException($"Riot Client is signed in to {owner.DisplayName}. Sign in to {account.DisplayName} first.");
        }

        await store.SaveSessionSnapshotAsync(account.Id, captured.Bytes);
        if (!string.IsNullOrEmpty(puuid))
            account.Puuid = puuid;

        await MarkCapturedAsync(account);
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
        var verificationUnavailable = false;

        // An account captured before its PUUID was known can only be claimed while it is active AND the live session file confirms it isn't a stale outgoing LCU.
        if (account is null && Active is { } active && string.IsNullOrEmpty(active.Puuid)
            && (active.GameName.Length == 0
                || string.Equals(active.RiotId, $"{live.GameName}#{live.TagLine}", StringComparison.OrdinalIgnoreCase)))
        {
            string? liveSessionPuuid = null;
            try { liveSessionPuuid = await riot.TryReadActiveSessionPuuidAsync(); }
            catch (IOException) { verificationUnavailable = true; }

            if (!verificationUnavailable)
            {
                if (!string.IsNullOrEmpty(liveSessionPuuid))
                {
                    if (string.Equals(liveSessionPuuid, live.Puuid, StringComparison.OrdinalIgnoreCase))
                        account = active;
                }
                else if (active.GameName.Length > 0)
                {
                    account = active;
                }
                else
                {
                    verificationUnavailable = true;
                }
            }
        }

        var updated = false;
        if (account is not null)
        {
            var learnedPuuid = string.IsNullOrEmpty(account.Puuid);
            if (learnedPuuid)
                account.Puuid = live.Puuid;

            updated = ApplyLive(account, live) || learnedPuuid;
            var shouldPersistTimestamp = !_lastPersistedSync.TryGetValue(account.Id, out var lastSaved)
                || (live.FetchedAt - lastSaved) >= SyncTimestampSaveInterval;

            if (updated || shouldPersistTimestamp)
            {
                await SaveAccountsAsync();
                _lastPersistedSync[account.Id] = live.FetchedAt;
            }

            // Always notify so "Synced at" stays fresh even when stats haven't changed.
            NotifyChanged();
        }

        if (!IsOperating && !verificationUnavailable)
            await SetActiveAsync(account);

        return (account, updated);
    }

    public async Task ApplyRefreshAsync(AccountRecord account, AccountRefreshResult result)
    {
        // result.Puuid is deliberately ignored: Riot API PUUIDs are encrypted per key and never match the client's.
        ApplyStats(account, result.GameName, result.TagLine, result.SummonerLevel, result.ProfileIconId, result.RankedSolo, result.FetchedAt);
        await SaveAccountsAsync();
        _lastPersistedSync[account.Id] = result.FetchedAt;
        NotifyChanged();
    }

    private static bool ApplyLive(AccountRecord account, LcuSnapshot live)
    {
        var region = Regions.Known(live.Region);
        var changed = ApplyStats(account, live.GameName, live.TagLine, live.SummonerLevel, live.ProfileIconId, live.RankedSolo, live.FetchedAt);

        if (region is null || region == account.Region)
            return changed;

        account.Region = region;
        return true;
    }

    private static bool ApplyStats(AccountRecord a, string name, string tag, int level, int icon, RankedSoloInfo? rank, DateTimeOffset at)
    {
        var before = (a.GameName, a.TagLine, a.SummonerLevel, a.ProfileIconId, a.RankedSolo, a.HasRankData);

        if (!string.IsNullOrWhiteSpace(name))
            a.GameName = name;
        if (!string.IsNullOrWhiteSpace(tag))
            a.TagLine = tag;
        if (level > 0)
            a.SummonerLevel = level;
        if (icon >= 0)
            a.ProfileIconId = icon;
        if (rank is not null)
        {
            a.RankedSolo = rank;
            a.HasRankData = true;
        }

        a.LastSyncedAt = at;

        return before != (a.GameName, a.TagLine, a.SummonerLevel, a.ProfileIconId, a.RankedSolo, a.HasRankData);
    }

    public async Task EditAsync(AccountRecord account, Action<AccountRecord> edit)
    {
        var snapshot = (account.Alias, account.GameName, account.TagLine, account.Region,
            account.SummonerLevel, account.RankedSolo, account.HasRankData);

        edit(account);
        try
        {
            await SaveAccountsAsync();
        }
        catch
        {
            (account.Alias, account.GameName, account.TagLine, account.Region,
                account.SummonerLevel, account.RankedSolo, account.HasRankData) = snapshot;
            throw;
        }

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

        _lastPersistedSync.Remove(account.Id);
        NotifyChanged();
        await store.DeleteSessionSnapshotAsync(account.Id);
    }

    public async Task UpdateSettingsAsync(Action<AppSettings> update)
    {
        var snapshot = (Settings.RiotClientPathOverride, Settings.LeagueClientPathOverride,
            Settings.LaunchTarget, Settings.Theme, Settings.AutoSyncWithLcu, Settings.ProtectedRiotApiKey);

        update(Settings);
        try
        {
            await SaveSettingsAsync();
        }
        catch
        {
            (Settings.RiotClientPathOverride, Settings.LeagueClientPathOverride,
                Settings.LaunchTarget, Settings.Theme, Settings.AutoSyncWithLcu, Settings.ProtectedRiotApiKey) = snapshot;
            throw;
        }

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
