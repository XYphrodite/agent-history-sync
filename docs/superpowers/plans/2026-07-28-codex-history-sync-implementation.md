# Codex History Sync Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a self-contained Windows 11 CLI and background agent that safely synchronizes encrypted Codex session history between PCs through a private GitHub repository.

**Architecture:** A .NET 10 core library owns Codex file discovery, authenticated encryption, three-way reconciliation, backups, and provider-neutral synchronization. A Git provider implements compare-and-swap publication through a dedicated clone, while one `codex-sync.exe` hosts manual commands and the logon agent. Codex SQLite state is never synchronized; a disposable-profile compatibility spike is a hard gate before the rest of the implementation.

**Tech Stack:** C# 14, .NET 10, xUnit, `Konscious.Security.Cryptography.Argon2`, Git 2.55+ invoked as a child process, Windows DPAPI, Windows Task Scheduler, JSON Lines, GitHub CLI only for private-visibility verification when available.

## Global Constraints

- Target Windows 11 x64 and publish `codex-sync.exe` as `win-x64`, self-contained, single-file.
- Source repository: `C:\Repos\codex-history-sync`; encrypted history uses a separate private GitHub repository.
- Synchronize active sessions, archived sessions, and referenced attachments only.
- Never synchronize `auth.json`, `*.sqlite*`, logs, caches, sandbox state, machine identities, or temporary files.
- Never mutate Codex history while a relevant Codex process is running.
- Never modify undocumented Codex SQLite schemas; failure of the JSONL reindex compatibility gate stops implementation.
- Use AES-256-GCM, Argon2id, a fresh nonce per changed object, authenticated metadata, opaque remote object names, and Windows DPAPI for the cached derived key.
- Preserve both sides of every divergent same-chat conflict; never select a winner implicitly.
- Use test-first development and one focused commit per task.
- All automated integration tests use disposable `CODEX_HOME` directories and local bare Git remotes.

---

## Planned File Structure

```text
CodexHistorySync.sln
Directory.Build.props
src/
  CodexHistorySync.Core/
    CodexHistorySync.Core.csproj
    Model/SyncModels.cs
    Codex/CodexPaths.cs
    Codex/SessionScanner.cs
    Codex/CodexCompatibilityProbe.cs
    Crypto/EncryptedEnvelope.cs
    Crypto/RepositoryCrypto.cs
    State/LocalStateStore.cs
    State/RepositoryManifest.cs
    Sync/ThreeWayPlanner.cs
    Sync/SyncEngine.cs
    Sync/BackupStore.cs
    Sync/ConflictStore.cs
    Providers/IStorageProvider.cs
  CodexHistorySync.Git/
    CodexHistorySync.Git.csproj
    GitCommand.cs
    GitStorageProvider.cs
    GitHubVisibilityVerifier.cs
  CodexHistorySync.Windows/
    CodexHistorySync.Windows.csproj
    DpapiKeyStore.cs
    CodexProcessDetector.cs
    AgentScheduler.cs
    WindowsNotifier.cs
  CodexHistorySync.Cli/
    CodexHistorySync.Cli.csproj
    Program.cs
    CliApplication.cs
    AgentWorker.cs
tests/
  CodexHistorySync.Core.Tests/
  CodexHistorySync.Git.Tests/
  CodexHistorySync.Windows.Tests/
  CodexHistorySync.IntegrationTests/
docs/
  compatibility.md
  operations.md
  security.md
```

---

### Task 1: Solution Scaffold and JSONL Compatibility Gate

**Files:**
- Create: `CodexHistorySync.sln`
- Create: `Directory.Build.props`
- Create: `src/CodexHistorySync.Core/CodexHistorySync.Core.csproj`
- Create: `src/CodexHistorySync.Core/Codex/CodexCompatibilityProbe.cs`
- Create: `src/CodexHistorySync.Cli/CodexHistorySync.Cli.csproj`
- Create: `src/CodexHistorySync.Cli/Program.cs`
- Create: `tests/CodexHistorySync.IntegrationTests/CodexHistorySync.IntegrationTests.csproj`
- Create: `tests/CodexHistorySync.IntegrationTests/CodexCompatibilityProbeTests.cs`
- Create: `docs/compatibility.md`

**Interfaces:**
- Produces: `CodexCompatibilityProbe.ProbeAsync(string codexExe, string sourceSession, CancellationToken) -> Task<CompatibilityResult>`
- Produces: `CompatibilityResult(bool IsCompatible, string CodexVersion, string Diagnostic)`
- Gate: no subsequent task begins until an opt-in probe returns `IsCompatible == true` for the installed Codex build.

- [ ] **Step 1: Scaffold the solution and projects**

Run:

```powershell
dotnet new sln -n CodexHistorySync
dotnet new classlib -n CodexHistorySync.Core -o src/CodexHistorySync.Core -f net10.0
dotnet new console -n CodexHistorySync.Cli -o src/CodexHistorySync.Cli -f net10.0
dotnet new xunit -n CodexHistorySync.IntegrationTests -o tests/CodexHistorySync.IntegrationTests -f net10.0
dotnet sln add src/CodexHistorySync.Core src/CodexHistorySync.Cli tests/CodexHistorySync.IntegrationTests
dotnet add src/CodexHistorySync.Cli reference src/CodexHistorySync.Core
dotnet add tests/CodexHistorySync.IntegrationTests reference src/CodexHistorySync.Core
```

