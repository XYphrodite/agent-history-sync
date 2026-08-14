using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Grok;
using CodexHistorySync.Core.Management;

namespace CodexHistorySync.Core.Tests.Management;

public sealed class LocalSessionCatalogTests
{
    [Fact]
    public async Task CodexSourceUsesOneEnumerationAndBoundedMetadata()
    {
        // Replacing the bounded metadata read with an unbounded read must make the recording IO observe a >64 KiB budget.
        await using var fixture = new CatalogFixture();
        var path = await fixture.WriteCodexAsync("bounded", "Bounded title", "question", "2026-08-09T15:00:00Z");
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

    [Fact]
    public async Task CodexSourceMakesDuplicateMetadataIdsUnreadable()
    {
        // Removing post-collection duplicate detection would expose both copies as readable.
        await using var fixture = new CatalogFixture();
        var active = await fixture.WriteCodexAsync("duplicate-source", "One", "one", "2026-08-09T10:00:00Z");
        var target = Path.Combine(fixture.CodexPaths.ArchivedSessions, "rollout-duplicate-source.jsonl");
        Directory.CreateDirectory(fixture.CodexPaths.ArchivedSessions);
        File.Copy(active, target);

        using var limiter = new SessionCatalogReadLimiter(8);
        var rows = await new CodexSessionCatalogSource(fixture.CodexPaths, new SystemSessionCatalogIo())
            .ScanAsync(limiter, CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.False(row.CanRead));
    }

    [Fact]
    public async Task CodexSourceIgnoresMalformedBytesAfterMetadataPrefix()
    {
        // Continuing past the prefix would treat trailing malformed bytes as a metadata failure.
        await using var fixture = new CatalogFixture();
        var path = await fixture.WriteCodexAsync("prefix-only", "Prefix title", "question", "2026-08-09T15:00:00Z");
        await File.AppendAllTextAsync(path, new string('x', 64 * 1024) + "{bad-json}");

        using var limiter = new SessionCatalogReadLimiter(8);
        var row = Assert.Single(await new CodexSessionCatalogSource(fixture.CodexPaths, new SystemSessionCatalogIo())
            .ScanAsync(limiter, CancellationToken.None));

        Assert.Equal("Prefix title", row.Title);
        Assert.True(row.CanRead);
    }

    [Fact]
    public async Task CodexSourceUsesLastIndexNameAndSkipsTechnicalPreview()
    {
        // Using first index entry or accepting technical wrappers would return either "Old" or injected context.
        await using var fixture = new CatalogFixture();
        await fixture.WriteCodexAsync("source-index", null, "<environment_context> injected", "2026-08-09T15:00:00Z");
        await fixture.WriteCodexIndexAsync(
            new { id = "source-index", thread_name = "Old" },
            new { id = "source-index", thread_name = "Newest" });

        using var limiter = new SessionCatalogReadLimiter(8);
        var row = Assert.Single(await new CodexSessionCatalogSource(fixture.CodexPaths, new SystemSessionCatalogIo())
            .ScanAsync(limiter, CancellationToken.None));

        Assert.Equal("Newest", row.Title);
    }

    [Fact]
    public async Task CodexSourceUsesCompleteEofIndexRecordWithoutTerminalNewline()
    {
        // Discarding every final line from a bounded tail would lose the newest official title at EOF.
        await using var fixture = new CatalogFixture();
        await fixture.WriteCodexAsync("eof-index", "Metadata fallback", "question", "2026-08-09T15:00:00Z");
        var newest = JsonSerializer.Serialize(new { id = "eof-index", thread_name = "EOF official title" });
        await fixture.WriteCodexIndexTextAsync(new string('x', 70 * 1024) + "\n" + newest);

        using var limiter = new SessionCatalogReadLimiter(8);
        var row = Assert.Single(await new CodexSessionCatalogSource(fixture.CodexPaths, new SystemSessionCatalogIo())
            .ScanAsync(limiter, CancellationToken.None));

        Assert.Equal("EOF official title", row.Title);
    }

    [Fact]
    public async Task CodexSourceIgnoresTruncatedEofIndexRecordWithoutTerminalNewline()
    {
        // Parsing a syntactically incomplete EOF record would let corrupt index data override safe metadata.
        await using var fixture = new CatalogFixture();
        await fixture.WriteCodexAsync("truncated-eof-index", "Metadata fallback", "question", "2026-08-09T15:00:00Z");
        await fixture.WriteCodexIndexTextAsync(new string('x', 70 * 1024) + "\n" +
            "{\"id\":\"truncated-eof-index\",\"thread_name\":\"Not complete\"");

        using var limiter = new SessionCatalogReadLimiter(8);
        var row = Assert.Single(await new CodexSessionCatalogSource(fixture.CodexPaths, new SystemSessionCatalogIo())
            .ScanAsync(limiter, CancellationToken.None));

        Assert.Equal("Metadata fallback", row.Title);
    }

    [Fact]
    public async Task CodexSourceUsesMeaningfulPreviewWhenMetadataTitleIsBlank()
    {
        // Treating blank metadata as a title would fall through to the ID instead of the actual user request.
        await using var fixture = new CatalogFixture();
        await fixture.WriteCodexUsersAsync("preview-source", "   ", "<environment_context> injected", "Real request");

        using var limiter = new SessionCatalogReadLimiter(8);
        var row = Assert.Single(await new CodexSessionCatalogSource(fixture.CodexPaths, new SystemSessionCatalogIo())
            .ScanAsync(limiter, CancellationToken.None));

        Assert.Equal("Real request", row.Title);
    }

    [Fact]
    public async Task CodexSourceKeepsIdentifiableMalformedMetadataVisibleButUnreadable()
    {
        // Ignoring the malformed retained record would incorrectly allow this candidate to be read.
        await using var fixture = new CatalogFixture();
        var path = await fixture.WriteCodexAsync("broken-source", "Title", "question", "2026-08-09T15:00:00Z");
        await File.AppendAllTextAsync(path, "{bad-json}\n");

        using var limiter = new SessionCatalogReadLimiter(8);
        var row = Assert.Single(await new CodexSessionCatalogSource(fixture.CodexPaths, new SystemSessionCatalogIo())
            .ScanAsync(limiter, CancellationToken.None));

        Assert.Equal("broken-source", row.SessionId);
        Assert.False(row.CanRead);
    }

    [Fact]
    public async Task CodexSourcePropagatesRequestedReadCancellation()
    {
        // Converting cancellation to an unreadable row would make a cancelled refresh appear complete.
        await using var fixture = new CatalogFixture();
        await fixture.WriteCodexAsync("cancel-source", null, "question", "2026-08-09T15:00:00Z");
        using var cancellation = new CancellationTokenSource();
        using var limiter = new SessionCatalogReadLimiter(8);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new CodexSessionCatalogSource(fixture.CodexPaths,
                new CancelingCatalogIo(new SystemSessionCatalogIo(), cancellation))
                .ScanAsync(limiter, cancellation.Token));
    }

