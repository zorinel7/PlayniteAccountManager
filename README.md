# Playnite Account Manager

> 0.9.59 build verification


## Test environment

The current version of Playnite Account Manager has been tested on the following PC configuration:

| Component | Tested hardware / setting |
|---|---|
| **CPU** | Intel Core i5-12400 |
| **GPU** | NVIDIA GeForce RTX 5060 Ti 16 GB |
| **RAM** | 32 GB DDR4 3200 MHz (2×16 GB) |
| **Monitor** | AOC CQ27G2U/BK, 27-inch |
| **Resolution** | 2560 × 1440 (1440p) |
| **Operating system** | Windows 11 |

The plugin was tested in this environment with Playnite and the supported launcher integrations described below. Hardware not listed here may work as well, but has not been part of the current validation setup.


Playnite Account Manager is a Playnite extension for managing multiple accounts from different game launchers and assigning a selected account to a specific game.

The project is designed for a console-like Playnite setup where several launcher accounts can be kept on one PC and the correct account can be prepared before a game starts.

## Current version

**0.9.59**

Current release highlights:
- EA App supports Manual and Automatic login per account.
- Ubisoft Connect supports Manual and Automatic login per account.
- GOG Galaxy supports manual login per assigned account.
- Battle.net supports manual login per assigned account.
- Battle.net manual login now clears the remembered account and local authentication state more completely.
- EA App automatic login reproduces a tested AutoHotkey keyboard sequence without requiring AutoHotkey.
- EA App is detected without depending on a versioned installation folder.
- Ubisoft Connect executable detection searches multiple Windows installation sources instead of relying on one fixed path.
- Epic Games supports Manual and Automatic login per account.
- Manual EA App / Epic Games / Ubisoft Connect / GOG Galaxy / Battle.net login uses a masked password and click-to-copy credentials.
- Steam and Ubisoft Connect account switching remain available.
- The Account Manager uses Playnite localization resources.

## What the extension does

The extension keeps a local list of launcher accounts and a separate assignment for each game.

Example:

```text
Steam — MAIN
├── Game A → MAIN
├── Game B → MAIN
└── Game C → alternate Steam account

Epic Games — Account 1
└── Game D → automatic login

EA App — Account 2
└── Game E → manual login
```

The main concept is simple: the game determines which account flow should run, instead of forcing the user to change launcher accounts manually every time.

## Main features

### Account management
- Create multiple accounts for the supported launcher types.
- Save a login/e-mail and password for each account.
- Edit or delete saved accounts.
- Assign an account to one or more selected Playnite games.
- Keep independent assignments for different games.
- Mark one Steam account as the primary account.
- Configure logout after the game for each assignment.
- Select a launcher-specific login mode for Epic Games and EA App.
- Test supported automatic login from the Account Manager.

### Per-game assignments

Every assignment stores:
- the selected account;
- whether the legacy automatic-login flag is enabled;
- whether the launcher session should be logged out/cleared after the game.

For Epic Games and EA App, the account-specific Manual / Automatic login mode is the preferred authentication setting.

## Launcher support

| Launcher | Status | Login support |
|---|---|---|
| **Steam** | Stable | Account switching + primary account |
| **Ubisoft Connect** | Stable | Manual or automatic |
| **Epic Games** | Stable | Manual or automatic |
| **EA App** | Stable | Manual or automatic |
| **GOG Galaxy** | Stable | Manual only |
| Xbox App | Experimental / paused | Not implemented |
| Rockstar Games Launcher | Account model available | Automatic login not implemented |
| **Battle.net** | Stable | Manual only |
| Other | Account model available | Launcher-specific integration not implemented |

The launcher selector contains additional future integration targets. Saving an account for one of those launchers does not mean that automatic login is already implemented.

## How the launch flow works

```text
Playnite game
    ↓
Find game assignment
    ↓
Find assigned account
    ↓
Prepare launcher session
    ↓
Manual or automatic login
    ↓
Continue normal Playnite startup
    ↓
Optional logout after game
```

A game without a specific assignment is not switched to an arbitrary saved account.

## Steam

Steam supports per-game account switching.

One Steam account can be marked as the primary account. When a Steam game has no explicit assignment, the plugin can use that primary account as the fallback.

This makes it possible to keep one everyday Steam account and only switch profiles for games that require a different account.

## Ubisoft Connect

Ubisoft Connect keeps the existing automatic login flow based on native keyboard input.

The launcher account can be assigned to individual games in the same way as Steam and the other supported launchers.

## GOG Galaxy