Add to `Directory.Build.props`:

```xml
<Project>
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <LangVersion>14.0</LangVersion>
    <Deterministic>true</Deterministic>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2: Write the failing opt-in compatibility test**

Create `CodexCompatibilityProbeTests.cs`:

```csharp
public sealed class CodexCompatibilityProbeTests
{
    [Fact]
    public async Task ImportedJsonlIsListedWithoutCopyingSqlite()
    {
        var fixture = Environment.GetEnvironmentVariable("CODEX_COMPAT_SESSION");
        var codex = Environment.GetEnvironmentVariable("CODEX_EXE");
        if (string.IsNullOrWhiteSpace(fixture) || string.IsNullOrWhiteSpace(codex))
            return;

        var result = await new CodexCompatibilityProbe()
            .ProbeAsync(codex, fixture, CancellationToken.None);

        Assert.True(result.IsCompatible, result.Diagnostic);
        Assert.False(string.IsNullOrWhiteSpace(result.CodexVersion));
    }
}
```

- [ ] **Step 3: Run the test and verify the missing type failure**

Run:

```powershell
dotnet test tests/CodexHistorySync.IntegrationTests --filter ImportedJsonlIsListedWithoutCopyingSqlite
```

Expected: compilation fails because `CodexCompatibilityProbe` is undefined.

- [ ] **Step 4: Implement the disposable-profile probe**

Implement `CompatibilityResult` and `CodexCompatibilityProbe`. The probe must:

1. create a unique directory below `Path.GetTempPath()`;
2. create only `sessions\YYYY\MM\DD` and copy the supplied JSONL there;
3. assert that no `*.sqlite*` file exists in the disposable home;
4. start `codex app-server --listen stdio://` with `CODEX_HOME` set only for that child process;
5. send the app-server initialization handshake and `thread/list` with `useStateDbOnly: false`;
6. match the imported thread ID parsed from the JSONL `session_meta` record;
7. stop the child process and delete the disposable home in `finally`.

The public shape must be:

```csharp
public sealed record CompatibilityResult(
    bool IsCompatible,
    string CodexVersion,
    string Diagnostic);

public sealed class CodexCompatibilityProbe
{
    public Task<CompatibilityResult> ProbeAsync(
        string codexExe,
        string sourceSession,
        CancellationToken cancellationToken);
}
```

Do not copy `auth.json` or invoke a model turn.

- [ ] **Step 5: Expose `doctor --compatibility-session`**

Make `Program.cs` accept:

```text
codex-sync doctor --compatibility-session C:\path\to\rollout.jsonl --codex-exe C:\path\to\codex.exe
```

Return exit code `0` when compatible, `3` when incompatible, and `2` for invalid arguments. Print the Codex version and diagnostic without printing session content.

- [ ] **Step 6: Run the gate against a user-selected existing session**

Run in ordinary PowerShell with Codex closed:

```powershell
$env:CODEX_COMPAT_SESSION='C:\absolute\path\to\one\existing\rollout.jsonl'
$env:CODEX_EXE=(Get-Command codex).Source
dotnet test tests/CodexHistorySync.IntegrationTests --filter ImportedJsonlIsListedWithoutCopyingSqlite
```

Expected: PASS and the disposable directory contains no copied SQLite file. If it fails, record the result in `docs/compatibility.md`, stop this plan, and revise the design around a supported Codex import API.

- [ ] **Step 7: Document and commit the compatibility result**

Record the exact `codex --version`, command, result, and the fact that the source fixture was only read and never committed. Then run:

```powershell
dotnet test
git add CodexHistorySync.sln Directory.Build.props src tests docs/compatibility.md
git commit -m "test: prove Codex JSONL import compatibility"
```

Expected: all tests pass and no fixture or prompt content is staged.

---

### Task 2: Domain Model, Paths, and Safe Session Discovery

**Files:**
- Create: `src/CodexHistorySync.Core/Model/SyncModels.cs`
- Create: `src/CodexHistorySync.Core/Codex/CodexPaths.cs`
- Create: `src/CodexHistorySync.Core/Codex/SessionScanner.cs`
- Create: `tests/CodexHistorySync.Core.Tests/CodexHistorySync.Core.Tests.csproj`
- Create: `tests/CodexHistorySync.Core.Tests/Codex/SessionScannerTests.cs`

**Interfaces:**
- Produces: `LogicalObjectId`, `ObjectKind`, `LocalObject`, `ContentHash`
- Produces: `CodexPaths.Resolve(string? configuredHome) -> CodexPaths`
- Produces: `SessionScanner.ScanAsync(CodexPaths, CancellationToken) -> Task<IReadOnlyList<LocalObject>>`

- [ ] **Step 1: Create the unit-test project and failing discovery tests**

Create tests that build a temporary tree containing `sessions`, `archived_sessions`, `attachments`, `auth.json`, `state_5.sqlite`, `logs_2.sqlite-wal`, `.sandbox-secrets`, and `tmp`. Assert that only session JSONL and explicitly referenced attachments are returned.

