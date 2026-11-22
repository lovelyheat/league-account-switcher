using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using LeagueAccountSwitcher.Core.Models;
using LeagueAccountSwitcher.Core.Services.Storage;

[assembly: InternalsVisibleTo("LeagueAccountSwitcher.Tests")]

namespace LeagueAccountSwitcher.Core.Services.RiotClient;

public sealed class RiotClientService : IRiotClientService
{
    // The complete set of processes this app may close. Vanguard (vgc, vgtray) must never be added.
    private static readonly string[] ProcessesToStop =
    [
        "LeagueClientUxRender",
        "LeagueClientUx",
        "LeagueClient",
        "League of Legends",
        "RiotClientUxRender",
        "RiotClientUx",
        "Riot Client",
        "RiotClientServices",
        "RiotClientCrashHandler",
    ];

    private readonly IAccountStore _accountStore;
    private readonly RiotEnvironment _environment;
    private readonly SemaphoreSlim _switchGate = new(1, 1);

    public RiotClientService(IAccountStore accountStore)
        : this(accountStore, new RiotEnvironment())
    {
    }

    internal RiotClientService(IAccountStore accountStore, RiotEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(accountStore);
        _accountStore = accountStore;
        _environment = environment;
    }

    public string? DetectRiotClientPath(string? overridePath = null) =>
        RiotClientPathDiscovery.FindRiotClient(overridePath, _environment.ProgramData, _environment.SearchRoots);

    public string? DetectLeagueInstallPath(string? overridePath = null) =>
        RiotClientPathDiscovery.FindLeagueInstall(overridePath, _environment.ProgramData, _environment.SearchRoots);

    public string GetRiotPrivateSettingsPath() =>
        Path.Combine(_environment.LocalAppData, "Riot Games", "Riot Client", "Data", "RiotGamesPrivateSettings.yaml");

    public bool HasActiveRiotSession() => ReadActiveSessionWithRetry().HasSession;

    public bool IsLeagueClientRunning() =>
        _environment.Processes.IsRunning("LeagueClient") || _environment.Processes.IsRunning("LeagueClientUx");

    public bool IsRiotClientRunning() =>
        _environment.Processes.IsRunning("RiotClientServices")
        || _environment.Processes.IsRunning("RiotClientUx")
        || _environment.Processes.IsRunning("Riot Client");

    public bool IsMatchRunning() =>
        _environment.Processes.IsRunning("League of Legends");

    public Task<RiotClientLiveAccount?> TryReadLiveRiotClientAccountAsync(CancellationToken ct = default) =>
        RiotClientLocalApi.TryFetchAsync(_environment.LocalAppData, ct);

    public async Task<CapturedSession> CaptureCurrentSessionAsync(CancellationToken ct = default)
    {
        var bytes = await ReadPrivateSettingsAsync(FileShare.ReadWrite | FileShare.Delete, ct).ConfigureAwait(false);
        var parsed = bytes is null ? new RiotSessionInfo(false, null, null) : ParseSession(bytes);
        if (bytes is null || !parsed.HasSession)
        {
            var live = await TryReadLiveRiotClientAccountAsync(ct).ConfigureAwait(false);
            if (live is not null)
            {
                if (!live.HasOfflineAccess)
                {
                    throw new InvalidOperationException(
                        "You're signed in to Riot Client, but \"Stay signed in\" wasn't checked. Sign out, sign back in with \"Stay signed in\" checked, then try again.");
                }

                throw new InvalidOperationException(
                    "Riot Client hasn't written your saved sign-in to disk yet. Click Play / open League of Legends in Riot Client (with \"Stay signed in\" checked), then try again.");
            }

            throw NoSignedInSessionException();
        }

        var puuid = parsed.Puuid;
        var region = parsed.Region;
        if (puuid is null || region is null)
        {
            var live = await TryReadLiveRiotClientAccountAsync(ct).ConfigureAwait(false);
            if (live is not null)
            {
                puuid ??= live.PersistedPuuid ?? live.Puuid;
                if (region is null && live.Region != "Unknown")
                    region = live.Region;
            }
        }

        return new CapturedSession(bytes, puuid, region);
    }

