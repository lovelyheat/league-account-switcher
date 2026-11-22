using System.Text;
using LeagueAccountSwitcher.Core.Models;
using LeagueAccountSwitcher.Core.Services.RiotClient;
using LeagueAccountSwitcher.Core.Services.Storage;

namespace LeagueAccountSwitcher.Tests;

public abstract class TempDirectoryTest : IDisposable
{
    protected string Root { get; } = Directory.CreateTempSubdirectory("las-tests-").FullName;

    protected string CreateFile(string relativePath, string content = "")
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        Directory.Delete(Root, recursive: true);
        GC.SuppressFinalize(this);
    }
}

public sealed class AccountStoreTests : TempDirectoryTest
{
    private readonly AccountStore _store;

    public AccountStoreTests() => _store = new AccountStore(Root);

    [Fact]
    public void Default_directory_is_under_local_app_data()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LeagueAccountSwitcher");

        Assert.Equal(expected, new AccountStore().DataDirectory);
    }

    [Fact]
    public async Task Missing_files_load_as_defaults()
    {
        var store = new AccountStore(Path.Combine(Root, "not-created-yet"));

        Assert.Empty(await store.LoadAccountsAsync());
        Assert.Null((await store.LoadSettingsAsync()).ActiveAccountId);
        Assert.Null(await store.LoadSessionSnapshotAsync("missing"));
        await store.DeleteSessionSnapshotAsync("missing");
    }

    [Fact]
    public async Task Accounts_round_trip_through_a_new_instance()
    {
        var account = new AccountRecord
        {
            Id = "abc",
            GameName = "Zoë",
            TagLine = "EUW",
            Region = "EUW",
            SummonerLevel = 247,
            RankedSolo = new RankedSoloInfo { Tier = "DIAMOND", Division = "II", LeaguePoints = 64 },
        };

        await _store.SaveAccountsAsync([account]);
        var loaded = Assert.Single(await new AccountStore(Root).LoadAccountsAsync());

        Assert.Equal("abc", loaded.Id);
        Assert.Equal("Zoë#EUW", loaded.RiotId);
        Assert.Equal(247, loaded.SummonerLevel);
        Assert.Equal(account.RankedSolo, loaded.RankedSolo);
    }

    [Fact]
    public async Task Settings_round_trip()
    {
        await _store.SaveSettingsAsync(new AppSettings
        {
            LaunchTarget = LaunchTarget.RiotClientOnly,
            ActiveAccountId = "abc",
        });

        var loaded = await new AccountStore(Root).LoadSettingsAsync();

        Assert.Equal(LaunchTarget.RiotClientOnly, loaded.LaunchTarget);
        Assert.Equal("abc", loaded.ActiveAccountId);
    }

    [Fact]
    public async Task Save_replaces_the_existing_file_and_leaves_no_temp_files()
    {
        await _store.SaveAccountsAsync([new AccountRecord { Id = "first" }, new AccountRecord { Id = "second" }]);
        await _store.SaveAccountsAsync([new AccountRecord { Id = "third" }]);

        Assert.Equal("third", Assert.Single(await _store.LoadAccountsAsync()).Id);
        Assert.Empty(Directory.GetFiles(Root, "*.tmp"));
    }

    [Fact]
    public async Task Failed_save_keeps_the_previous_file_and_cleans_up()
    {
        await _store.SaveAccountsAsync([new AccountRecord { Id = "original" }]);
        var path = Path.Combine(Root, "accounts.json");

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Record.ExceptionAsync(() => _store.SaveAccountsAsync([new AccountRecord { Id = "replacement" }]));
            Assert.True(error is IOException or UnauthorizedAccessException);
        }

        Assert.Equal("original", Assert.Single(await _store.LoadAccountsAsync()).Id);
        Assert.Empty(Directory.GetFiles(Root, "*.tmp"));
    }

    [Fact]
    public async Task Concurrent_saves_and_loads_do_not_collide()
    {
        await Task.WhenAll(Enumerable.Range(0, 30).Select(i => Task.Run(async () =>
        {
            await _store.SaveAccountsAsync([new AccountRecord { Id = $"account{i}" }]);
            Assert.Single(await _store.LoadAccountsAsync());
        })));

        Assert.Empty(Directory.GetFiles(Root, "*.tmp"));
    }

    [Theory]
    [InlineData("{ broken")]
    [InlineData("")]
    [InlineData("42")]
    public async Task Corrupt_json_loads_as_defaults_and_keeps_a_copy(string content)
    {
        CreateFile("accounts.json", content);
        CreateFile("settings.json", content);

        Assert.Empty(await _store.LoadAccountsAsync());
        Assert.Null((await _store.LoadSettingsAsync()).ActiveAccountId);
        Assert.Equal(content, File.ReadAllText(Path.Combine(Root, "accounts.json.corrupt")));
        Assert.Equal(content, File.ReadAllText(Path.Combine(Root, "settings.json.corrupt")));
    }

    [Fact]
    public async Task Null_json_entries_are_ignored()
    {
        CreateFile("accounts.json", """[null, { "Id": "kept" }]""");

        Assert.Equal("kept", Assert.Single(await _store.LoadAccountsAsync()).Id);
    }

    [Fact]
    public async Task Session_is_encrypted_at_rest_and_round_trips()
    {
        var plain = "ssid: very-secret-cookie"u8.ToArray();

        await _store.SaveSessionSnapshotAsync("abc", plain);

        var onDisk = File.ReadAllBytes(Path.Combine(Root, "sessions", "abc.bin"));
        Assert.Equal(-1, onDisk.AsSpan().IndexOf("very-secret-cookie"u8));
        Assert.Equal(plain, await new AccountStore(Root).LoadSessionSnapshotAsync("abc"));
    }

    [Fact]
    public async Task Session_can_be_deleted()
    {
        await _store.SaveSessionSnapshotAsync("abc", [1, 2, 3]);
        Assert.True(_store.HasSessionSnapshot("abc"));

        await _store.DeleteSessionSnapshotAsync("abc");

        Assert.False(_store.HasSessionSnapshot("abc"));
        Assert.Null(await _store.LoadSessionSnapshotAsync("abc"));
    }

    [Fact]
    public async Task Undecryptable_session_loads_as_null()
    {
        CreateFile(@"sessions\abc.bin", "not a DPAPI blob");

        Assert.Null(await _store.LoadSessionSnapshotAsync("abc"));
    }

    [Fact]
    public async Task Empty_session_is_rejected() =>
        await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveSessionSnapshotAsync("abc", []));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../evil")]
    [InlineData(@"..\evil")]
    [InlineData(@"C:\evil")]
    [InlineData("C:evil")]
    [InlineData("/evil")]
    [InlineData(@"\\host\share")]
    [InlineData("abc.")]
    [InlineData("abc:stream")]
    [InlineData("abc\n")]
    [InlineData("abc\0")]
    [InlineData("NUL")]
    [InlineData("com1")]
    public async Task Unsafe_account_ids_are_rejected(string id)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _store.SaveSessionSnapshotAsync(id, [1]));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.LoadSessionSnapshotAsync(id));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.DeleteSessionSnapshotAsync(id));
        Assert.False(_store.HasSessionSnapshot(id));
    }

    [Fact]
    public async Task Generated_account_ids_are_accepted()
    {
        foreach (var id in new[] { new AccountRecord().Id, Guid.NewGuid().ToString() })
        {
            await _store.SaveSessionSnapshotAsync(id, [1]);
            Assert.True(_store.HasSessionSnapshot(id));
        }
    }

    [Theory]
    [InlineData("RGAPI-1234")]
    [InlineData("пароль 🔐")]
    public void Secrets_round_trip(string secret)
    {
        var protectedValue = _store.ProtectSecret(secret);

        Assert.NotEqual(secret, protectedValue);
        Assert.Equal(secret, _store.UnprotectSecret(protectedValue));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("not base64!")]
    [InlineData("AAAAAAAA")]
    public void Unusable_secrets_unprotect_to_null(string? value) => Assert.Null(_store.UnprotectSecret(value));

    [Fact]
    public void Empty_secret_round_trips_to_null() => Assert.Null(_store.UnprotectSecret(_store.ProtectSecret("")));
}