Core assertions:

```csharp
Assert.Contains(found, x => x.Kind == ObjectKind.ActiveSession);
Assert.Contains(found, x => x.Kind == ObjectKind.ArchivedSession);
Assert.DoesNotContain(found, x => x.SourcePath.Contains("auth.json"));
Assert.DoesNotContain(found, x => x.SourcePath.Contains("sqlite"));
Assert.DoesNotContain(found, x => x.SourcePath.Contains("sandbox", StringComparison.OrdinalIgnoreCase));
```

- [ ] **Step 2: Run the tests and verify failure**

Run: `dotnet test tests/CodexHistorySync.Core.Tests --filter SessionScannerTests`

Expected: compilation fails because model and scanner types do not exist.

- [ ] **Step 3: Implement the focused model and path resolver**

Use these public types:

```csharp
public enum ObjectKind { ActiveSession, ArchivedSession, Attachment, Tombstone }
public readonly record struct LogicalObjectId(string Value);
public readonly record struct ContentHash(string Hex);
public sealed record LocalObject(
    LogicalObjectId Id,
    ObjectKind Kind,
    string SourcePath,
    ContentHash Hash,
    long Length,
    DateTimeOffset LastWriteTimeUtc);
```

`CodexPaths.Resolve` uses the explicit argument first, then `CODEX_HOME`, then `%USERPROFILE%\.codex`. It returns canonical absolute paths and rejects a missing home or a home rooted inside the sync repository.

- [ ] **Step 4: Implement stable JSONL scanning**

For each JSONL candidate, read with `FileShare.ReadWrite | FileShare.Delete`, verify two identical `(length, lastWriteUtc)` observations separated by an injectable delay, require a final newline, and parse every non-empty line with `JsonDocument.Parse`. Derive the logical ID from `session_meta.payload.id`; reject duplicate IDs and path traversal.

Attachment discovery follows only normalized paths present in supported session records and confirms each resolved path remains beneath `CodexPaths.Attachments`.

- [ ] **Step 5: Run scanner tests and commit**

Run:

```powershell
dotnet test tests/CodexHistorySync.Core.Tests --filter SessionScannerTests
dotnet test
git add src/CodexHistorySync.Core tests/CodexHistorySync.Core.Tests CodexHistorySync.sln
git commit -m "feat: discover safe Codex history objects"
```

Expected: all tests pass; exclusion tests prove credentials and SQLite are absent.

---

### Task 3: Authenticated Repository Encryption and DPAPI Key Cache

**Files:**
- Create: `src/CodexHistorySync.Core/Crypto/EncryptedEnvelope.cs`
- Create: `src/CodexHistorySync.Core/Crypto/RepositoryCrypto.cs`
- Modify: `src/CodexHistorySync.Core/CodexHistorySync.Core.csproj`
- Create: `src/CodexHistorySync.Windows/CodexHistorySync.Windows.csproj`
- Create: `src/CodexHistorySync.Windows/DpapiKeyStore.cs`
- Create: `tests/CodexHistorySync.Core.Tests/Crypto/RepositoryCryptoTests.cs`
- Create: `tests/CodexHistorySync.Windows.Tests/CodexHistorySync.Windows.Tests.csproj`
- Create: `tests/CodexHistorySync.Windows.Tests/DpapiKeyStoreTests.cs`

**Interfaces:**
- Produces: `RepositoryCrypto.DeriveMasterKeyAsync`, `EncryptAsync`, `DecryptAsync`
- Produces: `IKeyStore.SaveAsync`, `LoadAsync`, `DeleteAsync`
- Envelope format version: binary magic `CHS1`, format version `1`, Argon2id parameters stored in repository manifest.

- [ ] **Step 1: Add Argon2id and write failing crypto tests**

Run:

```powershell
dotnet add src/CodexHistorySync.Core package Konscious.Security.Cryptography.Argon2
dotnet new classlib -n CodexHistorySync.Windows -o src/CodexHistorySync.Windows -f net10.0
dotnet new xunit -n CodexHistorySync.Windows.Tests -o tests/CodexHistorySync.Windows.Tests -f net10.0
dotnet sln add src/CodexHistorySync.Windows tests/CodexHistorySync.Windows.Tests
dotnet add src/CodexHistorySync.Windows reference src/CodexHistorySync.Core
dotnet add tests/CodexHistorySync.Windows.Tests reference src/CodexHistorySync.Windows
```

Tests must prove round-trip correctness, fresh ciphertext for identical plaintext, rejection of wrong passwords, ciphertext mutation, associated-data mutation, truncated envelopes, and unknown format versions.

- [ ] **Step 2: Run tests and verify failure**

Run: `dotnet test --filter "RepositoryCryptoTests|DpapiKeyStoreTests"`

Expected: compilation fails because encryption and key-store types are missing.

- [ ] **Step 3: Implement the envelope and Argon2id derivation**

Use a 32-byte master key, 16-byte repository salt, 12-byte AES-GCM nonce, 16-byte tag, and 32-byte per-object key derived with HKDF-SHA256. Default Argon2id parameters are memory `65536 KiB`, iterations `3`, parallelism `2`; store them in the authenticated repository manifest so readers use the repository's exact values.

