using LeagueAccountSwitcher.Core.Models;

namespace LeagueAccountSwitcher.Core.Services;

public interface IRiotClientService
{
    string? DetectRiotClientPath(string? overridePath = null);
    string? DetectLeagueInstallPath(string? overridePath = null);
    string GetRiotPrivateSettingsPath();

    bool HasActiveRiotSession();
    bool IsLeagueClientRunning();
    bool IsRiotClientRunning();

    Task<byte[]> CaptureCurrentSessionBytesAsync(CancellationToken ct = default);
    Task<string?> TryReadActiveSessionPuuidAsync(CancellationToken ct = default);
    Task<string?> TryReadActiveSessionRegionAsync(CancellationToken ct = default);

    Task SwitchToAccountAsync(
        AccountRecord targetAccount,
        string? currentActiveAccountId,
        LaunchTarget launchTarget,
        string? riotClientPathOverride = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default);

    Task LaunchClientAsync(
        LaunchTarget launchTarget,
        string? riotClientPathOverride = null,
        CancellationToken ct = default);

    Task PrepareFreshLoginAsync(
        string? currentActiveAccountId,
        string? riotClientPathOverride = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default);
}
