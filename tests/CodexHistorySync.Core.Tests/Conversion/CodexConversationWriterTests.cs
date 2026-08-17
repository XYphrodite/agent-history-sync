using System.Text.Json;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Conversion;

namespace CodexHistorySync.Core.Tests.Conversion;

public sealed class CodexConversationWriterTests
{
    private static readonly Guid FirstId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SecondId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task WriteAsyncCreatesTimestampedRolloutAndPreservesPortableConversation()
    {
        // Wrong date placement, invalid native records, source-ID reuse, or field/turn loss must fail this test.
        await using var fixture = await CodexWriterFixture.CreateAsync();
        var conversation = fixture.Conversation(sourceSessionId: FirstId.ToString());
        var writer = fixture.Writer(
            new CodexExecutableOption(null, CodexExecutableAvailability.AutomaticDiscoveryAbsent),
            () => SecondId);

        var result = await writer.WriteAsync(conversation, CancellationToken.None);

        var expectedDirectory = Path.Combine(fixture.Paths.Sessions, "2026", "08", "09");
        Assert.Equal(SecondId.ToString(), result.SessionId);
        Assert.NotEqual(conversation.SourceSessionId, result.SessionId);
        Assert.Equal(expectedDirectory, Path.GetDirectoryName(result.NativePath));
        Assert.Equal($"rollout-2026-08-09T07-11-12-{SecondId}.jsonl", Path.GetFileName(result.NativePath));
        Assert.True(File.Exists(result.NativePath));
        await AssertNativeRecordsAsync(result.NativePath, result.SessionId, conversation, conversation.Turns.Count);
        AssertPortableConversation(conversation, await new CodexConversationReader().ReadAsync(result.NativePath, CancellationToken.None), result.SessionId);
        AssertNoStaging(fixture.Paths.Sessions);
    }

    [Fact]
    public async Task WriteAsyncRejectsReparseSessionsRootWithoutWritingPlaintextOutsideTheNativeStore()
    {
        // Removing full-chain validation would let an existing sessions junction receive the staged and final rollout.
        await using var fixture = await CodexWriterFixture.CreateAsync();
        var outside = Path.Combine(fixture.Root, "outside-codex-sessions");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "existing.txt");
        await File.WriteAllTextAsync(sentinel, "existing");
        Directory.Delete(fixture.Paths.Sessions);
        ConversationWriterReparseTestSupport.CreateDirectoryReparsePoint(fixture.Paths.Sessions, outside);
        var expectedOutside = Path.Combine(
            outside,
            Path.GetRelativePath(
                fixture.Paths.Sessions,
                fixture.RolloutPath(fixture.Conversation().CreatedAt, FirstId)));

        try
        {
            var error = await Record.ExceptionAsync(() => fixture.Writer(
                    new CodexExecutableOption(null, CodexExecutableAvailability.AutomaticDiscoveryAbsent),
                    () => FirstId)
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
        // Checking only the immediate staging parent misses a junction higher in the date hierarchy.
        await using var fixture = await CodexWriterFixture.CreateAsync();
        var year = Path.Combine(fixture.Paths.Sessions, "2026");
        var month = Path.Combine(year, "08");
        var outside = Path.Combine(fixture.Root, "outside-codex-month");
        Directory.CreateDirectory(year);
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "existing.txt");
        await File.WriteAllTextAsync(sentinel, "existing");
        ConversationWriterReparseTestSupport.CreateDirectoryReparsePoint(month, outside);
        var expectedOutside = Path.Combine(
            outside,
            "09",
            Path.GetFileName(fixture.RolloutPath(fixture.Conversation().CreatedAt, FirstId)));

        try
        {
            var error = await Record.ExceptionAsync(() => fixture.Writer(
                    new CodexExecutableOption(null, CodexExecutableAvailability.AutomaticDiscoveryAbsent),
                    () => FirstId)
                .WriteAsync(fixture.Conversation(), CancellationToken.None));

            Assert.False(File.Exists(expectedOutside));
            Assert.Equal([sentinel], Directory.EnumerateFiles(outside, "*", SearchOption.AllDirectories));
            Assert.Equal("existing", await File.ReadAllTextAsync(sentinel));
            Assert.IsType<InvalidDataException>(error);
        }
        finally
        {
            ConversationWriterReparseTestSupport.RemoveDirectoryReparsePoint(month);
        }
    }

