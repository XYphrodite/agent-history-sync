# Fast Metadata-Only Session Catalog Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**Goal:** Make agent-sync --manage build one complete Codex/Grok snapshot concurrently without full conversation normalization or hashing.

**Architecture:** Add a bounded catalog-I/O boundary and two metadata-only native sources, then reduce LocalSessionCatalog to concurrent orchestration plus active-state projection. Synchronization scanners remain unchanged and strict copy/delete validation stays in LocalSessionOperations.

**Tech Stack:** C# 14, .NET 10, Task.WhenAll, Parallel.ForEachAsync, xUnit.

## Global Constraints

- Keep the first UI frame a complete two-column snapshot; do not progressively insert rows.
- Run Codex and Grok discovery concurrently.
- Perform no full history normalization or SHA-256 during catalog construction.
- Read no more than 64 KiB from each metadata/history file used for display.
- Use one global limit of eight concurrent catalog reads.
- Enumerate each native candidate set once per refresh.
- Preserve path containment, reparse rejection, title priority, ordering, active/unreadable markers, safe errors, and cancellation.
- Do not modify SessionScanner, GrokSessionScanner, synchronization behavior, native readers/writers, or final copy/delete validation.
- Do not add a persistent cache.

---

### Task 1: Add bounded catalog I/O primitives

**Files:**
- Create: src/CodexHistorySync.Core/Management/SessionCatalogIo.cs
- Create: tests/CodexHistorySync.Core.Tests/Management/SessionCatalogIoTests.cs

**Interfaces:**
- Produces: ISessionCatalogIo, SystemSessionCatalogIo, BoundedTextRead.
- Produces: SessionCatalogReadLimiter.RunAsync<T>(Func<CancellationToken, Task<T>>, CancellationToken).

- [ ] **Step 1: Write failing bounded-read tests**

~~~csharp
[Fact]
public async Task PrefixAndTailNeverConsumeMoreThanTheBudget()
{
    await using var fixture = new CatalogIoFixture();
    var path = await fixture.WriteAsync(new string('a', 200_000));
    var io = new SystemSessionCatalogIo();

    var prefix = await io.ReadPrefixAsync(path, 64 * 1024, CancellationToken.None);
    var tail = await io.ReadTailAsync(path, 64 * 1024, CancellationToken.None);

    Assert.Equal(64 * 1024, prefix.BytesRead);
    Assert.Equal(64 * 1024, tail.BytesRead);
    Assert.False(prefix.IsComplete);
    Assert.False(tail.IsComplete);
}
~~~

Add a limiter test that starts 24 gated operations, waits until eight have entered, releases them, and asserts the observed peak is exactly eight. Use a one-second CancellationTokenSource for the rendezvous instead of a correctness sleep.

- [ ] **Step 2: Run RED**

~~~powershell
dotnet test tests\CodexHistorySync.Core.Tests\CodexHistorySync.Core.Tests.csproj -c Release --filter FullyQualifiedName~SessionCatalogIoTests
~~~

Expected: compilation fails because the catalog-I/O types do not exist.

- [ ] **Step 3: Implement the I/O boundary**

~~~csharp
internal readonly record struct BoundedTextRead(
    string Text,
    bool IsComplete,
    int BytesRead,
    long FileLength);

internal interface ISessionCatalogIo
{
    IReadOnlyList<string> EnumerateFiles(string root, string pattern);
    IReadOnlyList<string> EnumerateDirectories(string root);
    bool FileExists(string path);
    DateTimeOffset LastWriteTime(string path);
    Task<BoundedTextRead> ReadPrefixAsync(
        string path, int maximumBytes, CancellationToken cancellationToken);
    Task<BoundedTextRead> ReadTailAsync(
        string path, int maximumBytes, CancellationToken cancellationToken);
}

internal sealed class SessionCatalogReadLimiter(int maximumConcurrency) : IDisposable
{
    private readonly SemaphoreSlim gate = maximumConcurrency > 0
        ? new(maximumConcurrency, maximumConcurrency)
        : throw new ArgumentOutOfRangeException(nameof(maximumConcurrency));

