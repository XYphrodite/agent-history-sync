# Agent History Sync Operations

## Prerequisites

Agent History Sync runs on Windows. Install `git`, GitHub CLI (`gh`), and the agents you sync (Codex and/or Grok CLI), then authenticate `gh`. Setup accepts only HTTPS GitHub repository URLs. The **data** repository must already exist, be private, and be empty for `init` (recommended name: `agent-history-sync-data`).

The public command is `agent-sync`. For compatibility with existing installations, the installer deliberately keeps `agent-sync.exe` under `%LOCALAPPDATA%\Programs\CodexHistorySync`, while application state remains under `%LOCALAPPDATA%\CodexHistorySync`. These are separate directories; upgrading does not migrate or rename either one.

Passphrases are accepted only from an interactive console with hidden input. They are never accepted as command-line arguments, written to configuration, or printed. Keep the passphrase in a password manager: the DPAPI cache is tied to the current Windows user and cannot recover a forgotten passphrase on another device.

## Setup

Initialize an empty private repository:

```powershell
agent-sync init https://github.com/OWNER/REPOSITORY.git
```

`init` checks both private visibility and repository emptiness before prompting twice. It creates a random repository ID, device ID, and Argon2id salt; derives the repository key; and publishes the authenticated manifest and encrypted empty index in one initialization commit. It checks emptiness again immediately before publication to reject a concurrent initializer. Only after that commit succeeds does it cache the key with DPAPI and write local configuration and state.

If the remote commit succeeds but local caching fails, do not initialize again. The remote is valid and can be recovered with `join`.

On another Windows profile, first preview the import:

```powershell
agent-sync join https://github.com/OWNER/REPOSITORY.git
```

The join command verifies private visibility before prompting once. It pins the current branch SHA, reads and authenticates the manifest and encrypted index at that exact SHA, runs the disposable Codex compatibility probe with a controlled synthetic session, and prints authenticated planner-derived local, remote, pending, and conflict counts. It makes no persistent key, configuration, state, conflict-evidence, or Codex-history change during this dry run, and the pending in-memory key is zeroed when the command ends.

Apply the first import only after reviewing the counts:

```powershell
agent-sync join https://github.com/OWNER/REPOSITORY.git --apply
```

Wrong passphrases, tampered metadata, unsupported schema, failed visibility checks, failed compatibility probes, or a branch update after authentication stop before persistent join state or history imports. The applied join reports and returns exit code 4 from the actual pull result if conflicts are discovered after the preview.

## Manual synchronization

```powershell
agent-sync sync   # bidirectional
agent-sync pull   # download/apply only; never publish local history
agent-sync push   # publish only; never replace local history
```

All three commands call the same `SyncEngine` used by automation. Each command disposes its temporary engine after the operation, serializing with any active work and zeroing the engine-owned repository-key copy. Output contains only revisions, object counts, and byte sizes.

After the counter line, each run prints what the scan actually holds — a `local=` total with its size, then one line per agent that owns sessions on this machine:

```
revision=8a50fd1b uploaded=12 downloaded=0 deleted=0 conflicts=0 skipped-oversized=0
local=1046 size=1.9 GiB
  codex=1002 size=1.6 GiB (active=1001 archived=1 attachments=0)
  grok=39 size=366 MiB
  claude=5 size=5.0 MiB
```

The numbers come from the same scan the plan was built on, so they describe exactly what the run compared. An agent with no local sessions is omitted entirely: a machine without Grok must not read as one whose Grok sessions all vanished. Sizes are plaintext bytes on disk, not the encrypted payload the remote stores.

### Subagent threads are not synchronized

A Codex session whose `session_meta` payload carries `thread_source: "subagent"` or a `source.subagent` object is a subagent thread. The session manager has always hidden these; the scanner now also keeps them out of synchronization, and reports how many were held back as `excluded=N` on the `local=` line. On a machine that runs subagents heavily they can be the large majority of both session count and disk use.

An excluded session is **not** a deleted one. It stays on disk, keeps its baseline version, and never produces a tombstone — publishing one would erase that transcript on every other device that pulls. The practical consequence is that subagent sessions already published by an earlier version stay on the remote: they are no longer refreshed, and nothing removes them. Reclaiming that space means deliberately deleting those objects, which propagates as a deletion to every device; treat it as a separate, explicit operation rather than a side effect of upgrading. A successful operation records its last successful remote revision. Conflicts are preserved as encrypted evidence and return exit code 4; they are never resolved by overwriting live history implicitly.

If Codex or Grok locks a session after the stable scan but before encryption, Windows sharing/lock violations defer only that session. Other eligible objects are still published, and the locked session remains pending for a later run. Other I/O failures remain fatal.