    [Fact]
    public async Task WriteAsyncRevalidatesDestinationAncestorsImmediatelyBeforePublication()
    {
        // Dropping the publication-time ancestor check would move validated plaintext through a newly introduced junction.
        await using var fixture = await CodexWriterFixture.CreateAsync();
        var existing = Path.Combine(fixture.Paths.Sessions, "existing.txt");
        await File.WriteAllTextAsync(existing, "existing");
        var publisher = new AncestorReplacingConversationPublisher(
            fixture.Paths.Sessions,
            Path.Combine(fixture.Root, "relocated-codex-sessions"));
        var independentStaging = Path.Combine(fixture.Root, "independent-codex-staging");
        Directory.CreateDirectory(independentStaging);
        var writer = fixture.Writer(
            new CodexExecutableOption(null, CodexExecutableAvailability.AutomaticDiscoveryAbsent),
            () => FirstId,
            publisher: publisher,
            stagingFactory: new IndependentConversationStagingDirectoryFactory(independentStaging));

        var error = await Record.ExceptionAsync(() =>
            writer.WriteAsync(fixture.Conversation(), CancellationToken.None));

        Assert.Null(publisher.PublishedPlaintext);
        Assert.IsType<InvalidDataException>(error);
        Assert.Equal("existing", await File.ReadAllTextAsync(existing));
        Assert.False(File.Exists(fixture.RolloutPath(fixture.Conversation().CreatedAt, FirstId)));
        AssertNoStaging(fixture.Paths.Sessions);
    }

    [Fact]
    public async Task WriteAsyncRetriesOccupiedGeneratedIdWithoutOverwritingIt()
    {
        // Publishing over an occupied rollout would destroy an existing Codex conversation.
        await using var fixture = await CodexWriterFixture.CreateAsync();
        var conversation = fixture.Conversation();
        var occupied = fixture.RolloutPath(conversation.CreatedAt, FirstId);
        Directory.CreateDirectory(Path.GetDirectoryName(occupied)!);
        await File.WriteAllTextAsync(occupied, "existing");
        var ids = new Queue<Guid>([FirstId, SecondId]);
        var writer = fixture.Writer(
            new CodexExecutableOption(null, CodexExecutableAvailability.AutomaticDiscoveryAbsent),
            ids.Dequeue);

        var result = await writer.WriteAsync(conversation, CancellationToken.None);

        Assert.Equal(SecondId.ToString(), result.SessionId);
        Assert.Equal("existing", await File.ReadAllTextAsync(occupied));
        Assert.True(File.Exists(result.NativePath));
        AssertNoStaging(fixture.Paths.Sessions);
    }

    [Theory]
    [InlineData("11111111111111111111111111111111")]
    [InlineData("{11111111-1111-1111-1111-111111111111}")]
    public async Task WriteAsyncTreatsAlternateSourceUuidRepresentationsAsTheSameId(string sourceSessionId)
    {
        // String-only comparison would publish the source UUID again when its valid representation differs.
        await using var fixture = await CodexWriterFixture.CreateAsync();
        var ids = new Queue<Guid>([FirstId, SecondId]);
        var writer = fixture.Writer(
            new CodexExecutableOption(null, CodexExecutableAvailability.AutomaticDiscoveryAbsent),
            ids.Dequeue);

        var result = await writer.WriteAsync(fixture.Conversation(sourceSessionId), CancellationToken.None);

        Assert.Equal(SecondId.ToString(), result.SessionId);
        Assert.False(File.Exists(fixture.RolloutPath(fixture.Conversation().CreatedAt, FirstId)));
    }

