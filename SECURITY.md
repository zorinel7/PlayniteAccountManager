# Security

## Credentials

Playnite Account Manager may store credentials locally when a launcher integration requires them. Password storage uses Windows DPAPI scoped to the current Windows user.

Do not commit `credentials.dat`, tokens, launcher session data, or diagnostic files containing private information.

## Reporting a vulnerability

Please do not publish exploitable credential or authentication issues in a public issue. Use a private security report or contact the repository owner directly.