public sealed class RiotClientPathDiscoveryTests : TempDirectoryTest
{
    private const string InstallsJson = """
        {
            "associated_client": {
                "D:/Riot Games/League of Legends (PBE)/": "D:/Riot Games/Riot Client/RiotClientServices.exe",
                "D:/Riot Games/League of Legends/": "D:/Riot Games/Riot Client/RiotClientServices.exe",
                "D:/Riot Games/VALORANT/live/": "D:/Riot Games/Riot Client/RiotClientServices.exe"
            },
            "patchlines": {
                "KeystoneFoundationLiveWin": "G:/Patchline/RiotClientServices.exe"
            },
            "rc_beta": "F:/Beta/RiotClientServices.exe",
            "rc_default": "D:/Riot Games/Riot Client/RiotClientServices.exe",
            "rc_live": "E:/Live/RiotClientServices.exe"
        }
        """;

    private string ProgramData => Path.Combine(Root, "ProgramData");
    private string[] Roots => [Path.Combine(Root, "drive-c"), Path.Combine(Root, "drive-d")];

    private static string Json(string path) => path.Replace('\\', '/');

    [Fact]
    public void Client_paths_are_normalized_and_in_priority_order()
    {
        Assert.Equal(
            [
                @"D:\Riot Games\Riot Client\RiotClientServices.exe",
                @"E:\Live\RiotClientServices.exe",
                @"F:\Beta\RiotClientServices.exe",
                @"G:\Patchline\RiotClientServices.exe",
            ],
            RiotClientPathDiscovery.ParseRiotClientPaths(InstallsJson));
    }

