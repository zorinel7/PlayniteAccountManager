# Playnite Account Manager

A [Playnite](https://playnite.link) extension that keeps several accounts per game launcher and signs the right one in before a game starts. Each game is assigned an account; starting the game switches the launcher to it — automatically where the launcher allows it, otherwise with a controller-friendly manual login window.

**Version 0.9.65** · Windows · Playnite SDK 6 · GPL-3.0

## Supported launchers

| Launcher | Switching method | Time | Fallback |
|---|---|---|---|
| **Steam** | Remembered Steam accounts (`loginusers.vdf`), no password | — | — |
| **EA App** | Fills the login page through its accessibility tree (MSAA) | ~8 s | TAB sequence |
| **Ubisoft Connect** | Fills the login page through its accessibility tree (MSAA) | — | keyboard sequence |
| **Epic Games** | Restores a saved, encrypted "Remember me" session, no typing | ~5 s | TAB sequence (also creates the first session) |
| **Xbox App** | Selects the account in the Windows account picker, no password | ~8 s (0.1 s if already active) | manual window |
| **Rockstar Games Launcher** | Clears the session, fills the sign-in window through MSAA | — | manual window |
| **GOG Galaxy** | Clears the session, restarts Galaxy and fills its sign-in window | — | manual window |
| **Battle.net** | Clears the session, fills its sign-in window through MSAA | — | manual window |

EA App, Ubisoft Connect, Epic Games, GOG Galaxy and Battle.net can also be set to **Manual** per account.

## Installation

1. Download `PlayniteAccountManager_0.9.65.pext` from the latest [release](../../releases/latest) (or build it, see below).
2. Drag the file onto Playnite (or install it from *Add-ons*) and restart Playnite.
3. Open **Account Manager** (*Menadżer Kont*) from the Playnite main menu or from a game's context menu.

## Quick start

1. **Add an account** — name, launcher, login/e-mail and password. Choose *Automatic* or *Manual* for EA App, Ubisoft Connect, Epic Games, GOG Galaxy and Battle.net.
2. **Assign it** — select games in Playnite, open Account Manager and click *Assign*. Each assignment has:
   - **Automatic login** — keep it on for Steam and for Ubisoft Connect in automatic mode;
   - **Log out after game** — see [After the game](#after-the-game).
3. **Test** — the *Test* button runs the automatic login or shows the manual window.
4. **Start the game** — the assigned account is signed in first. Games without an assignment are left alone (except Steam, see below).

### Launcher-specific setup

| Launcher | What you need |
|---|---|
| Steam | Sign in to every Steam account once in Steam with "Remember me". Login = Steam account name. Mark one account as **Primary**: Steam games without an assignment use it (the first Steam account if none is marked). |
| Epic Games | Keep "Remember me" enabled in Epic. The first automatic login of an account types the credentials; later switches restore the saved session. |
| Xbox App | Add each Microsoft account to Windows: *Settings → Accounts → Email & accounts → Add a Microsoft account* ("Microsoft apps only"). Login = the account e-mail; the password is optional. |
| EA App, Ubisoft Connect, Rockstar, GOG Galaxy, Battle.net | Login and password. Additional verification (2FA, CAPTCHA) stops the automation and asks you to finish in the launcher. |

## How it works

- **EA App / Ubisoft Connect** — both login pages are Chromium pages. The plugin reads them through MSAA, finds the e-mail and password fields by role (not by caption or TAB order, so the launcher language does not matter), types into them, presses the button below and confirms that the login form is gone. Hidden off-screen inputs are ignored. If the fields are not exposed, the previous keyboard sequence is used.
- **Rockstar Games Launcher** — after the local session is cleared and the launcher restarts, the separate sign-in window (also a Chromium page) is filled the same way; success is the window closing. Its page answers accessibility queries slowly, so finding the form takes ~15 s on top of the launcher start. Any failure shows the manual login window.
- **Battle.net** — after the local session is cleared and Battle.net restarts, its sign-in window (Chromium) is filled through MSAA: a remembered account is left with *Switch account*, then e-mail → *Continue* and password → *Log in*. Fields are clicked before typing and the form is re-read after each step, because the page animates between steps. Success is the sign-in window closing; any failure shows the manual window.
- **GOG Galaxy** — the sign-in page runs in QtWebEngine, which exposes no accessibility tree. After clearing the session and restarting Galaxy, the plugin waits until `GalaxyClient.log` reports the loaded login page, clicks the e-mail and password fields at fixed positions of the fixed-size sign-in window, types the credentials and presses Enter. Success is the sign-in window closing or the log reporting the login redirect; otherwise the manual window is shown.
- **Epic Games** — the launcher draws its UI in Unreal Engine and exposes no accessibility tree, so accounts are switched with sessions instead: before switching, the current account's session is saved; the assigned account's session is restored and the launcher log confirms that the expected Epic account signed in. The rotated token is saved again right after login.
- **Xbox App** — profile menu → *Sign out* → *Sign in* → the account tile with the assigned e-mail in the Windows picker → *Continue* → Gaming Services confirmation (checked against the e-mail). Nothing happens if the remembered gamertag is already signed in.
- **Manual window** — shows the login and a masked password; clicking a field copies it to the clipboard for Ctrl+V in the launcher. Used for manual-mode accounts and as the Battle.net, GOG Galaxy, Rockstar and Xbox fallback.

Keyboard input is only sent while the launcher window has focus and stops immediately if focus moves to another application.

### After the game

When the game stops with **Log out after game** enabled, or whenever Playnite cancels the game start:

| Launcher | Action |
|---|---|
| Steam | Restores the Steam account that was active before |
| Epic Games | Saves the session for the next switch, then clears it |
| EA App, Ubisoft, GOG, Battle.net, Rockstar | Closes the launcher and clears the local session (installed games are never touched) |
| Xbox App | Stays signed in; the next game switches only if a different account is assigned |

## Security

- Passwords and Epic sessions are encrypted with **Windows DPAPI** (current Windows user + plugin-specific entropy). Their files in the plugin data folder are readable only by your Windows account and SYSTEM.
- Copied credentials are excluded from clipboard history (Win+V) and cloud clipboard and are cleared after 60 s or when the manual window closes.
- Passwords, tokens and field values are never logged; account names are masked in the Playnite log.
- DPAPI protects against other Windows users and copied files, not against malware running under your own account.

Plugin data (*Playnite → ExtensionsData → 2a6de1ef-…*):

| File | Content |
|---|---|
| `credentials.dat` | encrypted passwords |
| `epic-sessions\` | encrypted Epic sessions (delete to forget them) |
| `xbox-gamertags.txt` | gamertag per account (not secret) |

See [SECURITY.md](SECURITY.md) for reporting issues.

## Troubleshooting

| Problem | Check |
|---|---|
| Launcher not found | The plugin searches Program Files, registry, running processes and Start Menu shortcuts — make sure the launcher starts normally. |
| Automatic login stops | Look for lines starting with `EA MSAA`, `Ubisoft MSAA`, `Rockstar MSAA`, `Battle.net MSAA`, `GOG INPUT`, `Epic Games` or `Xbox` in the Playnite log (`%AppData%\Playnite\extensions.log`). |
| Epic always types the password | "Remember me" is disabled in Epic, or the saved session expired — the next successful login saves a new one. |
| Xbox shows the manual window | The account e-mail is not added in Windows *Email & accounts*, or it differs from the login in Account Manager. |
| Game Pass game reports a licence error | The Microsoft Store is signed in with a different account than the Xbox app ("Account mismatch" banner in the Xbox app). |

## Building

Requirements: Windows, Visual Studio 2022+ (MSBuild), .NET Framework 4.6.2 targeting pack.

```powershell
./build.ps1      # Release build
./package.ps1    # creates PlayniteAccountManager_0.9.65.pext
```

Every push to `main` runs a secret scan, the Release build and packaging in GitHub Actions and uploads the `.pext` as an artifact.

```text
src/PlayniteAccountManager/
├── Models/        accounts, assignments, launcher types
├── Services/      one adapter per launcher, ChromiumLoginAutomation (MSAA),
│                  EpicSessionVault, XboxAppUiAutomation, SecureCredentialStore
├── Views/         Account Manager and manual login window
└── Localization/  44 Playnite languages (en_US base, pl_PL complete)
```

Tested on Windows 11 (i5-12400, RTX 5060 Ti, 2560×1440) with Steam, EA App 13.809, Ubisoft Connect 170, Epic Games Launcher 20.3, GOG Galaxy 2.1, Battle.net and Xbox App 2610.

## History and license

Release notes: [CHANGELOG.md](CHANGELOG.md). Author: **zorinel**. Licensed under [GPL-3.0](LICENSE).
