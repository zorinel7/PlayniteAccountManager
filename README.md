# Playnite Account Manager

Playnite extension for managing multiple launcher accounts and assigning a selected account to an individual game.

## Current status

| Launcher | Status |
|---|---|
| Steam | Stable |
| Ubisoft Connect | Stable |
| EA App | Manual login |
| Epic Games | Manual login |
| Xbox App | Experimental / paused |

### Design

A launcher can have a **main account** plus alternative accounts assigned to individual games.

Example:

```text
Steam main account
├── Game A → main
├── Game B → main
└── Game C → alternative account
```

The goal is for Playnite to handle the account switch only when the selected game requires a different account.

## Repository layout

```text
PlayniteAccountManager/
├── src/PlayniteAccountManager/   # plugin source
├── scripts/                      # CI/security helper scripts
├── .github/workflows/            # automatic build
├── docs/                         # project documentation
├── build.ps1                     # Release build
├── package.ps1                   # plugin ZIP packaging
├── BUILD_PLAYNITE_ACCOUNT_MANAGER_0.9.7.bat
└── CHANGELOG.md
```

## Build locally

Requirements:

- Windows
- Visual Studio / MSBuild
- .NET Framework 4.6.2 targeting pack
- NuGet access for Playnite SDK

Run:

```powershell
./build.ps1
./package.ps1
```

The plugin package is created as:

```text
PlayniteAccountManager_0.9.45.pext
```

You can also run the supplied BAT file.

## GitHub Actions

Every push and pull request to `main` runs:

```text
secret scan
   ↓
MSBuild Release
   ↓
plugin package
   ↓
GitHub Actions artifact
```

## Credentials and privacy

No credentials should ever be committed to this repository. Runtime credential storage is local to the user's Windows profile.

See `SECURITY.md` for security notes.

## License

This repository includes the **GNU General Public License v3.0 (GPL-3.0)**. See `LICENSE`.