    [Fact]
    public void Duplicate_client_paths_are_collapsed()
    {
        const string json = """{ "rc_default": "D:/A/RiotClientServices.exe", "rc_live": "d:\\a\\RiotClientServices.exe" }""";

        Assert.Single(RiotClientPathDiscovery.ParseRiotClientPaths(json));
    }

    [Fact]
    public void League_directory_comes_from_associated_client_without_pbe_or_other_games()
    {
        Assert.Equal(
            [@"D:\Riot Games\League of Legends"],
            RiotClientPathDiscovery.ParseLeagueInstallDirectories(InstallsJson));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ broken")]
    [InlineData("[]")]
    [InlineData("""{ "rc_default": 5, "patchlines": [], "associated_client": "x" }""")]
    public void Unusable_installs_json_yields_nothing(string? json)
    {
        Assert.Empty(RiotClientPathDiscovery.ParseRiotClientPaths(json));
        Assert.Empty(RiotClientPathDiscovery.ParseLeagueInstallDirectories(json));
    }

    [Theory]
    [InlineData("C:/Windows/System32/cmd.exe")]
    [InlineData("//host/share/RiotClientServices.exe")]
    [InlineData("Riot Client/RiotClientServices.exe")]
    public void Untrusted_client_paths_are_rejected(string path) =>
        Assert.Empty(RiotClientPathDiscovery.ParseRiotClientPaths($$"""{ "rc_default": "{{path}}" }"""));

    [Theory]
    [InlineData("product_install_full_path: \"D:/Riot Games/League of Legends\"")]
    [InlineData("product_install_full_path: D:/Riot Games/League of Legends/")]
    [InlineData("product_install_full_path: 'D:\\Riot Games\\League of Legends'")]
    public void Metadata_install_path_is_parsed(string line)
    {
        var yaml = $"""
            locale_data:
                available_locales:
                - "en_US"
                default_locale: "en_GB"
            {line}
            product_install_root: "D:/Riot Games"
            """;

        Assert.Equal(@"D:\Riot Games\League of Legends", RiotClientPathDiscovery.ParseProductInstallPath(yaml));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("product_install_root: \"D:/Riot Games\"")]
    [InlineData("product_install_full_path: \"\"")]
    public void Metadata_without_install_path_yields_null(string? yaml) =>
        Assert.Null(RiotClientPathDiscovery.ParseProductInstallPath(yaml));

    [Fact]
    public void Client_override_accepts_file_or_directory()
    {
        var exe = CreateFile(@"custom\RiotClientServices.exe");

        Assert.Equal(exe, RiotClientPathDiscovery.FindRiotClient(exe, ProgramData, Roots));
        Assert.Equal(exe, RiotClientPathDiscovery.FindRiotClient(Path.GetDirectoryName(exe), ProgramData, Roots));
    }

    [Fact]
    public void Client_is_found_from_installs_json_skipping_stale_entries()
    {
        var exe = CreateFile(@"installed\RiotClientServices.exe");
        CreateFile(@"ProgramData\Riot Games\RiotClientInstalls.json", $$"""
            { "rc_default": "{{Json(Path.Combine(Root, "gone", "RiotClientServices.exe"))}}", "rc_live": "{{Json(exe)}}" }
            """);

        Assert.Equal(exe, RiotClientPathDiscovery.FindRiotClient(@"Q:\stale-override", ProgramData, Roots));
    }

    [Fact]
    public void Client_falls_back_to_search_roots()
    {
        var exe = CreateFile(@"drive-d\Riot Games\Riot Client\RiotClientServices.exe");

        Assert.Equal(exe, RiotClientPathDiscovery.FindRiotClient(null, ProgramData, Roots));
    }

    [Fact]
    public void League_override_accepts_exe_or_directory()
    {
        var exe = CreateFile(@"custom\LeagueClient.exe");
        var directory = Path.GetDirectoryName(exe);

        Assert.Equal(directory, RiotClientPathDiscovery.FindLeagueInstall(exe, ProgramData, Roots));
        Assert.Equal(directory, RiotClientPathDiscovery.FindLeagueInstall(directory, ProgramData, Roots));
    }

    [Fact]
    public void League_is_found_from_metadata()
    {
        var directory = Path.GetDirectoryName(CreateFile(@"any name\LeagueClient.exe"))!;
        CreateFile(
            @"ProgramData\Riot Games\Metadata\league_of_legends.live\league_of_legends.live.product_settings.yaml",
            $"product_install_full_path: \"{Json(directory)}\"");

        Assert.Equal(directory, RiotClientPathDiscovery.FindLeagueInstall(null, ProgramData, Roots));
    }

    [Fact]
    public void League_is_found_from_associated_client()
    {
        var directory = Path.GetDirectoryName(CreateFile(@"games\League of Legends\LeagueClient.exe"))!;
        CreateFile(@"ProgramData\Riot Games\RiotClientInstalls.json", $$"""
            { "associated_client": { "{{Json(directory)}}/": "unused" } }
            """);

        Assert.Equal(directory, RiotClientPathDiscovery.FindLeagueInstall(null, ProgramData, Roots));
    }

    [Fact]
    public void League_falls_back_to_search_roots()
    {
        var exe = CreateFile(@"drive-c\Riot Games\League of Legends\LeagueClient.exe");

        Assert.Equal(Path.GetDirectoryName(exe), RiotClientPathDiscovery.FindLeagueInstall(null, ProgramData, Roots));
    }

    [Fact]
    public void Nothing_installed_yields_null()
    {
        Assert.Null(RiotClientPathDiscovery.FindRiotClient(null, ProgramData, Roots));
        Assert.Null(RiotClientPathDiscovery.FindLeagueInstall(null, ProgramData, Roots));
    }

    [Fact]
    public void Default_search_covers_drives_c_to_f() =>
        Assert.Equal([@"C:\", @"D:\", @"E:\", @"F:\"], RiotClientPathDiscovery.DefaultSearchRoots);
}

public sealed class RiotYamlSessionParserTests
{
    private const string Puuid = "11111111-2222-3333-4444-555555555555";

    private const string SignedIn = $"""
        riot-login:
            persist:
                region: "EUW1"
                session:
                    cookies:
                    -   domain: "auth.riotgames.com"
                        hostOnly: true
                        name: "ssid"
                        path: "/"
                        value: "eyJhbGciOi.payload:with#symbols"
                    -   domain: "riotgames.com"
                        name: "sub"
                        value: "{Puuid}"
        rso-authenticator:
            tdid:
                name: "tdid"
                value: "device"
        """;

    [Fact]
    public void Signed_in_file_yields_session_puuid_and_region() =>
        Assert.Equal(new RiotSessionInfo(true, Puuid, "EUW"), RiotYamlSessionParser.Parse(SignedIn));

    [Fact]
    public void Crlf_and_byte_order_mark_are_tolerated()
    {
        var yaml = "\uFEFF" + SignedIn.ReplaceLineEndings("\r\n");

        Assert.Equal(new RiotSessionInfo(true, Puuid, "EUW"), RiotYamlSessionParser.Parse(yaml));
    }

    [Fact]
    public void Other_list_layouts_and_scalar_styles_are_supported()
    {
        const string yaml = $"""
            riot-login:
              persist:
                session:
                  cookies:
                    # indented list, value before name
                    - value: plain-token
                      name: 'ssid'
                    -
                      name: sub
                      value: {Puuid} # trailing comment
            """;

        Assert.Equal(new RiotSessionInfo(true, Puuid, null), RiotYamlSessionParser.Parse(yaml));
    }

    [Theory]
    [InlineData("ssid", true)]
    [InlineData("sub", true)]
    [InlineData("tdid", false)]
    [InlineData("clid", false)]
    public void Only_ssid_or_sub_make_a_session(string cookie, bool expected)
    {
        var yaml = $"""
            riot-login:
                persist:
                    session:
                        cookies:
                        -   name: "{cookie}"
                            value: "x"
            """;

        Assert.Equal(expected, RiotYamlSessionParser.Parse(yaml).HasSession);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not yaml at all")]
    [InlineData("riot-login:\n    persist: null")]
    [InlineData("riot-login:\n    persist:\n        session:\n            cookies: []")]
    [InlineData("riot-login:\n    persist:\n        session:\n            cookies:\n            -   name: ssid\n                value: \"\"")]
    [InlineData("riot-login:\n    persist:\n        session:\n            cookies:\n            -   name: ssid\n                value: null")]
    [InlineData("rso-authenticator:\n    tdid:\n        name: \"tdid\"\n        value: \"device\"")]
    [InlineData("other:\n    cookies:\n    -   name: ssid\n        value: x")]
    public void Signed_out_or_empty_files_have_no_session(string? yaml) =>
        Assert.Equal(new RiotSessionInfo(false, null, null), RiotYamlSessionParser.Parse(yaml));

    [Fact]
    public void Cookie_fields_do_not_leak_between_list_items()
    {
        const string yaml = """
            riot-login:
                persist:
                    session:
                        cookies:
                        -   name: "ssid"
                        -   name: "clid"
                            value: "x"
            """;

        Assert.False(RiotYamlSessionParser.Parse(yaml).HasSession);
    }

    [Theory]
    [InlineData("install:\n    globals:\n        locale: \"en_US\"\n        region: \"EUW\"", "EUW")]
    [InlineData("region: na1", "NA")]
    [InlineData("affinity: \"eun1\"", "EUNE")]
    [InlineData("affinity: kr\nregion: oc1", "OCE")]
    [InlineData("valorant:\n    region: eu\nleague:\n    region: la2", "LAS")]
    [InlineData("region: pbe1", "PBE")]
    [InlineData("affinity: eu", null)]
    [InlineData("locale: en_US", null)]
    public void Region_hints_are_normalized(string yaml, string? expected) =>
        Assert.Equal(expected, RiotYamlSessionParser.Parse(yaml).Region);
}

public sealed class RiotClientServiceTests : TempDirectoryTest
{
    private const string OutgoingPuuid = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string TargetPuuid = "bbbbbbbb-0000-0000-0000-000000000002";
    private const string StrangerPuuid = "cccccccc-0000-0000-0000-000000000003";

    private readonly AccountStore _store;
    private readonly FakeProcesses _processes = new();
    private readonly RiotClientService _service;
    private readonly AccountRecord _target = new() { Id = "target", GameName = "Target", TagLine = "EUW" };
    private readonly string _clientPath;

    public RiotClientServiceTests()
    {
        _store = new AccountStore(Path.Combine(Root, "store"));
        _clientPath = CreateFile(@"drive\Riot Games\Riot Client\RiotClientServices.exe");
        _service = CreateService();
    }

    private string SettingsPath => _service.GetRiotPrivateSettingsPath();

    private RiotClientService CreateService(TimeSpan? timeout = null, string drive = "drive") => new(
        _store,
        new RiotEnvironment
        {
            LocalAppData = Path.Combine(Root, "local"),
            ProgramData = Path.Combine(Root, "program-data"),
            SearchRoots = [Path.Combine(Root, drive)],
            Processes = _processes,
            Timeout = timeout ?? TimeSpan.FromSeconds(5),
            CloseGracePeriod = TimeSpan.FromMilliseconds(150),
        });

    private static byte[] Session(string puuid, string token = "token") => Encoding.UTF8.GetBytes($"""
        riot-login:
            persist:
                session:
                    cookies:
                    -   name: "ssid"
                        value: "{token}"
                    -   name: "sub"
                        value: "{puuid}"
        """);

    private void SetLiveSession(byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllBytes(SettingsPath, bytes);
    }

    private Task SaveTargetAsync() => _store.SaveSessionSnapshotAsync("target", Session(TargetPuuid));

    private Task SwitchAsync(string? currentId = null, LaunchTarget launch = LaunchTarget.LeagueOfLegends) =>
        _service.SwitchToAccountAsync(_target, currentId, launch);

    [Fact]
    public void Public_constructor_uses_the_real_local_app_data_path()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Riot Games", "Riot Client", "Data", "RiotGamesPrivateSettings.yaml");

        Assert.Equal(expected, new RiotClientService(_store).GetRiotPrivateSettingsPath());
    }

    [Fact]
    public async Task Missing_snapshot_fails_before_anything_is_touched()
    {
        var live = Session(OutgoingPuuid);
        SetLiveSession(live);
        _processes.Running.Add("RiotClientServices");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => SwitchAsync("outgoing"));

        Assert.Contains("Re-capture Session", error.Message);
        Assert.Empty(_processes.Calls);
        Assert.Equal(live, File.ReadAllBytes(SettingsPath));
        Assert.False(_store.HasSessionSnapshot("outgoing"));
    }

    [Fact]
    public async Task Corrupt_snapshot_fails_before_closing_clients()
    {
        CreateFile(@"store\sessions\target.bin", "garbage");
        _processes.Running.Add("RiotClientServices");

        await Assert.ThrowsAsync<InvalidOperationException>(() => SwitchAsync());

        Assert.Empty(_processes.Calls);
    }

    [Fact]
    public async Task Missing_client_fails_before_closing_clients()
    {
        await SaveTargetAsync();
        _processes.Running.Add("RiotClientServices");
        var service = CreateService(drive: "empty-drive");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SwitchToAccountAsync(_target, null, LaunchTarget.LeagueOfLegends));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareFreshLoginAsync(null));

        Assert.Empty(_processes.Calls);
    }

