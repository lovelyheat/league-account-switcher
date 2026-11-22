using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Text;
using LeagueAccountSwitcher.Core.Models;

[assembly: InternalsVisibleTo("LeagueAccountSwitcher.Tests")]

namespace LeagueAccountSwitcher.Core.Services.Lcu;

/// <summary>
/// Reads the signed-in account from the local League Client. Events are raised on thread-pool threads.
/// </summary>
public sealed class LcuService(IRiotClientService? riotClientService = null) : ILcuService
{
    private const string UxProcessName = "LeagueClientUx";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(8);

    private readonly HttpClient _http = CreateHttpClient();
    private readonly object _gate = new();

    // Last confirmed values per puuid, so a failed request never turns into "Unranked" or "Unknown".
    private readonly ConcurrentDictionary<string, RankedSoloInfo> _knownRanks = new();
    private readonly ConcurrentDictionary<string, string> _knownRegions = new();

    private CancellationTokenSource? _monitor;
    private volatile bool _isConnected;
    private bool _disposed;

    public event EventHandler<LcuSnapshot>? ActiveAccountSynced;
    public event EventHandler<bool>? ConnectionStateChanged;

    public bool IsConnected => _isConnected;

    public async Task<LcuSnapshot?> TryFetchCurrentAccountAsync(string? leaguePathOverride = null, CancellationToken ct = default)
    {
        // File and WMI discovery block, and callers are usually on the UI thread.
        var (snapshot, _) = await Task.Run(() => FetchAsync(leaguePathOverride, ct), ct);
        return snapshot;
    }

