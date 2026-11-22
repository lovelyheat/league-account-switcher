namespace LeagueAccountSwitcher.Core.Services;

public interface IAssetCacheService
{
    Task<string?> GetProfileIconPathAsync(int? profileIconId, CancellationToken ct = default);
    Task<string> GetLatestDataDragonVersionAsync(CancellationToken ct = default);
}
