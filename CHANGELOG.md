# Changelog

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
