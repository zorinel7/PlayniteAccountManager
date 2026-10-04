# Development notes

## Principles

1. Keep Steam and Ubisoft Connect regressions isolated from experimental launcher integrations.
2. Prefer launcher APIs or stable launcher files where practical.
3. Use Windows UI Automation only when the launcher exposes a usable accessibility tree.
4. Do not log passwords, tokens, or field contents.
5. Do not use screen coordinates for launcher automation.
6. Avoid unnecessary background loops while a game is running.

## Versioning

Use a semantic version where practical. Keep stable launcher behavior unchanged when introducing a new launcher.

Recommended flow:

```text
main
  ↑
feature/<launcher>
```

Merge experimental launcher work only after local testing.