    [Fact]
    public async Task Switch_restores_the_session_and_launches_league()
    {
        await SaveTargetAsync();

        await SwitchAsync();

        Assert.Equal(Session(TargetPuuid), File.ReadAllBytes(SettingsPath));
        Assert.Equal(_clientPath, _processes.StartedPath);
        Assert.Equal(["--launch-product=league_of_legends", "--launch-patchline=live"], _processes.StartedArguments);
    }

    [Fact]
    public async Task Riot_client_only_launch_has_no_arguments()
    {
        await SaveTargetAsync();

        await SwitchAsync(launch: LaunchTarget.RiotClientOnly);

        Assert.Equal(_clientPath, _processes.StartedPath);
        Assert.Empty(_processes.StartedArguments!);
    }

    [Fact]
    public async Task Launch_client_does_not_stop_anything()
    {
        _processes.Running.Add("RiotClientServices");

        await _service.LaunchClientAsync(LaunchTarget.LeagueOfLegends);

        Assert.Equal(["start"], _processes.Calls);
    }

    [Fact]
    public async Task Outgoing_session_is_saved_before_clients_are_closed()
    {
        await SaveTargetAsync();
        await _store.SaveSessionSnapshotAsync("outgoing", Session(OutgoingPuuid, "old"));
        var rotated = Session(OutgoingPuuid, "rotated");
        SetLiveSession(rotated);
        _processes.Running.Add("RiotClientServices");

        byte[]? savedWhenClosing = null;
        _processes.OnClose = () => savedWhenClosing ??= _store.LoadSessionSnapshotAsync("outgoing").GetAwaiter().GetResult();

        await SwitchAsync("outgoing");

        Assert.Equal(rotated, savedWhenClosing);
        Assert.Equal(Session(TargetPuuid), File.ReadAllBytes(SettingsPath));
    }