Public API:

```csharp
public sealed class RepositoryCrypto
{
    public Task<byte[]> DeriveMasterKeyAsync(
        ReadOnlyMemory<char> passphrase,
        Argon2Parameters parameters,
        CancellationToken cancellationToken);

    public Task EncryptAsync(
        Stream plaintext,
        Stream destination,
        ReadOnlyMemory<byte> masterKey,
        EnvelopeMetadata metadata,
        CancellationToken cancellationToken);

    public Task DecryptAsync(
        Stream ciphertext,
        Stream destination,
        ReadOnlyMemory<byte> masterKey,
        EnvelopeMetadata expectedMetadata,
        CancellationToken cancellationToken);
}
```

Supporting public records are:

```csharp
public sealed record Argon2Parameters(
    byte[] Salt,
    int MemoryKiB,
    int Iterations,
    int Parallelism);

public sealed record EnvelopeMetadata(
    int SchemaVersion,
    LogicalObjectId ObjectId,
    ObjectKind Kind);
```

Clear temporary key buffers with `CryptographicOperations.ZeroMemory` in `finally`.

- [ ] **Step 4: Implement the Windows DPAPI cache**

Define:

```csharp
public interface IKeyStore
{
    Task SaveAsync(string repositoryId, ReadOnlyMemory<byte> key, CancellationToken ct);
    Task<byte[]?> LoadAsync(string repositoryId, CancellationToken ct);
    Task DeleteAsync(string repositoryId, CancellationToken ct);
}
```

Store DPAPI `CurrentUser` ciphertext below `%LOCALAPPDATA%\CodexHistorySync\keys`. Sanitize `repositoryId`, use owner-only file ACLs, and never print the path contents. Tests run only on Windows and prove a saved key round-trips while raw file bytes do not contain the key.

- [ ] **Step 5: Run security-focused tests and commit**

Run:

```powershell
dotnet test --filter "RepositoryCryptoTests|DpapiKeyStoreTests"
dotnet test
git add src tests CodexHistorySync.sln
git commit -m "feat: encrypt history with DPAPI-protected keys"
```

Expected: all mutation and wrong-key tests pass.

---

### Task 4: Repository Manifest, Local State, and Three-Way Planner

**Files:**
- Create: `src/CodexHistorySync.Core/State/RepositoryManifest.cs`
- Create: `src/CodexHistorySync.Core/State/LocalStateStore.cs`
- Create: `src/CodexHistorySync.Core/Sync/ThreeWayPlanner.cs`
- Create: `tests/CodexHistorySync.Core.Tests/State/LocalStateStoreTests.cs`
- Create: `tests/CodexHistorySync.Core.Tests/Sync/ThreeWayPlannerTests.cs`

**Interfaces:**
- Produces: `RepositoryManifest`, `DeviceState`, `ObjectVersion`, `SyncAction`, `SyncPlan`
- Produces: `ThreeWayPlanner.CreatePlan(local, remote, baseline) -> SyncPlan`

- [ ] **Step 1: Write the failing reconciliation matrix**

Use `[Theory]` rows covering local-only change, remote-only change, same change, divergent change, local deletion, remote deletion, new object on each side, and deletion-versus-modification. Assert exact action kinds:

```csharp
Assert.Equal(expected, action.Kind);
Assert.Equal(objectId, action.ObjectId);
```

Deletion-versus-modification is always `Conflict`; it is never an automatic delete.

- [ ] **Step 2: Run tests and verify failure**

Run: `dotnet test tests/CodexHistorySync.Core.Tests --filter "ThreeWayPlannerTests|LocalStateStoreTests"`

Expected: compilation fails because planner and state types are absent.

- [ ] **Step 3: Implement immutable planning types**

```csharp
public enum SyncActionKind
{
    Upload, Download, Accept, ApplyTombstone, PublishTombstone, Conflict
}

public sealed record ObjectVersion(
    LogicalObjectId Id,
    ObjectKind Kind,
    ContentHash PlaintextHash,
    string Revision,
    bool IsDeleted);

public sealed record SyncAction(
    LogicalObjectId ObjectId,
    SyncActionKind Kind,
    ObjectVersion? Local,
    ObjectVersion? Remote,
    ObjectVersion? Baseline);

public sealed record SyncPlan(IReadOnlyList<SyncAction> Actions);
```

Keep `ThreeWayPlanner` pure: no filesystem, Git, clock, or encryption dependencies.

- [ ] **Step 4: Implement atomic local state storage**

Store device-local baseline state under `%LOCALAPPDATA%\CodexHistorySync\repositories\<repository-id>\state.json`. Write to a sibling temporary file, flush, and atomically replace. Reject unknown schema versions and duplicate object IDs.

- [ ] **Step 5: Run all planner/state tests and commit**

Run:

```powershell
dotnet test --filter "ThreeWayPlannerTests|LocalStateStoreTests"
dotnet test
git add src/CodexHistorySync.Core tests/CodexHistorySync.Core.Tests
git commit -m "feat: plan three-way history reconciliation"
```

Expected: the full matrix passes with no implicit conflict winner.

---