Interactive `sync`, `push`, and `pull` print elapsed phase updates for lock acquisition, bounded-parallel local scanning, remote metadata fetch, index authentication, planning, staging, publication, local application, and state persistence. Codex and Grok scans run concurrently, with at most eight session bodies processed concurrently inside each scanner. For an unchanged repository, the engine authenticates the encrypted index and verifies that its opaque IDs exactly match the materialized object filenames; ciphertext bytes are loaded, hashed, decrypted, and plaintext-hash checked lazily only for objects required by download or conflict actions.

Each successful publish rewrites `main` to a **single orphan commit** (force-with-lease against the CAS baseline). Previous commits become unreachable so GitHub can reclaim old encrypted blobs; the repository is a snapshot store, not an append-only audit log. `main` therefore has exactly one commit and no parent: there is no history to prune, and no oldest commit to delete.

### Mirror maintenance

The remote reclaims its orphaned blobs on its own. The local mirror under
`%LOCALAPPDATA%\CodexHistorySync\repositories\<id>\git` does not: every publish orphans the previous snapshot, but the mirror's reflog keeps those objects reachable and nothing drops them. Measured on one machine, that was 2.9 GB of pack files behind a 194 MB snapshot, with 4386 objects unreachable once reflogs were discounted.

Every third successful publish therefore expires the mirror's reflog entries and prunes what they held. The counter lives in `maintenance.json` beside `state.json`. Maintenance is opportunistic: a failure is swallowed, the counter is only reset once the collection succeeds, and a publish that already landed is never failed by it.

It touches this machine's mirror only. The remote keeps exactly the snapshot it already had, no object is removed from the index, and no other device observes anything — the pruned objects are ones the current snapshot no longer references.

## Session viewer

Read what is inside a session without opening its native file:

```powershell
agent-sync --sessions
```

One list holds every session from every installed agent, newest first, with an `AGENT` column; the selected conversation is rendered beside it. Like the manager, this screen never contacts the network, opens the sync repository, or writes sync state.

| Key | Action |
|---|---|
| Up / Down | Move the selection, or scroll the text once it has focus |
| Left / Right | Move focus between the list and the text |
| PgUp / PgDn / Home / End | Scroll the text |
| `/` | Search inside the open session; `N` steps to the next match and wraps |
| `E` | Export the open session to `%USERPROFILE%\Documents\agent-sync\<agent>-<id>.md` |
| `Del` | Delete the session locally, behind the same confirmation the manager uses |
| `R` | Rescan |
| `Q` / `Esc` | Exit |

The text is the conversation as `agent-sync` understands it for cross-agent copying: user and assistant turns only. Reasoning blocks, tool calls, and tool results are **not** shown — they are absent from the portable model, not hidden. Neither are technical wrappers such as `<system-reminder>`, for the same reason.

Sessions marked `!` cannot be opened. That is the same readability verdict that blocks copying them, so the pane explains itself rather than showing an empty conversation.

Copying between agents stays in `--manage`: it needs the destination prompt that screen already has.

## Local cross-agent session manager

Start the manager with:

```powershell
agent-sync --manage
```

It is a local-only tool: it does not construct the Git/GitHub sync runtime, contact the network, open the configured repository, or write sync state, conflict evidence, tombstones, or background-task configuration. It reads only the native Codex and Grok session homes and performs an explicit local copy or local delete when requested.

The screen has separate **Codex** and **Grok** panels. Each panel shows **Title** and **Last modified** columns; `*` marks a session that is actually open and `!` marks one that cannot safely be read. A leftover agent process does not mark the whole column. Grok uses `active_sessions.json` plus a live `grok` PID; Codex uses a held `thread-writer-locks/*.lock`. Copy and delete remain blocked only for those starred rows. Use `Up`/`Down` to select, `Left`/`Right` to change panels, `C` to copy, `Delete` to request deletion, `R` to refresh, `Q` to exit, and `Esc` to exit when no search filter is active.

Press `/` to search both panels by session title without reading conversation content. Filtering updates as you type and ignores case. `Backspace` edits the query, `Enter` keeps the current filter and returns to list navigation, and `Esc` clears the filter. An active query is shown below the panels; a panel with no matches displays `No matching sessions`.

Each refresh scans the Codex and Grok homes concurrently under one bounded-read limit. It reads only the bounded metadata needed to build the catalog; full native conversation parsing is deferred until the user selects a copy or delete action.

Native Codex sessions explicitly marked as subagents (including spawned workers, reviewers, nested workers, and guardians) are internal implementation records and are excluded from the manager. Their files remain untouched. Grok sessions are not classified heuristically from names, paths, or message text.

