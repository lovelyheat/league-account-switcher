using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace LeagueAccountSwitcher.Core.Services.Assets;

public sealed class AssetCacheService : IAssetCacheService
{
    private const string DataDragonUrl = "https://ddragon.leagueoflegends.com";
    private const string FallbackVersion = "15.1.1";

    private readonly string _cacheDirectory;
    private readonly HttpClient _http;
    private readonly object _versionLock = new();
    private readonly ConcurrentDictionary<int, Lazy<Task<string?>>> _downloads = new();
    private Task<string?>? _versionTask;

    public AssetCacheService(IAccountStore accountStore, HttpClient? httpClient = null)
        : this(Path.Combine(accountStore.DataDirectory, "cache", "icons"), httpClient)
    {
    }

    public AssetCacheService(string cacheDirectory, HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        _cacheDirectory = cacheDirectory;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task<string> GetLatestDataDragonVersionAsync(CancellationToken ct = default)
    {
        Task<string?> task;
        lock (_versionLock)
        {
            // A failed lookup is retried, otherwise starting offline would pin the fallback for the whole session.
            if (_versionTask is null || (_versionTask.IsCompleted && _versionTask.Result is null))
                _versionTask = FetchLatestVersionAsync();

            task = _versionTask;
        }

        return await task.WaitAsync(ct) ?? FallbackVersion;
    }

    public async Task<string?> GetProfileIconPathAsync(int? profileIconId, CancellationToken ct = default)
    {
        if (profileIconId is not { } id || id < 0)
            return null;

        var path = Path.Combine(_cacheDirectory, string.Create(CultureInfo.InvariantCulture, $"{id}.png"));
        if (File.Exists(path))
            return path;

        // Callers share one download, so one caller cancelling must not cancel it for the others.
        var download = _downloads.GetOrAdd(id, key => new Lazy<Task<string?>>(() => DownloadIconAsync(key, path)));
        return await download.Value.WaitAsync(ct);
    }

    private async Task<string?> FetchLatestVersionAsync()
    {
        try
        {
            var versions = JsonSerializer.Deserialize<string[]>(await _http.GetStringAsync($"{DataDragonUrl}/api/versions.json"));
            return versions is [var latest, ..] && !string.IsNullOrWhiteSpace(latest) ? latest : null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Data Dragon version lookup failed: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> DownloadIconAsync(int id, string path)
    {
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            if (File.Exists(path))
                return path;

            var version = Uri.EscapeDataString(await GetLatestDataDragonVersionAsync());
            var bytes = await _http.GetByteArrayAsync(
                string.Create(CultureInfo.InvariantCulture, $"{DataDragonUrl}/cdn/{version}/img/profileicon/{id}.png"));
            if (bytes.Length == 0)
                return null;

            Directory.CreateDirectory(_cacheDirectory);
            await File.WriteAllBytesAsync(tempPath, bytes);
            File.Move(tempPath, path, overwrite: true);
            return path;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Profile icon {id} download failed: {ex.Message}");
            TryDelete(tempPath);
            return null;
        }
        finally
        {
            _downloads.TryRemove(id, out _);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not remove {path}: {ex.Message}");
        }
    }
}
