# Session Titles and Descriptions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give every session a title and a description of its own, produced in `agent-sync --sessions` by a local LLM on a keystroke and correctable by hand, stored outside the agent homes, and shared between machines through the encrypted repository.

**Architecture:** A session annotation is a new first-class record — title, description, provenance, and the hash of the digest it was made from. Core gains a store, a digest builder, and a suggester behind an interface; the catalog is decorated rather than rewritten, so the four `*SessionCatalogSource` priority chains stay as they are and only report where their title came from. The viewer gains two keys and one header line. Sync carries the annotations as one new object kind.

**Tech Stack:** C# 14, .NET 10, `System.Text.Json`, xUnit, Spectre.Console.

## Global Constraints

- No agent home is written. An annotation never becomes an `ai-title` record, a Codex index entry, or any other native artifact.
- The catalog scan path makes no network request. Generation happens only from an explicit keystroke in `--sessions`, never from a scan, a render, or `--manage`.
- The suggester endpoint comes from configuration and has no default. With no endpoint configured the feature is inert: the key reports that titling is not configured and nothing else changes.
- One request at a time, connect timeout 2 s, request timeout 60 s, and cancellation on any key. An unreachable endpoint leaves the session exactly as it was and shows a message.
- A generated annotation is replaced freely; an edited one is overwritten only after an explicit confirmation.
- An annotation whose `DigestHash` no longer matches the session is shown as stale, never silently discarded.
- The annotation title is used **only** when the agent supplies no official name. Official Codex `thread_name`, Grok `generated_title`, and Claude `ai-title` keep first priority, unchanged.
- The 80-character title bound, single-line list rows, Unicode normalization, and safe `Text` rendering stay as they are.
- `ObjectKind` gains exactly one member, appended last, with the upgrade gate documented for the third time.
- Every production change follows strict RED-GREEN TDD.

---

## Task 1: The annotation record and its store

**Files:**
- Add: `src/CodexHistorySync.Core/Annotations/SessionAnnotation.cs`
- Add: `src/CodexHistorySync.Core/Annotations/SessionAnnotationStore.cs`
- Test: `tests/CodexHistorySync.Core.Tests/Annotations/SessionAnnotationStoreTests.cs`

**Interfaces:**
- Produces: `ISessionAnnotationStore` with `Task<IReadOnlyDictionary<SessionAnnotationKey, SessionAnnotation>> LoadAsync(ct)` and `Task SaveAsync(SessionAnnotationKey key, SessionAnnotation annotation, ct)`.
- `SessionAnnotationKey(ManagedAgent Agent, string SessionId)`; `SessionAnnotation(string Title, string? Description, SessionAnnotationSource Source, string DigestHash, string? Model, DateTimeOffset UpdatedAt)`; `enum SessionAnnotationSource { Generated, Edited }`.

- [x] **Step 1:** Failing tests — a saved annotation round-trips; an unknown agent value in the file is ignored without failing the load; a title longer than 80 characters is rejected at the boundary; a session id that is not a safe file-name component is rejected.
- [x] **Step 2:** Implement over `%LOCALAPPDATA%\CodexHistorySync\annotations.json`, written through a temporary file and `File.Replace`, exactly as `LocalStateStore` writes `state.json` (`src/CodexHistorySync.Core/State/LocalStateStore.cs:31-70`). Reuse its id validation rather than writing a second one.
- [x] **Step 3:** Test that a concurrent writer never leaves a half-written file: save twice from two tasks and assert the file parses.

## Task 2: Where the title came from

**Files:**
- Modify: `src/CodexHistorySync.Core/Management/ManagedSession.cs`
- Modify: `src/CodexHistorySync.Core/Management/ClaudeSessionCatalogSource.cs:20-60,99-180`
- Modify: `CodexSessionCatalogSource.cs`, `GrokSessionCatalogSource.cs`, `ContinueSessionCatalogSource.cs`
- Test: `tests/CodexHistorySync.Core.Tests/Management/*CatalogSourceTests.cs`

