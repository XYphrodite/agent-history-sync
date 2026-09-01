# Security Model

Agent History Sync encrypts every repository index and history object independently with AES-256-GCM. The public setup manifest contains the repository identifier, Argon2id parameters, salt, and a key-derived authenticator. It does not contain the passphrase or repository key. Every encrypted file begins with the `CHS1` envelope header; authenticated metadata binds its schema, logical object identifier, and object kind.

The public rename does not change existing storage: `%LOCALAPPDATA%\CodexHistorySync`, the `CHS1` envelope, manifest names, and authenticated formats are deliberately retained for compatibility.

## Threat model and visible metadata

The design protects history content from a Git host, a reader of the private repository, and accidental inclusion of unrelated Codex state. GitHub can still observe the repository owner, collaborators, commit authors, commit timing and count, ciphertext paths, ciphertext sizes, deletion history, and repository activity. A compromised Windows account, a process that can read Codex history while it is open, a malicious Codex build, or an attacker controlling the unlocked desktop is outside this boundary.

Use a dedicated empty **private** GitHub repository. `init` and `join` stop before reading or retaining a passphrase when `gh` cannot prove `visibility=PRIVATE`; inability to verify is a failure, not permission to continue. Git credentials remain with Git Credential Manager. Credential-bearing remote URLs are stripped before persistence and never belong in commands, configuration, logs, or support output.

## Keys and recovery limits

The passphrase is read only from a hidden interactive prompt. Argon2id derives the repository key; the passphrase is not stored. The derived key is cached with Windows DPAPI for the current Windows user. Another Windows user, another profile, a reinstalled OS, or a copied DPAPI blob cannot decrypt it. DPAPI is convenience storage, not passphrase recovery. If every enrolled device loses both its DPAPI cache and the passphrase, encrypted Git history cannot be recovered.

Keep the passphrase in a password manager and test a disposable second-device join. Do not place it in PowerShell history, command arguments, environment variables, tickets, or logs.

There is no in-place key-rotation command in the MVP. Rotate by stopping and uninstalling the agent, retaining verified local backups, creating a new empty private repository, running `init` against it from the authoritative device, pushing the current local history, joining every other device with the new passphrase, and completing the two-device convergence/conflict drill. Delete access to the old repository only after the new repository and recovery copies have been verified.

## Synchronized and excluded data

Only complete, stable active and archived JSONL session files are synchronized. The scanner rejects credentials, `auth.json`, SQLite files, logs, caches, sandbox state, `.sandbox-secrets`, machine identifiers, partial files, temporary files, and unsafe paths. Backups, conflicts, keys, device state, provider clones, logs, and staging directories live outside `CODEX_HOME` and are excluded from recursive ingestion.

For Claude Code, only `projects/**/*.jsonl` leaves the machine. `backups/`, `ide/`, `session-env/`, `shell-snapshots/`, settings files, and any credential material under `%USERPROFILE%\.claude` are never read by the scanner and never reach a Git blob. A transcript is synchronized whole, so tool output and file contents that Claude recorded in the conversation are inside the encrypted package — the same exposure the conversation itself already carries.

Attachment discovery is intentionally disabled in the MVP. The security audit places a unique canary in the synthetic attachment directory and proves it does not reach any Git blob. Attachments may be added only after Codex exposes a documented typed reference that can be validated without guessing paths or scanning arbitrary files.

Remote Git history is intentionally non-append-only: each publish force-updates `main` to one orphan commit under compare-and-swap (`force-with-lease`). GitHub may retain unreachable objects until its garbage collection runs; operators who need immediate reclaim can re-create the private repository.

Remote deletions are authenticated tombstones. Local backups created before replacement or deletion are retained for 30 days by default. Tombstones remain in repository history; rewriting Git history is outside the MVP.

## Failure boundary

- A wrong key, wrong passphrase, invalid manifest, unknown schema, or tampered index/object stops before local-history mutation or baseline advancement.
- Offline reads and failed pushes leave local history and the last successful baseline intact. Compare-and-swap push races fetch and replan, up to five attempts.
- Imports stage and validate a sibling file, create a verified backup, recheck Codex process state, and publish atomically. Interruption, an injected disk-full write, or Codex restarting at the mutation boundary leaves the original live file intact.
- Concurrent edits to the same chat are not merged. Both authenticated versions are preserved as encrypted conflict evidence and require `--keep-local` or `--keep-remote`; `--export-both` is a non-resolving recovery aid.
- Cleanup is best effort after an authoritative remote initialization. A cleanup error does not replace the primary result or encourage reinitializing an already-published repository.

