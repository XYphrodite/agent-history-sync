using CodexHistorySync.Core.Conversion;
using CodexHistorySync.Core.Grok;

namespace CodexHistorySync.Core.Tests.Conversion;

public sealed class GrokConversationWriterTests
{
    private static readonly Guid FirstId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SecondId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task WriteAsyncCreatesNewNativeSessionAndPreservesPortableConversation()
    {
        // Reusing the source ID, choosing the wrong cwd directory, or dropping metadata/turns must fail this test.
        await using var fixture = await GrokWriterFixture.CreateAsync();
        var conversation = fixture.Conversation(sourceSessionId: FirstId.ToString());
        var copyTime = new DateTimeOffset(2026, 8, 23, 17, 30, 0, TimeSpan.Zero);
        var writer = new GrokConversationWriter(fixture.Paths, () => SecondId, () => copyTime);

        var result = await writer.WriteAsync(conversation, CancellationToken.None);

        Assert.Equal(SecondId.ToString(), result.SessionId);
        Assert.NotEqual(conversation.SourceSessionId, result.SessionId);
        Assert.Equal(fixture.Paths.SessionDirectory(conversation.WorkingDirectory!, result.SessionId), result.NativePath);
        Assert.True(File.Exists(Path.Combine(result.NativePath, "chat_history.jsonl")));
        Assert.True(File.Exists(Path.Combine(result.NativePath, "summary.json")));
        Assert.True(File.Exists(Path.Combine(result.NativePath, "updates.jsonl")));
        Assert.True(File.Exists(Path.Combine(result.NativePath, "rewind_points.jsonl")));
        Assert.True(File.Exists(Path.Combine(result.NativePath, "signals.json")));
        Assert.True(File.Exists(Path.Combine(result.NativePath, "plan.json")));
        AssertPortableConversation(
            conversation with { LastModifiedAt = copyTime },
            await new GrokConversationReader().ReadAsync(result.NativePath, CancellationToken.None),
            result.SessionId);
        var chat = await File.ReadAllTextAsync(Path.Combine(result.NativePath, "chat_history.jsonl"));
        var summary = await File.ReadAllTextAsync(Path.Combine(result.NativePath, "summary.json"));
        Assert.Contains("\"type\":\"text\"", chat);
        Assert.DoesNotContain("input_text", chat);
        Assert.DoesNotContain("output_text", chat);
        Assert.Contains("\"generated_title\"", summary);
        Assert.Contains("\"current_model_id\"", summary);
        var updates = await File.ReadAllTextAsync(Path.Combine(result.NativePath, "updates.jsonl"));
        Assert.Contains("user_message_chunk", updates);
        Assert.Contains("agent_message_chunk", updates);
        Assert.Contains(result.SessionId, updates);
        AssertNoStaging(fixture.Paths.Sessions);
    }

    [Fact]
    public async Task WriteAsyncRejectsReparseSessionsRootWithoutWritingPlaintextOutsideTheNativeStore()
    {
        // Removing full-chain validation would let an existing sessions junction receive the staged and final session.
        await using var fixture = await GrokWriterFixture.CreateAsync();
        var outside = Path.Combine(fixture.Root, "outside-grok-sessions");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "existing.txt");
        await File.WriteAllTextAsync(sentinel, "existing");
        Directory.Delete(fixture.Paths.Sessions);
        ConversationWriterReparseTestSupport.CreateDirectoryReparsePoint(fixture.Paths.Sessions, outside);
        var expectedOutside = Path.Combine(
            outside,
            Path.GetRelativePath(
                fixture.Paths.Sessions,
                fixture.Paths.SessionDirectory(fixture.WorkingDirectory, FirstId.ToString())),
            "chat_history.jsonl");

