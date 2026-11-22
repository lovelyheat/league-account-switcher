# League Account Switcher (WinUI 3) — Implementation Plan

## 1. Goal & Scope

Build a clean, fast, native Windows 11 **WinUI 3** desktop application to switch between League of Legends accounts with one click, while displaying key account details at a glance:

- **Account Identity**: Riot ID (`GameName#Tag`), optional custom nickname, Summoner Profile Icon
- **Region**: Platform/region badge (e.g., `EUW`, `NA`, `KR`, `EUNE`, `OCE`, etc.)
- **Level**: Summoner Level (e.g., `Lv. 247`)
- **Ranked Solo/Duo**: Tier emblem, Tier & Division, LP, and Win/Loss (e.g., `Diamond II · 64 LP`, `Master · 215 LP`, or `Unranked`)

Keep the app intentionally **simple and focused**—no bloat, no background overlays, and no game/Vanguard tampering.

---

## 2. Environment & Prerequisites

Checked on this machine:
- **Windows SDK**: `10.0.22621.0` and `10.0.26100.0` are installed.
- **Riot Data Path**: `%LOCALAPPDATA%\Riot Games\Riot Client\Data\RiotGamesPrivateSettings.yaml` exists, while `C:\Riot Games\...` does not (so Riot Client install path must be discovered dynamically, never hardcoded).
- **.NET SDK**: Runtimes (`6.0`, `8.0`, `9.0`, `10.0`) are installed, but **no .NET SDK is currently installed**.
  - **Setup step (Phase 0)**: Install the `.NET 8 SDK` (LTS, best-in-class WinUI 3 / Windows App SDK support) via `winget install Microsoft.DotNet.SDK.8` before scaffolding and building.

---

## 3. Tech Stack & Project Configuration

| Area | Choice | Why |
| :--- | :--- | :--- |
| **UI Framework** | WinUI 3 (`Microsoft.WindowsAppSDK`) | Native Windows 11 Fluent Design, Mica backdrop, crisp typography |
| **Runtime / TFM** | .NET 8 (`net8.0-windows10.0.19041.0`) | LTS stability + full CsWinRT / WinUI 3 tooling compatibility |
| **Packaging** | Unpackaged desktop app (`<WindowsPackageType>None</WindowsPackageType>`) | Builds and runs directly via `dotnet build` / `dotnet run` without MSIX certificate setup |
| **Architecture** | MVVM (`CommunityToolkit.Mvvm`) | Clean separation between WinUI views and switching/API logic |
| **Secret Protection** | Windows DPAPI (`System.Security.Cryptography.ProtectedData`) | Encrypts saved Riot session files per-Windows-user at rest |
| **HTTP & JSON** | `HttpClient` + `System.Text.Json` | Zero extra dependencies for LCU, Data Dragon, and local storage |
| **YAML Parsing** | Lightweight parser or `YamlDotNet` | Reading Riot's `RiotGamesPrivateSettings.yaml` / product metadata safely |

---

## 4. Core Technical Design

### 4.1 Account Switching (Session Snapshot Method)

Instead of storing plaintext passwords or automating Riot Client's login UI (which breaks on UI updates and fails with 2FA), the switcher uses **Riot Client session snapshots** ("Stay signed in" tokens):

1. **Riot Client Path Discovery**
   - Read `C:\ProgramData\Riot Games\RiotClientInstalls.json` (`rc_default` / `rc_live` / `rc_beta`) to locate `RiotClientServices.exe` regardless of drive or custom install directory.
   - Allow manual override in Settings if needed.