    public async Task<T> RunAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await operation(cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public void Dispose() => gate.Dispose();
}
~~~

SystemSessionCatalogIo uses FileShare.ReadWrite | FileShare.Delete, strict UTF-8, and buffers no larger than maximumBytes. Prefix starts at byte zero. Tail seeks to max(0, fileLength - maximumBytes); its caller handles line boundaries. Enumeration returns an empty list for missing/inaccessible roots, rejects a reparse root, recurses with AttributesToSkip = ReparsePoint and IgnoreInaccessible = true, and sorts ordinal-ignore-case.

- [ ] **Step 4: Run GREEN**

~~~powershell
dotnet test tests\CodexHistorySync.Core.Tests\CodexHistorySync.Core.Tests.csproj -c Release --filter "FullyQualifiedName~SessionCatalogIoTests|FullyQualifiedName~LocalSessionCatalogTests"
~~~

- [ ] **Step 5: Commit**

~~~powershell
git add src/CodexHistorySync.Core/Management/SessionCatalogIo.cs tests/CodexHistorySync.Core.Tests/Management/SessionCatalogIoTests.cs
git commit -m "feat: add bounded session catalog IO"
~~~

---

### Task 2: Add the metadata-only Codex source

**Files:**
- Create: src/CodexHistorySync.Core/Management/SessionCatalogCandidate.cs
- Create: src/CodexHistorySync.Core/Management/CodexSessionCatalogSource.cs
- Modify: tests/CodexHistorySync.Core.Tests/Management/LocalSessionCatalogTests.cs

**Interfaces:**
- Produces: ILocalSessionCatalogSource.ScanAsync(SessionCatalogReadLimiter, CancellationToken).
- Produces: SessionCatalogCandidate(string SessionId, string NativePath, string Title, DateTimeOffset LastModifiedAt, bool CanRead).
- Consumes: CodexPaths and ISessionCatalogIo from Task 1.

- [ ] **Step 1: Write Codex RED regressions**

~~~csharp
[Fact]
public async Task CodexSourceUsesOneEnumerationAndBoundedMetadata()
{
    await using var fixture = new CatalogFixture();
    var path = await fixture.WriteCodexAsync(
        "bounded", "Bounded title", "question", "2026-08-09T15:00:00Z");
    await File.AppendAllTextAsync(path, new string('x', 2 * 1024 * 1024));
    var io = new RecordingCatalogIo(new SystemSessionCatalogIo());

    using var limiter = new SessionCatalogReadLimiter(8);
    var rows = await new CodexSessionCatalogSource(fixture.CodexPaths, io)
        .ScanAsync(limiter, CancellationToken.None);

    Assert.True(Assert.Single(rows).CanRead);
    Assert.All(io.ReadBudgets, value => Assert.InRange(value, 1, 64 * 1024));
    Assert.Equal(1, io.EnumerationCount(fixture.CodexPaths.Sessions));
    Assert.Equal(1, io.EnumerationCount(fixture.CodexPaths.ArchivedSessions));
}
~~~

Add fixtures for: duplicate session_meta IDs at different paths (both visible/unreadable); malformed JSON inside the retained prefix (visible/unreadable); malformed bytes only after 64 KiB (bounded metadata remains readable); cancellation from the recording reader; index last-entry-wins; technical wrappers; excluded directories; reparse targets; timestamp and title ordering.

- [ ] **Step 2: Run RED**

~~~powershell
dotnet test tests\CodexHistorySync.Core.Tests\CodexHistorySync.Core.Tests.csproj -c Release --filter "FullyQualifiedName~CodexSource|FullyQualifiedName~ScanAsyncUsesCodexIndex"
~~~

Expected: compilation fails because the source contracts do not exist.

- [ ] **Step 3: Implement the contracts and Codex source**

~~~csharp
internal interface ILocalSessionCatalogSource
{
    ManagedAgent Agent { get; }
    Task<IReadOnlyList<SessionCatalogCandidate>> ScanAsync(
        SessionCatalogReadLimiter limiter,
        CancellationToken cancellationToken);
}

internal sealed record SessionCatalogCandidate(
    string SessionId,
    string NativePath,
    string Title,
    DateTimeOffset LastModifiedAt,
    bool CanRead);
~~~

CodexSessionCatalogSource performs these exact operations:

1. Read the bounded session_index.jsonl tail once through the shared limiter.
2. Discard an initial partial line, ignore an incomplete final line, parse at most the last 64 complete lines, ignore malformed/non-object entries, and let the last valid thread_name for an ID win.
3. Enumerate active and archived roots once each, retaining current disallowed segments and ManagedSessionPathPolicy validation.
4. Process indexed candidate slots with Parallel.ForEachAsync, MaxDegreeOfParallelism = 8, and the shared limiter. Never append from workers to a shared List.
5. Read a 64 KiB prefix and inspect at most 64 complete records. Require one safe session_meta ID; conflicting IDs or malformed retained records make the candidate unreadable. Bytes beyond the prefix are never inspected.
6. Preserve official index, native metadata, meaningful user preview, and ID title priority plus current timestamp behavior.
7. Group by ID after collection and set CanRead false for every member of a duplicate group.

- [ ] **Step 4: Run GREEN and mutation evidence**

~~~powershell
dotnet test tests\CodexHistorySync.Core.Tests\CodexHistorySync.Core.Tests.csproj -c Release --filter "FullyQualifiedName~CodexSource|FullyQualifiedName~ScanAsyncUsesCodexIndex|FullyQualifiedName~ScanAsyncSkipsTechnical"
~~~

Temporarily replace the bounded prefix call with a complete-file read and confirm the large-tail regression fails. Restore and rerun GREEN.

- [ ] **Step 5: Commit**

~~~powershell
git add src/CodexHistorySync.Core/Management/SessionCatalogCandidate.cs src/CodexHistorySync.Core/Management/CodexSessionCatalogSource.cs tests/CodexHistorySync.Core.Tests/Management/LocalSessionCatalogTests.cs
git commit -m "feat: scan Codex catalog from bounded metadata"
~~~

---

### Task 3: Add the metadata-only Grok source

**Files:**
- Create: src/CodexHistorySync.Core/Management/GrokSessionCatalogSource.cs
- Modify: tests/CodexHistorySync.Core.Tests/Management/LocalSessionCatalogTests.cs

**Interfaces:**
- Implements ILocalSessionCatalogSource from Task 2.
- Consumes GrokPaths, ISessionCatalogIo, and SessionCatalogReadLimiter.

- [ ] **Step 1: Write Grok RED regressions**

~~~csharp
[Fact]
public async Task GrokSourceUsesOfficialSummaryWithoutReadingChat()
{
    await using var fixture = new CatalogFixture();
    var id = "61000000-0000-0000-0000-000000000011";
    var directory = await fixture.WriteGrokAsync(
        id, "Official", "fallback", "2026-08-09T15:00:00Z");
    var io = new RecordingCatalogIo(new SystemSessionCatalogIo());

    using var limiter = new SessionCatalogReadLimiter(8);
    var rows = await new GrokSessionCatalogSource(fixture.GrokPaths, io)
        .ScanAsync(limiter, CancellationToken.None);

    Assert.Equal("Official", Assert.Single(rows).Title);
    Assert.DoesNotContain(io.ReadPaths, value => string.Equals(
        value, Path.Combine(directory, "chat_history.jsonl"),
        StringComparison.OrdinalIgnoreCase));
    Assert.Equal(1, io.DirectoryEnumerationCount);
}
~~~

Add cases for: missing official title reads exactly one bounded chat prefix; chat larger than 64 KiB never requests a larger budget; duplicate UUID directories below two working-directory parents produce two unreadable rows; malformed/non-object/oversized summary remains visible and unreadable; summary ID mismatch; missing chat; reparse target; unsafe UUID; cancellation.

- [ ] **Step 2: Run RED**

~~~powershell
dotnet test tests\CodexHistorySync.Core.Tests\CodexHistorySync.Core.Tests.csproj -c Release --filter "FullyQualifiedName~GrokSource|FullyQualifiedName~ScanAsyncUsesGrok"
~~~

Expected: compilation fails because GrokSessionCatalogSource does not exist.

- [ ] **Step 3: Implement the Grok source**

Enumerate directories once, validate concrete UUID targets, and process indexed result slots with Parallel.ForEachAsync degree eight plus the shared limiter. Read summary.json first. Use this exact title priority:

~~~csharp
var title = GetString(root, "generated_title")
            ?? GetString(root, "session_summary")
            ?? GetString(info, "title")
            ?? GetString(root, "title");
~~~

Only when the normalized title is absent, read a bounded chat prefix and select the first non-technical user text or input_text block. Require info.id to equal the UUID directory name for readability. Use the chat timestamp when summary timestamps are absent. After collection, mark every duplicate-ID row unreadable.

- [ ] **Step 4: Run GREEN and mutation evidence**

~~~powershell
dotnet test tests\CodexHistorySync.Core.Tests\CodexHistorySync.Core.Tests.csproj -c Release --filter "FullyQualifiedName~GrokSource|FullyQualifiedName~ScanAsyncUsesGrok|FullyQualifiedName~ScanAsyncSkipsTechnical"
~~~

Temporarily remove the title-absent guard and confirm the no-chat-read regression fails. Restore and rerun GREEN.

- [ ] **Step 5: Commit**

~~~powershell
git add src/CodexHistorySync.Core/Management/GrokSessionCatalogSource.cs tests/CodexHistorySync.Core.Tests/Management/LocalSessionCatalogTests.cs
git commit -m "feat: scan Grok catalog from bounded metadata"
~~~

---

### Task 4: Rewire LocalSessionCatalog for concurrent sources

**Files:**
- Modify: src/CodexHistorySync.Core/Management/LocalSessionCatalog.cs
- Modify: tests/CodexHistorySync.Core.Tests/Management/LocalSessionCatalogTests.cs

**Interfaces:**
- Public constructor remains LocalSessionCatalog(CodexPaths?, GrokPaths?, IManagedSessionActiveState).
- Add an internal constructor accepting nullable ILocalSessionCatalogSource instances.
- Consume both metadata sources and one shared read limiter.

- [ ] **Step 1: Write orchestration RED tests**

~~~csharp
[Fact]
public async Task ScanAsyncStartsBothSourcesBeforeEitherCompletes()
{
    var rendezvous = new AsyncRendezvous(2);
    var codex = new BlockingCatalogSource(ManagedAgent.Codex, rendezvous);
    var grok = new BlockingCatalogSource(ManagedAgent.Grok, rendezvous);
    var catalog = new LocalSessionCatalog(codex, grok, new FakeActiveState());

    var snapshot = await catalog.ScanAsync(CancellationToken.None)
        .WaitAsync(TimeSpan.FromSeconds(2));

    Assert.Equal(1, codex.ScanCount);
    Assert.Equal(1, grok.ScanCount);
    Assert.Single(snapshot.Codex);
    Assert.Single(snapshot.Grok);
}
~~~

Use these exact test-local coordination types so the test has no timing sleep:

~~~csharp
private sealed class AsyncRendezvous(int participants)
{
    private int arrivals;
    private readonly TaskCompletionSource released =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task SignalAndWaitAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref arrivals) == participants)
            released.TrySetResult();
        await released.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}