        try
        {
            var error = await Record.ExceptionAsync(() =>
                new GrokConversationWriter(fixture.Paths, () => FirstId)
                    .WriteAsync(fixture.Conversation(), CancellationToken.None));

            Assert.False(File.Exists(expectedOutside));
            Assert.Equal([sentinel], Directory.EnumerateFiles(outside, "*", SearchOption.AllDirectories));
            Assert.Equal("existing", await File.ReadAllTextAsync(sentinel));
            Assert.IsType<InvalidDataException>(error);
        }
        finally
        {
            ConversationWriterReparseTestSupport.RemoveDirectoryReparsePoint(fixture.Paths.Sessions);
            Directory.CreateDirectory(fixture.Paths.Sessions);
        }
    }

    [Fact]
    public async Task WriteAsyncRejectsIntermediateDestinationJunctionWithoutWritingPlaintextOutsideTheNativeStore()
    {
        // Checking only the immediate staging parent misses a junction between the trusted home and sessions root.
        await using var fixture = await GrokWriterFixture.CreateAsync();
        var nativeParent = Path.Combine(fixture.Paths.Home, "managed");
        var sessions = Path.Combine(nativeParent, "sessions");
        var paths = new GrokPaths(fixture.Paths.Home, sessions);
        var outside = Path.Combine(fixture.Root, "outside-grok-native-parent");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "existing.txt");
        await File.WriteAllTextAsync(sentinel, "existing");
        ConversationWriterReparseTestSupport.CreateDirectoryReparsePoint(nativeParent, outside);
        var expectedOutside = Path.Combine(
            outside,
            "sessions",
            GrokPaths.EncodeCwdSegment(fixture.WorkingDirectory),
            FirstId.ToString(),
            "chat_history.jsonl");

        try
        {
            var error = await Record.ExceptionAsync(() =>
                new GrokConversationWriter(paths, () => FirstId)
                    .WriteAsync(fixture.Conversation(), CancellationToken.None));

            Assert.False(File.Exists(expectedOutside));
            Assert.Equal([sentinel], Directory.EnumerateFiles(outside, "*", SearchOption.AllDirectories));
            Assert.Equal("existing", await File.ReadAllTextAsync(sentinel));
            Assert.IsType<InvalidDataException>(error);
        }
        finally
        {
            ConversationWriterReparseTestSupport.RemoveDirectoryReparsePoint(nativeParent);
        }
    }

    [Fact]
    public async Task WriteAsyncRevalidatesDestinationAncestorsImmediatelyBeforePublication()
    {
        // Dropping the publication-time ancestor check would move validated plaintext through a newly introduced junction.
        await using var fixture = await GrokWriterFixture.CreateAsync();
        var existing = Path.Combine(fixture.Paths.Sessions, "existing.txt");
        await File.WriteAllTextAsync(existing, "existing");
        var publisher = new AncestorReplacingConversationPublisher(
            fixture.Paths.Sessions,
            Path.Combine(fixture.Root, "relocated-grok-sessions"));
        var independentStaging = Path.Combine(fixture.Root, "independent-grok-staging");
        Directory.CreateDirectory(independentStaging);
        var writer = new GrokConversationWriter(
            fixture.Paths,
            () => FirstId,
            new GrokConversationReader(),
            publisher,
            new IndependentConversationStagingDirectoryFactory(independentStaging));

        var error = await Record.ExceptionAsync(() =>
            writer.WriteAsync(fixture.Conversation(), CancellationToken.None));

        Assert.Null(publisher.PublishedPlaintext);
        Assert.IsType<InvalidDataException>(error);
        Assert.Equal("existing", await File.ReadAllTextAsync(existing));
        Assert.False(Directory.Exists(fixture.Paths.SessionDirectory(fixture.WorkingDirectory, FirstId.ToString())));
        AssertNoStaging(fixture.Paths.Sessions);
    }

    [Fact]
    public async Task WriteAsyncRetriesOccupiedGeneratedIdWithoutOverwritingIt()
    {
        // Publishing over an occupied generated ID would destroy an existing Grok session.
        await using var fixture = await GrokWriterFixture.CreateAsync();
        var occupied = fixture.Paths.SessionDirectory(fixture.WorkingDirectory, FirstId.ToString());
        Directory.CreateDirectory(occupied);
        var sentinel = Path.Combine(occupied, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "existing");
        var ids = new Queue<Guid>([FirstId, SecondId]);
        var writer = new GrokConversationWriter(fixture.Paths, ids.Dequeue);

        var result = await writer.WriteAsync(fixture.Conversation(), CancellationToken.None);

        Assert.Equal(SecondId.ToString(), result.SessionId);
        Assert.Equal("existing", await File.ReadAllTextAsync(sentinel));
        Assert.True(Directory.Exists(result.NativePath));
        AssertNoStaging(fixture.Paths.Sessions);
    }

    [Theory]
    [InlineData("11111111111111111111111111111111")]
    [InlineData("{11111111-1111-1111-1111-111111111111}")]
    public async Task WriteAsyncTreatsAlternateSourceUuidRepresentationsAsTheSameId(string sourceSessionId)
    {
        // String-only comparison would publish the source UUID again when its valid representation differs.
        await using var fixture = await GrokWriterFixture.CreateAsync();
        var ids = new Queue<Guid>([FirstId, SecondId]);
        var writer = new GrokConversationWriter(fixture.Paths, ids.Dequeue);

        var result = await writer.WriteAsync(fixture.Conversation(sourceSessionId), CancellationToken.None);

        Assert.Equal(SecondId.ToString(), result.SessionId);
        Assert.False(Directory.Exists(fixture.Paths.SessionDirectory(fixture.WorkingDirectory, FirstId.ToString())));
    }

    [Fact]
    public async Task WriteAsyncFailsAfterTenOccupiedIdsWithoutChangingExistingSessions()
    {
        // An unbounded retry or overwrite after repeated collisions would violate the fixed allocation boundary.
        await using var fixture = await GrokWriterFixture.CreateAsync();
        var occupied = fixture.Paths.SessionDirectory(fixture.WorkingDirectory, FirstId.ToString());
        Directory.CreateDirectory(occupied);
        var sentinel = Path.Combine(occupied, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "existing");
        var calls = 0;
        var writer = new GrokConversationWriter(fixture.Paths, () => { calls++; return FirstId; });

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            writer.WriteAsync(fixture.Conversation(), CancellationToken.None));

        Assert.Equal("Unable to allocate a unique Grok session ID after 10 attempts.", exception.Message);
        Assert.Equal(10, calls);
        Assert.Equal("existing", await File.ReadAllTextAsync(sentinel));
        AssertNoStaging(fixture.Paths.Sessions);
    }

    [Fact]
    public async Task WriteAsyncRetriesWhenFileOccupiesGeneratedDirectoryDuringPublication()
    {
        // A file racing into the UUID destination must be preserved while the writer consumes a new ID.
        await using var fixture = await GrokWriterFixture.CreateAsync();
        var ids = new Queue<Guid>([FirstId, SecondId]);
        var writer = new GrokConversationWriter(
            fixture.Paths,
            ids.Dequeue,
            new GrokConversationReader(),
            new RacingConversationPublisher());

        var result = await writer.WriteAsync(fixture.Conversation(), CancellationToken.None);

        var racedDestination = fixture.Paths.SessionDirectory(fixture.WorkingDirectory, FirstId.ToString());
        Assert.Equal(SecondId.ToString(), result.SessionId);
        Assert.Equal("existing", await File.ReadAllTextAsync(racedDestination));
        Assert.True(Directory.Exists(result.NativePath));
        AssertNoStaging(fixture.Paths.Sessions);
    }

    [Fact]
    public async Task WriteAsyncRemovesOwnedStagingWhenValidationFails()
    {
        // A failed destination-reader round trip must not leave publishable-looking temporary data behind.
        await using var fixture = await GrokWriterFixture.CreateAsync();
        var writer = new GrokConversationWriter(
            fixture.Paths,
            () => FirstId,
            new FailingConversationReader(),
            SystemConversationPublisher.Instance);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            writer.WriteAsync(fixture.Conversation(), CancellationToken.None));

        Assert.False(Directory.Exists(fixture.Paths.SessionDirectory(fixture.WorkingDirectory, FirstId.ToString())));
        AssertNoStaging(fixture.Paths.Sessions);
    }

    [Fact]
    public async Task WriteAsyncRemovesOwnedStagingWhenAtomicMoveFails()
    {
        // A publish failure must clean only the owned staging tree and leave no final session.
        await using var fixture = await GrokWriterFixture.CreateAsync();
        var writer = new GrokConversationWriter(
            fixture.Paths,
            () => FirstId,
            new GrokConversationReader(),
            new FailingConversationPublisher());

        await Assert.ThrowsAsync<IOException>(() =>
            writer.WriteAsync(fixture.Conversation(), CancellationToken.None));

        Assert.False(Directory.Exists(fixture.Paths.SessionDirectory(fixture.WorkingDirectory, FirstId.ToString())));
        AssertNoStaging(fixture.Paths.Sessions);
    }

    [Fact]
    public async Task WriteAsyncRefusesUnregisteredFileAddedImmediatelyBeforePublication()
    {
        // Moving a validated directory without rechecking its exact tree could publish injected files.
        await using var fixture = await GrokWriterFixture.CreateAsync();
        var publisher = new TamperingConversationPublisher();
        var writer = new GrokConversationWriter(
            fixture.Paths,
            () => FirstId,
            new GrokConversationReader(),
            publisher);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            writer.WriteAsync(fixture.Conversation(), CancellationToken.None));

        Assert.Equal("The staged conversation changed after validation.", exception.Message);
        Assert.False(Directory.Exists(fixture.Paths.SessionDirectory(fixture.WorkingDirectory, FirstId.ToString())));
        Assert.NotNull(publisher.InjectedPath);
        Assert.Equal("foreign", File.ReadAllText(publisher.InjectedPath));
    }

    [Fact]
    public async Task OwnedStagingRefusesUnexpectedEntryWithoutDeletingIt()
    {
        // Recursive cleanup without ownership validation could delete a foreign replacement tree.
        await using var fixture = await GrokWriterFixture.CreateAsync();
        var parent = Path.Combine(fixture.Paths.Sessions, "staging-parent");
        Directory.CreateDirectory(parent);
        var staging = SystemConversationStagingDirectoryFactory.Instance.Create(parent);
        var ownedFile = staging.FilePath("owned.txt");
        await File.WriteAllTextAsync(ownedFile, "owned");
        var foreignFile = Path.Combine(staging.RootPath, "foreign.txt");
        await File.WriteAllTextAsync(foreignFile, "keep");

        var deleted = staging.TryDelete();

        Assert.False(deleted);
        Assert.Equal("keep", await File.ReadAllTextAsync(foreignFile));
        Assert.True(Directory.Exists(staging.RootPath));
    }

    [Fact]
    public async Task WriteAsyncCleansOwnedRootWhenStagedSessionDirectoryCreationFails()
    {
        // Creating the staged session before the cleanup guard would leak the owned root on an I/O failure.
        await using var fixture = await GrokWriterFixture.CreateAsync();
        var stagingFactory = new DirectoryCreationFailureStagingFactory();
        var writer = new GrokConversationWriter(
            fixture.Paths,
            () => FirstId,
            new GrokConversationReader(),
            SystemConversationPublisher.Instance,
            stagingFactory);

        await Assert.ThrowsAsync<IOException>(() =>
            writer.WriteAsync(fixture.Conversation(), CancellationToken.None));

        Assert.Equal(1, stagingFactory.CleanupCalls);
        Assert.NotNull(stagingFactory.RootPath);
        Assert.False(Directory.Exists(stagingFactory.RootPath));
    }

    [Fact]
    public async Task OwnedStagingDisposesMarkerAndRemovesRootWhenMarkerInitializationFails()
    {
        // Losing the marker initialization error while its handle stays open would leak an unusable staging root.
        await using var fixture = await GrokWriterFixture.CreateAsync();
        var parent = Path.Combine(fixture.Paths.Sessions, "marker-failure-parent");
        Directory.CreateDirectory(parent);

        Assert.Throws<IOException>(() => SystemConversationStagingDirectory.Create(
            parent,
            _ => throw new IOException("Injected marker initialization failure.")));

        AssertNoStaging(parent);
    }

    private static void AssertPortableConversation(PortableConversation expected, PortableConversation actual, string expectedSessionId)
    {
        Assert.Equal(ConversationAgent.Grok, actual.SourceAgent);
        Assert.Equal(expectedSessionId, actual.SourceSessionId);
        Assert.Equal(expected.Title, actual.Title);
        Assert.Equal(Path.GetFullPath(expected.WorkingDirectory!), actual.WorkingDirectory);
        Assert.Equal(expected.CreatedAt, actual.CreatedAt);
        Assert.Equal(expected.LastModifiedAt, actual.LastModifiedAt);
        Assert.Equal(expected.Turns, actual.Turns);
    }

    private static void AssertNoStaging(string sessionsRoot)
    {
        if (!Directory.Exists(sessionsRoot)) return;
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(sessionsRoot, ".agent-sync-*", SearchOption.AllDirectories),
            _ => true);
    }

    private sealed class FailingConversationReader : IConversationReader
    {
        public Task<PortableConversation> ReadAsync(string nativePath, CancellationToken cancellationToken) =>
            Task.FromException<PortableConversation>(new InvalidDataException("Injected validation failure."));
    }

    private sealed class FailingConversationPublisher : IConversationPublisher
    {
        public void PublishFile(string stagingPath, string destinationPath, IConversationPublicationSeal seal) =>
            throw new IOException("Injected publication failure.");

        public void PublishDirectory(string stagingPath, string destinationPath, IConversationPublicationSeal seal) =>
            throw new IOException("Injected publication failure.");
    }

    private sealed class RacingConversationPublisher : IConversationPublisher
    {
        private bool raced;

        public void PublishFile(string stagingPath, string destinationPath, IConversationPublicationSeal seal) =>
            SystemConversationPublisher.Instance.PublishFile(stagingPath, destinationPath, seal);

        public void PublishDirectory(string stagingPath, string destinationPath, IConversationPublicationSeal seal)
        {
            if (!raced)
            {
                raced = true;
                File.WriteAllText(destinationPath, "existing");
                throw new IOException("Injected publication collision.");
            }
            SystemConversationPublisher.Instance.PublishDirectory(stagingPath, destinationPath, seal);
        }
    }

    private sealed class TamperingConversationPublisher : IConversationPublisher
    {
        public string? InjectedPath { get; private set; }

        public void PublishFile(string stagingPath, string destinationPath, IConversationPublicationSeal seal) =>
            SystemConversationPublisher.Instance.PublishFile(stagingPath, destinationPath, seal);

        public void PublishDirectory(string stagingPath, string destinationPath, IConversationPublicationSeal seal)
        {
            InjectedPath = Path.Combine(stagingPath, "injected.json");
            File.WriteAllText(InjectedPath, "foreign");
            SystemConversationPublisher.Instance.PublishDirectory(stagingPath, destinationPath, seal);
        }
    }

    private sealed class AncestorReplacingConversationPublisher(string ancestor, string relocated)
        : IConversationPublisher
    {
        public string? PublishedPlaintext { get; private set; }

        public void PublishFile(string stagingPath, string destinationPath, IConversationPublicationSeal seal) =>
            throw new NotSupportedException();

        public void PublishDirectory(string stagingPath, string destinationPath, IConversationPublicationSeal seal)
        {
            var relativeDestination = Path.GetRelativePath(ancestor, destinationPath);
            Directory.Move(ancestor, relocated);
            try
            {
                ConversationWriterReparseTestSupport.CreateDirectoryReparsePoint(ancestor, relocated);
                SystemConversationPublisher.Instance.PublishDirectory(stagingPath, destinationPath, seal);
                var outsideChat = Path.Combine(relocated, relativeDestination, "chat_history.jsonl");
                if (File.Exists(outsideChat)) PublishedPlaintext = File.ReadAllText(outsideChat);
            }
            finally
            {
                ConversationWriterReparseTestSupport.RemoveDirectoryReparsePoint(ancestor);
                if (Directory.Exists(relocated) && !Directory.Exists(ancestor)) Directory.Move(relocated, ancestor);
            }
        }
    }

    private sealed class DirectoryCreationFailureStagingFactory : IConversationStagingDirectoryFactory
    {
        public int CleanupCalls { get; private set; }
        public string? RootPath { get; private set; }

        public IConversationStagingDirectory Create(string parentDirectory)
        {
            var inner = SystemConversationStagingDirectoryFactory.Instance.Create(parentDirectory);
            RootPath = inner.RootPath;
            return new DirectoryCreationFailureStagingDirectory(inner, this);
        }

        private sealed class DirectoryCreationFailureStagingDirectory(
            IConversationStagingDirectory inner,
            DirectoryCreationFailureStagingFactory owner) : IConversationStagingDirectory
        {
            private string? blockingPath;

            public string RootPath => inner.RootPath;

            public string DirectoryPath(params string[] components)
            {
                var path = inner.DirectoryPath(components);
                File.WriteAllText(path, "blocking file");
                blockingPath = path;
                return path;
            }

            public string FilePath(params string[] components) => inner.FilePath(components);
            public IConversationPublicationSeal Seal() => inner.Seal();

            public bool TryDelete()
            {
                owner.CleanupCalls++;
                if (blockingPath is not null && File.Exists(blockingPath)) File.Delete(blockingPath);
                return inner.TryDelete();
            }
        }
    }

    private sealed class GrokWriterFixture : IAsyncDisposable
    {
        private GrokWriterFixture(string root, GrokPaths paths, string workingDirectory)
        {
            Root = root;
            Paths = paths;
            WorkingDirectory = workingDirectory;
        }

        public string Root { get; }
        public GrokPaths Paths { get; }
        public string WorkingDirectory { get; }

        public static Task<GrokWriterFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"grok-conversation-writer-{Guid.NewGuid():N}");
            var home = Path.Combine(root, "grok-home");
            var sessions = Path.Combine(home, "sessions");
            var workingDirectory = Path.Combine(root, "project");
            Directory.CreateDirectory(sessions);
            Directory.CreateDirectory(workingDirectory);
            return Task.FromResult(new GrokWriterFixture(root, new GrokPaths(home, sessions), workingDirectory));
        }

        public PortableConversation Conversation(string sourceSessionId = "source-session") => new(
            ConversationAgent.Codex,
            sourceSessionId,
            "Portable title",
            WorkingDirectory,
            new DateTimeOffset(2026, 8, 9, 10, 11, 12, TimeSpan.FromHours(3)),
            new DateTimeOffset(2026, 8, 9, 10, 14, 15, TimeSpan.FromHours(3)),
            [
                new PortableTurn(ConversationRole.User, "first question"),
                new PortableTurn(ConversationRole.Assistant, "first answer"),
                new PortableTurn(ConversationRole.User, "second question")
            ]);

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