    [Fact]
    public async Task Tokens_written_during_shutdown_are_saved()
    {
        await SaveTargetAsync();
        SetLiveSession(Session(OutgoingPuuid, "before"));
        _processes.Running.Add("RiotClientServices");
        var flushed = Session(OutgoingPuuid, "after");
        _processes.OnKill = () => File.WriteAllBytes(SettingsPath, flushed);

        await SwitchAsync("outgoing");

        Assert.Equal(flushed, await _store.LoadSessionSnapshotAsync("outgoing"));
    }

    [Fact]
    public async Task Sign_out_during_shutdown_does_not_replace_the_backup()
    {
        await SaveTargetAsync();
        var live = Session(OutgoingPuuid);
        SetLiveSession(live);
        _processes.Running.Add("RiotClientServices");
        _processes.OnKill = () => File.WriteAllText(SettingsPath, "riot-login:\n    persist: null");

        await SwitchAsync("outgoing");

        Assert.Equal(live, await _store.LoadSessionSnapshotAsync("outgoing"));
    }

    [Fact]
    public async Task Signed_out_file_does_not_replace_a_good_snapshot()
    {
        await SaveTargetAsync();
        var good = Session(OutgoingPuuid);
        await _store.SaveSessionSnapshotAsync("outgoing", good);
        SetLiveSession("rso-authenticator:\n    tdid:\n        value: device"u8.ToArray());

        await SwitchAsync("outgoing");

        Assert.Equal(good, await _store.LoadSessionSnapshotAsync("outgoing"));
    }