GOG Galaxy supports manual login for an assigned account.

### Manual login
- GOG Galaxy is detected automatically.
- The current Galaxy processes are closed.
- Only local authentication/session state is cleared; the GOG game database is not removed.
- GOG Galaxy is started again using the detected executable.
- The shared manual login window is shown.
- Login and password can be copied to the clipboard and pasted into GOG Galaxy with Ctrl+V.

The session cleanup removes the local authentication token/lock state used by Galaxy without deleting the installed-game database. GOG documents the Galaxy launcher executable and its ProgramData paths, and the client also uses a local refreshToken value for authentication.

## Battle.net

Battle.net supports manual login for an assigned account.

### Manual login
- Battle.net is detected automatically.
- The current Battle.net and Agent processes are closed before session cleanup.
- Local Battle.net authentication/session state is cleared without deleting installed-game data.
- The remembered account is removed from `%APPDATA%\\Battle.net\\Battle.net.config` and automatic-login flags are disabled.
- Known Battle.net authentication registry state is cleared before the launcher is restarted.
- The detected Battle.net launcher is started again.
- The shared manual login window is shown.
- Login and password can be copied to the clipboard and pasted into Battle.net with Ctrl+V.

The integration accepts both `Battle.net Launcher.exe` and `Battle.net.exe` and searches standard Windows locations, registry entries, running processes, Start Menu shortcuts and likely Battle.net installation roots.

## Epic Games

Each Epic Games account has its own login mode:

### Manual login
- The launcher/session is prepared for the selected account.
- The login is displayed normally.
- The password is masked on screen.
- Clicking the login copies the real value to the clipboard.
- Clicking the password copies the real value to the clipboard.
- The user can paste the credentials into Epic Games with Ctrl+V.

### Automatic login
- The previous Epic Games launcher/session state is cleared.
- Epic Games is started again.
- The plugin verifies the Epic foreground window before sending input.
- The tested keyboard sequence is reproduced internally.
- AutoHotkey is not required.

For assigned games, the Epic account login mode determines whether the flow is manual or automatic.

## EA App

EA App now follows the same overall model as Epic Games: each account can be configured for Manual or Automatic login.

### Manual login
- EA App is detected automatically.
- The current-user EA App session state is prepared for the assigned account flow.
- EA App is restarted.
- The manual login window is shown.
- Login and password can be copied to the clipboard.

### Automatic login

The automatic flow reproduces the tested AHK sequence supplied during development:

```text
close EADesktop.exe
    ↓
wait 2 seconds
    ↓
start detected EA App executable
    ↓
wait 15 seconds
    ↓
type login
    ↓
TAB × 5
    ↓
ENTER
    ↓
wait 1 second
    ↓
type password
    ↓
TAB × 2
    ↓
ENTER
```

No external AutoHotkey process is required.

## Ubisoft Connect executable detection

Ubisoft Connect is no longer dependent on a single hard-coded executable location.

The plugin searches for the launcher using:
1. standard Program Files locations;
2. Program Files (x86);
3. ProgramW6432;
4. Windows uninstall registry entries;
5. Windows App Paths registry entries;
6. the executable path of a running Ubisoft launcher process;
7. Ubisoft installation roots searched recursively for UbisoftConnect.exe / upc.exe;
8. Ubisoft Start Menu shortcuts.

This allows the launcher to be found after installation changes without changing the plugin configuration.

## EA App executable detection

The EA App installation path may contain a versioned directory that changes after an update. The plugin therefore does not depend on a fixed folder name.

It searches for EADesktop.exe and EALauncher.exe using several sources:

1. Standard Program Files locations.
2. Program Files (x86).
3. ProgramW6432, which is useful when a 32-bit Playnite process is running on 64-bit Windows.
4. Windows uninstall registry entries.
5. Windows App Paths registry entries.
6. The executable path of an already running EADesktop / EALauncher process.
7. A recursive search inside likely EA App installation roots.

Both EADesktop.exe and EALauncher.exe are accepted.

For example, a directory such as the following may change after an EA App update:

```text
13.805.0.6318-1791240282
```

The plugin is designed to continue locating the executable without knowing that version number in advance.

## Session handling

The extension is intended to avoid unnecessary launcher changes.

### Assigned game

When a game has an account assignment, the corresponding launcher preparation flow runs before normal game startup.

### Unassigned game

An unassigned game does not switch to an arbitrary stored account simply because accounts exist in the manager.

### Logout after game

Each game assignment has its own logout-after-game setting.

