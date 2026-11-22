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

    public bool HasActiveRiotSession() => ReadActiveSession().HasSession;

    public bool IsLeagueClientRunning() =>
        _environment.Processes.IsRunning("LeagueClient") || _environment.Processes.IsRunning("LeagueClientUx");

    public bool IsRiotClientRunning() =>
        _environment.Processes.IsRunning("RiotClientServices")
        || _environment.Processes.IsRunning("RiotClientUx")
        || _environment.Processes.IsRunning("Riot Client");

    public async Task<byte[]> CaptureCurrentSessionBytesAsync(CancellationToken ct = default)
    {
        var bytes = await ReadPrivateSettingsAsync(FileShare.ReadWrite | FileShare.Delete, ct).ConfigureAwait(false);
        return bytes is not null && ParseSession(bytes).HasSession
            ? bytes
            : throw new InvalidOperationException(
                "No signed-in Riot session was found. Sign in to Riot Client with \"Stay signed in\" checked, then try again.");
    }

    public Task<string?> TryReadActiveSessionPuuidAsync(CancellationToken ct = default) =>
        Task.Run(() => ReadActiveSession().Puuid, ct);

    public Task<string?> TryReadActiveSessionRegionAsync(CancellationToken ct = default) =>
        Task.Run(() => ReadActiveSession().Region ?? ReadClientSettingsRegion(), ct);

    public async Task SwitchToAccountAsync(
        AccountRecord targetAccount,
        string? currentActiveAccountId,
        LaunchTarget launchTarget,
        string? riotClientPathOverride = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
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

            // Snapshot file names are case-insensitive on Windows, so the IDs are compared the same way.
            var outgoingId = string.Equals(currentActiveAccountId, targetAccount.Id, StringComparison.OrdinalIgnoreCase)
                ? null
                : currentActiveAccountId;
            var liveSession = await CloseClientsPreservingSessionAsync(outgoingId, progress, ct).ConfigureAwait(false);

            if (IsSameSignedInAccount(liveSession, targetSession))
            {
                // The live file holds newer rotated tokens than the snapshot.
                progress?.Report($"{targetAccount.DisplayName} is already signed in. Keeping the current session.");
            }
            else
            {
                progress?.Report($"Restoring the session for {targetAccount.DisplayName}...");
                await RetryFileAsync(
                    async () =>
                    {
                        await AccountStore.WriteAtomicAsync(GetRiotPrivateSettingsPath(), targetSession, ct).ConfigureAwait(false);
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
        CancellationToken ct = default)
    {
        await _switchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var clientPath = RequireRiotClientPath(riotClientPathOverride);
            await CloseClientsPreservingSessionAsync(currentActiveAccountId, progress, ct).ConfigureAwait(false);

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
            Launch(clientPath, LaunchTarget.RiotClientOnly);
            progress?.Report("Sign in to the new account with \"Stay signed in\" checked.");
        }
        finally
        {
            _switchGate.Release();
        }
    }

    private async Task<byte[]?> CloseClientsPreservingSessionAsync(
        string? outgoingId,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        byte[]? beforeClose = null;
        if (!string.IsNullOrWhiteSpace(outgoingId))
        {
            beforeClose = await ReadPrivateSettingsAsync(FileShare.ReadWrite | FileShare.Delete, ct).ConfigureAwait(false);
            await BackUpAsync(outgoingId, beforeClose, progress, ct).ConfigureAwait(false);
        }

        progress?.Report("Closing Riot and League clients...");
        await StopClientsAsync(ct).ConfigureAwait(false);

        progress?.Report("Waiting for Riot Client to release its files...");
        // An exclusive open succeeds only once every other handle on the file is gone.
        var afterClose = await ReadPrivateSettingsAsync(FileShare.None, ct).ConfigureAwait(false);

        // Riot can write rotated tokens while shutting down.
        if (!string.IsNullOrWhiteSpace(outgoingId) && afterClose is not null &&
            (beforeClose is null || !afterClose.AsSpan().SequenceEqual(beforeClose)))
        {
            await BackUpAsync(outgoingId, afterClose, progress, ct).ConfigureAwait(false);
        }

        return afterClose;
    }

    private async Task BackUpAsync(string accountId, byte[]? session, IProgress<string>? progress, CancellationToken ct)
    {
        if (session is null)
            return;

        var live = ParseSession(session);
        if (!live.HasSession)
            return;

        // The user may have signed in to someone else inside Riot Client since the last switch.
        if (live.Puuid is not null &&
            await GetExpectedPuuidAsync(accountId, ct).ConfigureAwait(false) is { } expected &&
            !expected.Equals(live.Puuid, StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report("Riot Client is signed in to a different account, so the previous account's saved session was left unchanged.");
            return;
        }

        progress?.Report("Saving the current account's session...");
        await _accountStore.SaveSessionSnapshotAsync(accountId, session, ct).ConfigureAwait(false);
    }

    private async Task<string?> GetExpectedPuuidAsync(string accountId, CancellationToken ct)
    {
        var saved = await _accountStore.LoadSessionSnapshotAsync(accountId, ct).ConfigureAwait(false);
        if (saved is not null && ParseSession(saved).Puuid is { } savedPuuid)
            return savedPuuid;

        var accounts = await _accountStore.LoadAccountsAsync(ct).ConfigureAwait(false);
        var recorded = accounts.FirstOrDefault(a => a.Id == accountId)?.Puuid;

        // Riot API PUUIDs are encrypted per key and cannot be compared with the `sub` cookie, which is a GUID.
        return Guid.TryParse(recorded, out _) ? recorded : null;
    }

    private async Task StopClientsAsync(CancellationToken ct)
    {
        var processes = _environment.Processes;
        bool AnyRunning() => ProcessesToStop.Any(processes.IsRunning);

        if (!AnyRunning())
            return;

        var elapsed = Stopwatch.StartNew();
        var closeRequested = false;
        foreach (var name in ProcessesToStop)
            closeRequested |= processes.RequestClose(name);

        while (closeRequested && elapsed.Elapsed < _environment.CloseGracePeriod && AnyRunning())
            await Task.Delay(100, ct).ConfigureAwait(false);

        var delay = 100;
        while (AnyRunning())
        {
            if (elapsed.Elapsed >= _environment.Timeout)
            {
                throw new InvalidOperationException(
                    "Riot Client or League of Legends did not close. Close it manually and try again.");
            }

            foreach (var name in ProcessesToStop)
                processes.Kill(name);

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

    private RiotSessionInfo ReadActiveSession() =>
        RiotYamlSessionParser.Parse(RiotClientPathDiscovery.ReadTextOrNull(GetRiotPrivateSettingsPath()));

    private string? ReadClientSettingsRegion()
    {
        var path = Path.Combine(_environment.LocalAppData, "Riot Games", "Riot Client", "Config", "RiotClientSettings.yaml");
        return RiotYamlSessionParser.Parse(RiotClientPathDiscovery.ReadTextOrNull(path)).Region;
    }

    private static bool IsSameSignedInAccount(byte[]? live, byte[] target) =>
        live is not null &&
        ParseSession(live).Puuid is { } puuid &&
        puuid.Equals(ParseSession(target).Puuid, StringComparison.OrdinalIgnoreCase);

    private static RiotSessionInfo ParseSession(byte[] bytes) => RiotYamlSessionParser.Parse(Encoding.UTF8.GetString(bytes));
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