private sealed class BlockingCatalogSource(
    ManagedAgent agent,
    AsyncRendezvous rendezvous) : ILocalSessionCatalogSource
{
    public ManagedAgent Agent => agent;
    public int ScanCount { get; private set; }

    public async Task<IReadOnlyList<SessionCatalogCandidate>> ScanAsync(
        SessionCatalogReadLimiter limiter,
        CancellationToken cancellationToken)
    {
        ScanCount++;
        await rendezvous.SignalAndWaitAsync(cancellationToken);
        return [new SessionCatalogCandidate(
            "session", Path.GetFullPath("session.jsonl"), "Session",
            DateTimeOffset.UnixEpoch, CanRead: true)];
    }
}
~~~

Add tests proving: the shared recording limiter peaks at no more than eight across both sources; both agent activity queries overlap; each runs once; cancellation propagates; a source failure remains a whole-refresh failure; rows keep descending timestamp/ID ordering; a missing source returns an empty column without activity query.

- [ ] **Step 2: Run RED**

~~~powershell
dotnet test tests\CodexHistorySync.Core.Tests\CodexHistorySync.Core.Tests.csproj -c Release --filter "FullyQualifiedName~StartsBothSources|FullyQualifiedName~SharedReadLimit|FullyQualifiedName~ChecksActivityOncePerAgent"
~~~

