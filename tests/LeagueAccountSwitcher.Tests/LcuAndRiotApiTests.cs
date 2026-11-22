using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using LeagueAccountSwitcher.Core.Models;
using LeagueAccountSwitcher.Core.Services;
using LeagueAccountSwitcher.Core.Services.Assets;
using LeagueAccountSwitcher.Core.Services.Lcu;
using LeagueAccountSwitcher.Core.Services.RiotApi;

namespace LeagueAccountSwitcher.Tests.LcuAndRiotApi;

public sealed class LcuConnectionInfoTests
{
    [Theory]
    [InlineData("LeagueClient:12345:54321:auth_token:https")]
    [InlineData("  LeagueClient:12345:54321:auth_token:https\r\n")]
    public void Lockfile_is_parsed(string content)
    {
        Assert.True(LcuConnectionInfo.TryParseLockfile(content, out var info));
        Assert.Equal(new LcuConnectionInfo("LeagueClient", 12345, 54321, "auth_token", "https"), info);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("LeagueClient:12345:54321:token")]
    [InlineData("LeagueClient:12345:54321:token:https:extra")]
    [InlineData(":12345:54321:token:https")]
    [InlineData("LeagueClient:abc:54321:token:https")]
    [InlineData("LeagueClient:0:54321:token:https")]
    [InlineData("LeagueClient:-1:54321:token:https")]
    [InlineData("LeagueClient:12345:0:token:https")]
    [InlineData("LeagueClient:12345:65536:token:https")]
    [InlineData("LeagueClient:12345:54321::https")]
    [InlineData("LeagueClient:12345:54321:token:ftp")]
    public void Malformed_lockfile_is_rejected(string? content)
    {
        Assert.False(LcuConnectionInfo.TryParseLockfile(content!, out var info));
        Assert.Null(info);
    }

    [Theory]
    [InlineData("LeagueClientUx.exe --app-port=54321 --remoting-auth-token=aB-c_123")]
    [InlineData("LeagueClientUx.exe --remoting-auth-token=aB-c_123 --app-port=54321")]
    [InlineData("\"D:/Riot Games/League of Legends/LeagueClientUx.exe\" \"--remoting-auth-token=aB-c_123\" \"--app-port=54321\"")]
    [InlineData("LeagueClientUx.exe --app-port=\"54321\" --remoting-auth-token=\"aB-c_123\"")]
    [InlineData("LeagueClientUx.exe --app-port='54321' --remoting-auth-token='aB-c_123'")]
    [InlineData("LeagueClientUx.exe \"--riotclient-auth-token=other\" \"--riotclient-app-port=1111\" \"--remoting-auth-token=aB-c_123\" \"--app-port=54321\"")]
    public void Command_line_is_parsed(string commandLine)
    {
        Assert.True(LcuConnectionInfo.TryParseCommandLine(commandLine, 777, out var info));
        Assert.Equal(new LcuConnectionInfo("LeagueClientUx", 777, 54321, "aB-c_123", "https"), info);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("LeagueClientUx.exe --app-port=54321")]
    [InlineData("LeagueClientUx.exe --remoting-auth-token=token")]
    [InlineData("LeagueClientUx.exe --app-port=54x21 --remoting-auth-token=token")]
    [InlineData("LeagueClientUx.exe --app-port=99999 --remoting-auth-token=token")]
    [InlineData("LeagueClientUx.exe --app-port=54321 --remoting-auth-token=\"\"")]
    [InlineData("LeagueClientUx.exe --riotclient-app-port=1111 --remoting-auth-token=token")]
    public void Malformed_command_line_is_rejected(string? commandLine)
    {
        Assert.False(LcuConnectionInfo.TryParseCommandLine(commandLine!, 777, out var info));
        Assert.Null(info);
    }

    [Fact]
    public void ToString_does_not_expose_the_token()
    {
        Assert.DoesNotContain("secret", new LcuConnectionInfo("LeagueClient", 1, 2, "secret", "https").ToString());
    }
}

public sealed class LcuResponseParserTests
{
    [Fact]
    public void Summoner_is_parsed()
    {
        var summoner = LcuResponseParser.ParseSummoner(
            """{"puuid":"p","gameName":"Player","tagLine":"EUW","displayName":"Old","summonerLevel":247,"profileIconId":29}""");

        Assert.Equal(new LcuSummoner("p", "Player", "EUW", 247, 29), summoner);
    }