2. **Capturing an Account**
   - **Capture Current Session**: Reads `%LOCALAPPDATA%\Riot Games\Riot Client\Data\RiotGamesPrivateSettings.yaml` (which holds Riot's `riot-login.persist.session` cookies when "Stay signed in" is checked), encrypts it with DPAPI (`DataProtectionScope.CurrentUser`), and saves it under `%LOCALAPPDATA%\LeagueAccountSwitcher\sessions\<accountId>.bin`.
   - **Add New Account (Fresh Login)**: Saves/updates the currently active account's snapshot, closes Riot/League clients, clears the active `RiotGamesPrivateSettings.yaml`, launches Riot Client to the login screen, and captures the new session once sign-in completes.
3. **Switching to an Account**
   - **Preserve Token Rotation**: First snapshot and re-encrypt the *currently active* account's `RiotGamesPrivateSettings.yaml` so refreshed session cookies are never lost.
   - **Close Client Processes Safely**: Gracefully terminate League and Riot Client processes (`LeagueClient`, `LeagueClientUx`, `LeagueClientUxRender`, `League of Legends`, `RiotClientServices`, `RiotClientUx`, `RiotClientCrashHandler`) and wait until file handles are released. **Never touch Vanguard (`vgc`, `vgtray`).**
   - **Swap Session**: Decrypt the target account's snapshot and atomically write it to `%LOCALAPPDATA%\Riot Games\Riot Client\Data\RiotGamesPrivateSettings.yaml`.
   - **Launch**: Start `RiotClientServices.exe --launch-product=league_of_legends --launch-patchline=live` (or Riot Client only, configurable in Settings).

> **Important Note on Riot Sessions**: Switching via the app closes Riot Client *without* clicking "Sign Out", keeping saved sessions valid across switches (including 2FA accounts). If a user manually clicks "Sign Out" inside Riot Client, Riot revokes that session server-side, and the app will offer a 1-click **Re-capture Session** action.

### 4.2 Fetching Level, Ranked Solo/Duo Rank & Region

#### Primary Source — Local League Client API (LCU) *(Zero API Key Required)*
When League Client is running, it exposes a local HTTPS API authenticated via its `lockfile` (or `LeagueClientUx.exe` command-line args `--app-port` and `--remoting-auth-token`):

- `GET /lol-summoner/v1/current-summoner` → `puuid`, `gameName`, `tagLine`, `summonerLevel`, `profileIconId`
- `GET /lol-ranked/v1/current-ranked-stats` → `queueMap.RANKED_SOLO_5x5` (`tier`, `division`, `leaguePoints`, `wins`, `losses`, `isProvisional`)
- `GET /riotclient/region-locale` → `region` (e.g., `EUW`, `NA`, `KR`, `EUNE`, `OCE`, etc.)

**How sync works**:
- A lightweight background monitor watches for a running League Client while the switcher is open.
- Whenever League Client connects (after switching or capturing an account), the app matches the account by `puuid` (or active account ID) and automatically updates **Riot ID**, **Region**, **Level**, **Solo/Duo Rank + LP + W/L**, **Profile Icon**, and `LastSyncedAt`.
- All stats are cached locally so every saved account immediately displays its latest known Region, Level, and Solo/Duo Rank even when League is closed.

#### Static Visual Assets — Riot Data Dragon *(No API Key Required)*
- Fetches and caches summoner profile icons locally under `%LOCALAPPDATA%\LeagueAccountSwitcher\cache\icons\`.
- Ranked tier emblems (Iron → Challenger + Unranked) bundled cleanly as local assets/badges so rank display works offline with zero flicker.

#### Optional Enhancement — Riot API Key *(Inactive Account Refresh)*
- Because personal Riot Developer API keys expire every 24 hours, the app **does not require one**.
- In Settings, an optional Riot API key field can be provided if the user ever wants to click **Refresh All** to update Level & Solo/Duo Rank for accounts without logging into them (`Account-V1` → `Summoner-V4` → `League-V4`).

### 4.3 Local Storage Layout

All app data lives under `%LOCALAPPDATA%\LeagueAccountSwitcher\`:

```text
%LOCALAPPDATA%\LeagueAccountSwitcher\
├── accounts.json          # Non-sensitive account metadata (id, puuid, riotId, region, level, rank, lastSynced)
├── settings.json          # App preferences (paths, launch mode, theme, optional Riot API key via DPAPI)
├── sessions\
│   └── <accountId>.bin    # DPAPI-encrypted RiotGamesPrivateSettings.yaml snapshot
└── cache\
    └── icons\             # Cached Data Dragon profile icons
```

---

## 5. Project Structure

```text
LeagueAccountSwitcher.sln
└── src\
    └── LeagueAccountSwitcher\
        ├── LeagueAccountSwitcher.csproj
        ├── App.xaml / App.xaml.cs
        ├── MainWindow.xaml / MainWindow.xaml.cs
        ├── Models\
        │   ├── AccountRecord.cs          # Id, Puuid, GameName, TagLine, Alias, Region, Level, RankedSoloInfo, LastSyncedAt
        │   ├── RankedSoloInfo.cs         # Tier, Division, LeaguePoints, Wins, Losses, FormattedRank
        │   └── AppSettings.cs            # Paths, launch target, theme, auto-sync
        ├── Services\
        │   ├── IAccountStore.cs / AccountStore.cs            # JSON persistence + DPAPI session read/write
        │   ├── IRiotClientService.cs / RiotClientService.cs  # Path discovery, process stop, session swap, launch
        │   ├── ILcuService.cs / LcuService.cs                # Lockfile/process discovery + LCU REST queries
        │   ├── IAssetCacheService.cs / AssetCacheService.cs  # Data Dragon icon download & local cache
        │   └── IRiotApiService.cs / RiotApiService.cs        # Optional Riot API refresh for inactive accounts
        ├── ViewModels\
        │   ├── MainViewModel.cs          # Account collection, search/filter, switch command, LCU sync status
        │   ├── AccountItemViewModel.cs   # Display formatting (Lv., Rank/LP, Region badge, Active state)
        │   ├── AddAccountViewModel.cs    # Capture current vs fresh login workflow
        │   └── SettingsViewModel.cs      # Paths, preferences, optional API key
        ├── Views\
        │   ├── AddAccountDialog.xaml     # ContentDialog for adding/capturing an account
        │   ├── EditAccountDialog.xaml    # ContentDialog for custom label / manual fallback edit
        │   └── SettingsDialog.xaml       # Clean settings dialog
        └── Assets\
            ├── AppIcon.ico
            └── Ranks\                    # Clean rank tier emblems (Unranked, Iron..Challenger)
```

---

## 6. UI / UX Design (WinUI 3 Fluent)

- **Window**: Compact, resizable desktop window (~760×620 default) with **Mica** backdrop and a custom title bar (`ExtendsContentIntoTitleBar = true`).
- **Header Toolbar**:
  - Search box (filter by Riot ID, nickname, or region) — shown cleanly inline
  - Region filter dropdown (`All Regions`, `EUW`, `NA`, etc., dynamically populated from saved accounts)
  - Primary **`+ Add Account`** button
  - **`Sync`** button (triggers immediate LCU read of the active client) + **`Settings`** gear icon
- **Account Cards (Main List)**:
  Each account row is a crisp Fluent card displaying:
  1. **Left**: Circular/rounded Summoner Profile Icon + green active status ring/dot when currently active
  2. **Center-Left**:
     - **Riot ID** (`Faker#KR1`) in semi-bold + optional subtle nickname
     - **Region pill** (`EUW`, `NA`, `KR`) + **Level badge** (`Lv. 247`)
  3. **Center-Right (Ranked Solo/Duo)**:
     - Rank tier emblem + color-coded rank text:
       - Standard tiers: `Emerald III · 48 LP`
       - Apex tiers (Master / Grandmaster / Challenger): `Master · 215 LP` (no division)
       - Unranked: `Unranked`
     - Subtle secondary caption: `62W 48L (56%)` (when ranked)
  4. **Right**:
     - **`Switch`** accent button (disabled/labeled `Active` if already running on that account, with a quick `Launch` option if League isn't open)
     - Overflow `⋯` menu: *Re-capture Session*, *Edit Label*, *Remove Account*
- **Footer / Status Strip**:
  - Live indicator showing League Client connection state (`League Client connected · Synced 1m ago` or `Ready`) and transient `InfoBar` feedback during switches or errors.

---

## 7. Implementation Phases

### Phase 0 — Environment Setup
1. Install `.NET 8 SDK` via `winget` and verify `dotnet --info`.
2. Initialize solution (`LeagueAccountSwitcher.sln`) and unpackaged WinUI 3 project (`src/LeagueAccountSwitcher/LeagueAccountSwitcher.csproj`).
3. Verify clean `dotnet build` and window launch.

### Phase 1 — Core Storage & Riot Client Switching Engine
1. Implement `AccountRecord`, `RankedSoloInfo`, and `AppSettings` models.
2. Implement `AccountStore` with atomic JSON writes and DPAPI encryption/decryption for session snapshots.
3. Implement `RiotClientService`:
   - Auto-discover `RiotClientServices.exe` via `RiotClientInstalls.json`.
   - Safe Riot/League process termination (excluding Vanguard).
   - Active-session backup (token-rotation preservation) + session restore + League launch.

### Phase 2 — LCU Auto-Sync & Asset Caching
1. Implement `LcuService`:
   - Discover active League Client `port` and `auth-token` (via `lockfile` and `LeagueClientUx` process args fallback).
   - Query `/lol-summoner/v1/current-summoner`, `/lol-ranked/v1/current-ranked-stats`, and `/riotclient/region-locale`.
   - Background polling timer (lightweight, e.g., every 10s when app is open) to detect logins/rank changes and update the active account automatically.
2. Implement `AssetCacheService` to fetch & cache Data Dragon profile icons and provide bundled rank emblems.
3. Add optional `RiotApiService` (only active if user sets an API key in Settings) to refresh inactive accounts on demand.

### Phase 3 — WinUI 3 UI & Interaction Flows
1. Build `MainWindow` with Mica backdrop, custom title bar, search/region filter, account cards (Profile Icon, Riot ID, Region badge, Level, Ranked Solo/Duo emblem + Tier/Div/LP/W-L, Switch button), and empty-state onboarding.
2. Build `AddAccountDialog` supporting both **Capture Current Session** and **Sign In to New Account** flows.
3. Build `EditAccountDialog` (custom alias / manual info fallback) and `SettingsDialog` (paths, launch mode, theme, optional Riot API key).

### Phase 4 — Verification & Polish
1. Write unit tests for rank formatting (Unranked, Iron–Diamond with divisions, Master–Challenger apex tiers), DPAPI round-trip encryption, `RiotClientInstalls.json` parsing, and LCU JSON deserialization.
2. Build and run the WinUI 3 app end-to-end, verifying layout in both Dark and Light themes, empty state, and live LCU/Riot path discovery on this machine.
3. Update `README.md` with build/run instructions and usage notes.

---

## 8. Edge Cases & Safeguards

- **Token Rotation**: Always re-save the outgoing active account's `RiotGamesPrivateSettings.yaml` before overwriting it with the target account's snapshot.
- **Session Expired / Revoked**: If the user signed out manually inside Riot Client, switching may open the login prompt—surface a clear "Re-capture Session" option in the account menu.
- **File Locks**: Wait with a short retry/backoff after stopping Riot Client processes before copying `RiotGamesPrivateSettings.yaml`.
- **Apex Tiers**: Master, Grandmaster, and Challenger have no Roman division (`I` should be hidden, displaying e.g. `Grandmaster · 512 LP`).
- **Unranked / Fresh Accounts**: Gracefully show `Unranked` when `RANKED_SOLO_5x5.tier` is `NONE` or missing.
- **Vanguard Safety**: Never kill, restart, or touch `vgc` / `vgtray` / kernel services, and never inject into game processes.
