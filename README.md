<img src="assets/icon.svg" width="64" height="64" alt="">

# League Account Switcher (WinUI 3)

A lightweight, native Windows 11 **WinUI 3** desktop application for switching between League of Legends accounts with one click while displaying each account's **Region**, **Summoner Level**, and **Ranked Solo/Duo Rank**.

## Features

- **One-Click Session Switching (No Passwords Stored)**: Captures Riot Client's *"Stay signed in"* session (`RiotGamesPrivateSettings.yaml`), encrypts each snapshot per Windows user via **Windows DPAPI**, preserves rotated tokens on switch, and launches League of Legends directly. Works with 2FA accounts and never touches Vanguard (`vgc` / `vgtray`).
- **Automatic Account Stats (Zero API Key Required)**: Automatically syncs **Riot ID**, **Region**, **Summoner Level**, **Ranked Solo/Duo (Tier, Division, LP, W/L)**, and **Profile Icon** directly from the local League Client (LCU) whenever League is open.
- **Native WinUI 3 Fluent UI**: Mica backdrop, Dark/Light/System theme support, custom vector rank emblems (Unranked through Challenger), search, region filter, and sorting by last used, rank, level, or name.
- **Optional Riot API Refresh**: Optionally configure a Riot Developer API key in Settings to refresh inactive accounts without signing into them.

## Requirements

- Windows 10 (build 19041+) or Windows 11 (x64)
- [.NET 8 SDK (x64)](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)

## Build & Run

```powershell
# Build the solution (x64)
dotnet build LeagueAccountSwitcher.sln -p:Platform=x64

# Run the WinUI 3 app
dotnet run --project src/LeagueAccountSwitcher/LeagueAccountSwitcher.csproj -p:Platform=x64

# Run the unit test suite
dotnet test tests/LeagueAccountSwitcher.Tests/LeagueAccountSwitcher.Tests.csproj -p:Platform=x64
```

## Quick Usage

1. Sign in to Riot Client with **"Stay signed in"** checked.
2. Open **League Account Switcher** and click **Add Account → Capture Current Session**.
3. To add more accounts, click **Add Account → Sign In to New Account** (backs up your current session, opens a clean Riot Client login, then captures the new session).
4. Click **Switch** on any saved account to swap sessions and launch League of Legends.
   - *Tip:* Always switch through the app rather than clicking **Sign Out** inside Riot Client (signing out revokes the saved session token server-side).