    public void StartMonitoring(Func<string?> leaguePathOverrideProvider, TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(leaguePathOverrideProvider);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Created here so that an invalid interval throws to the caller.
            var timer = new PeriodicTimer(pollInterval ?? DefaultPollInterval);
            CancelMonitor();
            _monitor = new CancellationTokenSource();
            var token = _monitor.Token;
            _ = Task.Run(() => MonitorAsync(leaguePathOverrideProvider, timer, token));
        }
    }

    public void StopMonitoring()
    {
        lock (_gate)
            CancelMonitor();

        SetConnected(false, CancellationToken.None);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _isConnected = false;
            CancelMonitor();
        }

        _http.Dispose();
    }

    private void CancelMonitor()
    {
        _monitor?.Cancel();
        _monitor?.Dispose();
        _monitor = null;
    }

    private async Task MonitorAsync(Func<string?> pathProvider, PeriodicTimer timer, CancellationToken ct)
    {
        using var ownedTimer = timer;
        try
        {
            do
            {
                try
                {
                    var (snapshot, isComplete) = await FetchAsync(pathProvider(), ct);

                    // While the client boots, placeholders must not be synced over saved rank and region.
                    if (snapshot is not null && isComplete && !ct.IsCancellationRequested)
                        ActiveAccountSynced?.Invoke(this, snapshot);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    Debug.WriteLine($"LCU sync failed: {ex.Message}");
                }
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task<(LcuSnapshot? Snapshot, bool IsComplete)> FetchAsync(string? pathOverride, CancellationToken ct)
    {
        foreach (var connection in DiscoverConnections(pathOverride))
        {
            var (status, body) = await GetAsync(connection, "/lol-summoner/v1/current-summoner", ct);

            // No answer or a rejected token means a stale lockfile, so try the next source.
            if (status is null or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                continue;

            SetConnected(true, ct);

            if (status != HttpStatusCode.OK || LcuResponseParser.ParseSummoner(body) is not { } summoner)
                return (null, false);

            var (_, rankedBody) = await GetAsync(connection, "/lol-ranked/v1/current-ranked-stats", ct);
            var (_, regionBody) = await GetAsync(connection, "/riotclient/region-locale", ct);

            var rank = LcuResponseParser.ParseRankedSolo(rankedBody);
            if (rank is not null)
                _knownRanks[summoner.Puuid] = rank;
            else
                rank = _knownRanks.GetValueOrDefault(summoner.Puuid);

            var region = LcuResponseParser.ParseRegion(regionBody);
            if (region != "Unknown")
                _knownRegions[summoner.Puuid] = region;
            else
                region = _knownRegions.GetValueOrDefault(summoner.Puuid, region);

            var snapshot = new LcuSnapshot(
                summoner.Puuid,
                summoner.GameName,
                summoner.TagLine,
                summoner.SummonerLevel,
                summoner.ProfileIconId,
                region,
                rank,
                DateTimeOffset.UtcNow);

            return (snapshot, rank is not null);
        }

        SetConnected(false, ct);
        return (null, false);
    }

    private void SetConnected(bool connected, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_disposed || ct.IsCancellationRequested || _isConnected == connected)
                return;

            _isConnected = connected;
        }

        ConnectionStateChanged?.Invoke(this, connected);
    }

    private IEnumerable<LcuConnectionInfo> DiscoverConnections(string? pathOverride)
    {
        var directories = new[] { pathOverride, DetectInstallPath(pathOverride) }
            .OfType<string>()
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in directories)
        {
            if (ReadLockfile(directory) is { } fromLockfile)
                yield return fromLockfile;
        }

        // Evaluated only when no lockfile connection answered.
        foreach (var fromProcess in QueryClientProcesses())
            yield return fromProcess;
    }

    private string? DetectInstallPath(string? pathOverride)
    {
        try
        {
            return riotClientService?.DetectLeagueInstallPath(pathOverride);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"League install detection failed: {ex.Message}");
            return null;
        }
    }

    private static LcuConnectionInfo? ReadLockfile(string path)
    {
        try
        {
            // The setting may point at LeagueClient.exe instead of its folder.
            var directory = File.Exists(path) ? Path.GetDirectoryName(path)! : path;
            var lockfile = Path.Combine(directory, "lockfile");
            if (!File.Exists(lockfile))
                return null;

            using var stream = new FileStream(lockfile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return LcuConnectionInfo.TryParseLockfile(reader.ReadToEnd(), out var info) ? info : null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not read the League lockfile: {ex.Message}");
            return null;
        }
    }

    private static List<LcuConnectionInfo> QueryClientProcesses()
    {
        var connections = new List<LcuConnectionInfo>();
        try
        {
            // WMI is costly, and this runs on every poll while League is closed.
            var processes = Process.GetProcessesByName(UxProcessName);
            foreach (var process in processes)
                process.Dispose();

            if (processes.Length == 0)
                return connections;

            using var searcher = new ManagementObjectSearcher(
                $"SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = '{UxProcessName}.exe'");
            searcher.Options.Timeout = RequestTimeout;
            using var results = searcher.Get();

            foreach (var result in results)
            {
                using (result)
                {
                    if (result["CommandLine"] is string commandLine
                        && result["ProcessId"] is uint processId
                        && LcuConnectionInfo.TryParseCommandLine(commandLine, (int)processId, out var info))
                    {
                        connections.Add(info);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"LeagueClientUx discovery failed: {ex.Message}");
        }

        return connections;
    }

    private async Task<(HttpStatusCode? Status, string? Body)> GetAsync(LcuConnectionInfo connection, string path, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{connection.Protocol}://127.0.0.1:{connection.Port}{path}");
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"riot:{connection.AuthToken}")));

            using var response = await _http.SendAsync(request, ct);
            return (response.StatusCode, response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Debug.WriteLine($"LCU request {path} failed: {ex.Message}");
            return (null, null);
        }
    }

    // Riot's LCU certificate is self-signed, so only loopback requests may skip validation.
    internal static bool IsTrustedCertificate(Uri? requestUri, SslPolicyErrors errors) =>
        errors == SslPolicyErrors.None || requestUri?.Host is "127.0.0.1" or "localhost";

    private static HttpClient CreateHttpClient() => new(new HttpClientHandler
    {
        // A system proxy or a redirect would carry the client's auth token off the machine.
        UseProxy = false,
        AllowAutoRedirect = false,
        ServerCertificateCustomValidationCallback = (request, _, _, errors) => IsTrustedCertificate(request.RequestUri, errors),
    })
    {
        Timeout = RequestTimeout,
    };
}
