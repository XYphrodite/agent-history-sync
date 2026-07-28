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