    public async Task<byte[]> CaptureCurrentSessionBytesAsync(CancellationToken ct = default) =>
        (await CaptureCurrentSessionAsync(ct).ConfigureAwait(false)).Bytes;

    public async Task<string?> TryReadActiveSessionPuuidAsync(CancellationToken ct = default)
    {
        var bytes = await ReadPrivateSettingsAsync(FileShare.ReadWrite | FileShare.Delete, ct).ConfigureAwait(false);
        if (bytes is null)
            return null;

        var parsed = ParseSession(bytes);
        if (!parsed.HasSession)
            return null;

        if (parsed.Puuid is not null)
            return parsed.Puuid;

        var live = await TryReadLiveRiotClientAccountAsync(ct).ConfigureAwait(false);
        return live?.PersistedPuuid ?? live?.Puuid;
    }

    public async Task<string?> TryReadActiveSessionRegionAsync(CancellationToken ct = default)
    {
        var bytes = await ReadPrivateSettingsAsync(FileShare.ReadWrite | FileShare.Delete, ct).ConfigureAwait(false);
        var fromPrivate = bytes is null ? null : ParseSession(bytes).Region;
        if (fromPrivate is not null)
            return fromPrivate;

        var live = await TryReadLiveRiotClientAccountAsync(ct).ConfigureAwait(false);
        if (live is not null && live.Region != "Unknown")
            return live.Region;

        return ReadClientSettingsRegion();
    }