`C` converts the selected readable, inactive session into the other agent's native format. The copied conversation keeps its title, created timestamp, and ordered user/assistant turns. Copy skips IDE wrapper turns (environment context, files-mentioned headers, and similar) and keeps the catalog title when the source title would be a wrapper. The destination last-modified time is the copy time so the new row sorts as the newest session. System, reasoning, and tool content are omitted. The destination is always a newly generated native session identifier: the source remains unchanged and an existing destination session is never overwritten. Before a Codex destination is published, a configured or discovered Codex executable is checked with the disposable-profile compatibility probe; an automatically unavailable executable is the only normal case in which that probe is skipped.

A Grok destination is a native package the Grok CLI can resume: a UUID v7 session id, `chat_history.jsonl` with Grok `type:text` user content, `summary.json` including `current_model_id` and `title_is_manual`, plus `updates.jsonl`, `rewind_points.jsonl`, `signals.json`, and `plan.json`. Cloud sync still stores only the compact `chat_history` + `summary` package.

Copy and delete refuse an active, unreadable, malformed, changed, or out-of-root session. The manager rechecks activity and the exact source immediately before the final action. `Delete` first asks for confirmation and removes only the selected local native session: the selected Codex JSONL file or the selected Grok native session directory. It does not publish a deletion or write a tombstone. **Local deletion may be restored by sync** on a later pull or bidirectional synchronization.

A successful copy shows `Copied.`. Copy failures use a fixed safe reason when one is known (`Active sessions cannot be copied.`, `This session cannot be copied.`, `The session changed. Copy was cancelled.`, `Grok is not available.`, `Codex is not available.`, or `Codex rejected the copied session.`); otherwise they show `Copy failed for session …`. Refresh and delete failures stay on `Session refresh failed.` and `Delete failed for session …`. None of these messages display native paths, conversation content, or raw exception details.

Before hashing and upload, each session JSONL is reduced deterministically to a compact rediscovery view:

- drop bulk runtime records (`compacted`, `turn_context`, `world_state`, inter-agent metadata, `event_msg`);
- drop heavy tool I/O payload types (`function_call_output`, `custom_tool_call_output`, `patch_apply_*`, `reasoning`, …);
- replace embedded photos with `[image omitted]`;
- truncate remaining long strings to 2 KiB with a length marker.

`session_meta` and user/assistant message text are kept. Local Codex files on disk are not modified; only the synchronized view is smaller.

## Grok CLI sessions

When `%USERPROFILE%\.grok\sessions` exists, `agent-sync` also inventories Grok CLI sessions. Each session is stored as one encrypted package under logical id `g-<uuid>` containing:

- normalized `chat_history.jsonl` (system/tool lines dropped; long content truncated);
- `summary.json` when present.