Expected: the internal constructor is missing or the rendezvous times out.

- [ ] **Step 3: Replace legacy catalog orchestration**

Delete SessionScanner/GrokSessionScanner fields and all scanner-driven stable maps and second enumerations. Construct metadata sources only for non-null path sets. Use one limiter:

~~~csharp
public async Task<SessionCatalogSnapshot> ScanAsync(CancellationToken cancellationToken)
{
    using var limiter = new SessionCatalogReadLimiter(8);
    var codexTask = ScanAgentAsync(
        codexSource, ManagedAgent.Codex, limiter, cancellationToken);
    var grokTask = ScanAgentAsync(
        grokSource, ManagedAgent.Grok, limiter, cancellationToken);
    await Task.WhenAll(codexTask, grokTask).ConfigureAwait(false);
    return new SessionCatalogSnapshot(
        Order(codexTask.Result),
        Order(grokTask.Result));
}
~~~

ScanAgentAsync starts the source with Task.Run so synchronous filesystem enumeration in one source cannot delay starting the other. Start the agent-wide activity query alongside it. Await both and project each SessionCatalogCandidate to ManagedSession with the single activity result. Preserve the current fail-closed non-cancellation catch and requested-cancellation propagation.

- [ ] **Step 4: Run catalog, scanner, operation, and manager suites**