    public async Task SwitchToAccountAsync(
        AccountRecord targetAccount,
        string? currentActiveAccountId,
        LaunchTarget launchTarget,
        string? riotClientPathOverride = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        bool allowCloseRunningMatch = false,
        bool allowDiscardUnsavedSession = false,
        string? confirmedDiscardPuuid = null)
    {
        ArgumentNullException.ThrowIfNull(targetAccount);

        await _switchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            progress?.Report($"Checking the saved session for {targetAccount.DisplayName}...");
            var targetSession = await _accountStore.LoadSessionSnapshotAsync(targetAccount.Id, ct).ConfigureAwait(false);
            if (targetSession is not { Length: > 0 })
            {
                throw new InvalidOperationException(
                    $"No readable saved session for {targetAccount.DisplayName}. Use Re-capture Session to save it again.");
            }

            var clientPath = RequireRiotClientPath(riotClientPathOverride);
            EnsureMatchCanClose(allowCloseRunningMatch);

            var targetExpectedPuuid = ParseSession(targetSession).Puuid
                ?? (Guid.TryParse(targetAccount.Puuid, out _) ? targetAccount.Puuid : null)
                ?? await GetExpectedPuuidAsync(targetAccount.Id, ct).ConfigureAwait(false);

            var isRestartingTarget = string.Equals(currentActiveAccountId, targetAccount.Id, StringComparison.OrdinalIgnoreCase);
            var outgoingId = isRestartingTarget ? null : currentActiveAccountId;

            var (liveSession, latestTargetSession) = await CloseClientsPreservingSessionAsync(
                outgoingId,
                targetAccount.Id,
                targetExpectedPuuid,
                isRestartingTarget,
                allowCloseRunningMatch,
                allowDiscardUnsavedSession,
                confirmedDiscardPuuid,
                progress,
                ct).ConfigureAwait(false);

            if (liveSession is not null && IsLiveSessionForTarget(
                    ParseSession(liveSession),
                    targetExpectedPuuid,
                    isRestartingTarget,
                    isChangedPostClose: false))
            {
                // The live file holds newer rotated tokens than the snapshot; ensure its backup is fresh too.
                await BackUpAsync(targetAccount.Id, liveSession, progress, ct).ConfigureAwait(false);
                progress?.Report($"{targetAccount.DisplayName} is already signed in. Keeping the current session.");
            }
            else
            {
                var sessionToRestore = latestTargetSession ?? targetSession;
                progress?.Report($"Restoring the session for {targetAccount.DisplayName}...");
                await RetryFileAsync(
                    async () =>
                    {
                        await AccountStore.WriteAtomicAsync(GetRiotPrivateSettingsPath(), sessionToRestore, ct).ConfigureAwait(false);
                        return true;
                    },
                    ct).ConfigureAwait(false);
            }

            progress?.Report(launchTarget == LaunchTarget.LeagueOfLegends
                ? "Launching League of Legends..."
                : "Launching Riot Client...");
            Launch(clientPath, launchTarget);
            progress?.Report($"Switched to {targetAccount.DisplayName}.");
        }
        finally
        {
            _switchGate.Release();
        }
    }

    public Task LaunchClientAsync(
        LaunchTarget launchTarget,
        string? riotClientPathOverride = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Launch(RequireRiotClientPath(riotClientPathOverride), launchTarget);
        return Task.CompletedTask;
    }

    public async Task PrepareFreshLoginAsync(
        string? currentActiveAccountId,
        string? riotClientPathOverride = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        bool allowCloseRunningMatch = false,
        bool allowDiscardUnsavedSession = false,
        string? confirmedDiscardPuuid = null)
    {
        await _switchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var clientPath = RequireRiotClientPath(riotClientPathOverride);
            EnsureMatchCanClose(allowCloseRunningMatch);

            await CloseClientsPreservingSessionAsync(
                currentActiveAccountId,
                targetAccountId: null,
                targetExpectedPuuid: null,
                isRestartingTarget: false,
                allowCloseRunningMatch,
                allowDiscardUnsavedSession,
                confirmedDiscardPuuid,
                progress,
                ct).ConfigureAwait(false);

            progress?.Report("Clearing the active session for a new sign-in...");
            var settingsPath = GetRiotPrivateSettingsPath();
            foreach (var path in new[] { settingsPath, settingsPath + ".bak" })
            {
                await RetryFileAsync(
                    () =>
                    {
                        if (File.Exists(path))
                            File.Delete(path);
                        return Task.FromResult(true);
                    },
                    ct).ConfigureAwait(false);
            }

            progress?.Report("Launching Riot Client...");
            // Launch with League product context so Riot Client persists psl.authorization.riot-client immediately upon sign-in.
            Launch(clientPath, LaunchTarget.LeagueOfLegends);
            progress?.Report("Sign in to the new account with \"Stay signed in\" checked.");
        }
        finally
        {
            _switchGate.Release();
        }
    }

    private async Task<(byte[]? AfterClose, byte[]? LatestTargetSession)> CloseClientsPreservingSessionAsync(
        string? outgoingId,
        string? targetAccountId,
        string? targetExpectedPuuid,
        bool isRestartingTarget,
        bool allowCloseRunningMatch,
        bool allowDiscardUnsavedSession,
        string? confirmedDiscardPuuid,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var beforeClose = await ReadPrivateSettingsAsync(FileShare.ReadWrite | FileShare.Delete, ct).ConfigureAwait(false);
        var (beforeBackedUp, latestTarget) = await PreserveOrCheckSessionAsync(
            beforeClose,
            outgoingId,
            targetAccountId,
            targetExpectedPuuid,
            isRestartingTarget,
            isChangedPostClose: false,
            allowDiscardUnsavedSession,
            confirmedDiscardPuuid,
            progress,
            ct).ConfigureAwait(false);

        EnsureMatchCanClose(allowCloseRunningMatch);

        progress?.Report("Closing Riot and League clients...");
        await StopClientsAsync(allowCloseRunningMatch, ct).ConfigureAwait(false);

        progress?.Report("Waiting for Riot Client to release its files...");
        // An exclusive open succeeds only once every other handle on the file is gone.
        var afterClose = await ReadPrivateSettingsAsync(FileShare.None, ct).ConfigureAwait(false);

        if (afterClose is not null)
        {
            var changed = beforeClose is null || !afterClose.AsSpan().SequenceEqual(beforeClose);
            if (changed || !beforeBackedUp)
            {
                // Riot can write rotated tokens or finish signing in while shutting down.
                var (_, postCloseTarget) = await PreserveOrCheckSessionAsync(
                    afterClose,
                    outgoingId,
                    targetAccountId,
                    targetExpectedPuuid,
                    isRestartingTarget,
                    isChangedPostClose: changed,
                    allowDiscardUnsavedSession,
                    confirmedDiscardPuuid,
                    progress,
                    ct).ConfigureAwait(false);
                if (postCloseTarget is not null)
                    latestTarget = postCloseTarget;
            }
        }

        return (afterClose, latestTarget);
    }

    private async Task<(bool BackedUp, byte[]? TargetSession)> PreserveOrCheckSessionAsync(
        byte[]? sessionBytes,
        string? outgoingId,
        string? targetAccountId,
        string? targetExpectedPuuid,
        bool isRestartingTarget,
        bool isChangedPostClose,
        bool allowDiscardUnsavedSession,
        string? confirmedDiscardPuuid,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        if (sessionBytes is null)
            return (false, null);

        var live = ParseSession(sessionBytes);
        if (!live.HasSession)
            return (false, null);

        // 1. If the live session belongs to the target account being launched/restarted, preserve it on the target.
        if (!string.IsNullOrWhiteSpace(targetAccountId)
            && IsLiveSessionForTarget(live, targetExpectedPuuid, isRestartingTarget, isChangedPostClose))
        {
            if (await BackUpAsync(targetAccountId, sessionBytes, progress, ct).ConfigureAwait(false))
                return (true, sessionBytes);
        }

        // 2. Always resolve by PUUID first so another saved account's session is never written over an outgoing account with no known PUUID.
        if (live.Puuid is not null &&
            await FindOwnerAccountIdByPuuidAsync(live.Puuid, ct).ConfigureAwait(false) is { } ownerId)
        {
            if (string.Equals(ownerId, targetAccountId, StringComparison.OrdinalIgnoreCase))
            {
                if (await BackUpAsync(ownerId, sessionBytes, progress, ct).ConfigureAwait(false))
                    return (true, sessionBytes);
            }
            else if (await BackUpAsync(ownerId, sessionBytes, progress, ct).ConfigureAwait(false))
            {
                return (true, null);
            }
        }

        // 3. Fall back to the explicitly specified outgoing account only if identity is provable (or pre-close with no conflicting owner).
        if (!string.IsNullOrWhiteSpace(outgoingId))
        {
            var outgoingExpectedPuuid = await GetExpectedPuuidAsync(outgoingId, ct).ConfigureAwait(false);
            var canAssignToOutgoing = outgoingExpectedPuuid is not null
                ? live.Puuid is not null && outgoingExpectedPuuid.Equals(live.Puuid, StringComparison.OrdinalIgnoreCase)
                : !isChangedPostClose;

            if (canAssignToOutgoing && await BackUpAsync(outgoingId, sessionBytes, progress, ct).ConfigureAwait(false))
                return (true, null);
        }

        var discardMatchesConsent = allowDiscardUnsavedSession && (confirmedDiscardPuuid is null
            || string.Equals(live.Puuid ?? string.Empty, confirmedDiscardPuuid, StringComparison.OrdinalIgnoreCase));

        if (!discardMatchesConsent)
        {
            throw new InvalidOperationException(
                "The currently signed-in Riot account isn't saved (or changed before switching). Capture it first, or confirm replacing it.");
        }

        return (false, null);
    }

    private static bool IsLiveSessionForTarget(
        RiotSessionInfo live,
        string? targetExpectedPuuid,
        bool isRestartingTarget,
        bool isChangedPostClose)
    {
        if (!live.HasSession)
            return false;

        if (targetExpectedPuuid is not null)
            return live.Puuid is not null && targetExpectedPuuid.Equals(live.Puuid, StringComparison.OrdinalIgnoreCase);

        // When target has no known PUUID, only treat an unidentified pre-close session as the target if we are explicitly restarting it.
        return isRestartingTarget && !isChangedPostClose && live.Puuid is null;
    }

    private async Task<bool> BackUpAsync(string accountId, byte[]? session, IProgress<string>? progress, CancellationToken ct)
    {
        if (session is null)
            return false;

        var live = ParseSession(session);
        if (!live.HasSession)
            return false;

        // Fail closed: never overwrite an account with a known PUUID using a different or unknown identity.
        if (await GetExpectedPuuidAsync(accountId, ct).ConfigureAwait(false) is { } expected &&
            (live.Puuid is null || !expected.Equals(live.Puuid, StringComparison.OrdinalIgnoreCase)))
        {
            progress?.Report("Riot Client is signed in to a different account, so the previous account's saved session was left unchanged.");
            return false;
        }

        progress?.Report("Saving the current account's session...");
        await _accountStore.SaveSessionSnapshotAsync(accountId, session, ct).ConfigureAwait(false);
        return true;
    }

    private async Task<string?> FindOwnerAccountIdByPuuidAsync(string puuid, CancellationToken ct)
    {
        try
        {
            var accounts = await _accountStore.LoadAccountsAsync(ct).ConfigureAwait(false);
            return accounts.FirstOrDefault(a =>
                !string.IsNullOrEmpty(a.Puuid) &&
                Guid.TryParse(a.Puuid, out _) &&
                a.Puuid.Equals(puuid, StringComparison.OrdinalIgnoreCase))?.Id;
        }
        catch (CorruptAccountStoreException)
        {
            return null;
        }
    }

    private async Task<string?> GetExpectedPuuidAsync(string accountId, CancellationToken ct)
    {
        var saved = await _accountStore.LoadSessionSnapshotAsync(accountId, ct).ConfigureAwait(false);
        if (saved is not null && ParseSession(saved).Puuid is { } savedPuuid)
            return savedPuuid;

        try
        {
            var accounts = await _accountStore.LoadAccountsAsync(ct).ConfigureAwait(false);
            var recorded = accounts.FirstOrDefault(a => a.Id == accountId)?.Puuid;

            // Riot API PUUIDs are encrypted per key and cannot be compared with the `sub` cookie, which is a GUID.
            return Guid.TryParse(recorded, out _) ? recorded : null;
        }
        catch (CorruptAccountStoreException)
        {
            return null;
        }
    }

    private void EnsureMatchCanClose(bool allowCloseRunningMatch)
    {
        if (!allowCloseRunningMatch && IsMatchRunning())
        {
            throw new InvalidOperationException(
                "A League of Legends match is currently running. Finish the match or confirm closing the game first.");
        }
    }

    private async Task StopClientsAsync(bool allowCloseRunningMatch, CancellationToken ct)
    {
        var processes = _environment.Processes;
        bool AnyRunning() => ProcessesToStop.Any(processes.IsRunning);

        if (!AnyRunning())
            return;

        var elapsed = Stopwatch.StartNew();
        var closeRequested = false;
        foreach (var name in ProcessesToStop)
        {
            if (name == "League of Legends" && !allowCloseRunningMatch)
                continue;
            closeRequested |= processes.RequestClose(name);
        }

        while (closeRequested && elapsed.Elapsed < _environment.CloseGracePeriod && AnyRunning())
            await Task.Delay(100, ct).ConfigureAwait(false);

        // Re-check before force killing in case a match launched during the close grace period.
        EnsureMatchCanClose(allowCloseRunningMatch);

        var delay = 100;
        while (AnyRunning())
        {
            EnsureMatchCanClose(allowCloseRunningMatch);

            if (elapsed.Elapsed >= _environment.Timeout)
            {
                throw new InvalidOperationException(
                    "Riot Client or League of Legends did not close. Close it manually and try again.");
            }

            foreach (var name in ProcessesToStop)
            {
                if (allowCloseRunningMatch || name != "League of Legends")
                    processes.Kill(name);
            }

            await Task.Delay(delay, ct).ConfigureAwait(false);
            delay = Math.Min(delay * 2, 1000);
        }
    }

    private Task<byte[]?> ReadPrivateSettingsAsync(FileShare share, CancellationToken ct) =>
        RetryFileAsync<byte[]?>(
            async () =>
            {
                try
                {
                    await using var stream = new FileStream(GetRiotPrivateSettingsPath(), FileMode.Open, FileAccess.Read, share);
                    using var buffer = new MemoryStream();
                    await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
                    return buffer.ToArray();
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    return null;
                }
            },
            ct);

    private RiotSessionInfo ReadActiveSessionWithRetry()
    {
        var path = GetRiotPrivateSettingsPath();
        var elapsed = Stopwatch.StartNew();
        var delay = 50;
        while (true)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return RiotYamlSessionParser.Parse(reader.ReadToEnd());
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return new RiotSessionInfo(false, null, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (elapsed.Elapsed >= _environment.Timeout)
                    throw new IOException("Riot's session file is locked or not readable. Close Riot Client and try again.", ex);
            }

            Thread.Sleep(delay);
            delay = Math.Min(delay * 2, 500);
        }
    }

    private async Task<T> RetryFileAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        var elapsed = Stopwatch.StartNew();
        var delay = 50;
        while (true)
        {
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (elapsed.Elapsed >= _environment.Timeout)
                    throw new IOException("Riot's session file is locked or not writable. Close Riot Client and try again.", ex);
            }

            await Task.Delay(delay, ct).ConfigureAwait(false);
            delay = Math.Min(delay * 2, 1000);
        }
    }

    private string RequireRiotClientPath(string? overridePath) =>
        DetectRiotClientPath(overridePath) ?? throw new InvalidOperationException(
            "RiotClientServices.exe was not found. Install Riot Client or set its path in Settings.");

    private void Launch(string clientPath, LaunchTarget target)
    {
        string[] arguments = target == LaunchTarget.LeagueOfLegends
            ? ["--launch-product=league_of_legends", "--launch-patchline=live"]
            : [];
        _environment.Processes.Start(clientPath, arguments);
    }

    private string? ReadClientSettingsRegion()
    {
        var path = Path.Combine(_environment.LocalAppData, "Riot Games", "Riot Client", "Config", "RiotClientSettings.yaml");
        return RiotYamlSessionParser.Parse(RiotClientPathDiscovery.ReadTextOrNull(path)).Region;
    }

    private static RiotSessionInfo ParseSession(byte[] bytes) => RiotYamlSessionParser.Parse(Encoding.UTF8.GetString(bytes));

    private static InvalidOperationException NoSignedInSessionException() => new(
        "No signed-in Riot session was found. Sign in to Riot Client with \"Stay signed in\" checked, then try again.");
}

