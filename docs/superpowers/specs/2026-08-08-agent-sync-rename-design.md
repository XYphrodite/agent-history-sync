# Agent Sync User-Facing Rename Design

## Goal

Rename the user-facing product and command from `codex-sync` to `agent-sync` for version 0.3.0. The application continues to synchronize Codex and Grok CLI histories.

## Compatibility boundary

Only public-facing identifiers change: the executable, CLI usage text, installer defaults, release assets, scheduled-task name, notifications, and operational documentation.

The existing `%LOCALAPPDATA%\CodexHistorySync` state directory, repository format, encrypted envelope, project namespaces, and managed history layout remain unchanged. This preserves existing keys, configuration, backups, conflict evidence, and remote-data compatibility.

## Migration

`agent install` registers `AgentHistorySync`. If it finds the legacy `CodexHistorySync` scheduled task with the same executable path, arguments, user, and logon trigger, it removes that legacy task first. A non-owned legacy task is left untouched. `agent uninstall` removes both task names only when each is owned by the current executable and user. Installation status recognizes either owned task during migration.

## Release and documentation

The CLI assembly is published as `agent-sync.exe`; release assets, SHA-256 filenames, PowerShell installer behavior, examples, and operational text use `agent-sync`. The version is 0.3.0. Documentation explicitly calls out that the legacy local data directory is intentionally retained.

## Verification

Update scheduler and CLI assembly-name tests. Run the affected Windows and integration test projects, build the solution, and scan user-facing sources for stale `codex-sync` references. Internal compatibility references are permitted.
