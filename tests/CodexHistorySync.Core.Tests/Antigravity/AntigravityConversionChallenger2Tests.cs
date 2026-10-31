using CodexHistorySync.Core.Antigravity;
using CodexHistorySync.Core.Conversion;

namespace CodexHistorySync.Core.Tests.Antigravity;

public sealed class AntigravityConversionChallenger2Tests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _workingDir;
    private readonly AntigravityPaths _antigravityPaths;

    public AntigravityConversionChallenger2Tests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "agy-challenger2-" + Guid.NewGuid().ToString("N"));
        _workingDir = Path.Combine(_tempRoot, "workspace");
        Directory.CreateDirectory(_workingDir);
        var agyHome = Path.Combine(_tempRoot, "antigravity");
        Directory.CreateDirectory(agyHome);
        _antigravityPaths = new AntigravityPaths(agyHome);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public async Task WriteAsync_WithSourceIdCollision_RetriesAndSucceedsOnNextId()
    {
        var collidingId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        var secondId = Guid.NewGuid().ToString("D").ToLowerInvariant();

        var source = new PortableConversation(
            ConversationAgent.Antigravity,
            collidingId,
            "Collision Test",
            _workingDir,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow,
            [
                new PortableTurn(ConversationRole.User, "Hello!"),
                new PortableTurn(ConversationRole.Assistant, "Hi there!")
            ]);

        var callCount = 0;
        string Generator()
        {
            callCount++;
            return callCount == 1 ? collidingId : secondId;
        }

        var writer = new AntigravityConversationWriter(_antigravityPaths, idGenerator: Generator);
        var result = await writer.WriteAsync(source, CancellationToken.None);

        Assert.Equal(2, callCount);
        Assert.Equal(secondId, result.SessionId);
        Assert.True(File.Exists(_antigravityPaths.ConversationDatabasePath(secondId)));
        Assert.False(File.Exists(_antigravityPaths.ConversationDatabasePath(collidingId)));

        var summary = AntigravityDatabase.ReadSummary(_antigravityPaths.ConversationSummariesDbPath, secondId);
        Assert.NotNull(summary);
        Assert.Equal("Collision Test", summary.Title);
    }

    [Fact]
    public async Task WriteAsync_WithExistingDatabaseFileCollision_RetriesAndSucceedsOnNextId()
    {
        var existingId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        var freeId = Guid.NewGuid().ToString("D").ToLowerInvariant();

        // Pre-create existing conversation db
        Directory.CreateDirectory(_antigravityPaths.ConversationsDirectory);
        var existingDbPath = _antigravityPaths.ConversationDatabasePath(existingId);
        File.WriteAllText(existingDbPath, "existing database content");

        var source = new PortableConversation(
            ConversationAgent.Codex,
            "codex-session-id",
            "File Collision Test",
            _workingDir,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow,
            [
                new PortableTurn(ConversationRole.User, "User prompt"),
                new PortableTurn(ConversationRole.Assistant, "Model response")
            ]);

        var callCount = 0;
        string Generator()
        {
            callCount++;
            return callCount == 1 ? existingId : freeId;
        }

        var writer = new AntigravityConversationWriter(_antigravityPaths, idGenerator: Generator);
        var result = await writer.WriteAsync(source, CancellationToken.None);

        Assert.Equal(2, callCount);
        Assert.Equal(freeId, result.SessionId);
        Assert.True(File.Exists(_antigravityPaths.ConversationDatabasePath(freeId)));
        // Verify existing file was untouched
        Assert.Equal("existing database content", File.ReadAllText(existingDbPath));
    }

    [Fact]
    public async Task WriteAsync_With9ConsecutiveCollisions_SucceedsOn10thAttempt()
    {
        var collidingIds = Enumerable.Range(0, 9)
            .Select(_ => Guid.NewGuid().ToString("D").ToLowerInvariant())
            .ToList();
        var finalId = Guid.NewGuid().ToString("D").ToLowerInvariant();

        Directory.CreateDirectory(_antigravityPaths.ConversationsDirectory);
        foreach (var id in collidingIds)
        {
            File.WriteAllText(_antigravityPaths.ConversationDatabasePath(id), "pre-existing");
        }

        var source = new PortableConversation(
            ConversationAgent.Claude,
            "claude-session",
            "9 Collisions Test",
            _workingDir,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow,
            [
                new PortableTurn(ConversationRole.User, "Ping"),
                new PortableTurn(ConversationRole.Assistant, "Pong")
            ]);

        var callCount = 0;
        string Generator()
        {
            var idx = callCount++;
            return idx < 9 ? collidingIds[idx] : finalId;
        }

        var writer = new AntigravityConversationWriter(_antigravityPaths, idGenerator: Generator);
        var result = await writer.WriteAsync(source, CancellationToken.None);

        Assert.Equal(10, callCount);
        Assert.Equal(finalId, result.SessionId);
        Assert.True(File.Exists(_antigravityPaths.ConversationDatabasePath(finalId)));
    }

    [Fact]
    public async Task WriteAsync_With10ConsecutiveCollisions_ThrowsIOException()
    {
        var collidingId = Guid.NewGuid().ToString("D").ToLowerInvariant();
        Directory.CreateDirectory(_antigravityPaths.ConversationsDirectory);
        File.WriteAllText(_antigravityPaths.ConversationDatabasePath(collidingId), "collision");

        var source = new PortableConversation(
            ConversationAgent.Antigravity,
            "any-session",
            "10 Collisions Test",
            _workingDir,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow,
            [
                new PortableTurn(ConversationRole.User, "Ping"),
                new PortableTurn(ConversationRole.Assistant, "Pong")
            ]);

        var callCount = 0;
        string Generator()
        {
            callCount++;
            return collidingId;
        }

        var writer = new AntigravityConversationWriter(_antigravityPaths, idGenerator: Generator);
        var ex = await Assert.ThrowsAsync<IOException>(() => writer.WriteAsync(source, CancellationToken.None));

        Assert.Equal(10, callCount);
        Assert.Contains("Unable to allocate a unique Antigravity session ID after 10 attempts.", ex.Message);
    }

    [Fact]
    public async Task WriteAsync_WhenValidatorThrows_CleansUpAllStagedFilesAndSummary()
    {
        var targetId = Guid.NewGuid().ToString("D").ToLowerInvariant();

        var source = new PortableConversation(
            ConversationAgent.Codex,
            "codex-session",
            "Cleanup Test",
            _workingDir,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow,
            [
                new PortableTurn(ConversationRole.User, "Calculate 2+2"),
                new PortableTurn(ConversationRole.Assistant, "4")
            ]);

        var throwingValidator = new ThrowingConversationReader(new InvalidOperationException("Simulated validator crash"));
        var writer = new AntigravityConversationWriter(_antigravityPaths, idGenerator: () => targetId, validator: throwingValidator, utcNow: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteAsync(source, CancellationToken.None));

        // Verify that all artifacts were cleaned up
        var convDb = _antigravityPaths.ConversationDatabasePath(targetId);
        Assert.False(File.Exists(convDb), "Conversation database must be deleted on failure.");
        Assert.False(File.Exists(convDb + "-wal"), "WAL file must be deleted on failure.");
        Assert.False(File.Exists(convDb + "-shm"), "SHM file must be deleted on failure.");

        var brainDir = _antigravityPaths.BrainSessionDirectory(targetId);
        Assert.False(Directory.Exists(brainDir), "Brain session directory must be deleted on failure.");

        var anchorPath = _antigravityPaths.AnchorPath(targetId);
        Assert.False(File.Exists(anchorPath), "Anchor file must be deleted on failure.");

        var summary = AntigravityDatabase.ReadSummary(_antigravityPaths.ConversationSummariesDbPath, targetId);
        Assert.Null(summary);
    }

    [Fact]
    public async Task WriteAsync_WhenValidationMismatches_ThrowsInvalidDataExceptionAndCleansUp()
    {
        var targetId = Guid.NewGuid().ToString("D").ToLowerInvariant();

        var source = new PortableConversation(
            ConversationAgent.Grok,
            "grok-session",
            "Mismatch Test",
            _workingDir,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow,
            [
                new PortableTurn(ConversationRole.User, "What is AI?"),
                new PortableTurn(ConversationRole.Assistant, "Artificial Intelligence")
            ]);

        // Returns a conversation with corrupted turns to trigger validation failure
        var corruptedTurns = new List<PortableTurn>
        {
            new(ConversationRole.User, "Corrupted question"),
            new(ConversationRole.Assistant, "Corrupted answer")
        };
        var corruptValidator = new ReturningConversationReader(source with { SourceAgent = ConversationAgent.Antigravity, SourceSessionId = targetId, Turns = corruptedTurns });
        var writer = new AntigravityConversationWriter(_antigravityPaths, idGenerator: () => targetId, validator: corruptValidator, utcNow: null);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => writer.WriteAsync(source, CancellationToken.None));
        Assert.Contains("The staged Antigravity conversation failed validation.", ex.Message);

        // Verify cleanup
        Assert.False(File.Exists(_antigravityPaths.ConversationDatabasePath(targetId)));
        Assert.False(Directory.Exists(_antigravityPaths.BrainSessionDirectory(targetId)));
        Assert.False(File.Exists(_antigravityPaths.AnchorPath(targetId)));
        Assert.Null(AntigravityDatabase.ReadSummary(_antigravityPaths.ConversationSummariesDbPath, targetId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-guid")]
    [InlineData("../../path/traversal")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("{12345678-1234-1234-1234-123456789abc}")]
    [InlineData("~12345678-1234-1234-1234-123456789abc")]
    [InlineData("12345678-1234-1234-1234-123456789abc/sub")]
    public async Task WriteAsync_WithInvalidSessionId_ThrowsInvalidOperationException(string invalidId)
    {
        var source = new PortableConversation(
            ConversationAgent.Hermes,
            "hermes-1",
            "Invalid ID Test",
            _workingDir,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow,
            [
                new PortableTurn(ConversationRole.User, "Test")
            ]);

        var writer = new AntigravityConversationWriter(_antigravityPaths, idGenerator: () => invalidId);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteAsync(source, CancellationToken.None));

        Assert.Contains($"The generated session ID '{invalidId}' is invalid.", ex.Message);

        // Verify nothing was written
        if (Directory.Exists(_antigravityPaths.ConversationsDirectory))
        {
            Assert.Empty(Directory.GetFiles(_antigravityPaths.ConversationsDirectory));
        }
        if (Directory.Exists(_antigravityPaths.AnchorRoot))
        {
            Assert.Empty(Directory.GetFiles(_antigravityPaths.AnchorRoot));
        }
    }

    [Fact]
    public async Task WriteAsync_ConcurrentWriters_AllProduceConsistentSessionsWithoutCorruption()
    {
        const int concurrentCount = 10;
        var writer = new AntigravityConversationWriter(_antigravityPaths);

        var tasks = Enumerable.Range(0, concurrentCount).Select(async i =>
        {
            var conversation = new PortableConversation(
                ConversationAgent.Mimo,
                $"mimo-concurrent-{i}",
                $"Concurrent Session {i}",
                _workingDir,
                DateTimeOffset.UtcNow.AddMinutes(-i),
                DateTimeOffset.UtcNow,
                [
                    new PortableTurn(ConversationRole.User, $"Concurrent user message {i}"),
                    new PortableTurn(ConversationRole.Assistant, $"Concurrent assistant response {i}")
                ]);

            return await writer.WriteAsync(conversation, CancellationToken.None);
        }).ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.Equal(concurrentCount, results.Length);
        var sessionIds = results.Select(r => r.SessionId).ToHashSet();
        Assert.Equal(concurrentCount, sessionIds.Count);

        var reader = new AntigravityConversationReader();
        for (var i = 0; i < concurrentCount; i++)
        {
            var res = results[i];
            Assert.True(File.Exists(res.NativePath));
            Assert.True(File.Exists(_antigravityPaths.ConversationDatabasePath(res.SessionId)));

            var reread = await reader.ReadAsync(res.NativePath, CancellationToken.None);
            Assert.Equal($"Concurrent Session {i}", reread.Title);
            Assert.Equal(2, reread.Turns.Count);
            Assert.Equal($"Concurrent user message {i}", reread.Turns[0].Text);
            Assert.Equal($"Concurrent assistant response {i}", reread.Turns[1].Text);

            var summary = AntigravityDatabase.ReadSummary(_antigravityPaths.ConversationSummariesDbPath, res.SessionId);
            Assert.NotNull(summary);
            Assert.Equal($"Concurrent Session {i}", summary.Title);
        }
    }

    private sealed class ThrowingConversationReader : IConversationReader
    {
        private readonly Exception _exception;

        public ThrowingConversationReader(Exception exception)
        {
            _exception = exception;
        }

        public Task<PortableConversation> ReadAsync(string nativePath, CancellationToken cancellationToken)
        {
            throw _exception;
        }
    }

    private sealed class ReturningConversationReader : IConversationReader
    {
        private readonly PortableConversation _conversation;

        public ReturningConversationReader(PortableConversation conversation)
        {
            _conversation = conversation;
        }

        public Task<PortableConversation> ReadAsync(string nativePath, CancellationToken cancellationToken)
        {
            return Task.FromResult(_conversation);
        }
    }
}