### Task 5: Atomic Imports, Backups, Tombstones, and Conflict Preservation

**Files:**
- Create: `src/CodexHistorySync.Core/Sync/BackupStore.cs`
- Create: `src/CodexHistorySync.Core/Sync/ConflictStore.cs`
- Create: `src/CodexHistorySync.Core/Codex/CodexHistoryWriter.cs`
- Create: `src/CodexHistorySync.Core/Codex/ICodexProcessDetector.cs`
- Create: `src/CodexHistorySync.Core/IO/IAtomicFileSystem.cs`
- Create: `tests/CodexHistorySync.Core.Tests/Sync/BackupStoreTests.cs`
- Create: `tests/CodexHistorySync.Core.Tests/Sync/ConflictStoreTests.cs`
- Create: `tests/CodexHistorySync.Core.Tests/Codex/CodexHistoryWriterTests.cs`

**Interfaces:**
- Produces: `CodexHistoryWriter.ImportAsync`, `ApplyTombstoneAsync`
- Produces: `BackupStore.CreateAsync`, `RestoreAsync`, `PruneAsync`
- Produces: `ConflictStore.PreserveAsync`, `ListAsync`, `ResolveAsync`
- Produces: `ICodexProcessDetector` and `IAtomicFileSystem`, implemented by platform/filesystem adapters.

- [ ] **Step 1: Write failure-injection tests first**

Tests use an injectable `IAtomicFileSystem` whose `ReplaceAsync` can throw. Prove that:

- interrupted import leaves original bytes intact;
- every replacement/deletion has a backup;
- backups older than the configured 30-day default are pruned while newer backups remain;
- both conflict versions and provenance survive;
- a tombstone cannot delete a modified-since-baseline file.

- [ ] **Step 2: Run tests and verify failure**

Run: `dotnet test --filter "BackupStoreTests|ConflictStoreTests|CodexHistoryWriterTests"`

Expected: compilation fails because the writer and stores are absent.

- [ ] **Step 3: Implement backups outside synchronized paths**

Use `%LOCALAPPDATA%\CodexHistorySync\repositories\<repository-id>\backups\<timestamp>`. A backup manifest records original canonical path, content hash, timestamp, and operation ID. Restoration verifies the backup hash before replacement.

Define the process/file boundaries before implementing the writer:

```csharp
public interface ICodexProcessDetector
{
    bool IsRunning();
    Task WaitForExitAsync(CancellationToken cancellationToken);
}

public interface IAtomicFileSystem
{
    Task WriteTemporaryAsync(string path, Stream content, CancellationToken ct);
    Task ReplaceAsync(string temporaryPath, string destinationPath, CancellationToken ct);
    Task DeleteAsync(string path, CancellationToken ct);
}
```

- [ ] **Step 4: Implement atomic history writes**

Write incoming plaintext to a temporary sibling, flush with `Flush(true)`, validate JSONL again, create the backup, and call atomic replace. For new files, atomically rename only after validation. Check `ICodexProcessDetector.IsRunning()` immediately before replacement and abort if Codex became active.

- [ ] **Step 5: Implement conflicts and explicit resolution**

Keep conflicts under the local application data directory and store both encrypted versions plus a metadata file containing object ID, local hash, remote hash, baseline hash, device IDs, and timestamps. `ExportBoth` writes two clearly suffixed plaintext files to an explicit destination and never rewrites a thread ID.

- [ ] **Step 6: Run tests and commit**

Run:

```powershell
dotnet test --filter "BackupStoreTests|ConflictStoreTests|CodexHistoryWriterTests"
dotnet test
git add src/CodexHistorySync.Core tests/CodexHistorySync.Core.Tests
git commit -m "feat: preserve history through atomic imports"
```

Expected: forced interruption and conflict tests pass without data loss.

---

### Task 6: Provider Contract and Git Compare-and-Swap Publication

**Files:**
- Create: `src/CodexHistorySync.Core/Providers/IStorageProvider.cs`
- Create: `src/CodexHistorySync.Git/CodexHistorySync.Git.csproj`
- Create: `src/CodexHistorySync.Git/GitCommand.cs`
- Create: `src/CodexHistorySync.Git/GitStorageProvider.cs`
- Create: `src/CodexHistorySync.Git/GitHubVisibilityVerifier.cs`
- Create: `tests/CodexHistorySync.Git.Tests/CodexHistorySync.Git.Tests.csproj`
- Create: `tests/CodexHistorySync.Git.Tests/GitStorageProviderTests.cs`

**Interfaces:**
- Produces: `IStorageProvider.ReadSnapshotAsync`, `TryPublishAsync`
- Produces: `RemoteSnapshot`, `PublishRequest`, `PublishResult`

- [ ] **Step 1: Define the provider contract and failing two-clone tests**

```csharp
public interface IStorageProvider
{
    Task<RemoteSnapshot> ReadSnapshotAsync(CancellationToken ct);
    Task<PublishResult> TryPublishAsync(PublishRequest request, CancellationToken ct);
}

public sealed record PublishResult(bool Published, string CurrentRevision);
public sealed record RemoteSnapshot(
    string Revision,
    IReadOnlyDictionary<LogicalObjectId, ObjectVersion> Objects);
public sealed record PublishRequest(
    string ExpectedRevision,
    IReadOnlyList<EncryptedObjectChange> Changes,
    string CommitMessage);
public sealed record EncryptedObjectChange(
    LogicalObjectId ObjectId,
    string CiphertextPath,
    bool Delete);
```