    [Fact]
    public async Task Another_accounts_session_does_not_replace_a_snapshot()
    {
        await SaveTargetAsync();
        var good = Session(OutgoingPuuid);
        await _store.SaveSessionSnapshotAsync("outgoing", good);
        SetLiveSession(Session(StrangerPuuid));

        await SwitchAsync("outgoing");

        Assert.Equal(good, await _store.LoadSessionSnapshotAsync("outgoing"));
    }

    [Fact]
    public async Task Another_accounts_session_is_not_saved_against_a_known_puuid()
    {
        await SaveTargetAsync();
        await _store.SaveAccountsAsync([new AccountRecord { Id = "outgoing", Puuid = OutgoingPuuid }]);
        SetLiveSession(Session(StrangerPuuid));

        await SwitchAsync("outgoing");

        Assert.False(_store.HasSessionSnapshot("outgoing"));
    }

    [Fact]
    public async Task Riot_api_puuid_does_not_block_the_backup()
    {
        await SaveTargetAsync();
        await _store.SaveAccountsAsync([new AccountRecord { Id = "outgoing", Puuid = new string('x', 78) }]);
        SetLiveSession(Session(OutgoingPuuid));

        await SwitchAsync("outgoing");

        Assert.True(_store.HasSessionSnapshot("outgoing"));
    }