## Local session-manager boundary

`agent-sync --manage` is deliberately local-only: it does not initialize the sync runtime, make network requests, or create repository, sync-state, conflict, or tombstone records. A copy reads only a selected native session, reduces it to the portable title/timestamps/user-and-assistant-turn representation, and publishes a new native session for the other agent. System, reasoning, and tool content are not copied.

The `[U]` unreadable marker reflects bounded catalog readability only; a row without that marker is not a claim that its full conversation is valid. Every copy and delete performs a fresh full native parse, activity check, identity and path validation, fingerprint validation, and immediate pre-action recheck before any destination is published or source is removed.

The manager refuses active, unreadable, malformed, reparse-point, out-of-root, or changed selections. For copy, it validates the staged destination, records its exact owned tree and hashes, revalidates that exact tree immediately before the non-overwriting atomic move, and never replaces an existing destination. For delete, it revalidates the selected source immediately before the action. Grok deletion is anchored to the captured identity of the native sessions root and traverses/deletes relative to retained handles, rejecting root replacement, reparse points, unexpected entries, or changed identities before it removes the selected tree.

The supported local security boundary is immediate pre-publish/final-action hash, exact-tree, and path revalidation followed by the atomic move or action. Same-user adversarial mutation after that check or after publication is outside scope: native Codex and Grok sessions remain writable by that same user. This boundary does not weaken the earlier validation or permit a replacement to be accepted before the final action.

The release audit uses only synthetic disposable homes. It scans every blob reachable from every ref and historical commit, validates the allowed public manifest separately, authenticates every historical encrypted index and object with the test key, audits the dedicated working clone and typed agent logs, and checks forbidden filenames, marker content, local paths, and credential-bearing URLs. The clone's explicitly allowed structural `.git` data is limited to Git control files, configuration, index, refs and reflogs, standard hook samples and `info` files, plus loose, packed, and temporary object-store artifacts. Their paths and bytes are audited too; `.git` is not excluded as a whole. No real Codex history is used.

## Session titling boundary

`agent-sync --sessions` can ask a local model to name a session. The paragraph above still holds for `--manage`: that screen makes no network request at all. In the viewer one request is made per press of `T`, and on no other key, no scan, no render, and no refresh.

What is sent is the digest of the one selected session: its user and assistant turns, technical wrappers dropped, each turn cut at 2,000 characters and the whole cut to 18,000 with the middle elided. Reasoning, tool calls, and tool results are not part of the portable model and are not sent. Nothing else leaves the machine - no other session, no path, no repository state, and no key material.

Where it may be sent is configured and has no default. With no endpoint configured the feature is inert. A configured endpoint is refused unless it is `http` or `https` on `localhost`, a loopback address, or a private address: `10/8`, `172.16/12`, `192.168/16`, `169.254/16`, `100.64/10` (where tailnet nodes live), IPv6 loopback, `fc00::/7`, or `fe80::/10`. A DNS name other than `localhost` is refused, because a name can be repointed after it is configured.

Titles and descriptions are stored one file per session under `%LOCALAPPDATA%\CodexHistorySync\annotations`. No agent home is written: an annotation never becomes an `ai-title` record, a Codex index entry, or any other native artifact.

They are synchronized. An annotation is `CHS1`-encrypted like every other object, so its text is no more visible in the repository than a transcript is, and it is the first synchronized object that is not session history: the import path that writes into agent homes refuses it, and the annotations directory refuses everything else. A destination is built from the annotation itself and the configured directory, never from a path an incoming object carries.

## Reporting a suspected exposure

Stop the agent with `agent-sync agent uninstall`, close Codex, and revoke repository access. Preserve the encrypted repository and local recovery directories without posting them publicly. Treat a lost passphrase differently from an exposed passphrase: a lost passphrase cannot be recovered; an exposed passphrase requires migration to a newly keyed private repository.
