using LeagueAccountSwitcher.Core.Models;

namespace LeagueAccountSwitcher.Core.Services;

public interface IAccountStore
{
    string DataDirectory { get; }

    Task<IReadOnlyList<AccountRecord>> LoadAccountsAsync(CancellationToken ct = default);
    Task SaveAccountsAsync(IEnumerable<AccountRecord> accounts, CancellationToken ct = default);

    Task<AppSettings> LoadSettingsAsync(CancellationToken ct = default);
    Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default);

    Task SaveSessionSnapshotAsync(string accountId, byte[] rawSessionBytes, CancellationToken ct = default);
    Task<byte[]?> LoadSessionSnapshotAsync(string accountId, CancellationToken ct = default);
    bool HasSessionSnapshot(string accountId);
    Task DeleteSessionSnapshotAsync(string accountId, CancellationToken ct = default);

    string ProtectSecret(string plaintext);
    string? UnprotectSecret(string? protectedBase64);
}
