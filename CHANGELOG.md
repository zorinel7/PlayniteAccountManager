# Changelog

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
