using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LeagueAccountSwitcher.Core.Models;

namespace LeagueAccountSwitcher.Core.Services.Storage;

public sealed class AccountStore : IAccountStore
{
    // Changing these bytes makes every saved session and secret unreadable.
    private static readonly byte[] Entropy = "LeagueAccountSwitcher.SessionStore.v1"u8.ToArray();

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // An allowlist instead of character replacement, so two IDs can never map to the same snapshot.
    private static readonly Regex SafeAccountId = new(
        @"^(?!(?:CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])\z)[A-Z0-9_-]{1,128}\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _accountsPath;
    private readonly string _settingsPath;
    private readonly string _sessionsDirectory;

    public AccountStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LeagueAccountSwitcher"))
    {
    }

    public AccountStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        DataDirectory = Path.GetFullPath(dataDirectory);
        _accountsPath = Path.Combine(DataDirectory, "accounts.json");
        _settingsPath = Path.Combine(DataDirectory, "settings.json");
        _sessionsDirectory = Path.Combine(DataDirectory, "sessions");
    }

    public string DataDirectory { get; }

    public async Task<IReadOnlyList<AccountRecord>> LoadAccountsAsync(CancellationToken ct = default)
    {
        var accounts = await LoadJsonAsync<List<AccountRecord?>>(_accountsPath, ct).ConfigureAwait(false);
        return accounts?.OfType<AccountRecord>().ToList() ?? [];
    }

    public Task SaveAccountsAsync(IEnumerable<AccountRecord> accounts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(accounts);

        // Serialize before the first await so later edits to the caller's collection cannot race the write.
        return WriteLockedAsync(_accountsPath, JsonSerializer.SerializeToUtf8Bytes(accounts.ToList(), JsonOptions), ct);
    }

    public async Task<AppSettings> LoadSettingsAsync(CancellationToken ct = default) =>
        await LoadJsonAsync<AppSettings>(_settingsPath, ct).ConfigureAwait(false) ?? new AppSettings();

    public Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return WriteLockedAsync(_settingsPath, JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions), ct);
    }

    public async Task SaveSessionSnapshotAsync(string accountId, byte[] rawSessionBytes, CancellationToken ct = default)
    {
        var path = GetSnapshotPath(accountId);
        ArgumentNullException.ThrowIfNull(rawSessionBytes);
        if (rawSessionBytes.Length == 0)
            throw new ArgumentException("A session snapshot cannot be empty.", nameof(rawSessionBytes));

        var encrypted = ProtectedData.Protect(rawSessionBytes, Entropy, DataProtectionScope.CurrentUser);
        await WriteLockedAsync(path, encrypted, ct).ConfigureAwait(false);
    }

    public async Task<byte[]?> LoadSessionSnapshotAsync(string accountId, CancellationToken ct = default)
    {
        var path = GetSnapshotPath(accountId);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
                return null;

            var encrypted = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            return TryUnprotect(encrypted);
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool HasSessionSnapshot(string accountId) =>
        IsSafeAccountId(accountId) && File.Exists(GetSnapshotPath(accountId));

    public async Task DeleteSessionSnapshotAsync(string accountId, CancellationToken ct = default)
    {
        var path = GetSnapshotPath(accountId);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        finally
        {
            _gate.Release();
        }
    }

    public string ProtectSecret(string plaintext) =>
        string.IsNullOrEmpty(plaintext)
            ? string.Empty
            : Convert.ToBase64String(ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser));

    public string? UnprotectSecret(string? protectedBase64)
    {
        if (string.IsNullOrWhiteSpace(protectedBase64))
            return null;

        try
        {
            var plain = TryUnprotect(Convert.FromBase64String(protectedBase64));
            return plain is null ? null : Encoding.UTF8.GetString(plain);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    internal static async Task WriteAtomicAsync(string path, byte[] content, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(content, ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private async Task<T?> LoadJsonAsync<T>(string path, CancellationToken ct) where T : class
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
                return null;

            try
            {
                await using var stream = File.OpenRead(path);
                return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                // Callers save over what they loaded, so keep the damaged original recoverable.
                File.Copy(path, path + ".corrupt", overwrite: true);
                return null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task WriteLockedAsync(string path, byte[] content, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WriteAtomicAsync(path, content, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string GetSnapshotPath(string accountId) =>
        IsSafeAccountId(accountId)
            ? Path.Combine(_sessionsDirectory, $"{accountId}.bin")
            : throw new ArgumentException("Account ID may only contain letters, digits, '-' and '_'.", nameof(accountId));

    private static bool IsSafeAccountId(string? accountId) => accountId is not null && SafeAccountId.IsMatch(accountId);

    private static byte[]? TryUnprotect(byte[] encrypted)
    {
        try
        {
            return ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