Create a local bare remote and two clones. Assert that the first provider publishes, the second provider using the stale revision returns `Published == false`, and no merge conflict or partial commit is created.

- [ ] **Step 2: Run tests and verify failure**

Run: `dotnet test tests/CodexHistorySync.Git.Tests`

Expected: compilation fails because the provider is absent.

- [ ] **Step 3: Implement safe Git command execution**

Use `ProcessStartInfo.ArgumentList`, never a shell command string. Capture stdout/stderr separately, redact credentials embedded in URLs, enforce a timeout, and return an immutable result containing exit code and output. Set `GIT_TERMINAL_PROMPT=0` for background operations; manual setup may use Git Credential Manager before agent installation.

- [ ] **Step 4: Implement the dedicated-clone provider**

The provider clone lives under `%LOCALAPPDATA%\CodexHistorySync\repositories\<repository-id>\git`. Before publication it fetches `origin main`, verifies the expected revision equals `origin/main`, resets only this dedicated clone, applies encrypted object changes, commits, and pushes `HEAD:main`. A non-fast-forward push returns `Published == false` and the new remote revision after fetch.

Object paths are `objects/<first-two-hmac-chars>/<remaining-hmac>.chs`; shared repository metadata is `repository.chs`. Do not use Git merge or rebase.

- [ ] **Step 5: Verify private GitHub visibility**

`GitHubVisibilityVerifier` first accepts a successful `gh repo view <owner/repo> --json visibility` result only when `visibility` equals `PRIVATE`. If `gh` is absent or visibility cannot be verified, setup returns an actionable failure and does not clone. Existing Git credentials remain managed by Git/GCM.

- [ ] **Step 6: Run Git tests and commit**

Run:

```powershell
dotnet test tests/CodexHistorySync.Git.Tests
dotnet test
git add src/CodexHistorySync.Core src/CodexHistorySync.Git tests/CodexHistorySync.Git.Tests CodexHistorySync.sln
git commit -m "feat: publish encrypted snapshots through Git"
```

Expected: two-clone stale-publication test passes and no merge commits exist.

---

### Task 7: End-to-End Synchronization Engine

**Files:**
- Create: `src/CodexHistorySync.Core/Sync/SyncEngine.cs`
- Create: `tests/CodexHistorySync.IntegrationTests/TwoDeviceSyncTests.cs`
- Create: `tests/CodexHistorySync.IntegrationTests/SyncFailureTests.cs`

**Interfaces:**
- Produces: `SyncEngine.SynchronizeAsync(SyncMode, CancellationToken) -> Task<SyncResult>`
- Consumes: scanner, crypto, state store, planner, history writer, conflict store, and `IStorageProvider`.

- [ ] **Step 1: Write the failing two-device convergence test**

Arrange two disposable Codex homes, independent local state, one bare Git remote, and shared repository key. Device A creates chat A; device B creates chat B. Run synchronization in interleaved order and assert both homes end with both plaintext hashes while the remote contains only `.chs` ciphertext.

Also assert:

```csharp
Assert.Empty(Directory.EnumerateFiles(remote, "*.jsonl", SearchOption.AllDirectories));
Assert.DoesNotContain("prompt marker", ReadAllRemoteBytesAsText());
```

- [ ] **Step 2: Run tests and verify failure**

Run: `dotnet test tests/CodexHistorySync.IntegrationTests --filter TwoDeviceSyncTests`

Expected: compilation fails because `SyncEngine` is absent.

- [ ] **Step 3: Implement the synchronization cycle**

Define the public result types:

```csharp
public enum SyncMode { Pull, Push, Bidirectional }

public sealed record SyncResult(
    string RemoteRevision,
    int Uploaded,
    int Downloaded,
    int Deleted,
    int Conflicts,
    bool RemoteChangedDuringAttempt);
```

`SynchronizeAsync` must:

1. acquire a repository-scoped local mutex;
2. scan stable local objects;
3. read and authenticate the remote snapshot;
4. load baseline state;
5. create a pure three-way plan;
6. stage and validate all downloads before any local mutation;
7. preserve conflicts;
8. apply safe imports/tombstones only when Codex is stopped;
9. encrypt uploads and submit a compare-and-swap publish;
10. repeat from step 2 on publication races, up to five attempts;
11. atomically update baseline state only after local and remote operations succeed.

`SyncMode.Pull` forbids uploads, `SyncMode.Push` forbids local imports, and `SyncMode.Bidirectional` permits both. All modes still detect and report conflicts.

- [ ] **Step 4: Add failure tests**

Cover offline provider, corrupt ciphertext, wrong key, publication race, interrupted import, Codex starting between staging and replacement, and exhausted five-attempt retries. Assert baseline state is not advanced past an unsuccessful operation.

- [ ] **Step 5: Run integration tests and commit**

Run:

```powershell
dotnet test tests/CodexHistorySync.IntegrationTests
dotnet test
git add src/CodexHistorySync.Core tests/CodexHistorySync.IntegrationTests
git commit -m "feat: synchronize encrypted history end to end"
```

Expected: two devices converge and injected failures preserve originals and baselines.

---

### Task 8: CLI Setup, Status, Doctor, and Manual Sync Commands

**Files:**
- Create: `src/CodexHistorySync.Cli/CliApplication.cs`
- Modify: `src/CodexHistorySync.Cli/Program.cs`
- Create: `tests/CodexHistorySync.IntegrationTests/CliTests.cs`
- Create: `docs/operations.md`

**Interfaces:**
- Produces documented commands: `init`, `join`, `sync`, `pull`, `push`, `status`, `doctor`, `conflicts`, `resolve`.

- [ ] **Step 1: Write failing CLI contract tests**

Invoke `CliApplication.RunAsync(string[] args, CancellationToken)` with fake services. Assert exit codes: `0` success, `1` operational failure, `2` usage error, `3` compatibility/security gate failure, `4` unresolved conflicts. Verify output never includes supplied passphrases, plaintext prompt markers, or credential-bearing URLs.

- [ ] **Step 2: Run tests and verify failure**

Run: `dotnet test tests/CodexHistorySync.IntegrationTests --filter CliTests`

Expected: compilation fails because `CliApplication` is absent.

- [ ] **Step 3: Implement `init` and `join`**

`init` verifies private visibility, creates a random repository ID and salt, prompts for the passphrase twice with hidden input, derives/caches the key, publishes the authenticated repository manifest, and creates device-local state.

`join` verifies visibility, clones metadata, prompts once, authenticates the manifest before any local write, caches the key, runs the Codex compatibility probe, and performs a dry-run plan. It prints the object counts and requires `--apply` for the first import.

- [ ] **Step 4: Implement manual operation commands**

- `sync`, `pull`, and `push` call `SyncEngine` with the corresponding mode.
- `status` reports local/remote/pending/conflict counts and last successful revision.
- `doctor` checks Codex paths/version, Git version, `gh` private visibility, key access, repository schema, process state, free disk space, and agent installation.
- `conflicts` lists IDs, hashes, devices, and timestamps without plaintext.
- `resolve` accepts exactly one of `--keep-local`, `--keep-remote`, or `--export-both <directory>`.

- [ ] **Step 5: Document commands and commit**

Run:

```powershell
dotnet test --filter CliTests
dotnet run --project src/CodexHistorySync.Cli -- doctor
dotnet test
git add src/CodexHistorySync.Cli tests/CodexHistorySync.IntegrationTests docs/operations.md
git commit -m "feat: expose safe history sync commands"
```

Expected: CLI tests pass and `doctor` reports real environment findings without secrets.

---

### Task 9: Windows Process Detection, Notifications, and Logon Agent

**Files:**
- Create: `src/CodexHistorySync.Windows/CodexProcessDetector.cs`
- Create: `src/CodexHistorySync.Windows/AgentScheduler.cs`
- Create: `src/CodexHistorySync.Windows/WindowsNotifier.cs`
- Create: `src/CodexHistorySync.Cli/AgentWorker.cs`
- Create: `tests/CodexHistorySync.Windows.Tests/CodexProcessDetectorTests.cs`
- Create: `tests/CodexHistorySync.Windows.Tests/AgentSchedulerTests.cs`
- Create: `tests/CodexHistorySync.IntegrationTests/AgentWorkerTests.cs`

**Interfaces:**
- Produces: `ICodexProcessDetector.IsRunning()` and `WaitForExitAsync`
- Produces: `AgentScheduler.InstallAsync(executablePath)` and `UninstallAsync`
- Produces: `AgentWorker.RunAsync(CancellationToken)`

- [ ] **Step 1: Write failing process and worker state-machine tests**

Use fake process-state events and a fake clock. Assert:

- logon while Codex stopped triggers bidirectional sync;
- Codex active permits stable export/push but never import;
- active-to-stopped waits the quiescence interval and performs bidirectional sync;
- incoming changes during activity generate one deferred-import notification;
- repeated failures back off exponentially from 30 seconds up to 30 minutes;
- cancellation exits without starting another cycle.

- [ ] **Step 2: Run tests and verify failure**

Run: `dotnet test --filter "CodexProcessDetectorTests|AgentSchedulerTests|AgentWorkerTests"`

Expected: compilation fails because Windows agent types are absent.

- [ ] **Step 3: Implement robust process detection**

Detect the configured Codex executable path plus known first-party `codex.exe` and ChatGPT/Codex desktop processes. Match canonical executable paths when accessible; treat access-denied inspection conservatively as active. Combine process events with a 2-second filesystem quiescence interval. Keep process names configurable for future Codex releases.

- [ ] **Step 4: Implement Task Scheduler installation**

Install a per-user task named `CodexHistorySync` triggered at logon, running:

```text
"<absolute-path>\codex-sync.exe" agent run
```

Use Task Scheduler APIs or `schtasks.exe` with `ProcessStartInfo.ArgumentList`; never construct a shell string. `agent uninstall` removes only that exact task after verifying its action points to this executable.