    [Theory]
    [InlineData("""{"puuid":"p","displayName":"Legacy"}""")]
    [InlineData("""{"puuid":"p","gameName":"","tagLine":null,"displayName":"Legacy"}""")]
    public void Summoner_falls_back_to_the_display_name(string json)
    {
        Assert.Equal(new LcuSummoner("p", "Legacy", "", 0, 0), LcuResponseParser.ParseSummoner(json));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{"puuid":""}""")]
    [InlineData("""{"puuid":42}""")]
    [InlineData("""{"errorCode":"RPC_ERROR","message":"You are not logged in"}""")]
    public void Summoner_without_a_puuid_is_rejected(string? json)
    {
        Assert.Null(LcuResponseParser.ParseSummoner(json));
    }

    [Fact]
    public void Standard_rank_is_parsed()
    {
        var rank = LcuResponseParser.ParseRankedSolo(
            """{"queueMap":{"RANKED_SOLO_5x5":{"tier":"EMERALD","division":"III","leaguePoints":48,"wins":62,"losses":48,"isProvisional":false}}}""");

        Assert.Equal(new RankedSoloInfo { Tier = "EMERALD", Division = "III", LeaguePoints = 48, Wins = 62, Losses = 48 }, rank);
    }

    [Theory]
    [InlineData("MASTER", "I")]
    [InlineData("GRANDMASTER", "NA")]
    [InlineData("CHALLENGER", "")]
    [InlineData("master", "i")]
    public void Apex_tiers_have_no_division(string tier, string division)
    {
        var rank = LcuResponseParser.ParseRankedSolo(
            $$$"""{"queueMap":{"RANKED_SOLO_5x5":{"tier":"{{{tier}}}","division":"{{{division}}}","leaguePoints":215} } }""");

        Assert.Equal(new RankedSoloInfo { Tier = tier.ToUpperInvariant(), LeaguePoints = 215 }, rank);
    }

    [Theory]
    [InlineData("\"NONE\"")]
    [InlineData("\"UNRANKED\"")]
    [InlineData("\"\"")]
    [InlineData("null")]
    public void Unranked_tiers_are_normalized(string tier)
    {
        var rank = LcuResponseParser.ParseRankedSolo(
            $$$"""{"queueMap":{"RANKED_SOLO_5x5":{"tier":{{{tier}}},"division":"NA","leaguePoints":0} } }""");

        Assert.Equal(RankedSoloInfo.Unranked, rank);
    }

    [Fact]
    public void Placement_games_keep_their_record()
    {
        var rank = LcuResponseParser.ParseRankedSolo(
            """{"queueMap":{"RANKED_SOLO_5x5":{"tier":"NONE","division":"NA","wins":2,"losses":1,"isProvisional":true}}}""");

        Assert.Equal(new RankedSoloInfo { Wins = 2, Losses = 1, IsProvisional = true }, rank);
    }

    [Fact]
    public void Queue_list_is_the_fallback_for_the_queue_map()
    {
        var rank = LcuResponseParser.ParseRankedSolo(
            """{"queueMap":{},"queues":[null,{"queueType":"RANKED_FLEX_SR","tier":"GOLD"},{"queueType":"RANKED_SOLO_5x5","tier":"SILVER","division":"I"}]}""");

        Assert.Equal(new RankedSoloInfo { Tier = "SILVER", Division = "I" }, rank);
    }

    [Theory]
    [InlineData("""{"queueMap":{}}""")]
    [InlineData("""{"queues":[]}""")]
    [InlineData("""{"queueMap":{"RANKED_FLEX_SR":{"tier":"GOLD"}}}""")]
    public void Missing_solo_queue_is_unranked(string json)
    {
        Assert.Equal(RankedSoloInfo.Unranked, LcuResponseParser.ParseRankedSolo(json));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("""{"errorCode":"RPC_ERROR"}""")]
    [InlineData("""{"queueMap":null,"queues":null}""")]
    public void Unusable_ranked_response_is_unknown(string? json)
    {
        Assert.Null(LcuResponseParser.ParseRankedSolo(json));
    }

    [Theory]
    [InlineData("""{"region":"EUW1","webRegion":"na"}""", "EUW")]
    [InlineData("""{"region":"OC1"}""", "OCE")]
    [InlineData("""{"region":"","webRegion":"eune"}""", "EUNE")]
    [InlineData("""{"webRegion":"la1"}""", "LAN")]
    [InlineData("{}", "Unknown")]
    [InlineData("garbage", "Unknown")]
    [InlineData(null, "Unknown")]
    public void Region_is_normalized(string? json, string expected)
    {
        Assert.Equal(expected, LcuResponseParser.ParseRegion(json));
    }
}

public sealed class RankAndRegionModelTests
{
    [Theory]
    [InlineData("DIAMOND", "II", 64, "Diamond II · 64 LP", 730_064)]
    [InlineData("IRON", "IV", 0, "Iron IV · 0 LP", 110_000)]
    [InlineData("MASTER", "I", 215, "Master · 215 LP", 840_215)]
    [InlineData("GRANDMASTER", "", 512, "Grandmaster · 512 LP", 940_512)]
    [InlineData("CHALLENGER", "", 1200, "Challenger · 1200 LP", 1_041_200)]
    [InlineData("NONE", "NA", 0, "Unranked", 0)]
    [InlineData("UNRANKED", "", 0, "Unranked", 0)]
    public void Rank_is_formatted_and_scored(string tier, string division, int points, string text, int score)
    {
        var rank = new RankedSoloInfo { Tier = tier, Division = division, LeaguePoints = points };

        Assert.Equal(text, rank.FormattedRank);
        Assert.Equal(score, rank.SortScore);
    }

    [Theory]
    [InlineData(62, 48, "62W 48L (56%)")]
    [InlineData(10, 0, "10W 0L (100%)")]
    [InlineData(0, 0, "")]
    public void Record_is_formatted(int wins, int losses, string expected)
    {
        Assert.Equal(expected, new RankedSoloInfo { Wins = wins, Losses = losses }.FormattedRecord);
    }

    [Theory]
    [InlineData(" euw1 ", "EUW")]
    [InlineData("EUN1", "EUNE")]
    [InlineData("la2", "LAS")]
    [InlineData("kr", "KR")]
    [InlineData("xyz", "XYZ")]
    [InlineData("", "Unknown")]
    [InlineData(null, "Unknown")]
    public void Region_is_normalized(string? raw, string expected)
    {
        Assert.Equal(expected, LeagueRegion.Normalize(raw));
    }

    [Theory]
    [InlineData("euw", "euw1", "europe")]
    [InlineData("NA1", "na1", "americas")]
    [InlineData("KR", "kr", "asia")]
    [InlineData("OCE", "oc1", "sea")]
    public void Region_metadata_is_found(string raw, string platform, string route)
    {
        var metadata = LeagueRegion.TryGetMetadata(raw);

        Assert.Equal((platform, route), (metadata?.PlatformId, metadata?.RegionalRoute));
    }

    [Theory]
    [InlineData("PBE")]
    [InlineData("Unknown")]
    [InlineData(null)]
    public void Unsupported_region_has_no_metadata(string? raw)
    {
        Assert.Null(LeagueRegion.TryGetMetadata(raw));
    }
}

public sealed class LcuServiceTests : IDisposable
{
    private const string Token = "test_token";
    private const string SummonerPath = "/lol-summoner/v1/current-summoner";
    private const string RankedPath = "/lol-ranked/v1/current-ranked-stats";
    private const string RegionPath = "/riotclient/region-locale";
    private const string RankedJson = """{"queueMap":{"RANKED_SOLO_5x5":{"tier":"DIAMOND","division":"II","leaguePoints":64}}}""";

    private readonly TempDirectory _install = new();
    private readonly ConcurrentDictionary<string, (int Status, string Body)> _responses = new(new Dictionary<string, (int, string)>
    {
        [SummonerPath] = (200, """{"puuid":"p","gameName":"Player","tagLine":"EUW","summonerLevel":247,"profileIconId":29}"""),
        [RankedPath] = (200, RankedJson),
        [RegionPath] = (200, """{"region":"EUW"}"""),
    });

    private readonly FakeLcuServer _server;

    public LcuServiceTests()
    {
        _server = new FakeLcuServer(Token, path => _responses.GetValueOrDefault(path, (404, "{}")));
        File.WriteAllText(LockfilePath, $"LeagueClient:1234:{_server.Port}:{Token}:https");
    }

    private string LockfilePath => Path.Combine(_install.Path, "lockfile");

    public void Dispose()
    {
        _server.Dispose();
        _install.Dispose();
    }

    [Fact]
    public async Task Fetches_a_snapshot_while_the_client_holds_the_lockfile()
    {
        using var held = new FileStream(LockfilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        using var service = new LcuService();

        var snapshot = await service.TryFetchCurrentAccountAsync(_install.Path);

        Assert.NotNull(snapshot);
        Assert.Equal(("p", "Player", "EUW", 247, 29), (snapshot.Puuid, snapshot.GameName, snapshot.TagLine, snapshot.SummonerLevel, snapshot.ProfileIconId));
        Assert.Equal("EUW", snapshot.Region);
        Assert.Equal("Diamond II · 64 LP", snapshot.RankedSolo.FormattedRank);
        Assert.True(service.IsConnected);
    }

    [Fact]
    public async Task Accepts_a_path_to_the_client_executable()
    {
        var executable = Path.Combine(_install.Path, "LeagueClient.exe");
        File.WriteAllText(executable, "");
        using var service = new LcuService();

        Assert.NotNull(await service.TryFetchCurrentAccountAsync(executable));
    }

    [Fact]
    public async Task Uses_the_detected_install_path()
    {
        var riotClient = StubProxy.Create<IRiotClientService>(nameof(IRiotClientService.DetectLeagueInstallPath), _install.Path);
        using var service = new LcuService(riotClient);

        Assert.NotNull(await service.TryFetchCurrentAccountAsync());
    }

    [Fact]
    public async Task Skips_a_lockfile_with_a_rejected_token()
    {
        using var stale = new TempDirectory();
        File.WriteAllText(Path.Combine(stale.Path, "lockfile"), $"LeagueClient:1:{_server.Port}:old_token:https");
        var riotClient = StubProxy.Create<IRiotClientService>(nameof(IRiotClientService.DetectLeagueInstallPath), _install.Path);
        using var service = new LcuService(riotClient);

        Assert.NotNull(await service.TryFetchCurrentAccountAsync(stale.Path));
    }

    [Theory]
    [InlineData(404, """{"message":"You are not logged in"}""")]
    [InlineData(200, """{"puuid":""}""")]
    [InlineData(200, "not json")]
    public async Task Booting_client_is_connected_without_a_snapshot(int status, string body)
    {
        _responses[SummonerPath] = (status, body);
        using var service = new LcuService();

        Assert.Null(await service.TryFetchCurrentAccountAsync(_install.Path));
        Assert.True(service.IsConnected);
    }

    [Fact]
    public async Task Failed_secondary_requests_still_return_the_summoner()
    {
        _responses[RankedPath] = (500, "");
        _responses[RegionPath] = (503, "");
        using var service = new LcuService();

        var snapshot = await service.TryFetchCurrentAccountAsync(_install.Path);

        Assert.Equal(("p", "Unknown", RankedSoloInfo.Unranked), (snapshot?.Puuid, snapshot?.Region, snapshot?.RankedSolo));
    }

    [Fact]
    public async Task Failed_secondary_requests_keep_the_last_confirmed_values()
    {
        using var service = new LcuService();
        await service.TryFetchCurrentAccountAsync(_install.Path);
        _responses[RankedPath] = (500, "");
        _responses[RegionPath] = (500, "");

        var snapshot = await service.TryFetchCurrentAccountAsync(_install.Path);

        Assert.Equal(("EUW", "DIAMOND"), (snapshot?.Region, snapshot?.RankedSolo.Tier));
    }

    [Fact]
    public async Task Cancellation_is_propagated()
    {
        using var service = new LcuService();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.TryFetchCurrentAccountAsync(_install.Path, new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task Monitoring_raises_events_and_reports_a_stop()
    {
        using var service = new LcuService();
        var states = new ConcurrentQueue<bool>();
        var synced = new TaskCompletionSource<LcuSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.ConnectionStateChanged += (_, connected) => states.Enqueue(connected);
        service.ActiveAccountSynced += (_, snapshot) => synced.TrySetResult(snapshot);

        service.StartMonitoring(() => _install.Path, TimeSpan.FromMilliseconds(50));
        var result = await synced.Task.WaitAsync(TimeSpan.FromSeconds(15));
        service.StopMonitoring();

        Assert.Equal("p", result.Puuid);
        Assert.Equal([true, false], states);
        Assert.False(service.IsConnected);
    }

    [Fact]
    public async Task Monitoring_waits_for_rank_before_syncing()
    {
        _responses[RankedPath] = (503, "");
        using var service = new LcuService();
        var synced = new TaskCompletionSource<LcuSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.ActiveAccountSynced += (_, snapshot) => synced.TrySetResult(snapshot);

        service.StartMonitoring(() => _install.Path, TimeSpan.FromMilliseconds(50));
        await _server.WaitForRequestsAsync(RegionPath, count: 2);
        Assert.False(synced.Task.IsCompleted);

        _responses[RankedPath] = (200, RankedJson);
        Assert.Equal("DIAMOND", (await synced.Task.WaitAsync(TimeSpan.FromSeconds(15))).RankedSolo.Tier);
    }

    [Fact]
    public async Task Monitoring_survives_a_failing_handler()
    {
        using var service = new LcuService();
        var calls = 0;
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.ActiveAccountSynced += (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                throw new InvalidOperationException("Handler failed");

            second.TrySetResult();
        };

        service.StartMonitoring(() => _install.Path, TimeSpan.FromMilliseconds(50));

        await second.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void Lifecycle_calls_are_idempotent()
    {
        var service = new LcuService();
        service.StopMonitoring();
        service.StartMonitoring(() => _install.Path);
        service.StartMonitoring(() => _install.Path);
        service.Dispose();
        service.Dispose();
        service.StopMonitoring();

        Assert.Throws<ObjectDisposedException>(() => service.StartMonitoring(() => _install.Path));
    }

    [Theory]
    [InlineData("https://127.0.0.1:2999/", true)]
    [InlineData("https://localhost:2999/", true)]
    [InlineData("https://example.com/", false)]
    [InlineData("https://127.0.0.1.example.com/", false)]
    [InlineData("https://localhost.example.com/", false)]
    [InlineData(null, false)]
    public void Self_signed_certificate_is_trusted_only_on_loopback(string? url, bool expected)
    {
        var uri = url is null ? null : new Uri(url);

        Assert.Equal(expected, LcuService.IsTrustedCertificate(uri, SslPolicyErrors.RemoteCertificateChainErrors));
    }
}

public sealed class AssetCacheServiceTests : IDisposable
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47];
    private readonly TempDirectory _cache = new();

    public void Dispose() => _cache.Dispose();

    [Fact]
    public async Task Cached_icon_needs_no_network()
    {
        var path = Path.Combine(_cache.Path, "29.png");
        await File.WriteAllBytesAsync(path, Png);
        var http = new FakeHttpHandler(ServeDataDragon);

        Assert.Equal(path, await CreateService(http).GetProfileIconPathAsync(29));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Downloaded_icon_is_cached()
    {
        var http = new FakeHttpHandler(ServeDataDragon);
        var service = CreateService(http);

        var path = await service.GetProfileIconPathAsync(29);
        var cachedPath = await service.GetProfileIconPathAsync(29);

        Assert.Equal(Path.Combine(_cache.Path, "29.png"), path);
        Assert.Equal(path, cachedPath);
        Assert.Equal(Png, await File.ReadAllBytesAsync(path!));
        Assert.Equal([path!], Directory.GetFiles(_cache.Path));
        Assert.Equal(
            ["https://ddragon.leagueoflegends.com/api/versions.json", "https://ddragon.leagueoflegends.com/cdn/16.23.1/img/profileicon/29.png"],
            http.Urls);
    }

    [Fact]
    public async Task Store_constructor_uses_the_icon_cache_directory()
    {
        var store = StubProxy.Create<IAccountStore>($"get_{nameof(IAccountStore.DataDirectory)}", _cache.Path);
        var service = new AssetCacheService(store, new HttpClient(new FakeHttpHandler(ServeDataDragon)));

        Assert.Equal(Path.Combine(_cache.Path, "cache", "icons", "29.png"), await service.GetProfileIconPathAsync(29));
    }

    [Fact]
    public async Task Concurrent_requests_share_one_download_even_if_one_is_cancelled()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var http = new FakeHttpHandler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(".png", StringComparison.Ordinal))
                await release.Task;

            return ServeDataDragon(request);
        });
        var service = CreateService(http);
        using var cancellation = new CancellationTokenSource();

        var cancelled = service.GetProfileIconPathAsync(29, cancellation.Token);
        var others = Enumerable.Range(0, 20).Select(_ => service.GetProfileIconPathAsync(29)).ToArray();
        cancellation.Cancel();
        release.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.All(await Task.WhenAll(others), path => Assert.NotNull(path));
        Assert.Single(http.Urls, url => url.EndsWith(".png", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Offline_returns_fallbacks_then_recovers()
    {
        var online = false;
        var service = CreateService(new FakeHttpHandler(request => online ? ServeDataDragon(request) : throw new HttpRequestException("Offline")));

        Assert.Equal("15.1.1", await service.GetLatestDataDragonVersionAsync());
        Assert.Null(await service.GetProfileIconPathAsync(29));
        Assert.Empty(Directory.GetFiles(_cache.Path));

        online = true;
        Assert.NotNull(await service.GetProfileIconPathAsync(29));
        Assert.Equal("16.23.1", await service.GetLatestDataDragonVersionAsync());
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "missing")]
    [InlineData(HttpStatusCode.OK, "")]
    public async Task Unusable_download_is_not_cached(HttpStatusCode status, string body)
    {
        var service = CreateService(new FakeHttpHandler(request => request.RequestUri!.AbsolutePath.EndsWith(".png", StringComparison.Ordinal)
            ? FakeHttpHandler.Text(body, status)
            : ServeDataDragon(request)));

        Assert.Null(await service.GetProfileIconPathAsync(29));
        Assert.Empty(Directory.GetFiles(_cache.Path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    public async Task Invalid_icon_id_is_ignored(int? id)
    {
        var http = new FakeHttpHandler(ServeDataDragon);

        Assert.Null(await CreateService(http).GetProfileIconPathAsync(id));
        Assert.Empty(http.Requests);
    }

    private static HttpResponseMessage ServeDataDragon(HttpRequestMessage request) =>
        request.RequestUri!.AbsolutePath == "/api/versions.json"
            ? FakeHttpHandler.Text("""["16.23.1","16.22.1"]""")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) };

    private AssetCacheService CreateService(FakeHttpHandler http) => new(_cache.Path, new HttpClient(http));
}

public sealed class RiotApiServiceTests
{
    private const string Key = "RGAPI-test";
    private const string ClientPuuid = "0f8fad5b-d9cb-469f-a165-70867728950e";
    private const string AccountJson = """{"puuid":"api-puuid","gameName":"Renamed","tagLine":"NEW"}""";
    private const string SummonerJson = """{"id":"summoner-id","summonerLevel":301,"profileIconId":4568}""";
    private const string LeagueJson = """
        [
          {"queueType":"RANKED_FLEX_SR","tier":"GOLD","rank":"I"},
          {"queueType":"RANKED_SOLO_5x5","tier":"DIAMOND","rank":"II","leaguePoints":64,"wins":62,"losses":48}
        ]
        """;

    [Fact]
    public async Task Puuid_runs_the_full_pipeline()
    {
        var http = new FakeHttpHandler(Serve());

        var result = await Refresh(http, Account("EUW", "stored-puuid"));

        Assert.NotNull(result);
        Assert.Equal(("api-puuid", "Renamed", "NEW", 301, 4568), (result.Puuid, result.GameName, result.TagLine, result.SummonerLevel, result.ProfileIconId));
        Assert.Equal(new RankedSoloInfo { Tier = "DIAMOND", Division = "II", LeaguePoints = 64, Wins = 62, Losses = 48 }, result.RankedSolo);
        Assert.Equal(
            [
                "https://europe.api.riotgames.com/riot/account/v1/accounts/by-puuid/stored-puuid",
                "https://euw1.api.riotgames.com/lol/summoner/v4/summoners/by-puuid/api-puuid",
                "https://euw1.api.riotgames.com/lol/league/v4/entries/by-puuid/api-puuid",
            ],
            http.Urls);
        Assert.All(http.Requests, request => Assert.Equal(Key, request.ApiKey));
    }

    [Fact]
    public async Task Riot_id_is_escaped()
    {
        var http = new FakeHttpHandler(Serve());

        await Refresh(http, new AccountRecord { Region = "KR", GameName = "Hide on bush/é?#", TagLine = "K R" });

        Assert.Equal(
            "https://asia.api.riotgames.com/riot/account/v1/accounts/by-riot-id/Hide%20on%20bush%2F%C3%A9%3F%23/K%20R",
            http.Urls.First());
    }

    [Fact]
    public async Task Client_puuid_is_resolved_by_riot_id_and_preserved()
    {
        var http = new FakeHttpHandler(Serve());

        var result = await Refresh(http, Account("NA", ClientPuuid));

        Assert.Equal(ClientPuuid, result?.Puuid);
        Assert.EndsWith("/by-riot-id/Player/TAG", http.Urls.First());
    }

    [Fact]
    public async Task Puuid_from_another_key_falls_back_to_the_riot_id()
    {
        var serve = Serve();
        var http = new FakeHttpHandler(request => request.RequestUri!.AbsolutePath.Contains("accounts/by-puuid")
            ? FakeHttpHandler.Text("{}", HttpStatusCode.BadRequest)
            : serve(request));

        Assert.Equal("api-puuid", (await Refresh(http, Account("EUW", "other-key-puuid")))?.Puuid);
        Assert.EndsWith("/by-riot-id/Player/TAG", http.Urls.ElementAt(1));
    }

    [Theory]
    [InlineData("OCE", "oc1")]
    [InlineData("PH", "sg2")]
    [InlineData("TH", "sg2")]
    [InlineData("VN", "vn2")]
    public async Task Sea_regions_use_hosts_that_exist(string region, string platform)
    {
        var http = new FakeHttpHandler(Serve());

        await Refresh(http, Account(region));

        Assert.StartsWith("https://asia.api.riotgames.com/", http.Urls.First());
        Assert.All(http.Urls.Skip(1), url => Assert.StartsWith($"https://{platform}.api.riotgames.com/", url));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""[{"queueType":"RANKED_FLEX_SR","tier":"GOLD"}]""")]
    public async Task No_solo_entry_is_unranked(string league)
    {
        var result = await Refresh(new FakeHttpHandler(Serve(league)), Account("EUW"));

        Assert.Equal(RankedSoloInfo.Unranked, result?.RankedSolo);
    }

    [Fact]
    public async Task Apex_entry_has_no_division()
    {
        var league = """[{"queueType":"RANKED_SOLO_5x5","tier":"MASTER","rank":"I","leaguePoints":215}]""";

        var result = await Refresh(new FakeHttpHandler(Serve(league)), Account("EUW"));

        Assert.Equal(new RankedSoloInfo { Tier = "MASTER", LeaguePoints = 215 }, result?.RankedSolo);
    }

    [Fact]
    public async Task League_not_found_falls_back_to_the_summoner_id()
    {
        var serve = Serve();
        var http = new FakeHttpHandler(request => request.RequestUri!.AbsolutePath.Contains("entries/by-puuid")
            ? FakeHttpHandler.Text("{}", HttpStatusCode.NotFound)
            : serve(request));

        Assert.Equal("DIAMOND", (await Refresh(http, Account("EUW")))?.RankedSolo.Tier);
        Assert.EndsWith("/entries/by-summoner/summoner-id", http.Urls.Last());
    }

    [Theory]
    [InlineData("accounts/")]
    [InlineData("summoners/")]
    [InlineData("entries/")]
    public async Task Not_found_returns_null_instead_of_blank_stats(string missing)
    {
        var serve = Serve();
        var http = new FakeHttpHandler(request => request.RequestUri!.AbsolutePath.Contains(missing)
            ? FakeHttpHandler.Text("{}", HttpStatusCode.NotFound)
            : serve(request));

        Assert.Null(await Refresh(http, Account("EUW")));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "invalid or expired")]
    [InlineData(HttpStatusCode.Forbidden, "invalid or expired")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate limit")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "HTTP 503")]
    public async Task Api_errors_are_descriptive(HttpStatusCode status, string message)
    {
        foreach (var failing in new[] { "accounts/", "summoners/", "entries/" })
        {
            var serve = Serve();
            var http = new FakeHttpHandler(request => request.RequestUri!.AbsolutePath.Contains(failing)
                ? FakeHttpHandler.Text("{}", status)
                : serve(request));

            var error = await Assert.ThrowsAsync<HttpRequestException>(() => Refresh(http, Account("EUW")));

            Assert.Equal(status, error.StatusCode);
            Assert.Contains(message, error.Message);
            Assert.DoesNotContain(Key, error.Message);
        }
    }

    [Fact]
    public async Task Rate_limit_reports_the_retry_delay()
    {
        var http = new FakeHttpHandler(_ =>
        {
            var response = FakeHttpHandler.Text("{}", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(17));
            return response;
        });

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Refresh(http, Account("EUW")));

        Assert.Contains("17 seconds", error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("RGAPI-line\nbreak")]
    [InlineData("RGAPI-clé")]
    public async Task Malformed_key_is_rejected_before_any_request(string key)
    {
        var http = new FakeHttpHandler(Serve());
        var service = new RiotApiService(new HttpClient(http));

        await Assert.ThrowsAsync<ArgumentException>(() => service.RefreshAccountAsync(Account("EUW"), key));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Unsupported_region_or_missing_identity_returns_null()
    {
        var http = new FakeHttpHandler(Serve());

        Assert.Null(await Refresh(http, Account("Unknown")));
        Assert.Null(await Refresh(http, new AccountRecord { Region = "EUW" }));
        Assert.Empty(http.Requests);
    }

    private static AccountRecord Account(string region, string? puuid = null) =>
        new() { Region = region, Puuid = puuid, GameName = "Player", TagLine = "TAG" };

    private static Func<HttpRequestMessage, HttpResponseMessage> Serve(string league = LeagueJson) => request =>
        request.RequestUri!.AbsolutePath switch
        {
            var path when path.StartsWith("/riot/account/", StringComparison.Ordinal) => FakeHttpHandler.Text(AccountJson),
            var path when path.StartsWith("/lol/summoner/", StringComparison.Ordinal) => FakeHttpHandler.Text(SummonerJson),
            _ => FakeHttpHandler.Text(league),
        };

    private static Task<AccountRefreshResult?> Refresh(FakeHttpHandler http, AccountRecord account) =>
        new RiotApiService(new HttpClient(http)).RefreshAccountAsync(account, Key);
}

// DispatchProxy needs a public, unsealed type. It answers one member and rejects all others.
public class StubProxy : DispatchProxy
{
    private string _member = "";
    private object? _value;

    internal static T Create<T>(string member, object? value)
        where T : class
    {
        var proxy = DispatchProxy.Create<T, StubProxy>();
        var stub = (StubProxy)(object)proxy;
        stub._member = member;
        stub._value = value;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        targetMethod?.Name == _member ? _value : throw new NotSupportedException(targetMethod?.Name);
}

internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("las-tests-").FullName;

    public void Dispose() => Directory.Delete(Path, recursive: true);
}

internal sealed class FakeHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : this(request => Task.FromResult(respond(request)))
    {
    }

    public ConcurrentQueue<(string Url, string? ApiKey)> Requests { get; } = new();

    public IEnumerable<string> Urls => Requests.Select(request => request.Url);

    public static HttpResponseMessage Text(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body) };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var key = request.Headers.TryGetValues("X-Riot-Token", out var values) ? values.Single() : null;
        Requests.Enqueue((request.RequestUri!.AbsoluteUri, key));
        return respond(request);
    }
}