// Internal seam: tests must not close the developer's real Riot Client or touch their real session.
internal sealed record RiotEnvironment
{
    public string LocalAppData { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public string ProgramData { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    public IReadOnlyList<string> SearchRoots { get; init; } = RiotClientPathDiscovery.DefaultSearchRoots;
    public IRiotProcesses Processes { get; init; } = new WindowsRiotProcesses();
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan CloseGracePeriod { get; init; } = TimeSpan.FromSeconds(3);
}

internal interface IRiotProcesses
{
    bool IsRunning(string name);
    bool RequestClose(string name);
    void Kill(string name);
    void Start(string path, IReadOnlyList<string> arguments);
}

internal sealed class WindowsRiotProcesses : IRiotProcesses
{
    public bool IsRunning(string name) => ForEachInCurrentSession(name, _ => true);

    public bool RequestClose(string name) => ForEachInCurrentSession(name, process => process.CloseMainWindow());

    public void Kill(string name) => ForEachInCurrentSession(name, process =>
    {
        // Never the process tree: Riot Client can be the parent of other games and helpers.
        process.Kill(entireProcessTree: false);
        return true;
    });

    public void Start(string path, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(path),
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo);
    }

    private static bool ForEachInCurrentSession(string name, Func<Process, bool> action)
    {
        using var current = Process.GetCurrentProcess();
        var any = false;
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                try
                {
                    // Other signed-in Windows users may be running their own clients.
                    if (process.SessionId == current.SessionId)
                        any |= action(process);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    // Already exited or access denied. The caller polls and reports whatever is still running.
                }
            }
        }

        return any;
    }
}
