using LeagueAccountSwitcher.Core.Models;
using LeagueAccountSwitcher.Core.Services;

namespace LeagueAccountSwitcher.Services;

// Build-time stand-ins until ServiceProviderFactory registers the Core implementations. Nothing is persisted.
internal sealed class PlaceholderAccountStore : IAccountStore
{
    public string DataDirectory => string.Empty;
    public Task<IReadOnlyList<AccountRecord>> LoadAccountsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AccountRecord>>([]);
    public Task SaveAccountsAsync(IEnumerable<AccountRecord> accounts, CancellationToken ct = default) => Task.CompletedTask;
    public Task<AppSettings> LoadSettingsAsync(CancellationToken ct = default) => Task.FromResult(new AppSettings());
    public Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveSessionSnapshotAsync(string accountId, byte[] rawSessionBytes, CancellationToken ct = default) => Task.CompletedTask;
    public Task<byte[]?> LoadSessionSnapshotAsync(string accountId, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
    public bool HasSessionSnapshot(string accountId) => false;
    public Task DeleteSessionSnapshotAsync(string accountId, CancellationToken ct = default) => Task.CompletedTask;
    public string ProtectSecret(string plaintext) => throw new NotSupportedException("Secret storage is not connected in this build.");
    public string? UnprotectSecret(string? protectedBase64) => null;
}

internal sealed class PlaceholderRiotClientService : IRiotClientService
{
    private static Task NotConnected() => Task.FromException(new NotSupportedException("Riot Client control is not connected in this build."));

    public string? DetectRiotClientPath(string? overridePath = null) => null;
    public string? DetectLeagueInstallPath(string? overridePath = null) => null;
    public string GetRiotPrivateSettingsPath() => string.Empty;
    public bool HasActiveRiotSession() => false;
    public bool IsLeagueClientRunning() => false;
    public bool IsRiotClientRunning() => false;
    public Task<byte[]> CaptureCurrentSessionBytesAsync(CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    public Task<string?> TryReadActiveSessionPuuidAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<string?> TryReadActiveSessionRegionAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);

    public Task SwitchToAccountAsync(AccountRecord targetAccount, string? currentActiveAccountId, LaunchTarget launchTarget,
        string? riotClientPathOverride = null, IProgress<string>? progress = null, CancellationToken ct = default) => NotConnected();

    public Task LaunchClientAsync(LaunchTarget launchTarget, string? riotClientPathOverride = null, CancellationToken ct = default) => NotConnected();

    public Task PrepareFreshLoginAsync(string? currentActiveAccountId, string? riotClientPathOverride = null,
        IProgress<string>? progress = null, CancellationToken ct = default) => NotConnected();
}

internal sealed class PlaceholderLcuService : ILcuService
{
    public event EventHandler<LcuSnapshot>? ActiveAccountSynced { add { } remove { } }
    public event EventHandler<bool>? ConnectionStateChanged { add { } remove { } }
    public bool IsConnected => false;
    public Task<LcuSnapshot?> TryFetchCurrentAccountAsync(string? leaguePathOverride = null, CancellationToken ct = default) => Task.FromResult<LcuSnapshot?>(null);
    public void StartMonitoring(Func<string?> leaguePathOverrideProvider, TimeSpan? pollInterval = null) { }
    public void StopMonitoring() { }
    public void Dispose() { }
}

internal sealed class PlaceholderAssetCacheService : IAssetCacheService
{
    public Task<string?> GetProfileIconPathAsync(int? profileIconId, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<string> GetLatestDataDragonVersionAsync(CancellationToken ct = default) => Task.FromResult(string.Empty);
}

internal sealed class PlaceholderRiotApiService : IRiotApiService
{
    public Task<AccountRefreshResult?> RefreshAccountAsync(AccountRecord account, string apiKey, CancellationToken ct = default) => Task.FromResult<AccountRefreshResult?>(null);
}
