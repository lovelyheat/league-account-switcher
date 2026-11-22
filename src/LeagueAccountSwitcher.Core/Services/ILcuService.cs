using LeagueAccountSwitcher.Core.Models;

namespace LeagueAccountSwitcher.Core.Services;

public interface ILcuService : IDisposable
{
    event EventHandler<LcuSnapshot>? ActiveAccountSynced;
    event EventHandler<bool>? ConnectionStateChanged;

    bool IsConnected { get; }

    Task<LcuSnapshot?> TryFetchCurrentAccountAsync(string? leaguePathOverride = null, CancellationToken ct = default);
    void StartMonitoring(Func<string?> leaguePathOverrideProvider, TimeSpan? pollInterval = null);
    void StopMonitoring();
}