    [Fact]
    public async Task CodexSourceSkipsExcludedDirectoryAndPreservesMetadataTimestamp()
    {
        // Including excluded files or falling back to file time would respectively expose this row or lose its known timestamp.
        await using var fixture = new CatalogFixture();
        var allowed = await fixture.WriteCodexAsync("timestamp-source", null, "question", "2026-08-09T15:00:00Z");
        var logs = Path.Combine(fixture.CodexPaths.Sessions, "logs");
        Directory.CreateDirectory(logs);
        File.Copy(allowed, Path.Combine(logs, "rollout-excluded.jsonl"));

        using var limiter = new SessionCatalogReadLimiter(8);
        var row = Assert.Single(await new CodexSessionCatalogSource(fixture.CodexPaths, new SystemSessionCatalogIo())
            .ScanAsync(limiter, CancellationToken.None));

        Assert.Equal("timestamp-source", row.SessionId);
        Assert.Equal(DateTimeOffset.Parse("2026-08-09T15:00:00Z"), row.LastModifiedAt);
    }

    [Fact]
    public async Task CodexSourceDoesNotFollowReparseFileTargets()
    {
        // Accepting a link beneath the root would let metadata outside the managed tree enter the catalog.
        await using var fixture = new CatalogFixture();
        var outside = await fixture.WriteCodexAsync("outside-source", "Outside", "question", "2026-08-09T15:00:00Z");
        var link = Path.Combine(fixture.CodexPaths.Sessions, "linked-source.jsonl");
        try
        {
            File.CreateSymbolicLink(link, outside);
            fixture.ReparsePaths.Add(link);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw Xunit.Sdk.SkipException.ForSkip($"Symbolic-link creation is unavailable: {exception.GetType().Name}");
        }

        using var limiter = new SessionCatalogReadLimiter(8);
        var rows = await new CodexSessionCatalogSource(fixture.CodexPaths, new SystemSessionCatalogIo())
            .ScanAsync(limiter, CancellationToken.None);

        Assert.DoesNotContain(rows, row => string.Equals(row.NativePath, Path.GetFullPath(link), StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("<environment_context>")]
    [InlineData("<recommended_plugins>")]
    [InlineData("<user_info>")]
    [InlineData("<system-reminder>")]
    [InlineData("<permissions instructions>")]
    [InlineData("<skills_instructions>")]
    [InlineData("<apps_instructions>")]
    [InlineData("<plugins_instructions>")]
    public async Task ScanAsyncSkipsTechnicalOpeningUserMessages(string openingTag)
    {
        // Returning the first supported user preview would expose the injected wrapper instead of the user request.
        await using var fixture = new CatalogFixture();
        await fixture.WriteCodexUsersAsync("technical-codex", null,
            $" \t{openingTag} injected context", "Real user request");
        await fixture.WriteGrokUsersAsync("57000000-0000-0000-0000-000000000007", null,
            $" \t{openingTag} injected context", "Real user request");

        var snapshot = await fixture.CreateCatalog().ScanAsync(CancellationToken.None);

        Assert.Equal("Real user request", Assert.Single(snapshot.Codex).Title);
        Assert.Equal("Real user request", Assert.Single(snapshot.Grok).Title);
    }

    [Fact]
    public async Task ScanAsyncKeepsOrdinaryTextContainingTag()
    {
        // Treating a tag anywhere in the text as technical would hide this ordinary user request.
        await using var fixture = new CatalogFixture();
        const string request = "Explain this later <environment_context> tag";
        await fixture.WriteCodexUsersAsync("ordinary-codex", null, request);
        await fixture.WriteGrokUsersAsync("58000000-0000-0000-0000-000000000008", null, request);

        var snapshot = await fixture.CreateCatalog().ScanAsync(CancellationToken.None);

        Assert.Equal(request, Assert.Single(snapshot.Codex).Title);
        Assert.Equal(request, Assert.Single(snapshot.Grok).Title);
    }

    [Fact]
    public async Task ScanAsyncUsesIdWhenOnlyTechnicalUserMessages()
    {
        // Returning a technical fallback instead of no preview would prevent the safe session ID last resort.
        await using var fixture = new CatalogFixture();
        const string codexId = "technical-only-codex";
        const string grokId = "59000000-0000-0000-0000-000000000009";
        await fixture.WriteCodexUsersAsync(codexId, null, "<environment_context>context", "<apps_instructions>apps");
        await fixture.WriteGrokUsersAsync(grokId, null, "<environment_context>context", "<apps_instructions>apps");

        var snapshot = await fixture.CreateCatalog().ScanAsync(CancellationToken.None);

        Assert.Equal(codexId, Assert.Single(snapshot.Codex).Title);
        Assert.Equal(grokId, Assert.Single(snapshot.Grok).Title);
    }

    [Fact]
    public async Task ScanAsyncUsesRootGrokTitleWhenHigherPriorityTitlesAreAbsent()
    {
        // Removing root-title fallback would expose the user-preview title instead.
        await using var fixture = new CatalogFixture();
        const string id = "60000000-0000-0000-0000-000000000010";
        await fixture.WriteGrokSummaryAsync(id, null, null, null, "Root-only Grok title", "fallback request");

        var session = Assert.Single((await fixture.CreateCatalog().ScanAsync(CancellationToken.None)).Grok);

        Assert.Equal("Root-only Grok title", session.Title);
    }

    [Fact]
    public async Task ScanAsyncUsesCodexIndexThreadNameBeforeMetadataAndHistory()
    {
        // Replacing the official index lookup with metadata precedence must restore "Metadata title".
        await using var fixture = new CatalogFixture();
        await fixture.WriteCodexAsync("indexed", "Metadata title",
            "<environment_context>technical</environment_context>", "2026-08-09T12:00:00Z");
        await fixture.WriteCodexIndexAsync(
            new { id = "indexed", thread_name = "Official Codex name" });

        var session = Assert.Single((await fixture.CreateCatalog().ScanAsync(CancellationToken.None)).Codex);

        Assert.Equal("Official Codex name", session.Title);
    }

    [Fact]
    public async Task ScanAsyncUsesLastCodexIndexEntryForDuplicateId()
    {
        // Keeping the first matching index entry must return "Old name" instead.
        await using var fixture = new CatalogFixture();
        await fixture.WriteCodexAsync("duplicate", null, "fallback", "2026-08-09T12:00:00Z");
        await fixture.WriteCodexIndexAsync(
            new { id = "duplicate", thread_name = "Old name" },
            new { id = "duplicate", thread_name = "Newest name" });

        var session = Assert.Single((await fixture.CreateCatalog().ScanAsync(CancellationToken.None)).Codex);

        Assert.Equal("Newest name", session.Title);
    }

    [Fact]
    public async Task ScanAsyncIgnoresNonObjectCodexIndexLines()
    {
        // Treating a valid non-object JSON value as an object currently aborts scanning instead of reaching this record.
        await using var fixture = new CatalogFixture();
        await fixture.WriteCodexAsync("non-object-index", "Metadata title", "fallback", "2026-08-09T12:00:00Z");
        await fixture.WriteCodexIndexTextAsync("null\n[]\n\"ignored\"\n" +
            JsonSerializer.Serialize(new { id = "non-object-index", thread_name = "Official title" }) + "\n");

        var session = Assert.Single((await fixture.CreateCatalog().ScanAsync(CancellationToken.None)).Codex);

        Assert.Equal("Official title", session.Title);
    }

    [Fact]
    public async Task ScanAsyncUsesOnlyLast64CompleteCodexIndexRecords()
    {
        // Removing the final-record cap would let this old same-session title override metadata.
        await using var fixture = new CatalogFixture();
        await fixture.WriteCodexAsync("capped-index", "Metadata title", "fallback", "2026-08-09T12:00:00Z");
        var records = new[] { JsonSerializer.Serialize(new { id = "capped-index", thread_name = "Dropped old title" }) }
            .Concat(Enumerable.Range(0, 64)
                .Select(index => JsonSerializer.Serialize(new { id = $"noise-{index}", thread_name = "noise" })));
        await fixture.WriteCodexIndexTextAsync(string.Join("\n", records) + "\n");

        var session = Assert.Single((await fixture.CreateCatalog().ScanAsync(CancellationToken.None)).Codex);

        Assert.Equal("Metadata title", session.Title);
    }

    [Fact]
    public async Task ScanAsyncDiscardsCompleteCodexIndexLineAtRetainedTailBoundary()
    {
        // Removing the initial-tail-line discard would make this boundary record override metadata.
        await using var fixture = new CatalogFixture();
        await fixture.WriteCodexAsync("boundary-index", "Metadata title", "fallback", "2026-08-09T12:00:00Z");
        var boundaryRecord = JsonSerializer.Serialize(new { id = "boundary-index", thread_name = "Boundary title" }) + "\n";
        var retainedFillerLength = 64 * 1024 - Encoding.UTF8.GetByteCount(boundaryRecord);
        var indexText = new string('x', 70 * 1024) + boundaryRecord + new string('y', retainedFillerLength);
        await fixture.WriteCodexIndexTextAsync(indexText);

        var session = Assert.Single((await fixture.CreateCatalog().ScanAsync(CancellationToken.None)).Codex);

        Assert.Equal("Metadata title", session.Title);
    }

    [Fact]
    public async Task ScanAsyncReadsBoundedCodexIndexTailAndIgnoresMalformedLines()
    {
        // Reading from byte zero would incorrectly load "Old index name"; parsing the partial first tail line would throw.
        await using var fixture = new CatalogFixture();
        await fixture.WriteCodexAsync("old-index", "Old metadata", "old fallback", "2026-08-09T11:00:00Z");
        await fixture.WriteCodexAsync("retained-index", "Retained metadata", "retained fallback", "2026-08-09T12:00:00Z");

        var oldRecord = JsonSerializer.Serialize(new
        {
            id = "old-index", thread_name = "Old index name", padding = new string('x', 70 * 1024)
        });
        var retainedRecords = Enumerable.Range(0, 65)
            .Select(index => JsonSerializer.Serialize(new { id = $"noise-{index}", thread_name = "noise" }));
        var indexText = oldRecord + "\n" + string.Join("\n", retainedRecords) +
                        "\n{malformed-json}\n" +
                        JsonSerializer.Serialize(new { id = "retained-index", thread_name = "Old retained name" }) + "\n" +
                        JsonSerializer.Serialize(new { id = "retained-index", thread_name = "Final retained name" }) + "\n";
        await fixture.WriteCodexIndexTextAsync(indexText);

        var sessions = await fixture.CreateCatalog().ScanAsync(CancellationToken.None);

        Assert.Equal("Old metadata", Assert.Single(sessions.Codex, session => session.SessionId == "old-index").Title);
        Assert.Equal("Final retained name",
            Assert.Single(sessions.Codex, session => session.SessionId == "retained-index").Title);
    }

    [Fact]
    public async Task ScanAsyncReadsNativeGrokTextBlockAndNormalizesWhitespace()
    {
        await using var fixture = new CatalogFixture();
        const string id = "53000000-0000-0000-0000-000000000003";
        var session = fixture.GrokPaths.SessionDirectory(fixture.WorkingDirectory, id);
        Directory.CreateDirectory(session);
        await File.WriteAllTextAsync(Path.Combine(session, "chat_history.jsonl"),
            JsonSerializer.Serialize(new
            {
                type = "user",
                content = new[] { new { type = "text", text = "  First\r\n\t  Grok   question  " } }
            }) + "\n", new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(session, "summary.json"),
            JsonSerializer.Serialize(new
            {
                info = new { id, cwd = fixture.WorkingDirectory, title = (string?)null,
                    updated_at = "2026-08-09T13:00:00Z" }
            }), new UTF8Encoding(false));

        var snapshot = await fixture.CreateCatalog().ScanAsync(CancellationToken.None);

        Assert.Equal("First Grok question", Assert.Single(snapshot.Grok).Title);
    }

    [Fact]
    public async Task ScanAsyncUsesGrokGeneratedTitleBeforeOtherSources()
    {
        // Removing generated_title precedence would expose the legacy info.title instead.
        await using var fixture = new CatalogFixture();
        const string id = "54000000-0000-0000-0000-000000000004";
        await fixture.WriteGrokSummaryAsync(id, "Official Grok name", "Summary name",
            "Legacy info", "Legacy root", "<user_info>technical</user_info>");

        var session = Assert.Single((await fixture.CreateCatalog().ScanAsync(CancellationToken.None)).Grok);

        Assert.Equal("Official Grok name", session.Title);
    }

    [Fact]
    public async Task ScanAsyncUsesStringGrokSessionSummaryWhenGeneratedTitleIsAbsent()
    {
        // Ignoring the string summary would fall through to the legacy info.title.
        await using var fixture = new CatalogFixture();
        const string id = "55000000-0000-0000-0000-000000000005";
        await fixture.WriteGrokSummaryAsync(id, null, "Official summary",
            "Legacy info", "Legacy root", "fallback");

        var session = Assert.Single((await fixture.CreateCatalog().ScanAsync(CancellationToken.None)).Grok);

        Assert.Equal("Official summary", session.Title);
    }

    [Fact]
    public async Task ScanAsyncIgnoresNonStringGrokSessionSummaryBeforeInfoTitle()
    {
        // Accepting JSON objects as titles would prevent the legacy string title from being shown.
        await using var fixture = new CatalogFixture();
        const string id = "56000000-0000-0000-0000-000000000006";
        await fixture.WriteGrokSummaryAsync(id, null, new { title = "Object summary" },
            "Legacy info", "Legacy root", "fallback");

        var session = Assert.Single((await fixture.CreateCatalog().ScanAsync(CancellationToken.None)).Grok);

        Assert.Equal("Legacy info", session.Title);
    }

    [Fact]
    public async Task ScanAsyncNormalizesExplicitTitleAndFallsBackWhenItIsOnlyWhitespace()
    {
        await using var fixture = new CatalogFixture();
        await fixture.WriteCodexAsync("normalized", "  Multi\r\n\t line   title  ", "question",
            "2026-08-09T12:00:00Z");
        await fixture.WriteCodexAsync("fallback", " \r\n\t ", "", "2026-08-09T11:00:00Z");

        var snapshot = await fixture.CreateCatalog().ScanAsync(CancellationToken.None);

        Assert.Equal("Multi line title", snapshot.Codex[0].Title);
        Assert.Equal("fallback", snapshot.Codex[1].Title);
    }

    [Fact]
    public async Task ScanAsyncOrdersEachAgentByDescendingModifiedTimeAndExtractsTitles()
    {
        await using var fixture = new CatalogFixture();
        var olderCodex = await fixture.WriteCodexAsync(
            "older-codex", "Explicit Codex title", "older question", "2026-08-09T10:00:00Z");
        var newerCodex = await fixture.WriteCodexAsync(
            "newer-codex", null, "Fallback Codex question", "2026-08-09T12:00:00Z");
        var olderGrok = await fixture.WriteGrokAsync(
            "10000000-0000-0000-0000-000000000001", "Explicit Grok title", "older Grok question",
            "2026-08-09T09:00:00Z");
        var newerGrok = await fixture.WriteGrokAsync(
            "20000000-0000-0000-0000-000000000002", null, "Fallback Grok question",
            "2026-08-09T13:00:00Z");

        var snapshot = await fixture.CreateCatalog().ScanAsync(CancellationToken.None);

        Assert.Collection(snapshot.Codex,
            session => Assert.Equal(
                ("newer-codex", "Fallback Codex question", Path.GetFullPath(newerCodex)),
                (session.SessionId, session.Title, session.NativePath)),
            session => Assert.Equal(
                ("older-codex", "Explicit Codex title", Path.GetFullPath(olderCodex)),
                (session.SessionId, session.Title, session.NativePath)));
        Assert.Collection(snapshot.Grok,
            session => Assert.Equal(
                ("20000000-0000-0000-0000-000000000002", "Fallback Grok question", Path.GetFullPath(newerGrok)),
                (session.SessionId, session.Title, session.NativePath)),
            session => Assert.Equal(
                ("10000000-0000-0000-0000-000000000001", "Explicit Grok title", Path.GetFullPath(olderGrok)),
                (session.SessionId, session.Title, session.NativePath)));
        Assert.True(snapshot.Codex[0].LastModifiedAt > snapshot.Codex[1].LastModifiedAt);
        Assert.True(snapshot.Grok[0].LastModifiedAt > snapshot.Grok[1].LastModifiedAt);
        Assert.All(snapshot.Codex.Concat(snapshot.Grok), session => Assert.True(session.CanRead));
    }

    [Fact]
    public async Task ScanAsyncChecksActivityOncePerAgent()
    {
        // Per-row process queries make startup cost grow with the number of displayed sessions.
        await using var fixture = new CatalogFixture();
        await fixture.WriteCodexAsync("activity-codex-one", null, "one", "2026-08-09T10:00:00Z");
        await fixture.WriteCodexAsync("activity-codex-two", null, "two", "2026-08-09T11:00:00Z");
        await fixture.WriteGrokAsync(
            "51000000-0000-0000-0000-000000000001", null, "one", "2026-08-09T12:00:00Z");
        await fixture.WriteGrokAsync(
            "52000000-0000-0000-0000-000000000002", null, "two", "2026-08-09T13:00:00Z");

        var snapshot = await fixture.CreateCatalog().ScanAsync(CancellationToken.None);

        Assert.Equal(2, snapshot.Codex.Count);
        Assert.Equal(2, snapshot.Grok.Count);
        Assert.Equal(1, fixture.ActiveState.TotalQueries[ManagedAgent.Codex]);
        Assert.Equal(1, fixture.ActiveState.TotalQueries[ManagedAgent.Grok]);
    }

    [Fact]
    public async Task ScanAsyncTreatsActivityQueryFailureAsActive()
    {
        // Losing fail-closed handling would expose copy/delete actions while process state is unknown.
        await using var fixture = new CatalogFixture();
        await fixture.WriteCodexAsync("activity-failure", null, "question", "2026-08-09T10:00:00Z");
        fixture.ActiveState.AgentFailure = new InvalidOperationException("process lookup failed");

        var snapshot = await fixture.CreateCatalog().ScanAsync(CancellationToken.None);

        Assert.True(Assert.Single(snapshot.Codex).IsActive);
    }

    [Fact]
    public async Task ScanAsyncPropagatesRequestedCancellationFromActivityQuery()
    {
        // Treating requested cancellation as an ordinary lookup failure would leave refresh unresponsive.
        await using var fixture = new CatalogFixture();
        await fixture.WriteCodexAsync("activity-cancel", null, "question", "2026-08-09T10:00:00Z");
        using var cancellation = new CancellationTokenSource();
        fixture.ActiveState.BeforeAgentQuery = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.CreateCatalog().ScanAsync(cancellation.Token));
    }

    [Fact]
    public async Task ScanAsyncKeepsActiveEntriesVisibleWhenNativeScannerDefersThem()
    {
        await using var fixture = new CatalogFixture();
        var sessionId = "30000000-0000-0000-0000-000000000003";
        await fixture.WriteGrokAsync(sessionId, "Active Grok", "question", "2026-08-09T14:00:00Z");
        await File.WriteAllTextAsync(
            Path.Combine(fixture.GrokHome, "active_sessions.json"),
            JsonSerializer.Serialize(new[] { new { session_id = sessionId } }),
            new UTF8Encoding(false));
        fixture.ActiveState.ActiveIds.Add(sessionId);

        var snapshot = await fixture.CreateCatalog().ScanAsync(CancellationToken.None);

        var session = Assert.Single(snapshot.Grok);
        Assert.Equal(sessionId, session.SessionId);
        Assert.True(session.IsActive);
        Assert.True(session.CanRead);
    }

    [Fact]
    public async Task ScanAsyncKeepsActiveSafelyIdentifiableGrokDirectoryVisibleWhenChatIsMissing()
    {
        await using var fixture = new CatalogFixture();
        var sessionId = "31000000-0000-0000-0000-000000000003";
        var sessionPath = await fixture.WriteGrokSummaryOnlyAsync(sessionId, "Missing chat");
        fixture.ActiveState.ActiveIds.Add(sessionId);

        var snapshot = await fixture.CreateCatalog().ScanAsync(CancellationToken.None);

        var session = Assert.Single(snapshot.Grok);
        Assert.Equal(sessionId, session.SessionId);
        Assert.Equal(Path.GetFullPath(sessionPath), session.NativePath);
        Assert.Equal("Missing chat", session.Title);
        Assert.True(session.IsActive);
        Assert.False(session.CanRead);
    }

    [Fact]
    public async Task ScanAsyncKeepsSafelyIdentifiableMalformedEntriesAsUnreadable()
    {
        await using var fixture = new CatalogFixture();
        var codexPath = await fixture.WriteMalformedCodexAsync("malformed-codex");
        var grokId = "40000000-0000-0000-0000-000000000004";
        var grokPath = await fixture.WriteMalformedGrokAsync(grokId);

        var snapshot = await fixture.CreateCatalog().ScanAsync(CancellationToken.None);

        var codex = Assert.Single(snapshot.Codex);
        Assert.Equal(("malformed-codex", Path.GetFullPath(codexPath), "malformed-codex", false),
            (codex.SessionId, codex.NativePath, codex.Title, codex.CanRead));
        var grok = Assert.Single(snapshot.Grok);
        Assert.Equal((grokId, Path.GetFullPath(grokPath), grokId, false),
            (grok.SessionId, grok.NativePath, grok.Title, grok.CanRead));
    }

    [Fact]
    public async Task ScanAsyncReturnsEmptyColumnsWhenNativeRootsAreAbsent()
    {
        await using var fixture = new CatalogFixture(createNativeRoots: false);

        var snapshot = await fixture.CreateCatalog().ScanAsync(CancellationToken.None);

        Assert.Empty(snapshot.Codex);
        Assert.Empty(snapshot.Grok);
    }

    [Fact]
    public async Task ScanAsyncDoesNotSelectCodexCandidatesExcludedByNativeScannerRules()
    {
        await using var fixture = new CatalogFixture();
        var value = nameof(ScanAsyncDoesNotSelectCodexCandidatesExcludedByNativeScannerRules);
        var original = await fixture.WriteCodexAsync(value, value, value, DateTimeOffset.UtcNow.ToString());
        var disallowedDirectory = Path.Combine(
            fixture.CodexPaths.Sessions,
            new string(['l', 'o', 'g', 's']));
        Directory.CreateDirectory(disallowedDirectory);
        File.Move(original, Path.Combine(disallowedDirectory, Path.GetFileName(original)));

        var snapshot = await fixture.CreateCatalog().ScanAsync(CancellationToken.None);

        Assert.Empty(snapshot.Codex);
    }

    [Fact]
    public async Task ScanAsyncUsesBoundedMetadataWithoutInvokingFullConversationReaderForLargeTail()
    {
        await using var fixture = new CatalogFixture();
        var path = await fixture.WriteCodexAsync(
            "bounded-codex", "Bounded title", "question", "2026-08-09T15:00:00Z");
        var largeTail = "{\"type\":\"event_msg\",\"payload\":{\"ignored\":\"" +
                        new string('x', 2 * 1024 * 1024);
        await File.AppendAllTextAsync(path, largeTail + "\n", new UTF8Encoding(false));

        var snapshot = await fixture.CreateCatalog().ScanAsync(CancellationToken.None);

        var session = Assert.Single(snapshot.Codex);
        Assert.Equal("Bounded title", session.Title);
        Assert.True(new FileInfo(path).Length > 2 * 1024 * 1024);
    }

    [Fact]
    public async Task ScanAsyncDoesNotFollowReparseCandidatesOutsideTheNativeRoot()
    {
        await using var fixture = new CatalogFixture();
        var outside = Directory.CreateDirectory(Path.Combine(fixture.Root, "outside-sessions"));
        var outsideId = "50000000-0000-0000-0000-000000000005";
        var outsideSession = Path.Combine(outside.FullName, outsideId);
        Directory.CreateDirectory(outsideSession);
        await CatalogFixture.WriteGrokFilesAsync(
            outsideSession, outsideId, fixture.WorkingDirectory, "Outside", "outside question",
            "2026-08-09T15:00:00Z");
        var link = Path.Combine(fixture.GrokPaths.Sessions, "linked-outside");
        try
        {
            Directory.CreateSymbolicLink(link, outside.FullName);
            fixture.ReparsePaths.Add(link);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw Xunit.Sdk.SkipException.ForSkip(
                $"Symbolic-link creation is unavailable: {exception.GetType().Name}");
        }

        var snapshot = await fixture.CreateCatalog().ScanAsync(CancellationToken.None);

        Assert.DoesNotContain(snapshot.Grok, session => session.SessionId == outsideId);
    }

    private sealed class CatalogFixture : IAsyncDisposable
    {
        private static readonly UTF8Encoding Utf8 = new(false);
        private readonly string container;

        public CatalogFixture(bool createNativeRoots = true)
        {
            container = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "codex-history-sync-task3-tests"));
            Directory.CreateDirectory(container);
            Root = Path.Combine(container, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            CodexHome = Path.Combine(Root, "codex-home");
            GrokHome = Path.Combine(Root, "grok-home");
            WorkingDirectory = Path.Combine(Root, "working-directory");
            Directory.CreateDirectory(CodexHome);
            Directory.CreateDirectory(GrokHome);
            Directory.CreateDirectory(WorkingDirectory);
            CodexPaths = CodexPaths.ResolveLayout(CodexHome);
            GrokPaths = new GrokPaths(GrokHome, Path.Combine(GrokHome, "sessions"));
            if (createNativeRoots)
            {
                Directory.CreateDirectory(CodexPaths.Sessions);
                Directory.CreateDirectory(CodexPaths.ArchivedSessions);
                Directory.CreateDirectory(GrokPaths.Sessions);
            }
        }

        public string Root { get; }
        public string CodexHome { get; }
        public string GrokHome { get; }
        public string WorkingDirectory { get; }
        public CodexPaths CodexPaths { get; }
        public GrokPaths GrokPaths { get; }
        public FakeActiveState ActiveState { get; } = new();
        public List<string> ReparsePaths { get; } = [];

        public LocalSessionCatalog CreateCatalog() => new(
            CodexPaths,
            GrokPaths,
            ActiveState,
            new SessionScanner(TimeSpan.Zero),
            new GrokSessionScanner(TimeSpan.Zero));

        public async Task<string> WriteCodexAsync(string id, string? title, string userText, string modifiedAt)
        {
            var directory = Path.Combine(CodexPaths.Sessions, "2026", "08", "09");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"rollout-{id}.jsonl");
            var metadata = new
            {
                type = "session_meta",
                payload = new { id, timestamp = "2026-08-09T08:00:00Z", cwd = WorkingDirectory, title }
            };
            var message = new
            {
                type = "response_item",
                payload = new
                {
                    type = "message", role = "user", timestamp = modifiedAt,
                    content = new[] { new { type = "input_text", text = userText } }
                }
            };
            await File.WriteAllTextAsync(path,
                JsonSerializer.Serialize(metadata) + "\n" + JsonSerializer.Serialize(message) + "\n", Utf8);
            return path;
        }

