# Changelog

## 0.9.59 - Battle.net session cleanup fix
- Fixed Battle.net manual login cleanup when `Client.SavedAccountNames` is stored as a JSON string instead of an array.
- The remembered Battle.net account is now explicitly cleared from `%APPDATA%\\Battle.net\\Battle.net.config`.
- Battle.net `RememberAccountName`, `AutoLogin` and `AutoLoginCN` values are disabled during manual account switching while preserving the existing value type used by the config.
- Added cleanup of known current-user Battle.net authentication registry state (`Identity`, `Authenticator` and `WEB_TOKEN` values).
- Existing Battle.net cached-data and cache cleanup remains in place.
- Existing Steam, Ubisoft Connect, GOG Galaxy, EA App and Epic Games integrations remain unchanged.

## 0.9.58 - Battle.net manual login
- Added manual Battle.net login for assigned games.
- Battle.net executable detection checks running launcher processes, standard Program Files locations, Windows uninstall entries, App Paths, Start Menu shortcuts and likely Battle.net installation roots.
- Manual Battle.net login closes Battle.net, Battle.net Launcher and Agent processes before clearing local authentication/session state.
- Session cleanup clears Battle.net CachedData.db files, local cache folders and the saved account-name list in Battle.net.config without deleting installed-game data.
- The detected Battle.net launcher is started again and the shared masked credential window is shown, with click-to-copy login and password.
- Added Battle.net manual login to the Account Manager test flow.
- Existing Steam, Ubisoft Connect, GOG Galaxy, EA App and Epic Games behavior remains unchanged.

## 0.9.57 - GOG Galaxy manual login
- Added manual GOG Galaxy login for assigned games.
- GOG Galaxy executable detection checks running Galaxy processes, standard Program Files locations, the GOG Galaxy / GalaxyClient registry path, Windows uninstall entries, App Paths, Start Menu shortcuts and likely installation roots.
- Manual GOG Galaxy login closes GalaxyClient, GalaxyClientService and GalaxyCommunication before clearing the local authentication/session state.
- Session cleanup removes GOG Galaxy authentication token/lock state without deleting the local installed-game database.
- After session preparation the detected GalaxyClient.exe is started and the shared masked credential window is shown, with click-to-copy login and password.
- Added GOG Galaxy manual login to the Account Manager test flow.
- Existing Steam, Ubisoft Connect, EA App and Epic Games behavior remains unchanged.

## 0.9.56 - Ubisoft Connect manual/automatic login mode
- Added a per-account Ubisoft Connect login mode: Manual or Automatic.
- Added the same manual login flow for Ubisoft Connect that is already used by EA App and Epic Games.
- Manual Ubisoft Connect login prepares the launcher, clears the existing local Ubisoft launcher state, starts the detected launcher and then displays the shared credential window.
- Automatic Ubisoft Connect login behavior remains unchanged; the existing automatic keyboard automation is still used when Automatic mode is selected.
- Ubisoft Connect executable discovery was expanded to cover Program Files, Program Files (x86), ProgramW6432, Windows uninstall registry entries, App Paths, running launcher processes, Start Menu shortcuts and recursive searches in likely Ubisoft installation roots.
- Fixed the Ubisoft uninstall registry path used by launcher detection.
- Added Ubisoft login mode selection and localization strings to the Account Manager.
- Existing Steam, EA App and Epic Games behavior remains available.
## 0.9.55 - EA App automatic login integration
- Added per-account EA App login mode selection: Manual or Automatic.
- Added automatic EA App login using the same keyboard sequence as the supplied AHK script, implemented internally without requiring AutoHotkey.
- Automatic EA App login closes the previous launcher session, clears the current-user EA App session state, waits 2 seconds, starts EA App, waits 15 seconds, then enters the saved login and password with the configured Tab/Enter sequence.
- EA App launcher discovery no longer depends on a versioned installation folder. The plugin searches standard installation locations, Windows registry entries, running EA App processes, and likely EA App installation roots for EADesktop.exe / EALauncher.exe.
- Assigned games can now trigger EA App automatic login independently of the legacy per-game "Automatyczne logowanie" checkbox.
- Added automatic EA App login testing from the Account Manager.
- Existing manual EA App login, Epic Games, Steam and Ubisoft Connect behavior remains available.

## 0.9.54 - Playnite language localization
- Added localization resources for the language/locale codes supplied for the plugin.
- Account Manager UI now uses Playnite language resources through DynamicResource.
- Runtime messages set from C# use Playnite's ResourceProvider.GetString.
- Localization files are included in the .pext package.
- Polish (pl_PL) localization is included as the primary fully translated UI.

## 0.9.53 - Increase account manager and login window sizes
- Increased the default and minimum size of the Account Manager window so long text and buttons are not clipped.
- Increased the default and minimum size of the manual launcher login window.
- No launcher login behavior was changed.
## 0.9.52 - Fix manual Epic login window during game startup
- Manual Epic login dialog is now shown through the Playnite WPF Dispatcher when `OnGameStarting` runs outside the UI thread.
- Added explicit logging of the saved Epic login mode and assignment AutoLogin state for easier diagnostics.
- Epic automatic login behavior and all other launchers remain unchanged.