~~~powershell
dotnet test tests\CodexHistorySync.Core.Tests\CodexHistorySync.Core.Tests.csproj -c Release --filter "FullyQualifiedName~SessionScannerTests|FullyQualifiedName~GrokSessionScannerTests|FullyQualifiedName~SessionCatalogIoTests|FullyQualifiedName~LocalSessionCatalogTests|FullyQualifiedName~LocalSessionOperationsTests"
dotnet test tests\CodexHistorySync.IntegrationTests\CodexHistorySync.IntegrationTests.csproj -c Release --filter FullyQualifiedName~SessionManager
~~~

Expected: all selected tests pass, including unchanged synchronization scanner suites.

- [ ] **Step 5: Commit**

~~~powershell
git add src/CodexHistorySync.Core/Management/LocalSessionCatalog.cs tests/CodexHistorySync.Core.Tests/Management/LocalSessionCatalogTests.cs
git commit -m "perf: build manager catalog concurrently"
~~~

---

### Task 5: Prove action safety, document, and measure

**Files:**
- Modify: tests/CodexHistorySync.Core.Tests/Management/LocalSessionCatalogTests.cs
- Modify: tests/CodexHistorySync.Core.Tests/Management/LocalSessionOperationsTests.cs
- Modify: docs/operations.md
- Modify: docs/security.md

**Interfaces:**
- Consume metadata-only ManagedSession rows.
- Verify without changing LocalSessionOperations.CopyAsync/DeleteAsync.

- [ ] **Step 1: Add action-boundary regressions**

Create Codex and Grok histories with valid bounded metadata and malformed content after 64 KiB. Obtain each selected row through the real LocalSessionCatalog, assert CanRead, then pass it to real LocalSessionOperations:

~~~csharp
await Assert.ThrowsAsync<ManagedSessionOperationException>(() =>
    operations.CopyAsync(selected, CancellationToken.None));
Assert.Empty(recordingDestinationWriter.Writes);
Assert.True(File.Exists(selected.NativePath) || Directory.Exists(selected.NativePath));
~~~

This proves catalog admission does not bypass full native parsing and final validation.

- [ ] **Step 2: Run focused safety tests**

~~~powershell
dotnet test tests\CodexHistorySync.Core.Tests\CodexHistorySync.Core.Tests.csproj -c Release --filter "FullyQualifiedName~MetadataOnlyCatalogAdmission|FullyQualifiedName~LocalSessionOperationsTests"
~~~

Expected: bounded catalog rows are visible, both malformed full conversations are rejected, and nothing is published or deleted.

- [ ] **Step 3: Update documentation**

In docs/operations.md, state that refresh scans Codex and Grok concurrently, reads bounded metadata, and performs full parsing only for a selected action. In docs/security.md, state that the unreadable marker reflects bounded catalog readability while every Copy/Delete still performs fresh full parsing, activity, identity/path, fingerprint, and immediate pre-action checks.

- [ ] **Step 4: Run full verification and publish smoke**

~~~powershell
dotnet test CodexHistorySync.sln -c Release
dotnet publish src\CodexHistorySync.Cli\CodexHistorySync.Cli.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -o artifacts\agent-sync-win-x64-fast-catalog
~~~

Expected: full suite green; publish contains exactly one agent-sync.exe, zero PDB files; --help exits zero and contains [--manage].

- [ ] **Step 5: Record real-home diagnostic timing**

Run the old artifact and the new artifact using ProcessStartInfo with UseShellExecute false and redirected stdin/stdout/stderr. Close StandardInput immediately, wait for exit after the completed manager frame is emitted, and record elapsed time and displayed session counts. Do not modify or delete native sessions. Treat timing as diagnostic evidence, not a unit-test threshold.

- [ ] **Step 6: Review and commit**

~~~powershell
git diff --check
git diff --stat
git add tests/CodexHistorySync.Core.Tests/Management/LocalSessionCatalogTests.cs tests/CodexHistorySync.Core.Tests/Management/LocalSessionOperationsTests.cs docs/operations.md docs/security.md
git commit -m "test: verify fast catalog action boundary"
~~~

Request an independent review focused on bounded reads, real Codex/Grok overlap, the global concurrency limit, duplicates, cancellation, reparse paths, and final action validation. Address every Critical or Important finding through a new RED-to-GREEN cycle before integration.