        public async Task WriteCodexUsersAsync(string id, string? title, params string[] userTexts)
        {
            var directory = Path.Combine(CodexPaths.Sessions, "2026", "08", "09");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"rollout-{id}.jsonl");
            var records = new List<string>
            {
                JsonSerializer.Serialize(new
                {
                    type = "session_meta",
                    payload = new { id, timestamp = "2026-08-09T08:00:00Z", cwd = WorkingDirectory, title }
                })
            };
            records.AddRange(userTexts.Select((text, index) => JsonSerializer.Serialize(new
            {
                type = "response_item",
                payload = new
                {
                    type = "message", role = "user", timestamp = $"2026-08-09T12:{index:D2}:00Z",
                    content = new[] { new { type = "input_text", text } }
                }
            })));
            await File.WriteAllTextAsync(path, string.Join("\n", records) + "\n", Utf8);
        }

        public Task WriteCodexIndexAsync(params object[] records) =>
            WriteCodexIndexTextAsync(string.Join("\n", records.Select(record => JsonSerializer.Serialize(record))) + "\n");

        public Task WriteCodexIndexTextAsync(string text) =>
            File.WriteAllTextAsync(Path.Combine(CodexHome, "session_index.jsonl"), text, Utf8);

        public async Task<string> WriteGrokAsync(
            string id,
            string? title,
            string userText,
            string modifiedAt)
        {
            var session = GrokPaths.SessionDirectory(WorkingDirectory, id);
            Directory.CreateDirectory(session);
            await WriteGrokFilesAsync(session, id, WorkingDirectory, title, userText, modifiedAt);
            return session;
        }

        public async Task WriteGrokUsersAsync(string id, string? title, params string[] userTexts)
        {
            var session = GrokPaths.SessionDirectory(WorkingDirectory, id);
            Directory.CreateDirectory(session);
            var records = userTexts.Select(text => JsonSerializer.Serialize(new
            {
                role = "user",
                content = new[] { new { type = "input_text", text } }
            }));
            await File.WriteAllTextAsync(Path.Combine(session, "chat_history.jsonl"), string.Join("\n", records) + "\n", Utf8);
            await File.WriteAllTextAsync(Path.Combine(session, "summary.json"), JsonSerializer.Serialize(new
            {
                info = new
                {
                    id, cwd = WorkingDirectory, title,
                    created_at = "2026-08-09T08:00:00Z", updated_at = "2026-08-09T16:00:00Z"
                }
            }), Utf8);
        }

        public async Task WriteGrokSummaryAsync(
            string id,
            string? generatedTitle,
            object? sessionSummary,
            string? infoTitle,
            string? rootTitle,
            string userText)
        {
            var session = GrokPaths.SessionDirectory(WorkingDirectory, id);
            Directory.CreateDirectory(session);
            await File.WriteAllTextAsync(Path.Combine(session, "chat_history.jsonl"),
                JsonSerializer.Serialize(new
                {
                    role = "user",
                    content = new[] { new { type = "input_text", text = userText } }
                }) + "\n", Utf8);
            await File.WriteAllTextAsync(Path.Combine(session, "summary.json"),
                JsonSerializer.Serialize(new
                {
                    generated_title = generatedTitle,
                    session_summary = sessionSummary,
                    info = new
                    {
                        id, cwd = WorkingDirectory, title = infoTitle,
                        created_at = "2026-08-09T08:00:00Z", updated_at = "2026-08-09T16:00:00Z"
                    },
                    title = rootTitle
                }), Utf8);
        }

        public async Task<string> WriteMalformedCodexAsync(string id)
        {
            var directory = Path.Combine(CodexPaths.Sessions, "2026", "08", "09");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"rollout-{id}.jsonl");
            await File.WriteAllTextAsync(path,
                JsonSerializer.Serialize(new { type = "session_meta", payload = new { id } }) + "\n{bad-json}\n", Utf8);
            return path;
        }

        public async Task<string> WriteMalformedGrokAsync(string id)
        {
            var session = GrokPaths.SessionDirectory(WorkingDirectory, id);
            Directory.CreateDirectory(session);
            await File.WriteAllTextAsync(Path.Combine(session, "chat_history.jsonl"),
                "{\"role\":\"user\",\"content\":\"question\"}\n", Utf8);
            await File.WriteAllTextAsync(Path.Combine(session, "summary.json"), "{bad-json}", Utf8);
            return session;
        }

        public async Task<string> WriteGrokSummaryOnlyAsync(string id, string title)
        {
            var session = GrokPaths.SessionDirectory(WorkingDirectory, id);
            Directory.CreateDirectory(session);
            await File.WriteAllTextAsync(Path.Combine(session, "summary.json"),
                JsonSerializer.Serialize(new
                {
                    info = new
                    {
                        id, cwd = WorkingDirectory, title,
                        created_at = "2026-08-09T08:00:00Z", updated_at = "2026-08-09T16:00:00Z"
                    }
                }), Utf8);
            return session;
        }

        public static async Task WriteGrokFilesAsync(
            string session,
            string id,
            string cwd,
            string? title,
            string userText,
            string modifiedAt)
        {
            await File.WriteAllTextAsync(Path.Combine(session, "chat_history.jsonl"),
                JsonSerializer.Serialize(new
                {
                    role = "user",
                    content = new[] { new { type = "input_text", text = userText } }
                }) + "\n", Utf8);
            await File.WriteAllTextAsync(Path.Combine(session, "summary.json"),
                JsonSerializer.Serialize(new
                {
                    info = new
                    {
                        id, cwd, title, created_at = "2026-08-09T08:00:00Z", updated_at = modifiedAt
                    }
                }), Utf8);
        }

        public ValueTask DisposeAsync()
        {
            foreach (var link in ReparsePaths)
            {
                if ((Directory.Exists(link) || File.Exists(link)) &&
                    File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint))
                {
                    if (Directory.Exists(link)) Directory.Delete(link);
                    else File.Delete(link);
                }
            }

            var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Root));
            var expectedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(container));
            if (!string.Equals(Path.GetDirectoryName(canonicalRoot), expectedParent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing to clean up a test root outside its exact container.");
            if (Directory.Exists(canonicalRoot)) Directory.Delete(canonicalRoot, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeActiveState : IManagedSessionActiveState
    {
        public HashSet<string> ActiveIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<ManagedAgent, int> TotalQueries { get; } = new();
        public Exception? AgentFailure { get; set; }
        public Action? BeforeAgentQuery { get; set; }

        public Task<bool> IsAgentActiveAsync(ManagedAgent agent, CancellationToken cancellationToken)
        {
            BeforeAgentQuery?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            TotalQueries[agent] = TotalQueries.GetValueOrDefault(agent) + 1;
            if (AgentFailure is not null) throw AgentFailure;
            return Task.FromResult(ActiveIds.Count != 0);
        }

        public Task<bool> IsActiveAsync(
            ManagedAgent agent,
            string sessionId,
            string nativePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TotalQueries[agent] = TotalQueries.GetValueOrDefault(agent) + 1;
            return Task.FromResult(ActiveIds.Contains(sessionId));
        }
    }

    private sealed class RecordingCatalogIo(ISessionCatalogIo inner) : ISessionCatalogIo
    {
        private readonly Dictionary<string, int> enumerations = new(StringComparer.OrdinalIgnoreCase);

        public List<int> ReadBudgets { get; } = [];

        public IReadOnlyList<string> EnumerateFiles(string root, string pattern)
        {
            enumerations[root] = EnumerationCount(root) + 1;
            return inner.EnumerateFiles(root, pattern);
        }

        public IReadOnlyList<string> EnumerateDirectories(string root) => inner.EnumerateDirectories(root);
        public bool FileExists(string path) => inner.FileExists(path);
        public DateTimeOffset LastWriteTime(string path) => inner.LastWriteTime(path);

        public Task<BoundedTextRead> ReadPrefixAsync(string path, int maximumBytes, CancellationToken cancellationToken)
        {
            ReadBudgets.Add(maximumBytes);
            return inner.ReadPrefixAsync(path, maximumBytes, cancellationToken);
        }

        public Task<BoundedTextRead> ReadTailAsync(string path, int maximumBytes, CancellationToken cancellationToken)
        {
            ReadBudgets.Add(maximumBytes);
            return inner.ReadTailAsync(path, maximumBytes, cancellationToken);
        }

        public int EnumerationCount(string root) => enumerations.GetValueOrDefault(root);
    }

    private sealed class CancelingCatalogIo(ISessionCatalogIo inner, CancellationTokenSource cancellation) : ISessionCatalogIo
    {
        public IReadOnlyList<string> EnumerateFiles(string root, string pattern) => inner.EnumerateFiles(root, pattern);
        public IReadOnlyList<string> EnumerateDirectories(string root) => inner.EnumerateDirectories(root);
        public bool FileExists(string path) => inner.FileExists(path);
        public DateTimeOffset LastWriteTime(string path) => inner.LastWriteTime(path);

        public Task<BoundedTextRead> ReadPrefixAsync(string path, int maximumBytes, CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return inner.ReadPrefixAsync(path, maximumBytes, cancellationToken);
        }

        public Task<BoundedTextRead> ReadTailAsync(string path, int maximumBytes, CancellationToken cancellationToken) =>
            inner.ReadTailAsync(path, maximumBytes, cancellationToken);
    }

}