    [Fact]
    public async Task WriteAsyncFailsAfterTenOccupiedIdsWithoutChangingExistingRollout()
    {
        // An unbounded retry or overwrite after repeated collisions would violate the allocation boundary.
        await using var fixture = await CodexWriterFixture.CreateAsync();
        var conversation = fixture.Conversation();
        var occupied = fixture.RolloutPath(conversation.CreatedAt, FirstId);
        Directory.CreateDirectory(Path.GetDirectoryName(occupied)!);
        await File.WriteAllTextAsync(occupied, "existing");
        var calls = 0;
        var writer = fixture.Writer(
            new CodexExecutableOption(null, CodexExecutableAvailability.AutomaticDiscoveryAbsent),
            () => { calls++; return FirstId; });

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            writer.WriteAsync(conversation, CancellationToken.None));

        Assert.Equal("Unable to allocate a unique Codex session ID after 10 attempts.", exception.Message);
        Assert.Equal(10, calls);
        Assert.Equal("existing", await File.ReadAllTextAsync(occupied));
        AssertNoStaging(fixture.Paths.Sessions);
    }

    [Fact]
    public async Task WriteAsyncRetriesWhenDirectoryOccupiesGeneratedFileDuringPublication()
    {
        // A directory racing into the rollout destination must be preserved while the writer consumes a new ID.
        await using var fixture = await CodexWriterFixture.CreateAsync();
        var ids = new Queue<Guid>([FirstId, SecondId]);
        var writer = fixture.Writer(
            new CodexExecutableOption(null, CodexExecutableAvailability.AutomaticDiscoveryAbsent),
            ids.Dequeue,
            publisher: new RacingConversationPublisher());

        var result = await writer.WriteAsync(fixture.Conversation(), CancellationToken.None);

        var racedDestination = fixture.RolloutPath(fixture.Conversation().CreatedAt, FirstId);
        Assert.Equal(SecondId.ToString(), result.SessionId);
        Assert.True(Directory.Exists(racedDestination));
        Assert.True(File.Exists(result.NativePath));
        AssertNoStaging(fixture.Paths.Sessions);
    }

    [Theory]
    [InlineData(CodexExecutableAvailability.Configured)]
    [InlineData(CodexExecutableAvailability.Discovered)]
    public async Task WriteAsyncRequiresSuccessfulProbeForAvailableExecutable(CodexExecutableAvailability availability)
    {
        // Treating configured or discovered Codex as absent would publish an unprobed rollout.
        await using var fixture = await CodexWriterFixture.CreateAsync();
        var executable = await fixture.CreateExecutablePlaceholderAsync();
        string? probedExecutable = null;
        string? probedSession = null;
        var probeCalls = 0;
        var writer = fixture.Writer(
            new CodexExecutableOption(executable, availability),
            () => FirstId,
            probe: (path, session, _) =>
            {
                probeCalls++;
                probedExecutable = path;
                probedSession = session;
                Assert.True(File.Exists(session));
                Assert.Matches(
                    "^rollout-2026-08-09T07-11-12-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\\.jsonl$",
                    Path.GetFileName(session));
                return Task.FromResult(new CompatibilityResult(true, "test", "compatible"));
            });

        var result = await writer.WriteAsync(fixture.Conversation(), CancellationToken.None);

        Assert.Equal(1, probeCalls);
        Assert.Equal(executable, probedExecutable);
        Assert.NotNull(probedSession);
        Assert.False(File.Exists(probedSession));
        Assert.True(File.Exists(result.NativePath));
        AssertNoStaging(fixture.Paths.Sessions);
    }

    [Fact]
    public async Task WriteAsyncRefusesPublicationWhenCompatibilityProbeFails()
    {
        // Publishing after an incompatible probe result would bypass the destination compatibility gate.
        await using var fixture = await CodexWriterFixture.CreateAsync();
        var executable = await fixture.CreateExecutablePlaceholderAsync();
        var writer = fixture.Writer(
            new CodexExecutableOption(executable, CodexExecutableAvailability.Configured),
            () => FirstId,
            probe: (_, _, _) => Task.FromResult(new CompatibilityResult(false, "test", "incompatible")));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            writer.WriteAsync(fixture.Conversation(), CancellationToken.None));

        Assert.Equal("The staged Codex conversation failed the compatibility probe.", exception.Message);
        Assert.False(File.Exists(fixture.RolloutPath(fixture.Conversation().CreatedAt, FirstId)));
        AssertNoStaging(fixture.Paths.Sessions);
    }

    [Fact]
    public async Task WriteAsyncPreservesCallerCancellationReportedByCompatibilityProbe()
    {
        // Translating caller cancellation into format incompatibility would misreport an interrupted operation.
        await using var fixture = await CodexWriterFixture.CreateAsync();
        var executable = await fixture.CreateExecutablePlaceholderAsync();
        using var cancellation = new CancellationTokenSource();
        var writer = fixture.Writer(
            new CodexExecutableOption(executable, CodexExecutableAvailability.Discovered),
            () => FirstId,
            probe: (_, _, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult(new CompatibilityResult(false, "test", "cancelled"));
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            writer.WriteAsync(fixture.Conversation(), cancellation.Token));

        Assert.False(File.Exists(fixture.RolloutPath(fixture.Conversation().CreatedAt, FirstId)));
        AssertNoStaging(fixture.Paths.Sessions);
    }

    [Fact]
    public async Task WriteAsyncRejectsInvalidExplicitExecutableWithoutProbingOrStaging()
    {
        // Silently downgrading an invalid CODEX_EXE configuration to automatic absence hides operator error.
        await using var fixture = await CodexWriterFixture.CreateAsync();
        var missingExecutable = Path.Combine(fixture.Root, "missing-codex.exe");
        var probeCalls = 0;
        var writer = fixture.Writer(
            new CodexExecutableOption(missingExecutable, CodexExecutableAvailability.Configured),
            () => FirstId,
            probe: (_, _, _) =>
            {
                probeCalls++;
                return Task.FromResult(new CompatibilityResult(true, "test", "compatible"));
            });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.WriteAsync(fixture.Conversation(), CancellationToken.None));

        Assert.Equal("The configured Codex executable is unavailable.", exception.Message);
        Assert.Equal(0, probeCalls);
        AssertNoStaging(fixture.Paths.Sessions);
    }

    [Fact]
    public async Task WriteAsyncSkipsProbeOnlyWhenAutomaticDiscoveryIsAbsent()
    {
        // Invoking the probe in the explicit absent state would prevent valid Grok-only installations from converting.
        await using var fixture = await CodexWriterFixture.CreateAsync();
        var probeCalls = 0;
        var writer = fixture.Writer(
            new CodexExecutableOption(null, CodexExecutableAvailability.AutomaticDiscoveryAbsent),
            () => FirstId,
            probe: (_, _, _) =>
            {
                probeCalls++;
                throw new InvalidOperationException("Probe must be skipped.");
            });

        var result = await writer.WriteAsync(fixture.Conversation(), CancellationToken.None);

        Assert.Equal(0, probeCalls);
        Assert.True(File.Exists(result.NativePath));
        AssertNoStaging(fixture.Paths.Sessions);
    }

    [Fact]
    public async Task WriteAsyncRemovesOwnedStagingWhenValidationFails()
    {
        // A reader-rejected rollout must not leave temporary or final native data.
        await using var fixture = await CodexWriterFixture.CreateAsync();
        var writer = fixture.Writer(
            new CodexExecutableOption(null, CodexExecutableAvailability.AutomaticDiscoveryAbsent),
            () => FirstId,
            reader: new FailingConversationReader());

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            writer.WriteAsync(fixture.Conversation(), CancellationToken.None));

        Assert.False(File.Exists(fixture.RolloutPath(fixture.Conversation().CreatedAt, FirstId)));
        AssertNoStaging(fixture.Paths.Sessions);
    }

    [Fact]
    public async Task WriteAsyncRemovesOwnedStagingWhenAtomicMoveFails()
    {
        // A failed atomic publication must clean the owned staged JSONL and leave no final rollout.
        await using var fixture = await CodexWriterFixture.CreateAsync();
        var writer = fixture.Writer(
            new CodexExecutableOption(null, CodexExecutableAvailability.AutomaticDiscoveryAbsent),
            () => FirstId,
            publisher: new FailingConversationPublisher());

        await Assert.ThrowsAsync<IOException>(() =>
            writer.WriteAsync(fixture.Conversation(), CancellationToken.None));

        Assert.False(File.Exists(fixture.RolloutPath(fixture.Conversation().CreatedAt, FirstId)));
        AssertNoStaging(fixture.Paths.Sessions);
    }

    [Fact]
    public async Task WriteAsyncRefusesRegisteredFileTamperingImmediatelyBeforePublication()
    {
        // Moving a pathname without rechecking the validated object could publish attacker-replaced JSONL.
        await using var fixture = await CodexWriterFixture.CreateAsync();
        var writer = fixture.Writer(
            new CodexExecutableOption(null, CodexExecutableAvailability.AutomaticDiscoveryAbsent),
            () => FirstId,
            publisher: new TamperingConversationPublisher());

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            writer.WriteAsync(fixture.Conversation(), CancellationToken.None));

        Assert.Equal("The staged conversation changed after validation.", exception.Message);
        Assert.False(File.Exists(fixture.RolloutPath(fixture.Conversation().CreatedAt, FirstId)));
        AssertNoStaging(fixture.Paths.Sessions);
    }

    [Fact]
    public async Task WriteAsyncReturnsCommittedResultWhenStagingCleanupReportsFailure()
    {
        // Reporting failure after atomic publication could make a retry create a duplicate conversion.
        await using var fixture = await CodexWriterFixture.CreateAsync();
        var writer = fixture.Writer(
            new CodexExecutableOption(null, CodexExecutableAvailability.AutomaticDiscoveryAbsent),
            () => FirstId,
            stagingFactory: new ReportingFailureStagingFactory());

        var result = await writer.WriteAsync(fixture.Conversation(), CancellationToken.None);

        Assert.Equal(FirstId.ToString(), result.SessionId);
        Assert.True(File.Exists(result.NativePath));
        AssertNoStaging(fixture.Paths.Sessions);
    }

    [Fact]
    public async Task WriteAsyncCleansOwnedRootWhenStagedFileRegistrationFails()
    {
        // Registering the staged rollout before the cleanup guard would leak its owned root on failure.
        await using var fixture = await CodexWriterFixture.CreateAsync();
        var stagingFactory = new FileRegistrationFailureStagingFactory();
        var writer = fixture.Writer(
            new CodexExecutableOption(null, CodexExecutableAvailability.AutomaticDiscoveryAbsent),
            () => FirstId,
            stagingFactory: stagingFactory);

        await Assert.ThrowsAsync<IOException>(() =>
            writer.WriteAsync(fixture.Conversation(), CancellationToken.None));

        Assert.Equal(1, stagingFactory.CleanupCalls);
        Assert.NotNull(stagingFactory.RootPath);
        Assert.False(Directory.Exists(stagingFactory.RootPath));
    }

    private static async Task AssertNativeRecordsAsync(
        string path,
        string sessionId,
        PortableConversation conversation,
        int turnCount)
    {
        var lines = await File.ReadAllLinesAsync(path);
        var expectedUserMessages = conversation.Turns
            .Where(turn => turn.Role == ConversationRole.User)
            .Select(turn => turn.Text)
            .ToArray();
        Assert.Equal(turnCount + expectedUserMessages.Length + 1, lines.Length);
        using var metadata = JsonDocument.Parse(lines[0]);
        Assert.Equal("session_meta", metadata.RootElement.GetProperty("type").GetString());
        var payload = metadata.RootElement.GetProperty("payload");
        Assert.Equal(sessionId, payload.GetProperty("session_id").GetString());
        Assert.Equal(sessionId, payload.GetProperty("id").GetString());
        Assert.Equal(conversation.CreatedAt.ToString("O"), payload.GetProperty("timestamp").GetString());
        Assert.Equal(conversation.WorkingDirectory, payload.GetProperty("cwd").GetString());
        Assert.Equal("codex-history-sync", payload.GetProperty("originator").GetString());
        Assert.Equal("0.4.1", payload.GetProperty("cli_version").GetString());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("model_provider").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("base_instructions").ValueKind);
        var responseItems = new List<JsonDocument>();
        var discoverableUserMessages = new List<string>();
        try
        {
            for (var index = 1; index < lines.Length; index++)
            {
                var record = JsonDocument.Parse(lines[index]);
                var root = record.RootElement;
                var type = root.GetProperty("type").GetString();
                if (type == "response_item")
                {
                    Assert.Equal("message", root.GetProperty("payload").GetProperty("type").GetString());
                    responseItems.Add(record);
                }
                else
                {
                    Assert.Equal("event_msg", type);
                    var eventPayload = root.GetProperty("payload");
                    Assert.Equal("user_message", eventPayload.GetProperty("type").GetString());
                    discoverableUserMessages.Add(eventPayload.GetProperty("message").GetString()!);
                    record.Dispose();
                }
            }

            Assert.Equal(turnCount, responseItems.Count);
            Assert.Equal(expectedUserMessages, discoverableUserMessages);
        }
        finally
        {
            foreach (var responseItem in responseItems) responseItem.Dispose();
        }
    }

    private static void AssertPortableConversation(PortableConversation expected, PortableConversation actual, string expectedSessionId)
    {
        Assert.Equal(ConversationAgent.Codex, actual.SourceAgent);
        Assert.Equal(expectedSessionId, actual.SourceSessionId);
        Assert.Equal(expected.Title, actual.Title);
        Assert.Equal(expected.WorkingDirectory, actual.WorkingDirectory);
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

        public void PublishFile(string stagingPath, string destinationPath, IConversationPublicationSeal seal)
        {
            if (!raced)
            {
                raced = true;
                Directory.CreateDirectory(destinationPath);
                throw new IOException("Injected publication collision.");
            }
            SystemConversationPublisher.Instance.PublishFile(stagingPath, destinationPath, seal);
        }

        public void PublishDirectory(string stagingPath, string destinationPath, IConversationPublicationSeal seal) =>
            SystemConversationPublisher.Instance.PublishDirectory(stagingPath, destinationPath, seal);
    }

    private sealed class TamperingConversationPublisher : IConversationPublisher
    {
        public void PublishFile(string stagingPath, string destinationPath, IConversationPublicationSeal seal)
        {
            File.WriteAllText(stagingPath, "{\"tampered\":true}\n");
            SystemConversationPublisher.Instance.PublishFile(stagingPath, destinationPath, seal);
        }

        public void PublishDirectory(string stagingPath, string destinationPath, IConversationPublicationSeal seal) =>
            SystemConversationPublisher.Instance.PublishDirectory(stagingPath, destinationPath, seal);
    }

    private sealed class AncestorReplacingConversationPublisher(string ancestor, string relocated)
        : IConversationPublisher
    {
        public string? PublishedPlaintext { get; private set; }

        public void PublishFile(string stagingPath, string destinationPath, IConversationPublicationSeal seal)
        {
            var relativeDestination = Path.GetRelativePath(ancestor, destinationPath);
            Directory.Move(ancestor, relocated);
            try
            {
                ConversationWriterReparseTestSupport.CreateDirectoryReparsePoint(ancestor, relocated);
                SystemConversationPublisher.Instance.PublishFile(stagingPath, destinationPath, seal);
                var outsideDestination = Path.Combine(relocated, relativeDestination);
                if (File.Exists(outsideDestination)) PublishedPlaintext = File.ReadAllText(outsideDestination);
            }
            finally
            {
                ConversationWriterReparseTestSupport.RemoveDirectoryReparsePoint(ancestor);
                if (Directory.Exists(relocated) && !Directory.Exists(ancestor)) Directory.Move(relocated, ancestor);
            }
        }

        public void PublishDirectory(string stagingPath, string destinationPath, IConversationPublicationSeal seal) =>
            throw new NotSupportedException();
    }

    private sealed class ReportingFailureStagingFactory : IConversationStagingDirectoryFactory
    {
        public IConversationStagingDirectory Create(string parentDirectory) =>
            new ReportingFailureStagingDirectory(
                SystemConversationStagingDirectoryFactory.Instance.Create(parentDirectory));
    }

    private sealed class FileRegistrationFailureStagingFactory : IConversationStagingDirectoryFactory
    {
        public int CleanupCalls { get; private set; }
        public string? RootPath { get; private set; }

        public IConversationStagingDirectory Create(string parentDirectory)
        {
            var inner = SystemConversationStagingDirectoryFactory.Instance.Create(parentDirectory);
            RootPath = inner.RootPath;
            return new FileRegistrationFailureStagingDirectory(inner, this);
        }

        private sealed class FileRegistrationFailureStagingDirectory(
            IConversationStagingDirectory inner,
            FileRegistrationFailureStagingFactory owner) : IConversationStagingDirectory
        {
            public string RootPath => inner.RootPath;
            public string DirectoryPath(params string[] components) => inner.DirectoryPath(components);
            public string FilePath(params string[] components) => throw new IOException("Injected path registration failure.");
            public IConversationPublicationSeal Seal() => inner.Seal();

            public bool TryDelete()
            {
                owner.CleanupCalls++;
                return inner.TryDelete();
            }
        }
    }

    private sealed class ReportingFailureStagingDirectory(IConversationStagingDirectory inner)
        : IConversationStagingDirectory
    {
        public string RootPath => inner.RootPath;
        public string DirectoryPath(params string[] components) => inner.DirectoryPath(components);
        public string FilePath(params string[] components) => inner.FilePath(components);
        public IConversationPublicationSeal Seal() => inner.Seal();

        public bool TryDelete()
        {
            Assert.True(inner.TryDelete());
            return false;
        }
    }

    private sealed class CodexWriterFixture : IAsyncDisposable
    {
        private CodexWriterFixture(string root, CodexPaths paths, string workingDirectory)
        {
            Root = root;
            Paths = paths;
            WorkingDirectory = workingDirectory;
        }

        public string Root { get; }
        public CodexPaths Paths { get; }
        public string WorkingDirectory { get; }

        public static Task<CodexWriterFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"codex-conversation-writer-{Guid.NewGuid():N}");
            var home = Path.Combine(root, "codex-home");
            var sessions = Path.Combine(home, "sessions");
            var workingDirectory = Path.Combine(root, "project");
            Directory.CreateDirectory(sessions);
            Directory.CreateDirectory(workingDirectory);
            return Task.FromResult(new CodexWriterFixture(
                root,
                new CodexPaths(home, sessions, Path.Combine(home, "archived_sessions"), Path.Combine(home, "attachments")),
                workingDirectory));
        }

        public CodexConversationWriter Writer(
            CodexExecutableOption executable,
            Func<Guid> idGenerator,
            IConversationReader? reader = null,
            IConversationPublisher? publisher = null,
            Func<string, string, CancellationToken, Task<CompatibilityResult>>? probe = null,
            IConversationStagingDirectoryFactory? stagingFactory = null) =>
            new(
                Paths,
                executable,
                idGenerator,
                reader ?? new CodexConversationReader(),
                publisher ?? SystemConversationPublisher.Instance,
                probe ?? ((_, _, _) => Task.FromResult(new CompatibilityResult(true, "test", "compatible"))),
                stagingFactory);

        public PortableConversation Conversation(string sourceSessionId = "source-session") => new(
            ConversationAgent.Grok,
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

        public string RolloutPath(DateTimeOffset createdAt, Guid id)
        {
            var utc = createdAt.UtcDateTime;
            return Path.Combine(
                Paths.Sessions,
                utc.ToString("yyyy"),
                utc.ToString("MM"),
                utc.ToString("dd"),
                $"rollout-{utc:yyyy-MM-dd'T'HH-mm-ss}-{id}.jsonl");
        }

        public async Task<string> CreateExecutablePlaceholderAsync()
        {
            var path = Path.Combine(Root, "codex.exe");
            await File.WriteAllTextAsync(path, "placeholder");
            return path;
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