/// <summary>A minimal LCU: self-signed TLS on loopback with Basic auth.</summary>
internal sealed class FakeLcuServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentQueue<string> _paths = new();
    private readonly X509Certificate2 _certificate;
    private readonly string _authorization;
    private readonly Func<string, (int Status, string Body)> _respond;

    public FakeLcuServer(string token, Func<string, (int Status, string Body)> respond)
    {
        _respond = respond;
        _authorization = "Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"riot:{token}"));

        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=rclient", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        // SChannel cannot serve TLS with the ephemeral key, so round-trip it through PFX.
        _certificate = new X509Certificate2(certificate.Export(X509ContentType.Pfx));

        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptAsync();
    }

    public int Port { get; }

    public async Task WaitForRequestsAsync(string path, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (_paths.Count(p => p == path) < count)
            await Task.Delay(20, timeout.Token);
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _listener.Stop();
        _certificate.Dispose();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
                _ = ServeAsync(await _listener.AcceptTcpClientAsync(_shutdown.Token));
        }
        catch (Exception) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            using (client)
            await using (var tls = new SslStream(client.GetStream()))
            {
                await tls.AuthenticateAsServerAsync(_certificate);
                using var reader = new StreamReader(tls, Encoding.ASCII, false, 1024, leaveOpen: true);
                var path = (await reader.ReadLineAsync())!.Split(' ')[1];
                var authorized = false;
                while (await reader.ReadLineAsync() is { Length: > 0 } header)
                    authorized |= header == _authorization;

                _paths.Enqueue(path);
                var (status, body) = authorized ? _respond(path) : (401, "");
                var payload = Encoding.UTF8.GetBytes(body);
                await tls.WriteAsync(Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {status} Test\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n"));
                await tls.WriteAsync(payload);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The client went away when its test finished.
        }
    }
}