**Interfaces:**
- Produces: `ManagedSession.TitleSource` of `enum ManagedTitleSource { Official, Fallback, SessionId }`, defaulted so existing hand-built sessions still compile.

- [x] **Step 1:** Failing tests, one per agent: an official name reports `Official`, a first-message preview reports `Fallback`, and a bare UUID reports `SessionId`.
- [x] **Step 2:** Thread the value out of each existing priority chain without changing which title wins. For Claude that is the `tailTitle ?? aiTitle ?? summaryTitle ?? firstUserPreview` chain — the first three are `Official`, the preview is `Fallback`.
- [x] **Step 3:** Assert no title text changes anywhere: the existing catalog suites must pass untouched.

## Task 3: Overlaying annotations onto the catalog

**Files:**
- Add: `src/CodexHistorySync.Core/Management/AnnotatedSessionCatalog.cs`
- Test: `tests/CodexHistorySync.Core.Tests/Management/AnnotatedSessionCatalogTests.cs`

- [x] **Step 1:** Failing tests — an annotation replaces a `Fallback` or `SessionId` title, leaves an `Official` one alone, and is dropped for a session that no longer exists. Staleness is decided in Task 7 instead: it needs the conversation, and reading every session on every scan is exactly what the fast-catalog work exists to avoid. The viewer has the open session already read.
- [x] **Step 2:** Implement as an `ILocalSessionCatalog` decorator that loads the store once per scan and overlays it. Both screens get it by construction, so neither view learns anything new about annotations.
- [x] **Step 3:** Assert the decorator adds no second scan and no filesystem walk of its own.

## Task 4: The digest sent to the model

**Files:**
- Add: `src/CodexHistorySync.Core/Annotations/SessionDigest.cs`
- Test: `tests/CodexHistorySync.Core.Tests/Annotations/SessionDigestTests.cs`

- [x] **Step 1:** Failing tests — turns are rendered role-prefixed in order; technical wrappers are dropped through the existing `ConversationTechnicalText` rules; a conversation over the character bound keeps its head and its tail with an elision marker between them; the hash is stable across two builds of the same conversation and changes when a turn changes.
- [x] **Step 2:** Implement `Build(PortableConversation, int maxChars = 18000)` returning the text and its SHA-256. Input is `PortableConversation` only, so all four agents are covered by one implementation and `ISessionContentReader` supplies it unchanged.

## Task 5: The suggester

**Files:**
- Add: `src/CodexHistorySync.Core/Annotations/ISessionTitleSuggester.cs`
- Add: `src/CodexHistorySync.Core/Annotations/OllamaSessionTitleSuggester.cs`
- Test: `tests/CodexHistorySync.Core.Tests/Annotations/OllamaSessionTitleSuggesterTests.cs`

**Interfaces:**
- Produces: `Task<SessionAnnotationDraft?> SuggestAsync(SessionDigestResult digest, CancellationToken ct)`; `SessionAnnotationDraft(string Title, string Description, string Model)`.

- [x] **Step 1:** Failing tests against a stubbed `HttpMessageHandler` — a well-formed response becomes a draft; a title over 80 characters is truncated on the boundary; a non-JSON body, an HTTP 500, a connect timeout, and a cancellation each return null without throwing; no request is made when no endpoint is configured.
- [x] **Step 2:** Implement `POST {endpoint}/api/chat` with `stream:false`, the `{title, description}` JSON schema in `format`, and `options` of `temperature 0.3`, `num_ctx 16384`, `num_predict 8000`. Default model `qwen3:8b` — measured faster and more specific than `gpt-oss:20b` on real sessions, 23 s against 37 s. Serialize the body before sending it: Ollama closes the connection on a chunked request body, which is what streaming the JSON produces.
- [x] **Step 3:** Test that two overlapping calls are serialized rather than issued together.

## Task 6: Configuration

**Files:**
- Modify: the CLI configuration surface and `DefaultCliServices.cs`
- Modify: `docs/security.md:41`, `docs/operations.md`
- Test: `tests/CodexHistorySync.Core.Tests` configuration cases

