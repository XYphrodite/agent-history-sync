# Codex History Sync Design

**Date:** 2026-07-28  
**Status:** Approved design, pending written-spec review

## Goal

Build a Windows 11 utility that synchronizes local Codex conversation history between multiple PCs through an encrypted private GitHub repository. Different chats may be edited concurrently on different devices. Editing the same chat on multiple devices at the same time is outside the supported workflow, but conflicting versions must be detected and preserved without data loss.

The first storage provider is Git/GitHub. The core must allow later providers such as OneDrive or a network share without changing synchronization or encryption logic.

## Scope

The MVP synchronizes active sessions, archived sessions, and session attachments from the Windows Codex home directory, which defaults to `%USERPROFILE%\.codex`. It provides both manual CLI commands and automatic background synchronization.

The MVP does not synchronize authentication tokens, SQLite databases, logs, caches, sandbox state, or machine identities. It does not merge two divergent continuations of one conversation automatically.

## Repository and Delivery

The source repository lives at `C:\Repos\codex-history-sync`. The application is a self-contained Windows x64 executable named `codex-sync.exe`, so target machines do not need a separately installed .NET runtime.

The encrypted history remote is a separate private GitHub repository selected during setup. It is data storage, not the source repository.

## Architecture

### Local Codex adapter

The adapter discovers the effective `CODEX_HOME`, defaulting to `%USERPROFILE%\.codex`, and handles:

- active session JSONL files under `sessions`;
- archived session JSONL files under `archived_sessions`;
- attachments referenced by synchronized sessions;
- local backups and atomic installation of incoming files.

It never copies Codex SQLite state between machines. Import/reindex behavior must be verified against the supported Codex executable during implementation. If the current Codex build cannot rebuild its local index from session files safely, implementation stops at that compatibility gate rather than modifying an undocumented SQLite schema.

### Encryption layer

Each logical object is encrypted independently with AES-256-GCM. A repository-level random salt and Argon2id derive a master key from the user's passphrase. Per-object keys are derived from the master key, and every changed object receives a fresh unique nonce. Authenticated associated data binds ciphertext to the schema version, logical object identifier, and object type.

The passphrase is never written to disk. On each PC, the derived key is stored using Windows DPAPI or Credential Manager and is accessible only to that Windows user. The user enters the shared passphrase once when joining a device.

GitHub can observe repository activity, ciphertext sizes, and commit timing. Chat titles, contents, attachments, and plaintext local paths are encrypted. Repository paths use opaque identifiers.

### Synchronization engine

The engine performs a three-way comparison for each logical chat using:

- the current local plaintext hash;
- the current remote authenticated hash/reference;
- the last common version recorded in device-local state.

Outcomes are deterministic:

| Local | Remote | Result |
|---|---|---|
| changed | unchanged | upload local version |
| unchanged | changed | import remote version |
| same change | same change | accept the shared version |
| different change | different change | preserve both and create a conflict |
| deleted | unchanged | publish a tombstone |
| unchanged | deleted | apply the tombstone after backup |

The engine does not silently choose a winner for divergent content.

### Provider interface

The synchronization engine depends on a storage-provider interface that supports:

- reading the latest remote revision;
- listing and retrieving encrypted objects;
- publishing an atomic revision with compare-and-swap semantics;
- reporting connectivity and authentication failures.

The Git provider implements this interface with a dedicated local clone and a private GitHub remote. It fetches `origin/main`, reconciles objects in application logic, creates a commit, and pushes. If the push is rejected because another device advanced the branch, it fetches the new revision and repeats reconciliation with a bounded retry count. Git never merges ciphertext files.

The dedicated clone is tool-managed and contains no user-authored files. Any reset of its worktree is therefore scoped to that clone and must never target the source repository or `CODEX_HOME`.

### CLI and background agent

Manual commands use the same synchronization engine as the agent:

```text
codex-sync init
codex-sync join <private-github-url>
codex-sync sync
codex-sync pull
codex-sync push
codex-sync status
codex-sync doctor
codex-sync conflicts
codex-sync resolve <chat-id> --keep-local
codex-sync resolve <chat-id> --keep-remote
codex-sync resolve <chat-id> --export-both
codex-sync agent install
codex-sync agent uninstall
```

`init` creates encrypted-repository metadata and the first device identity. `join` validates the repository format and passphrase before changing local history. `doctor` checks Git, GitHub access, Codex paths, key access, repository format, and agent state without exposing secrets.

