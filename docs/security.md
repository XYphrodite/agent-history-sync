# Security Model

Codex History Sync encrypts every repository index and history object independently with AES-256-GCM. The public setup manifest contains the repository identifier, Argon2id parameters, salt, and a key-derived authenticator. It does not contain the passphrase or repository key. Every encrypted file begins with the `CHS1` envelope header; authenticated metadata binds its schema, logical object identifier, and object kind.

## Threat model and visible metadata

The design protects history content from a Git host, a reader of the private repository, and accidental inclusion of unrelated Codex state. GitHub can still observe the repository owner, collaborators, commit authors, commit timing and count, ciphertext paths, ciphertext sizes, deletion history, and repository activity. A compromised Windows account, a process that can read Codex history while it is open, a malicious Codex build, or an attacker controlling the unlocked desktop is outside this boundary.

Use a dedicated empty **private** GitHub repository. `init` and `join` stop before reading or retaining a passphrase when `gh` cannot prove `visibility=PRIVATE`; inability to verify is a failure, not permission to continue. Git credentials remain with Git Credential Manager. Credential-bearing remote URLs are stripped before persistence and never belong in commands, configuration, logs, or support output.

## Keys and recovery limits

The passphrase is read only from a hidden interactive prompt. Argon2id derives the repository key; the passphrase is not stored. The derived key is cached with Windows DPAPI for the current Windows user. Another Windows user, another profile, a reinstalled OS, or a copied DPAPI blob cannot decrypt it. DPAPI is convenience storage, not passphrase recovery. If every enrolled device loses both its DPAPI cache and the passphrase, encrypted Git history cannot be recovered.

Keep the passphrase in a password manager and test a disposable second-device join. Do not place it in PowerShell history, command arguments, environment variables, tickets, or logs.

There is no in-place key-rotation command in the MVP. Rotate by stopping and uninstalling the agent, retaining verified local backups, creating a new empty private repository, running `init` against it from the authoritative device, pushing the current local history, joining every other device with the new passphrase, and completing the two-device convergence/conflict drill. Delete access to the old repository only after the new repository and recovery copies have been verified.

## Synchronized and excluded data

Only complete, stable active and archived JSONL session files are synchronized. The scanner rejects credentials, `auth.json`, SQLite files, logs, caches, sandbox state, `.sandbox-secrets`, machine identifiers, partial files, temporary files, and unsafe paths. Backups, conflicts, keys, device state, provider clones, logs, and staging directories live outside `CODEX_HOME` and are excluded from recursive ingestion.

Attachment discovery is intentionally disabled in the MVP. The security audit places a unique canary in the synthetic attachment directory and proves it does not reach any Git blob. Attachments may be added only after Codex exposes a documented typed reference that can be validated without guessing paths or scanning arbitrary files.

Remote deletions are authenticated tombstones. Local backups created before replacement or deletion are retained for 30 days by default. Tombstones remain in repository history; rewriting Git history is outside the MVP.

## Failure boundary

- A wrong key, wrong passphrase, invalid manifest, unknown schema, or tampered index/object stops before local-history mutation or baseline advancement.
- Offline reads and failed pushes leave local history and the last successful baseline intact. Compare-and-swap push races fetch and replan, up to five attempts.
- Imports stage and validate a sibling file, create a verified backup, recheck Codex process state, and publish atomically. Interruption, an injected disk-full write, or Codex restarting at the mutation boundary leaves the original live file intact.
- Concurrent edits to the same chat are not merged. Both authenticated versions are preserved as encrypted conflict evidence and require `--keep-local` or `--keep-remote`; `--export-both` is a non-resolving recovery aid.
- Cleanup is best effort after an authoritative remote initialization. A cleanup error does not replace the primary result or encourage reinitializing an already-published repository.

The release audit uses only synthetic disposable homes. It scans every blob reachable from every ref and historical commit, validates the allowed public manifest separately, authenticates every historical encrypted index and object with the test key, audits the dedicated working clone and typed agent logs, and checks forbidden filenames, marker content, local paths, and credential-bearing URLs. The clone's explicitly allowed structural `.git` data is limited to Git control files, configuration, index, refs and reflogs, standard hook samples and `info` files, plus loose, packed, and temporary object-store artifacts. Their paths and bytes are audited too; `.git` is not excluded as a whole. No real Codex history is used.

## Reporting a suspected exposure

Stop the agent with `codex-sync agent uninstall`, close Codex, and revoke repository access. Preserve the encrypted repository and local recovery directories without posting them publicly. Treat a lost passphrase differently from an exposed passphrase: a lost passphrase cannot be recovered; an exposed passphrase requires migration to a newly keyed private repository.