    [Theory]
    [InlineData("target")]
    [InlineData("TARGET")]
    public async Task Target_snapshot_is_not_overwritten_when_it_is_already_active(string currentId)
    {
        await SaveTargetAsync();
        SetLiveSession(Session(OutgoingPuuid));

        await SwitchAsync(currentId);

        Assert.Equal(Session(TargetPuuid), await _store.LoadSessionSnapshotAsync("target"));
        Assert.Equal(Session(TargetPuuid), File.ReadAllBytes(SettingsPath));
    }

    [Fact]
    public async Task Newer_live_tokens_of_the_target_are_not_rolled_back()
    {
        await SaveTargetAsync();
        var rotated = Session(TargetPuuid, "rotated");
        SetLiveSession(rotated);

        await SwitchAsync("target");

        Assert.Equal(rotated, File.ReadAllBytes(SettingsPath));
    }

    [Fact]
    public async Task Only_riot_and_league_processes_are_closed()
    {
        await SaveTargetAsync();
        string[] expected =
        [
            "League of Legends", "LeagueClient", "LeagueClientUx", "LeagueClientUxRender",
            "Riot Client", "RiotClientCrashHandler", "RiotClientServices", "RiotClientUx", "RiotClientUxRender",
        ];
        string[] untouchable = ["vgc", "vgtray", "VALORANT-Win64-Shipping", "explorer"];
        _processes.Running.UnionWith(expected.Concat(untouchable));

        await SwitchAsync();

        Assert.Equal(expected.Order(), _processes.Killed.Order());
        Assert.Equal(expected.Order(), _processes.CloseRequested.Order());
        Assert.Equal(untouchable.Order(), _processes.Running.Order());
    }

    [Fact]
    public async Task Windows_that_close_gracefully_are_not_killed()
    {
        await SaveTargetAsync();
        _processes.Running.Add("LeagueClientUx");
        _processes.ExitsOnClose = true;

        await SwitchAsync();

        Assert.Empty(_processes.Killed);
    }

    [Fact]
    public async Task Process_that_will_not_exit_aborts_before_the_swap()
    {
        await SaveTargetAsync();
        var live = Session(OutgoingPuuid);
        SetLiveSession(live);
        _processes.Running.Add("RiotClientServices");
        _processes.IgnoresKill = true;
        var service = CreateService(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SwitchToAccountAsync(_target, null, LaunchTarget.LeagueOfLegends));

        Assert.Equal(live, File.ReadAllBytes(SettingsPath));
        Assert.Null(_processes.StartedPath);
    }

    [Fact]
    public async Task Switch_waits_for_the_file_lock_to_release()
    {
        await SaveTargetAsync();
        SetLiveSession(Session(OutgoingPuuid));

        Task switching;
        using (new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            switching = SwitchAsync();
            await Task.Delay(250);
            Assert.False(switching.IsCompleted);
        }

        await switching;
        Assert.Equal(Session(TargetPuuid), File.ReadAllBytes(SettingsPath));
    }

