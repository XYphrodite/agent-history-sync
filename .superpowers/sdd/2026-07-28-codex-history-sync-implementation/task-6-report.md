# Task 6 Report: Git Storage Provider

## Status

Implemented and committed as `8cc5063 feat: publish encrypted snapshots through Git`.

## Files

- `CodexHistorySync.sln`
- `src/CodexHistorySync.Core/Providers/IStorageProvider.cs`
- `src/CodexHistorySync.Git/CodexHistorySync.Git.csproj`
- `src/CodexHistorySync.Git/GitCommand.cs`
- `src/CodexHistorySync.Git/GitStorageProvider.cs`
- `src/CodexHistorySync.Git/GitHubVisibilityVerifier.cs`
- `tests/CodexHistorySync.Git.Tests/CodexHistorySync.Git.Tests.csproj`
- `tests/CodexHistorySync.Git.Tests/GitStorageProviderTests.cs`

## Red-Green Evidence

- RED: `dotnet test tests\CodexHistorySync.Git.Tests\CodexHistorySync.Git.Tests.csproj -v normal` failed as intended with CS0246 because `GitStorageProvider` did not yet exist.
- GREEN: `dotnet test tests\CodexHistorySync.Git.Tests\CodexHistorySync.Git.Tests.csproj --no-restore` passed 3/3: two-clone stale compare-and-swap, credential-redacted Git failure output, and non-private GitHub visibility rejection.
- Full: `dotnet test` passed 117/117 across Core (97), Git (3), Windows (6), and Integration (11) tests.

## Safety Review

- Child Git commands use `ProcessStartInfo.ArgumentList`, separate bounded stdout/stderr capture, timeout, cancellation process-tree termination, disabled Git prompts, and URL-credential redaction.
- Publication fetches then compares `origin/main`, resets/cleans only a marker-owned dedicated clone, never merges or rebases, and cleans a rejected-push local commit before returning a refreshed stale result.
- Repository IDs and object IDs are validated; clone, source, destination, read, write, reset, and recursive enumeration reject reparse points. The configurable storage root is rejected when it is inside a Git worktree.
- GitHub setup accepts only JSON visibility exactly equal to `PRIVATE`; absent `gh`, authentication/command failures, malformed JSON, and unknown visibility are actionable failures.

## Concerns

- `ObjectVersion` includes plaintext hash and deletion metadata that cannot be recovered by a keyless Git provider from the current encrypted-object path/header. The current snapshot maps validated opaque object paths with an empty hash and `ActiveSession`; Task 7 needs an authenticated encrypted index/manifest contract to supply authoritative version metadata.
- Under this Windows sandbox, Git-created bare-object files can be owned by a different execution token, so test teardown tolerates an `UnauthorizedAccessException` after leaving a uniquely named disposable temp fixture. Production paths are unaffected.

## Round 1 Review Correction

Implemented in `23124e5 fix: harden Git snapshot publication contract`.

### Contract Migration

- `RemoteSnapshot` is now keyless: it transports the fetched Git revision, exact `repository.chs` ciphertext, and exact opaque encrypted object ciphertext. It no longer exposes or fabricates `ObjectVersion`, kind, plaintext hash, or deletion state.
- `PublishRequest` now atomically carries an optional encrypted-index replacement/deletion plus encrypted object additions/deletions. Task 7 owns index authentication/decryption and construction of authoritative planner inputs.
- Reads fetch and explicitly materialize the same `origin/main` revision before reading ciphertext. Explicit remote-tracking ref deletion plus `fetch --prune` prevents a deleted remote branch from retaining stale state.

### Round 1 Red-Green Evidence

- RED contract/race batch: `dotnet test tests\CodexHistorySync.Git.Tests\CodexHistorySync.Git.Tests.csproj --no-restore` failed with CS0246 for the wished-for keyless contract and deterministic publication seam.
- RED extended redaction: the focused `GitCommand_RedactsUrlQueryAndScpCredentialsFromAllOutput` test exposed `client_secret` and API-key values before the query redactor was expanded.
- RED visibility classification: `GitHubRemote_CannotBypassVisibilityThroughLocalClassification` failed because a GitHub URL could initially be labeled local.
- GREEN focused: `dotnet test tests\CodexHistorySync.Git.Tests\CodexHistorySync.Git.Tests.csproj --no-restore` passed 10/10.
- GREEN full: `dotnet test` passed 124/124 across Core (97), Git (10), Windows (6), and Integration (11).

### Round 1 Safety Review

- Ownership identity moved under `.git/codex-history-sync/repository-id`, survives reset/clean, and is verified before every destructive materialization or cleanup, including rejected-push cleanup.
- A deterministic two-party pre-push gate proves both publishers can pass preflight and exactly one loses the true non-fast-forward race with `Published=false`, refreshed revision, and no merge/rebase.
- No-op requests perform a final remote CAS check; a deterministic intervening publication returns stale instead of success.
- GitHub remotes require a successful exact-`PRIVATE` verifier before clone. Local bypass is restricted to explicitly classified absolute filesystem/file remotes, so a GitHub URL cannot be misclassified as local.
- Redaction covers URL user-info, credential query fields, and token-bearing SCP-style remotes in both output streams; process-start exceptions do not retain an unredacted inner exception.
- Fixture teardown clears attributes, retries, and fails visibly if the disposable remote or clone leaks.

### Round 1 Remaining Concerns

- None within Task 6. Task 7 must authenticate/decrypt `IndexCiphertext` before trusting any object version metadata, as required by the migrated contract.