Terminal logs, locks, sqlite, and recap caches under the session folder are **not** synchronized. On import, packages are written back to  
`%USERPROFILE%\.grok\sessions\<url-encoded-cwd>\<uuid>\`.

GitHub still rejects individual blobs larger than 100 MiB. After reduction, payloads larger than ~95 MiB are skipped on upload (`skipped-oversized=N`) and remain local-only.

## Claude Code sessions

When `%USERPROFILE%\.claude\projects` exists, `agent-sync` also inventories Claude Code sessions. Each session is one encrypted package under logical id `cl-<uuid>` containing the transcript JSONL together with the session's own `cwd` and the literal project directory name.

Set `CLAUDE_CONFIG_DIR` to point at a different Claude home, the same way `GROK_HOME` and `CODEX_HOME` work. Nothing outside `projects/` is read or synchronized — not `backups/`, `ide/`, `shell-snapshots/`, `session-env/`, settings, or credentials.

On import, packages are written back to `%USERPROFILE%\.claude\projects\<project>\<uuid>.jsonl` using the **stored** project directory name. That name is never reconstructed from the `cwd`: Claude collapses both `:` and `\` to `-`, so `c:\Repos\Reborn` and `c-\Repos\Reborn` would produce the same directory and a reconstruction could write into the wrong project.

Claude publishes no active-session file. A session is treated as live — and deferred to a later run rather than failing the run — when a `claude` process is running **and** its transcript was written within the last 30 seconds. Expect your own open session to stay unsynchronized until it goes quiet or the process exits.

### Upgrade every machine before the first Claude push

`ObjectKind` is stored in the encrypted index as an integer, and an older `agent-sync` rejects the **entire** index when it meets a value it does not know:

```
Repository index contains an invalid object kind.
```

That breaks `pull` on the old machine completely, not just for Claude objects. **Upgrade every machine that shares the repository before the first push that carries a Claude session.** Checking is cheap: `agent-sync status` prints `claude-sessions=` on an upgraded build and does not on an older one.

## Status and diagnostics

```powershell
agent-sync status
agent-sync doctor
agent-sync doctor --compatibility-session <inactive archived JSONL> --codex-exe <codex executable>
```

`status` also prints a second line for Claude: the resolved projects root (or `none`), how many Claude sessions the scan saw, and whether that count is uncertain because something could not be read. `doctor` reports `claude-paths`, which fails only when no Claude home was found — that is the first thing to check when the manager shows no Claude panel.

`status` performs the same authenticated, non-mutating three-way planning used by synchronization. It reports local, remote, pending, and conflict counts plus both the current authenticated remote revision and the last successfully synchronized revision. Its conflict count is the exact identity-deduplicated union of persisted evidence and conflicts in the current plan; an unreadable evidence store makes status fail closed. Equal counts do not hide divergent objects.

`doctor` reports PASS or FAIL for these checks without printing paths, child-process output, URLs, credentials, keys, prompts, or exception text:

- Codex paths and version
- Git version
- GitHub private visibility
- DPAPI key access
- repository schema/index authentication
- Codex process detection
- free disk space
- background-agent installation

Before setup, the repository/key checks are expected to fail. Before agent installation, the agent check is also expected to fail.

The optional `doctor --compatibility-session` form is the documented Codex JSONL reindex gate. It requires both `--compatibility-session` and `--codex-exe`, runs only the disposable-profile compatibility probe, never prints the session path or executable path, and returns exit code `0` on PASS or `3` on FAIL. See `docs/compatibility.md` for recorded results.

Set `CODEX_EXE` to force a specific Codex binary for process detection and the join-time compatibility probe. When unset, the CLI resolves the first-party Windows IDE extension install (`openai.chatgpt-*-win32-x64\bin\windows-x86_64\codex.exe`) under:

- `%USERPROFILE%\.vscode\extensions` (VS Code)
- `%USERPROFILE%\.vscode-oss\extensions` (**VSCodium**)
- `%USERPROFILE%\.vscode-insiders\extensions`
- `%USERPROFILE%\.cursor\extensions` / `.cursor-insiders`
- `%USERPROFILE%\.windsurf\extensions`

then `codex.exe` on `PATH`. VSCodium does not use the Microsoft Marketplace by default: install Codex via Open VSX if available, or **Extensions: Install from VSIX…** (official `openai.chatgpt` VSIX).

If Codex is not installed, `join` prints a warning and continues so Grok sessions and on-disk Codex JSONL can still be imported. When Codex *is* present but the disposable reindex probe fails, `join` hard-fails with `Gate failed: codex-compatibility` and a `diagnostic:` line. Install the OpenAI Codex IDE extension (VS Code / Cursor / Windsurf) or put `codex.exe` on `PATH` / set `CODEX_EXE` so reindex can be verified. Use `doctor --compatibility-session <jsonl> --codex-exe <path>` to re-test the probe alone.

## Background agent

Install or remove the current executable's per-user logon task:

```powershell
agent-sync agent install
agent-sync agent uninstall
```

The task is named `AgentHistorySync`, runs only for the current Windows user at logon, and has one exact action: the canonical absolute path of the installing executable with arguments `agent run`. Installation and removal query Task Scheduler XML through the Task Scheduler API rather than localized command output. They refuse to replace or remove a same-name task whose executable, arguments, user, or logon trigger does not match.

At logon the agent performs a bidirectional sync if Codex is stopped. While Codex is active it runs push-only synchronization and an authenticated read-only preview; it never requests an import or conflict resolution. When every relevant Codex process exits, the agent continues checking process state through a two-second quiet window before bidirectional synchronization. A process restart resets that window. Failures retry after 30 seconds with exponential backoff capped at 30 minutes, and Ctrl+C is a normal shutdown when `agent run` is launched interactively.

Set `CODEX_EXE` to the absolute Codex executable path when a specific installation must be matched. With a configured path, only that executable and known first-party names under trusted roots (including the VS Code extension layout) count as Codex; a same custom name alone at an arbitrary path is ignored. Known names `codex`, `Codex`, and `ChatGPT` at an unrecognized readable path fail closed as active so imports are postponed rather than racing an unknown Codex channel. Access-denied process/path inspection is also treated as active.

Notifications report only pending-restart counts, unresolved-conflict counts, repeated-failure counts, and recovery. Structured JSON-line logs live under `%LOCALAPPDATA%\CodexHistorySync\logs`, rotate at 10 MiB, and retain at most five files. They contain operation IDs, modes, object counts, elapsed milliseconds, sanitized revision tokens, and fixed error codes. Exception messages, paths, URLs, credentials, keys, and history plaintext are not accepted by the logging surface.

## Conflicts

List unresolved conflicts:

```powershell
agent-sync conflicts
```

The listing contains only conflict IDs, hashes, device IDs, and UTC timestamps. It never displays decrypted history.

Choose exactly one resolution action:

```powershell
agent-sync resolve CONFLICT_ID --keep-local
agent-sync resolve CONFLICT_ID --keep-remote
agent-sync resolve CONFLICT_ID --export-both C:\existing-parent\new-export-directory
```

`--export-both` decrypts both retained envelopes into a newly created directory. The destination must be absolute, must not already exist, must have an existing parent, must be outside Codex history and conflict storage, and must pass the existing reparse-point/path-boundary checks. Export does not resolve the conflict, retains its encrypted evidence, and returns exit code 4 while any conflict remains.

The keep actions authenticate the stored evidence and current remote snapshot while holding the repository lock. Each side retains its own authenticated object kind, so resolving a same-ID active/archived conflict moves the chosen version to the correct history area. The chosen side is published with compare-and-swap before any local mutation, then applied through the guarded history writer and recorded as the new baseline.

After remote, local, and state work all succeed, live evidence is retired by an atomic same-parent rename. A rename failure leaves it live and makes the resolution fail for an idempotent retry. Cleanup after a successful rename is best effort: retired artifacts are excluded from conflict listings and are safely retried on a later listing or process restart. No plaintext is printed.

## Exit codes

| Code | Meaning |
|---:|---|
| 0 | Command completed successfully |
| 1 | Operational failure, such as unavailable Git/GitHub or local I/O failure |
| 2 | Invalid command or option usage |
| 3 | Security or compatibility gate failure |
| 4 | One or more conflicts remain unresolved |

Diagnostics deliberately use stable summaries. Raw exception messages and external command output are not copied to CLI output because they may contain credential-bearing URLs, prompt plaintext, or other sensitive material.

## Backup recovery drill

Backups live outside Codex history under `%LOCALAPPDATA%\CodexHistorySync\repositories\REPOSITORY_ID\backups`. Each record contains `content.bin` and `manifest.json`; the manifest records the absolute original path and SHA-256 content hash. Normal synchronization verifies backup bytes before every restore. There is no backup-restore CLI command in the MVP, so manual disaster recovery is deliberately offline and explicit.

Close Codex, remove only this executable's background task, and select the intended backup record:

```powershell
agent-sync agent uninstall
$record = 'C:\Users\USER\AppData\Local\CodexHistorySync\repositories\REPOSITORY_ID\backups\BACKUP_ID'
$manifest = Get-Content (Join-Path $record 'manifest.json') -Raw | ConvertFrom-Json
$content = Join-Path $record 'content.bin'
$actual = (Get-FileHash $content -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $manifest.contentHash.hex.ToLowerInvariant()) { throw 'Backup hash verification failed.' }
```

Before replacing anything, copy the current target to a separate recovery directory. Restore through a sibling temporary file on the same volume, then replace the destination:

```powershell
$target = [IO.Path]::GetFullPath($manifest.originalPath)
$safety = "$target.before-manual-restore"
Copy-Item -LiteralPath $target -Destination $safety
$temporary = "$target.restore-$([guid]::NewGuid().ToString('N')).tmp"
Copy-Item -LiteralPath $content -Destination $temporary
if ((Get-FileHash $temporary -Algorithm SHA256).Hash.ToLowerInvariant() -ne $actual) { throw 'Staged restore verification failed.' }
[IO.File]::Replace($temporary, $target, $null)
```

Start Codex and confirm the restored chat is visible, close Codex, run `agent-sync doctor` and `agent-sync sync`, then reinstall the agent with `agent-sync agent install`. Keep the safety copy until the next successful two-device convergence check. If the target does not exist, use `Move-Item -LiteralPath $temporary -Destination $target` instead of `File.Replace`.

For conflict recovery, export both retained versions before choosing a side:

```powershell
agent-sync conflicts
agent-sync resolve CONFLICT_ID --export-both C:\Recovery\new-export-directory
Get-FileHash C:\Recovery\new-export-directory\*.jsonl -Algorithm SHA256
agent-sync resolve CONFLICT_ID --keep-local   # or --keep-remote
agent-sync sync
```

`--export-both` leaves the conflict unresolved. Same-chat concurrent editing is unsupported: the utility preserves both whole-file versions and never attempts a line or JSON merge.