The agent starts at Windows logon. While Codex is running, it may export stable, complete JSONL snapshots and stage remote data, but it must not replace Codex files. A JSONL file is stable only after repeated size/mtime checks and successful parsing through the final newline. After all relevant Codex processes exit, the agent waits for a short quiescence interval, performs full bidirectional reconciliation, creates backups, and atomically installs incoming data. While Codex remains closed, it synchronizes periodically.

If incoming changes are pending when Codex starts, the agent defers installation and notifies the user that a restart is required. The agent must never terminate Codex automatically.

## Conflict Handling

A conflict occurs when local and remote versions both differ from the last common version. Both encrypted versions and their provenance are retained in a conflict area. The live Codex file is not overwritten until the user resolves the conflict.

- `--keep-local` publishes the local version as the new common version.
- `--keep-remote` backs up the local version and installs the remote version.
- `--export-both` decrypts both versions into a user-selected export directory. Creating a second live Codex thread is allowed only if implementation can do so through a supported Codex interface; the utility must not rewrite undocumented session identifiers.

## Deletion and Retention

Deletions propagate as encrypted tombstones rather than immediate physical removal. Applying a tombstone creates a local backup first. Backups are retained for 30 days by default, with a configurable retention period.

Permanent remote cleanup is a separate explicit maintenance operation. It reports affected object counts and requires confirmation. Git history rewriting is outside the MVP.

## Security Requirements

- Refuse to synchronize `auth.json`, credential stores, SQLite databases, logs, caches, `.sandbox`, `.sandbox-secrets`, machine identifiers, or temporary files.
- Never log prompt text, decrypted attachments, passphrases, derived keys, Git credentials, or plaintext repository paths.
- Authenticate all encrypted metadata and reject unknown schema versions before writes.
- Use atomic temporary-file replacement for local imports.
- Keep backups outside synchronized Codex paths to prevent recursive ingestion.
- Use Git Credential Manager for GitHub credentials; the utility must not manage PATs itself.
- Verify private-repository visibility when GitHub tooling/API access is available. If visibility cannot be verified, stop setup with instructions instead of assuming the repository is private.

## Failure Behavior

- Offline or GitHub unavailable: retain local changes and retry later.
- Push race: fetch the new remote revision and rerun reconciliation.
- Wrong passphrase or authentication failure: make no local-history changes.
- Corrupt or tampered ciphertext: quarantine the object and report an error.
- Partial JSONL or active write: defer that session until stable.
- Disk full or interrupted import: leave the original file intact and clean temporary files on the next run.
- Unsupported Codex storage behavior: fail the compatibility check and provide diagnostics; never patch SQLite speculatively.
- Repeated automatic failure: apply exponential backoff and surface a Windows notification while leaving manual commands available.

## Testing

### Unit tests

- AES-GCM round trips, unique nonces, wrong passwords, tampering, and associated-data mismatch.
- Argon2id repository parameters and key separation.
- Complete three-way synchronization matrix, tombstones, retention, and conflict resolution.
- Log redaction and exclusion rules.

### Integration tests

- Two isolated device directories and a local bare Git remote.
- Concurrent commits to different chats and rejected-push retry.
- Active and archived sessions, attachments, partial final JSONL records, compaction, and deletions.
- Offline operation, corrupt objects, interrupted operations, and full disks where practical.

### Windows tests

- DPAPI/Credential Manager key persistence and user isolation.
- Agent install/uninstall and logon startup.
- Codex process detection, stable-file export, deferred import, and post-exit synchronization.

### End-to-end acceptance

All end-to-end tests use disposable `CODEX_HOME` directories and never touch real user history. Two simulated PCs must be able to create different chats concurrently, exchange them through Git, expose both chats after Codex restarts, and recover from an injected same-chat conflict without losing either version.

## Implementation Gates

Before building the full sync engine, a compatibility spike must prove that the installed supported Codex version discovers imported active and archived JSONL sessions without transferring SQLite state. The spike uses disposable profiles and records exact Codex versions. This is a hard gate.

Before enabling background writes, tests must prove atomic import and backup restoration under forced interruption. Encryption test vectors and the two-device Git race test must pass before any real-history opt-in.

## Success Criteria

The MVP is complete when:

1. A user can initialize a private encrypted GitHub history repository and join it from a second Windows 11 PC.
2. Manual and automatic synchronization share one engine and produce equivalent results.
3. Different chats created concurrently on two devices converge on both devices.
4. Same-chat divergence is detected, both versions survive, and the user can resolve it explicitly.
5. No credentials, SQLite state, plaintext prompts, or plaintext attachments reach Git.
6. Network, process, encryption, and interruption failures do not destroy existing Codex history.