- [ ] **Step 5: Implement worker, notifications, and redacted logs**

Logs live under `%LOCALAPPDATA%\CodexHistorySync\logs`, rotate at 10 MiB, retain five files, and record operation IDs, object counts, timings, revisions, and error codes only. Notifications cover pending restart, unresolved conflict, repeated failure, and successful recovery after failures.

- [ ] **Step 6: Run agent tests and commit**

Run:

```powershell
dotnet test --filter "CodexProcessDetectorTests|AgentSchedulerTests|AgentWorkerTests"
dotnet test
git add src/CodexHistorySync.Windows src/CodexHistorySync.Cli tests
git commit -m "feat: synchronize automatically through a Windows agent"
```

Expected: all state-machine and task ownership tests pass.

---

### Task 10: Security Audit Tests, Packaging, and Recovery Drill

**Files:**
- Modify: `src/CodexHistorySync.Cli/CodexHistorySync.Cli.csproj`
- Create: `tests/CodexHistorySync.IntegrationTests/SecurityBoundaryTests.cs`
- Create: `tests/CodexHistorySync.IntegrationTests/RecoveryDrillTests.cs`
- Create: `docs/security.md`
- Modify: `docs/operations.md`
- Create: `README.md`

**Interfaces:**
- Produces: release artifact `artifacts/win-x64/codex-sync.exe`
- Produces: documented backup restoration and conflict recovery procedures.

- [ ] **Step 1: Write repository-wide security boundary tests**

Create a synthetic Codex home containing unique marker strings in prompts, `auth.json`, SQLite, logs, sandbox secrets, and attachments. Complete a sync and recursively inspect the bare remote and working clone. Assert forbidden filenames and marker strings are absent, all object files parse only as authenticated `CHS1` envelopes, and logs contain none of the markers.

- [ ] **Step 2: Write the recovery drill test**

Simulate: successful baseline sync, local modification, remote modification on another device, conflict preservation, `--export-both`, remote resolution, local import interruption, and restoration from backup. Assert the final live object and both exported conflict versions match their expected hashes.

- [ ] **Step 3: Run tests and verify the new tests fail before final wiring**

Run:

```powershell
dotnet test tests/CodexHistorySync.IntegrationTests --filter "SecurityBoundaryTests|RecoveryDrillTests"
```

Expected: at least one test fails until packaging paths, final redaction, and restoration wiring are complete.

- [ ] **Step 4: Finish security wiring and documentation**

Document threat model, GitHub-visible metadata, passphrase recovery limits, DPAPI behavior per Windows user, rotation procedure, private-repository verification, exclusions, tombstone retention, backup restore commands, and the unsupported same-chat concurrent-edit workflow. Update the approved design status to implemented only after acceptance passes.

- [ ] **Step 5: Publish the self-contained executable**

Add these properties to the CLI project:

```xml
<PropertyGroup>
  <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  <SelfContained>true</SelfContained>
  <PublishSingleFile>true</PublishSingleFile>
  <PublishTrimmed>false</PublishTrimmed>
  <AssemblyName>codex-sync</AssemblyName>
</PropertyGroup>
```

Run:

```powershell
dotnet publish src/CodexHistorySync.Cli -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/win-x64
```

- [ ] **Step 6: Run the complete verification suite**

Run with Codex closed and explicit compatibility fixture variables:

```powershell
dotnet test -c Release
artifacts\win-x64\codex-sync.exe doctor
artifacts\win-x64\codex-sync.exe --help
git status --short
```

Expected: zero test failures, compatibility gate passes, `doctor` contains no secret material, help lists every documented command, and the worktree contains only intended release/documentation changes.

- [ ] **Step 7: Perform a disposable two-device acceptance run**

Use two disposable `CODEX_HOME` directories, a temporary bare remote, and a test passphrase. Create different valid session fixtures on each side, run both agents with interleaved publication, stop them, and verify both homes converge after restart/reindex. Inject one same-chat divergence and verify conflict preservation and explicit resolution.

Do not opt into real history until this disposable acceptance run passes.

- [ ] **Step 8: Commit the release-ready MVP**

```powershell
git add README.md docs src tests Directory.Build.props CodexHistorySync.sln
git commit -m "feat: deliver encrypted Codex history synchronization"
```

Expected: the commit excludes `artifacts`, test fixtures copied from real history, keys, local state, and encrypted-history clones; ensure `.gitignore` contains those paths before committing.

---

## Final Acceptance Checklist

- [ ] Compatibility probe proves JSONL sessions become visible without copying SQLite.
- [ ] Two disposable devices converge different concurrently created chats.
- [ ] Same-chat divergence preserves both versions and requires explicit resolution.
- [ ] Remote Git data contains no plaintext prompts, attachments, credentials, local paths, or forbidden state files.
- [ ] Wrong keys, tampering, network loss, push races, interruption, and Codex process races cause no history loss.
- [ ] Manual commands and agent cycles call the same `SyncEngine`.
- [ ] Agent install/uninstall changes only its own per-user scheduled task.
- [ ] Self-contained `win-x64` executable runs on a clean Windows 11 profile.
- [ ] Operations, recovery, security, and compatibility documentation match verified behavior.