- [x] **Step 1:** Failing tests — absent configuration disables the feature; a non-loopback, non-private endpoint is refused; an `http` endpoint on a private address is accepted.
- [x] **Step 2:** Add a `titles` configuration block: `endpoint`, `model`, `language`. No default endpoint.
- [x] **Step 3:** Extend the security model document. The `--manage` local-only paragraph stays true; a new paragraph states that `--sessions` sends the digest of one selected session to the operator-configured endpoint, only on the generate key, and never automatically.

## Task 7: The viewer

**Files:**
- Modify: `src/CodexHistorySync.Cli/Management/SessionViewerCommand.cs`, `SessionViewerState.cs`, `SessionViewerApplication.cs`, `SpectreSessionViewerView.cs`, `ISessionViewerView.cs`
- Test: `tests/CodexHistorySync.Core.Tests/…/SessionViewerStateTests.cs`, viewer application tests

- [x] **Step 1:** Failing tests — `t` requests a suggestion, `a` opens the annotation editor, and both are ignored while the list is empty or the session is unreadable.
- [x] **Step 2:** Add `GenerateAnnotation` and `EditAnnotation` commands on `t` and `a`, the only unbound letters left beside the existing `n e r q` and `/`.
- [x] **Step 3:** Generation runs off the render path with a `Generating…` status, stays cancellable by any key, and repaints once when it lands. No frame waits on the network.
- [x] **Step 4:** The editor prompts title then description, seeded with what is already there; `Esc` abandons, an empty title abandons. Overwriting an `Edited` annotation asks first.
- [x] **Step 5:** Render the description as a header line above the conversation, wrapped to `ContentWidth` and clipped to two rows, with a stale marker when the digest no longer matches. List rows stay single-line.

## Task 8: Carrying annotations between machines

**Files:**
- Modify: `src/CodexHistorySync.Core/Model/SyncModels.cs`
- Modify: `src/CodexHistorySync.Core/Sync/SyncEngine.cs`, `ThreeWayPlanner.cs`
- Modify: `docs/operations.md`, `README.md`
- Test: `tests/CodexHistorySync.IntegrationTests`

- [x] **Step 1:** Failing tests — an annotation published on one machine appears on another after `pull`, its text never appears in the repository in plaintext, and removing it on one machine removes it on the other. **Newest-wins was not implemented and should not be:** the index carries hashes, not timestamps, so the planner cannot tell which side is newer without decrypting both. Two machines that name one session conflict like two machines editing one session, and resolve the same way.
- [x] **Step 0 (done):** Storage reshaped for it. Annotations live one file per session under `%LOCALAPPDATA%CodexHistorySyncannotations`, because a session is the unit that is edited, published, and merged; a shared document would make two machines that named two different sessions collide over one object. `SessionAnnotationStore.Serialize`/`TryRead` produce and read the exact bytes that travel.
- [x] **Step 2:** Append `ObjectKind.SessionAnnotations` last in the enum, one object per annotated session, carrying the same comment the `ClaudeSession` and `ContinueSession` members carry.

**What the import path costs, measured against the code rather than guessed:** every kind that exists today is session history, and `ApplyStagedImportAsync` hands the staged bytes to `CodexHistoryWriter.ImportAsync` - tombstones, expected-state checks, atomic tree moves into an agent home. `ValidateHistoryPayload` ends in `ValidateSessionJsonl`, and the engine says so out loud: "Only session history can be imported." An annotation is the first object that is **not** session history and must never reach an agent home. So this task also needs a second, much smaller import destination for the annotations directory, guards on both sides so neither kind can be written into the other place, tombstone behaviour for an annotation whose session was deleted, and the security-boundary suite taught that a `CHS1` object may now decrypt to something that is not a transcript.
- [x] **Step 3:** Document the upgrade gate a third time: an older build rejects the whole index on an unknown kind, so every machine sharing the repository must be upgraded before the first push that carries an annotation. Add an `annotations=` line to `agent-sync status` so a build that knows the kind can be told apart from one that does not.
- [x] **Step 4:** Verify the security boundary suite still passes: annotations are `CHS1`-encrypted like every other object and no plaintext title reaches the repository.
