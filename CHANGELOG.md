# Changelog

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
