using LeagueAccountSwitcher.Core.Models;

namespace LeagueAccountSwitcher.Core.Services;

public interface IRiotApiService
{
    Task<AccountRefreshResult?> RefreshAccountAsync(AccountRecord account, string apiKey, CancellationToken ct = default);
}
