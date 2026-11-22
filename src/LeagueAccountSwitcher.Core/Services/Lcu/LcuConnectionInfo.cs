using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LeagueAccountSwitcher.Core.Services.Lcu;

public sealed partial record LcuConnectionInfo(string ProcessName, int ProcessId, int Port, string AuthToken, string Protocol)
{
    /// <summary>Parses <c>LeagueClient:pid:port:token:protocol</c>.</summary>
    public static bool TryParseLockfile(string lockfileContent, [NotNullWhen(true)] out LcuConnectionInfo? info)
    {
        info = null;
        if (lockfileContent?.Trim().Split(':') is not [var name, var pidText, var portText, var token, var protocol])
            return false;

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(token))
            return false;

        if (!int.TryParse(pidText, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid == 0)
            return false;

        if (!TryParsePort(portText, out var port) || protocol is not ("https" or "http"))
            return false;

        info = new LcuConnectionInfo(name, pid, port, token, protocol);
        return true;
    }

    /// <summary>Parses the arguments that <c>LeagueClientUx.exe</c> is started with.</summary>
    public static bool TryParseCommandLine(string commandLine, int processId, [NotNullWhen(true)] out LcuConnectionInfo? info)
    {
        info = null;
        if (string.IsNullOrWhiteSpace(commandLine))
            return false;

        var portMatch = AppPortArgument().Match(commandLine);
        var tokenMatch = AuthTokenArgument().Match(commandLine);
        if (!portMatch.Success || !tokenMatch.Success || !TryParsePort(portMatch.Groups[1].Value, out var port))
            return false;

        info = new LcuConnectionInfo("LeagueClientUx", processId, port, tokenMatch.Groups[1].Value, "https");
        return true;
    }

    // The generated record ToString would put the auth token into logs.
    public override string ToString() => $"{ProcessName} ({ProcessId}) {Protocol}://127.0.0.1:{Port}";

    private static bool TryParsePort(string text, out int port) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535;

    // The lookbehind keeps --riotclient-app-port and similar Riot Client arguments from matching.
    [GeneratedRegex("""(?<![\w-])--app-port=["']?(\d+)(?=["'\s]|$)""")]
    private static partial Regex AppPortArgument();

    [GeneratedRegex("""(?<![\w-])--remoting-auth-token=["']?([^\s"']+)""")]
    private static partial Regex AuthTokenArgument();
}
