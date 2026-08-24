using CodexHistorySync.Core.Claude;
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
    public async Task CodexConversationWrittenAsClaudeIsAcceptedByDestinationReader()
    {
        // A transcript that loses the title record or the ordered text must fail at the Claude reader boundary.
        await using var fixture = await CrossAgentFixture.CreateAsync();
        var source = fixture.Conversation(ConversationAgent.Codex, "codex-source");
        var copyTime = new DateTimeOffset(2026, 8, 24, 17, 30, 0, TimeSpan.Zero);

        var result = await new ClaudeConversationWriter(fixture.ClaudePaths, () => fixture.ClaudeId, () => copyTime)
            .WriteAsync(source, CancellationToken.None);
        var imported = await new ClaudeConversationReader().ReadAsync(result.NativePath, CancellationToken.None);

        AssertConverted(source with { LastModifiedAt = copyTime }, imported, ConversationAgent.Claude, fixture.ClaudeId.ToString());
    }

    [Fact]
    public async Task ClaudeConversationWrittenAsCodexAndGrokIsAcceptedByBothReaders()
    {
        // The Claude reader is the only source of a portable conversation for the other two writers,
        // so a copy out of Claude has to survive both destination readers unchanged.
        await using var fixture = await CrossAgentFixture.CreateAsync();
        var copyTime = new DateTimeOffset(2026, 8, 24, 17, 30, 0, TimeSpan.Zero);
        var staged = await new ClaudeConversationWriter(fixture.ClaudePaths, () => fixture.ClaudeId, () => copyTime)
            .WriteAsync(fixture.Conversation(ConversationAgent.Codex, "codex-source"), CancellationToken.None);
        var source = await new ClaudeConversationReader().ReadAsync(staged.NativePath, CancellationToken.None);

        var asCodex = await new CodexConversationWriter(
                fixture.CodexPaths,
                new CodexExecutableOption(null, CodexExecutableAvailability.AutomaticDiscoveryAbsent),
                new CodexCompatibilityProbe(),
                () => fixture.CodexId,
                () => copyTime)
            .WriteAsync(source, CancellationToken.None);
        var asGrok = await new GrokConversationWriter(fixture.GrokPaths, () => fixture.GrokId, () => copyTime)
            .WriteAsync(source, CancellationToken.None);

        AssertConverted(
            source with { LastModifiedAt = copyTime },
            await new CodexConversationReader().ReadAsync(asCodex.NativePath, CancellationToken.None),
            ConversationAgent.Codex,
            fixture.CodexId.ToString());
        AssertConverted(
            source with { LastModifiedAt = copyTime },
            await new GrokConversationReader().ReadAsync(asGrok.NativePath, CancellationToken.None),
            ConversationAgent.Grok,
            fixture.GrokId.ToString());
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
        private CrossAgentFixture(string root, CodexPaths codexPaths, GrokPaths grokPaths, ClaudePaths claudePaths, string workingDirectory)
        {
            Root = root;
            CodexPaths = codexPaths;
            GrokPaths = grokPaths;
            ClaudePaths = claudePaths;
            WorkingDirectory = workingDirectory;
        }

        public string Root { get; }
        public CodexPaths CodexPaths { get; }
        public GrokPaths GrokPaths { get; }
        public ClaudePaths ClaudePaths { get; }
        public string WorkingDirectory { get; }
        public Guid CodexId { get; } = Guid.Parse("33333333-3333-3333-3333-333333333333");
        public Guid GrokId { get; } = Guid.Parse("44444444-4444-4444-4444-444444444444");
        public Guid ClaudeId { get; } = Guid.Parse("55555555-5555-5555-5555-555555555555");

        public static Task<CrossAgentFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"cross-agent-compatibility-{Guid.NewGuid():N}");
            var codexHome = Path.Combine(root, "codex-home");
            var grokHome = Path.Combine(root, "grok-home");
            var codexSessions = Path.Combine(codexHome, "sessions");
            var grokSessions = Path.Combine(grokHome, "sessions");
            var claudeHome = Path.Combine(root, "claude-home");
            var claudeProjects = Path.Combine(claudeHome, "projects");
            var workingDirectory = Path.Combine(root, "project");
            Directory.CreateDirectory(codexSessions);
            Directory.CreateDirectory(grokSessions);
            Directory.CreateDirectory(claudeProjects);
            Directory.CreateDirectory(workingDirectory);
            return Task.FromResult(new CrossAgentFixture(
                root,
                new CodexPaths(codexHome, codexSessions, Path.Combine(codexHome, "archived_sessions"), Path.Combine(codexHome, "attachments")),
                new GrokPaths(grokHome, grokSessions),
                new ClaudePaths(claudeHome, claudeProjects),
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
