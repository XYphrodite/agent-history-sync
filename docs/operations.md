# Codex History Sync Operations

## Prerequisites

Codex History Sync currently runs on Windows. Install `git`, GitHub CLI (`gh`), and Codex, then authenticate `gh`. Setup accepts only HTTPS GitHub repository URLs. The repository must already exist, be private, and be empty for `init`.

Passphrases are accepted only from an interactive console with hidden input. They are never accepted as command-line arguments, written to configuration, or printed. Keep the passphrase in a password manager: the DPAPI cache is tied to the current Windows user and cannot recover a forgotten passphrase on another device.

## Setup

Initialize an empty private repository:

```powershell
codex-sync init https://github.com/OWNER/REPOSITORY.git
```

`init` checks both private visibility and repository emptiness before prompting twice. It creates a random repository ID, device ID, and Argon2id salt; derives the repository key; and publishes the authenticated manifest and encrypted empty index in one initialization commit. It checks emptiness again immediately before publication to reject a concurrent initializer. Only after that commit succeeds does it cache the key with DPAPI and write local configuration and state.

If the remote commit succeeds but local caching fails, do not initialize again. The remote is valid and can be recovered with `join`.

On another Windows profile, first preview the import:

```powershell
codex-sync join https://github.com/OWNER/REPOSITORY.git
```

The join command verifies private visibility before prompting once. It pins the current branch SHA, reads and authenticates the manifest and encrypted index at that exact SHA, runs the disposable Codex compatibility probe with a controlled synthetic session, and prints authenticated planner-derived local, remote, pending, and conflict counts. It makes no persistent key, configuration, state, conflict-evidence, or Codex-history change during this dry run, and the pending in-memory key is zeroed when the command ends.

Apply the first import only after reviewing the counts:

```powershell
codex-sync join https://github.com/OWNER/REPOSITORY.git --apply
```

Wrong passphrases, tampered metadata, unsupported schema, failed visibility checks, failed compatibility probes, or a branch update after authentication stop before persistent join state or history imports. The applied join reports and returns exit code 4 from the actual pull result if conflicts are discovered after the preview.

## Manual synchronization

```powershell
codex-sync sync   # bidirectional
codex-sync pull   # download/apply only; never publish local history
codex-sync push   # publish only; never replace local history
```

All three commands call the same `SyncEngine` used by automation. Output contains only revisions and object counts. A successful operation records its last successful remote revision. Conflicts are preserved as encrypted evidence and return exit code 4; they are never resolved by overwriting live history implicitly.

## Status and diagnostics

```powershell
codex-sync status
codex-sync doctor
```

`status` performs the same authenticated, non-mutating three-way planning used by synchronization. It reports local, remote, pending, and conflict counts plus the current authenticated remote revision; equal counts do not hide divergent objects.

`doctor` reports PASS or FAIL for these checks without printing paths, child-process output, URLs, credentials, keys, prompts, or exception text:

- Codex paths and version
- Git version
- GitHub private visibility
- DPAPI key access
- repository schema/index authentication
- Codex process detection
- free disk space
- background-agent installation

Before setup, the repository/key checks are expected to fail. Before Task 9 agent installation, the agent check is also expected to fail.

## Conflicts

List unresolved conflicts:

```powershell
codex-sync conflicts
```

The listing contains only conflict IDs, hashes, device IDs, and UTC timestamps. It never displays decrypted history.

Choose exactly one resolution action:

```powershell
codex-sync resolve CONFLICT_ID --keep-local
codex-sync resolve CONFLICT_ID --keep-remote
codex-sync resolve CONFLICT_ID --export-both C:\existing-parent\new-export-directory
```

`--export-both` decrypts both retained envelopes into a newly created directory. The destination must be absolute, must not already exist, must have an existing parent, must be outside Codex history and conflict storage, and must pass the existing reparse-point/path-boundary checks. Export does not resolve the conflict, retains its encrypted evidence, and returns exit code 4 while any conflict remains.

The keep actions authenticate the stored evidence and current remote snapshot while holding the repository lock. The chosen side is published with compare-and-swap before any local mutation, then applied through the guarded history writer and recorded as the new baseline. Evidence is removed only after remote, local, and state work all succeed. A failure retains evidence for diagnosis or an idempotent retry; no plaintext is printed.

## Exit codes

| Code | Meaning |
|---:|---|
| 0 | Command completed successfully |
| 1 | Operational failure, such as unavailable Git/GitHub or local I/O failure |
| 2 | Invalid command or option usage |
| 3 | Security or compatibility gate failure |
| 4 | One or more conflicts remain unresolved |

Diagnostics deliberately use stable summaries. Raw exception messages and external command output are not copied to CLI output because they may contain credential-bearing URLs, prompt plaintext, or other sensitive material.
