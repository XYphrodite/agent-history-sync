using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Conversion;
using CodexHistorySync.Core.Grok;
using CodexHistorySync.Windows;

namespace CodexHistorySync.IntegrationTests;

public sealed class CrossAgentCompatibilityTests
{
    [Fact]
    public async Task CodexConversationWrittenAsGrokIsAcceptedByDestinationReader()
    {
        // A writer format that only its implementation understands must fail at the destination-reader boundary.
        await using var fixture = await CrossAgentFixture.CreateAsync();
        var source = fixture.Conversation(ConversationAgent.Codex, "codex-source");
        var copyTime = new DateTimeOffset(2026, 8, 23, 17, 30, 0, TimeSpan.Zero);

        var result = await new GrokConversationWriter(fixture.GrokPaths, () => fixture.GrokId, () => copyTime)
            .WriteAsync(source, CancellationToken.None);
        var imported = await new GrokConversationReader().ReadAsync(result.NativePath, CancellationToken.None);

        AssertConverted(source with { LastModifiedAt = copyTime }, imported, ConversationAgent.Grok, fixture.GrokId.ToString());
    }

    [Fact]
    public async Task GrokConversationWrittenAsCodexIsAcceptedByDestinationReader()
    {
        // A rollout that loses portable metadata or ordered text must fail at the Codex reader boundary.
        await using var fixture = await CrossAgentFixture.CreateAsync();
        var source = fixture.Conversation(ConversationAgent.Grok, "grok-source");
        var copyTime = new DateTimeOffset(2026, 8, 23, 17, 30, 0, TimeSpan.Zero);
        var writer = new CodexConversationWriter(
            fixture.CodexPaths,
            new CodexExecutableOption(null, CodexExecutableAvailability.AutomaticDiscoveryAbsent),
            new CodexCompatibilityProbe(),
            () => fixture.CodexId,
            () => copyTime);

        var result = await writer.WriteAsync(source, CancellationToken.None);
        var imported = await new CodexConversationReader().ReadAsync(result.NativePath, CancellationToken.None);

        AssertConverted(source with { LastModifiedAt = copyTime }, imported, ConversationAgent.Codex, fixture.CodexId.ToString());
    }

    [Fact]
    public async Task GeneratedCodexRolloutPassesDisposableProbeWhenExecutableIsAvailable()
    {
        // Emitting reader-valid but Codex-incompatible JSONL must fail when a real executable is discoverable.
        var resolution = new CodexExecutableLocator().ResolveWithSource();
        if (resolution.ExecutablePath is null || !File.Exists(resolution.ExecutablePath)) return;

        await using var fixture = await CrossAgentFixture.CreateAsync();
        var availability = resolution.Source switch
        {
            CodexExecutableSource.Configured => CodexExecutableAvailability.Configured,
            CodexExecutableSource.Discovered => CodexExecutableAvailability.Discovered,
            CodexExecutableSource.AutomaticDiscoveryAbsent => CodexExecutableAvailability.AutomaticDiscoveryAbsent,
            _ => throw new InvalidOperationException("The Codex executable source is invalid.")
        };
        var writer = new CodexConversationWriter(
            fixture.CodexPaths,
            new CodexExecutableOption(resolution.ExecutablePath, availability),
            new CodexCompatibilityProbe(),
            () => fixture.CodexId);

        var result = await writer.WriteAsync(
            fixture.Conversation(ConversationAgent.Grok, "probe-source"),
            CancellationToken.None);

        Assert.True(File.Exists(result.NativePath));
    }

    private static void AssertConverted(
        PortableConversation expected,
        PortableConversation actual,
        ConversationAgent destinationAgent,
        string destinationSessionId)
    {
        Assert.Equal(destinationAgent, actual.SourceAgent);
        Assert.Equal(destinationSessionId, actual.SourceSessionId);
        Assert.Equal(expected.Title, actual.Title);
        Assert.Equal(Path.GetFullPath(expected.WorkingDirectory!), actual.WorkingDirectory);
        Assert.Equal(expected.CreatedAt, actual.CreatedAt);
        Assert.Equal(expected.LastModifiedAt, actual.LastModifiedAt);
        Assert.Equal(expected.Turns, actual.Turns);
    }

    private sealed class CrossAgentFixture : IAsyncDisposable
    {
        private CrossAgentFixture(string root, CodexPaths codexPaths, GrokPaths grokPaths, string workingDirectory)
        {
            Root = root;
            CodexPaths = codexPaths;
            GrokPaths = grokPaths;
            WorkingDirectory = workingDirectory;
        }

        public string Root { get; }
        public CodexPaths CodexPaths { get; }
        public GrokPaths GrokPaths { get; }
        public string WorkingDirectory { get; }
        public Guid CodexId { get; } = Guid.Parse("33333333-3333-3333-3333-333333333333");
        public Guid GrokId { get; } = Guid.Parse("44444444-4444-4444-4444-444444444444");

        public static Task<CrossAgentFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"cross-agent-compatibility-{Guid.NewGuid():N}");
            var codexHome = Path.Combine(root, "codex-home");
            var grokHome = Path.Combine(root, "grok-home");
            var codexSessions = Path.Combine(codexHome, "sessions");
            var grokSessions = Path.Combine(grokHome, "sessions");
            var workingDirectory = Path.Combine(root, "project");
            Directory.CreateDirectory(codexSessions);
            Directory.CreateDirectory(grokSessions);
            Directory.CreateDirectory(workingDirectory);
            return Task.FromResult(new CrossAgentFixture(
                root,
                new CodexPaths(codexHome, codexSessions, Path.Combine(codexHome, "archived_sessions"), Path.Combine(codexHome, "attachments")),
                new GrokPaths(grokHome, grokSessions),
                workingDirectory));
        }

        public PortableConversation Conversation(ConversationAgent sourceAgent, string sourceSessionId) => new(
            sourceAgent,
            sourceSessionId,
            "Cross-agent title",
            WorkingDirectory,
            new DateTimeOffset(2026, 8, 9, 10, 11, 12, TimeSpan.FromHours(3)),
            new DateTimeOffset(2026, 8, 9, 10, 14, 15, TimeSpan.FromHours(3)),
            [
                new PortableTurn(ConversationRole.User, "question"),
                new PortableTurn(ConversationRole.Assistant, "answer")
            ]);

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