    [Fact]
    public async Task Permanent_file_lock_times_out_without_launching()
    {
        await SaveTargetAsync();
        SetLiveSession(Session(OutgoingPuuid));
        var service = CreateService(TimeSpan.FromMilliseconds(200));

        using var held = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await Assert.ThrowsAsync<IOException>(
            () => service.SwitchToAccountAsync(_target, null, LaunchTarget.LeagueOfLegends));

        Assert.Null(_processes.StartedPath);
    }

    [Fact]
    public async Task Switch_reports_progress()
    {
        await SaveTargetAsync();
        var progress = new ListProgress();

        await _service.SwitchToAccountAsync(_target, null, LaunchTarget.LeagueOfLegends, progress: progress);

        Assert.Equal("Switched to Target#EUW.", progress.Messages[^1]);
        Assert.True(progress.Messages.Count >= 4);
    }

    [Fact]
    public async Task Fresh_login_backs_up_clears_and_launches_plain_client()
    {
        var live = Session(OutgoingPuuid);
        SetLiveSession(live);
        File.WriteAllText(SettingsPath + ".bak", "backup");
        _processes.Running.Add("RiotClientServices");

        await _service.PrepareFreshLoginAsync("outgoing");

        Assert.Equal(live, await _store.LoadSessionSnapshotAsync("outgoing"));
        Assert.False(File.Exists(SettingsPath));
        Assert.False(File.Exists(SettingsPath + ".bak"));
        Assert.Contains("RiotClientServices", _processes.Killed);
        Assert.Empty(_processes.StartedArguments!);
    }

    [Fact]
    public async Task Fresh_login_works_without_any_riot_data()
    {
        await _service.PrepareFreshLoginAsync(null);

        Assert.Equal(_clientPath, _processes.StartedPath);
    }

    [Fact]
    public async Task Active_session_can_be_captured_and_inspected()
    {
        var live = Session(OutgoingPuuid);
        SetLiveSession(live);
        CreateFile(@"local\Riot Games\Riot Client\Config\RiotClientSettings.yaml", "install:\n    globals:\n        region: \"EUW\"");

        Assert.True(_service.HasActiveRiotSession());
        Assert.Equal(live, await _service.CaptureCurrentSessionBytesAsync());
        Assert.Equal(OutgoingPuuid, await _service.TryReadActiveSessionPuuidAsync());
        Assert.Equal("EUW", await _service.TryReadActiveSessionRegionAsync());
    }

    [Fact]
    public async Task No_active_session_is_reported_safely()
    {
        Assert.False(_service.HasActiveRiotSession());
        Assert.Null(await _service.TryReadActiveSessionPuuidAsync());
        Assert.Null(await _service.TryReadActiveSessionRegionAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.CaptureCurrentSessionBytesAsync());
    }

    [Fact]
    public void Running_state_follows_the_process_list()
    {
        Assert.False(_service.IsLeagueClientRunning());
        Assert.False(_service.IsRiotClientRunning());

        _processes.Running.UnionWith(["LeagueClientUx", "RiotClientServices"]);

        Assert.True(_service.IsLeagueClientRunning());
        Assert.True(_service.IsRiotClientRunning());
    }

    private sealed class ListProgress : IProgress<string>
    {
        public List<string> Messages { get; } = [];

        public void Report(string value) => Messages.Add(value);
    }

    private sealed class FakeProcesses : IRiotProcesses
    {
        public HashSet<string> Running { get; } = [];
        public HashSet<string> Killed { get; } = [];
        public HashSet<string> CloseRequested { get; } = [];
        public List<string> Calls { get; } = [];
        public string? StartedPath { get; private set; }
        public IReadOnlyList<string>? StartedArguments { get; private set; }
        public bool ExitsOnClose { get; set; }
        public bool IgnoresKill { get; set; }
        public Action? OnClose { get; set; }
        public Action? OnKill { get; set; }

        public bool IsRunning(string name) => Running.Contains(name);

        public bool RequestClose(string name)
        {
            Calls.Add("close");
            CloseRequested.Add(name);
            OnClose?.Invoke();
            if (!Running.Contains(name))
                return false;

            if (ExitsOnClose)
                Running.Remove(name);
            return true;
        }

        public void Kill(string name)
        {
            Calls.Add("kill");
            if (!Running.Contains(name))
                return;

            Killed.Add(name);
            OnKill?.Invoke();
            if (!IgnoresKill)
                Running.Remove(name);
        }

        public void Start(string path, IReadOnlyList<string> arguments)
        {
            Calls.Add("start");
            StartedPath = path;
            StartedArguments = arguments;
        }
    }
}