When enabled, the plugin clears the active launcher session after the game stops or when Playnite cancels the startup flow.

### Important session limitation

Current session cleanup targets the relevant current-user application state. It does not guarantee that every machine-wide, service-level or externally stored authentication state is removed.

## Manual login window

The shared manual login dialog is used by EA App, Epic Games, Ubisoft Connect, GOG Galaxy and Battle.net.

The password is not shown as plain text. The visible value is masked:

```text
••••••••••••
```

Clicking the login or password field copies the real value to the Windows clipboard.

The intended controller-friendly flow is:

```text
Select game
  → assigned account
  → launcher prepared
  → login window
  → copy login/password
  → Ctrl+V
  → confirm
  → launch game
```

The dialog is shown through Playnite WPF dispatching so it can also be displayed when the game-start callback is not running on the normal UI thread.

## Account editor

The account editor provides the following fields and options:
- Account name.
- Launcher.
- Login / e-mail.
- Password.
- Automatic login assignment flag.
- Logout after game.
- Primary Steam account.
- Epic Games login mode.
- EA App login mode.
- GOG Galaxy manual login.
- Battle.net manual login.

For Epic Games and EA App, the launcher-specific Manual / Automatic selector is separate from the older assignment checkbox.

## Localization

The Account Manager UI uses Playnite localization resources through WPF DynamicResource and ResourceProvider.GetString.

Localization files are included in the .pext package.

The project contains resources for the locale codes supplied for the Playnite language set:

```text
af_ZA  ar_SA  bg_BG  ca_ES  cs_CZ  cy_GB  da_DK  de_DE
el_GR  en_US  eo_UY  es_ES  et_EE  fa_IR  fi_FI  fr_FR
ga_IE  gl_ES  he_IL  hr_HR  hu_HU  id_ID  it_IT  ja_JP
ko_KR  lt_LT  mr_IN  nl_NL  no_NO  pl_PL  pt_BR  pt_PT
ro_RO  ru_RU  si_LK  sk_SK  sl_SI  sr_SP  sv_SE  tr_TR
uk_UA  vi_VN  zh_CN  zh_TW
```

en_US is the base resource dictionary. Locale files override strings available for the selected locale and can inherit missing entries from English.

Polish pl_PL is the primary fully translated interface.

The intended behavior is for the extension language to follow the language selected for the Playnite application.

## Installation

1. Download or build the .pext package.
2. Open Playnite.
3. Install the extension from Playnite add-on / extension management.
4. Restart Playnite when requested.
5. Open Menadżer Kont from the Playnite main menu.
6. Add launcher accounts and assign them to games.

## Basic setup

### Ubisoft Connect

Each Ubisoft Connect account now has its own **Manual / Automatic** login mode.

**Manual**
- Ubisoft Connect is detected automatically.
- The existing Ubisoft local launcher state is cleared in the same preparation flow used for the account switch.
- Ubisoft Connect is restarted.
- The shared manual login window is shown.
- Login and password can be copied to the clipboard.

**Automatic**
- The existing automatic Ubisoft Connect login flow is kept unchanged.
- The selected account still uses the existing native keyboard automation and launcher preparation.
- Only the account mode selection and launcher detection were extended.

### Epic Games
1. Add an Epic Games account.
2. Enter and save the login and password.
3. Select Manualne logowanie or Automatyczne logowanie.
4. Save the account.
5. Assign the account to the required game.
6. Use the automatic-login test button when automatic mode is selected.

### GOG Galaxy
1. Add a GOG Galaxy account.
2. Enter and save the login and password.
3. Assign the account to the required game.
4. Use the test button to verify launcher detection and show the manual credential window.

### Battle.net
1. Add a Battle.net account.
2. Enter and save the login and password.
3. Assign the account to the required game.
4. Use the test button to verify launcher detection and show the manual credential window.

### EA App
1. Add an EA App account.
2. Enter and save the login and password.
3. Select Manualne logowanie or Automatyczne logowanie.
4. Save the account.
5. Assign the account to the required game.
6. Test automatic login before relying on it for normal launches.

### Steam
1. Add the Steam accounts you use.
2. Mark the preferred account as Główne konto Steam.
3. Assign an alternate Steam account only to games that need it.

### Ubisoft Connect
1. Add the Ubisoft account.
2. Enter and save the login and password.
3. Select **Manualne logowanie** or **Automatyczne logowanie**.
4. Save the account.
5. Assign the account to the required game.
6. Use the test button for the selected login mode when needed.

## Troubleshooting

### Ubisoft Connect was not found

