# Security

## Credentials

Playnite Account Manager may store credentials locally when a launcher integration requires them. Password storage uses Windows DPAPI scoped to the current Windows user, with additional plugin-specific entropy. The `credentials.dat` file is restricted to the current user and SYSTEM.

DPAPI protects the data against other Windows users and offline copies of the file. It does not protect against malware already running under your own Windows account.

Epic Games session snapshots (launcher "Remember me" refresh tokens) are stored per account in `epic-sessions\` in the plugin data folder with the same DPAPI protection and file permissions. Deleting that folder removes all saved Epic sessions; the next login of each account falls back to the keyboard sequence.

Credentials copied from the manual login window are excluded from Windows clipboard history and cloud clipboard and are cleared automatically. Automatic login types credentials only while the launcher window has keyboard focus.

Do not commit `credentials.dat`, tokens, launcher session data, or diagnostic files containing private information.

## Reporting a vulnerability

Please do not publish exploitable credential or authentication issues in a public issue. Use a private security report or contact the repository owner directly.