## 0.9.51 - Fix Epic automatic game launch login
- Epic Automatic mode now runs independently of the legacy per-assignment "Automatyczne logowanie" checkbox.
- Epic automatic input now follows the supplied AHK more closely by operating on the Epic foreground window after the same 15-second startup delay.
- The plugin verifies that the foreground window belongs to EpicGamesLauncher.exe before sending login credentials.
- Manual Epic login and other launchers remain unchanged.

## 0.9.50 - Epic Games manual/automatic login
- Added a per-account Epic Games login mode: Manual or Automatic.
- Automatic mode reproduces the supplied AutoHotkey sequence inside the plugin without requiring AutoHotkey.
- Automatic Epic login closes Epic Games Launcher and EpicWebHelper, clears the active Epic session state, starts the launcher again, and then sends the configured Tab/Enter/input sequence.
- Login and password are read from the existing protected credential store; the real password is never shown in the UI.
- Manual Epic Games login continues to use the existing masked credential window.
- Steam, Ubisoft Connect and EA App behavior is left unchanged.


## 0.9.49 - Fix EA App detection on 32-bit Playnite processes
- EA App detection now explicitly checks the Windows `ProgramW6432` environment variable, covering the 64-bit `C:\Program Files` directory when Playnite runs as a 32-bit process.
- The detection log now records `ProgramFiles`, `ProgramFiles(x86)` and `ProgramW6432` when EA App cannot be found, making future diagnostics easier.


## 0.9.48 - Fix EA App launcher detection
- EA App detection now checks the documented default installation paths for both 64-bit and 32-bit Program Files locations.
- Added detection through the Windows uninstall registry entries and App Paths registry entries.
- Added detection of the actual executable path from a running EADesktop/EALauncher process.
- Added a last-resort search inside likely EA App installation folders for EADesktop.exe and EALauncher.exe.
- Manual EA/Epic password masking from 0.9.47 is retained.

## 0.9.47 - Remove EA helper and mask manual passwords
- EA App no longer requires PlayniteAccountManager.EAHelper.exe.
- Removed the elevated helper and scheduled-task/UAC setup from the build and package.
- Assigned EA App games clear the current user's EA Desktop state without an administrator prompt.
- Unassigned EA App games leave the active EA App session untouched.
- EA and Epic manual login windows display a masked password while click-to-copy continues to copy the real password to the clipboard.
- The package contains only PlayniteAccountManager.dll and extension.yaml.

# Changelog

## 0.9.46 - EA App manual per-game login hardening
- EA App now mirrors the Epic Games manual login flow more closely.
- Assigned EA App games clear the active EA session before the manual login window is shown.
- Unassigned EA App games leave the existing EA App session untouched.
- EA launcher discovery happens before stopping the running launcher, so non-standard installations can still be detected.
- Both EADesktop and EALauncher processes are stopped before the session state is cleared.
- EALauncher.exe is preferred when starting EA App.
- Login and password fields continue to copy to the clipboard for manual Ctrl+V entry in EA App.
- Fixed the CI artifact/package version to 0.9.46.

## 0.9.45 - Harden Epic Games manual login
- Epic Games session cleanup also terminates EpicWebHelper processes before deleting login state.
- Epic Games manual login remains fully user-driven: the manager never enters the credentials into the Epic launcher.
- Assigned Epic Games games clear the active Epic session before the manual login window is shown.
- Unassigned Epic Games games leave the existing Epic Games session untouched.
- Fixed the GitHub Actions artifact name/path to match the current package version.

## 0.9.44 - Epic Games manual per-game login
- Fixed the Epic Games account registry path used when clearing the active session.
- Epic Games launcher discovery now also checks the running launcher before it is closed.
- Fixed the account manager test button so Epic Games manual login is actually available for Epic accounts.
- Assigned Epic Games games clear the current Epic session before the manual login window is shown.
- Unassigned Epic Games games leave the current Epic Games session untouched.
- Login and password fields in the manual login window copy to the clipboard when clicked, allowing Ctrl+V in Epic Games Launcher.

- Steam and Ubisoft Connect code kept from the stable baseline.
- Project structure prepared for additional launchers.

## 0.9.0 - Stable baseline
- Per-game Steam account assignment.
- Main Steam account fallback for games without an explicit assignment.
- Stable Ubisoft Connect integration retained.

## 0.9.43 - Manual EA App and Epic Games login
- Restored EA App and Epic Games launcher support.
- Added manual per-game login flow for assigned EA App and Epic Games accounts.
- Added a credential window with click-to-copy login and password fields.
- EA App and Epic Games sessions are cleared before an assigned game starts.
- Unassigned EA App and Epic Games games do not modify the current launcher session.
- EA App session cleanup uses the existing per-user elevated helper model; Playnite itself remains unelevated.