Make sure UbisoftConnect.exe or upc.exe exists and that Ubisoft Connect can start normally.

The plugin checks standard Windows locations, registry information, running processes, Start Menu shortcuts and likely Ubisoft installation roots.

### EA App was not found

Check that EADesktop.exe or EALauncher.exe exists and that EA App can start normally.

The plugin does not depend on the version number in the EA App folder. It checks common Windows locations, registry information, running processes and likely installation roots.

The automatic login log records the detected executable path.

### Automatic login stops before typing

The automatic flow depends on the launcher showing the expected login UI and keeping the expected keyboard navigation order.

Check that:
- the launcher starts normally;
- the launcher window is visible and can receive keyboard focus;
- another application is not stealing the foreground window;
- the saved login and password are correct;
- the launcher UI has not changed its TAB order or authentication flow.

### Manual login window does not appear

Check the Playnite log around the game startup event.

The Account Manager logs launcher preparation and authentication mode information to make startup problems easier to diagnose.

### Automatic login worked before a launcher update

Launcher UI changes can alter the number of TAB presses or the timing required by the automation. In that situation the internal sequence may need to be updated.

## Security and credentials

Credentials are stored locally in the Windows user profile.

Password storage uses Windows DPAPI scoped to the current Windows user. Credentials are not stored in Git.

Never commit files containing private authentication data.

Examples:

```text
credentials.dat
launcher tokens
session data
private diagnostic logs
```

See SECURITY.md for additional security notes.

## Development

### Technology
- C#
- .NET Framework 4.6.2
- WPF
- Playnite SDK
- Windows DPAPI
- Win32 keyboard input / SendInput
- GitHub Actions

### Adapter-based structure

Launcher-specific behavior is kept in adapters and services so that new integrations can be added without replacing the core account-assignment model.

Important components include:

```text
Models/
  AccountRecord
  GameAccountAssignment
  AccountManagerSettings
  LauncherType

Services/
  AccountManagerStore
  SecureCredentialStore
  SteamAdapter
  UbisoftConnectAdapter
  EpicGamesAdapter
  EAAppAdapter
  EpicGamesUiAutomation
  EAAppUiAutomation
  *SessionStore

Views/
  AccountManagerView
  AccountManagerViewModel
  ManualLauncherLoginView
```

### Repository layout

```text
PlayniteAccountManager/
├── src/
│   └── PlayniteAccountManager/
│       ├── Models/
│       ├── Services/
│       ├── Views/
│       ├── Localization/
│       ├── extension.yaml
│       └── PlayniteAccountManager.csproj
├── scripts/
├── docs/
├── .github/workflows/
├── build.ps1
├── package.ps1
├── CHANGELOG.md
├── SECURITY.md
└── LICENSE
```

### Local build

Requirements:
- Windows.
- Visual Studio / MSBuild.
- .NET Framework 4.6.2 targeting pack.
- NuGet access to the Playnite SDK.

Build and package:

```powershell
./build.ps1
./package.ps1
```

The generated package is:

```text
PlayniteAccountManager_0.9.58.pext
```

### GitHub Actions

Pushes and pull requests to main run the automated pipeline:

```text
secret scan
    ↓
MSBuild Release
    ↓
plugin packaging
    ↓
GitHub Actions artifact
```

## Project goals

The long-term goal is to make Playnite behave more like a console front end while keeping the flexibility of PC launchers and multiple accounts.

The intended model is:

```text
Game
 ↓
Account assignment
 ↓
Launcher detection
 ↓
Session preparation
 ↓
Manual / automatic login
 ↓
Game startup
 ↓
Optional logout
```

Future launcher integrations can follow the same adapter-based design without changing the account-assignment model.

## Version history

See CHANGELOG.md for the complete history.

Recent releases:
- **0.9.59** — Fix Battle.net remembered-account and session cleanup.
- **0.9.58** — Battle.net manual login and launcher/session detection.
- **0.9.57** — GOG Galaxy manual login and launcher/session detection.
- **0.9.56** — Ubisoft Connect Manual / Automatic login mode and expanded launcher detection.
- **0.9.55** — EA App automatic login and version-independent launcher detection.
- **0.9.54** — Playnite localization resources.
- **0.9.53** — Larger Account Manager and manual login windows.
- **0.9.52** — Manual Epic login window startup fix.
- **0.9.51** — Epic automatic game-start login fix.
- **0.9.50** — Epic Games manual/automatic login.

## License

This project is licensed under the **GNU General Public License v3.0 (GPL-3.0)**.

See LICENSE.
