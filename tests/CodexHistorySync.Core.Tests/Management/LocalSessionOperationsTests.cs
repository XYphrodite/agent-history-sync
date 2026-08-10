using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Conversion;
using CodexHistorySync.Core.Grok;
using CodexHistorySync.Core.Management;

namespace CodexHistorySync.Core.Tests.Management;

public sealed class LocalSessionOperationsTests
{
    [Fact]
    public async Task CopyAsyncReadsCodexAndDispatchesPortableConversationOnlyToGrokWriter()
    {
        await using var fixture = new OperationsFixture();
        var path = await fixture.WriteCodexAsync("codex-source", "Codex source", "question", "answer");
        fixture.GrokWriter.Result = new ConversationWriteResult("grok-copy", "grok-destination");

        var result = await fixture.CreateOperations().CopyAsync(
            fixture.Session(ManagedAgent.Codex, "codex-source", path), CancellationToken.None);

        Assert.Equal("grok-copy", result);
        Assert.Empty(fixture.CodexWriter.Conversations);
        var copied = Assert.Single(fixture.GrokWriter.Conversations);
        Assert.Equal(ConversationAgent.Codex, copied.SourceAgent);
        Assert.Equal("codex-source", copied.SourceSessionId);
        Assert.Equal("Codex source", copied.Title);
        Assert.Equal(fixture.WorkingDirectory, copied.WorkingDirectory);
        Assert.Equal(
            [new PortableTurn(ConversationRole.User, "question"), new PortableTurn(ConversationRole.Assistant, "answer")],
            copied.Turns);
    }

    [Fact]
    public async Task CopyAsyncReadsGrokAndDispatchesPortableConversationOnlyToCodexWriter()
    {
        await using var fixture = new OperationsFixture();
        var id = "10000000-0000-0000-0000-000000000001";
        var path = await fixture.WriteGrokAsync(id, "Grok source", "question", "answer");
        fixture.CodexWriter.Result = new ConversationWriteResult("codex-copy", "codex-destination");

        var result = await fixture.CreateOperations().CopyAsync(
            fixture.Session(ManagedAgent.Grok, id, path), CancellationToken.None);

        Assert.Equal("codex-copy", result);
        Assert.Empty(fixture.GrokWriter.Conversations);
        var copied = Assert.Single(fixture.CodexWriter.Conversations);
        Assert.Equal(ConversationAgent.Grok, copied.SourceAgent);
        Assert.Equal(id, copied.SourceSessionId);
        Assert.Equal("Grok source", copied.Title);
    }

    [Fact]
    public async Task CopyAsyncRefusesCatalogActiveUnreadableAndNewlyActiveSources()
    {
        await using var fixture = new OperationsFixture();
        var declaredActivePath = await fixture.WriteCodexAsync("declared-active", "Active", "q", "a");
        var unreadablePath = await fixture.WriteCodexAsync("declared-unreadable", "Unreadable", "q", "a");
        var newlyActivePath = await fixture.WriteCodexAsync("newly-active", "Newly active", "q", "a");
        fixture.ActiveState.ActiveIds.Add("newly-active");

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.CreateOperations().CopyAsync(
            fixture.Session(ManagedAgent.Codex, "declared-active", declaredActivePath) with { IsActive = true },
            CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.CreateOperations().CopyAsync(
            fixture.Session(ManagedAgent.Codex, "declared-unreadable", unreadablePath) with { CanRead = false },
            CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.CreateOperations().CopyAsync(
            fixture.Session(ManagedAgent.Codex, "newly-active", newlyActivePath), CancellationToken.None));

        Assert.Empty(fixture.CodexWriter.Conversations);
        Assert.Empty(fixture.GrokWriter.Conversations);
        Assert.Contains(fixture.ActiveState.Checks, check => check.SessionId == "newly-active");
    }

    [Fact]
    public async Task CopyAsyncRefusesSourceOutsideItsExactAgentRoot()
    {
        await using var fixture = new OperationsFixture();
        var outsideDirectory = Directory.CreateDirectory(Path.Combine(fixture.Root, "outside"));
        var outsidePath = await fixture.WriteCodexAsync(
            "outside-source", "Outside", "q", "a", outsideDirectory.FullName);

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateOperations().CopyAsync(
            fixture.Session(ManagedAgent.Codex, "outside-source", outsidePath), CancellationToken.None));

        Assert.True(File.Exists(outsidePath));
        Assert.Empty(fixture.GrokWriter.Conversations);
    }

    [Fact]
    public async Task CopyAsyncRefusesReparsePointTarget()
    {
        await using var fixture = new OperationsFixture();
        var outsideDirectory = Directory.CreateDirectory(Path.Combine(fixture.Root, "outside-link-target"));
        var outsidePath = await fixture.WriteCodexAsync(
            "linked-source", "Linked", "q", "a", outsideDirectory.FullName);
        var link = Path.Combine(fixture.CodexPaths.Sessions, "linked-source.jsonl");
        try
        {
            File.CreateSymbolicLink(link, outsidePath);
            fixture.ReparsePaths.Add(link);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw Xunit.Sdk.SkipException.ForSkip(
                $"Symbolic-link creation is unavailable: {exception.GetType().Name}");
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateOperations().CopyAsync(
            fixture.Session(ManagedAgent.Codex, "linked-source", link), CancellationToken.None));

        Assert.True(File.Exists(outsidePath));
        Assert.Empty(fixture.GrokWriter.Conversations);
    }

    [Fact]
    public async Task CopyAsyncRefusesSourceWhoseBytesChangeDuringIdentityValidation()
    {
        await using var fixture = new OperationsFixture();
        var path = await fixture.WriteCodexAsync("changing-source", "Changing", "q", "a");
        var reader = new MutatingReader(new CodexConversationReader(), path);

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.CreateOperations(codexReader: reader).CopyAsync(
            fixture.Session(ManagedAgent.Codex, "changing-source", path), CancellationToken.None));

        Assert.True(reader.Mutated);
        Assert.Empty(fixture.GrokWriter.Conversations);
    }

    [Fact]
    public async Task DeleteAsyncDeletesExactlyOneCodexFileAndLeavesSyncArtifactsByteForByteUnchanged()
    {
        await using var fixture = new OperationsFixture();
        var target = await fixture.WriteCodexAsync("delete-codex", "Delete", "q", "a");
        var sibling = await fixture.WriteCodexAsync("keep-codex", "Keep", "q", "a");
        var before = await fixture.WriteAndSnapshotSyncArtifactsAsync();

        await fixture.CreateOperations().DeleteAsync(
            fixture.Session(ManagedAgent.Codex, "delete-codex", target), CancellationToken.None);

        Assert.False(File.Exists(target));
        Assert.True(File.Exists(sibling));
        Assert.Empty(fixture.DirectoryDeleter.Deletions);
        await fixture.AssertSyncArtifactsEqualAsync(before);
    }

    [Fact]
    public async Task DeleteAsyncDirectlyDeletesExactlyOneGrokDirectoryWithoutConfirmationDependency()
    {
        await using var fixture = new OperationsFixture();
        var targetId = "20000000-0000-0000-0000-000000000002";
        var siblingId = "30000000-0000-0000-0000-000000000003";
        var target = await fixture.WriteGrokAsync(targetId, "Delete", "q", "a");
        var sibling = await fixture.WriteGrokAsync(siblingId, "Keep", "q", "a");
        var parent = Directory.GetParent(target)!.FullName;
        var before = await fixture.WriteAndSnapshotSyncArtifactsAsync();

        await fixture.CreateOperations().DeleteAsync(
            fixture.Session(ManagedAgent.Grok, targetId, target), CancellationToken.None);

        Assert.False(Directory.Exists(target));
        Assert.True(Directory.Exists(sibling));
        Assert.True(Directory.Exists(parent));
        Assert.Equal((Path.GetFullPath(fixture.GrokPaths.Sessions), Path.GetFullPath(target)),
            Assert.Single(fixture.DirectoryDeleter.Deletions));
        await fixture.AssertSyncArtifactsEqualAsync(before);
    }

    [Fact]
    public async Task DeleteAsyncRechecksActiveStateImmediatelyBeforeMutation()
    {
        await using var fixture = new OperationsFixture();
        var path = await fixture.WriteCodexAsync("became-active", "Active", "q", "a");
        fixture.ActiveState.ActiveIds.Add("became-active");

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.CreateOperations().DeleteAsync(
            fixture.Session(ManagedAgent.Codex, "became-active", path), CancellationToken.None));

        Assert.True(File.Exists(path));
        Assert.Contains(fixture.ActiveState.Checks, check => check.SessionId == "became-active");
    }

    private sealed class OperationsFixture : IAsyncDisposable
    {
        private static readonly UTF8Encoding Utf8 = new(false);
        private readonly string container;
        private IReadOnlyDictionary<string, string> artifactPaths = new Dictionary<string, string>();

        public OperationsFixture()
        {
            container = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "codex-history-sync-task3-tests"));
            Directory.CreateDirectory(container);
            Root = Path.Combine(container, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            var codexHome = Path.Combine(Root, "codex-home");
            var grokHome = Path.Combine(Root, "grok-home");
            WorkingDirectory = Path.Combine(Root, "working-directory");
            Directory.CreateDirectory(codexHome);
            Directory.CreateDirectory(grokHome);
            Directory.CreateDirectory(WorkingDirectory);
            CodexPaths = CodexPaths.ResolveLayout(codexHome);
            GrokPaths = new GrokPaths(grokHome, Path.Combine(grokHome, "sessions"));
            Directory.CreateDirectory(CodexPaths.Sessions);
            Directory.CreateDirectory(CodexPaths.ArchivedSessions);
            Directory.CreateDirectory(GrokPaths.Sessions);
        }

        public string Root { get; }
        public string WorkingDirectory { get; }
        public CodexPaths CodexPaths { get; }
        public GrokPaths GrokPaths { get; }
        public FakeActiveState ActiveState { get; } = new();
        public RecordingWriter CodexWriter { get; } = new("codex-result");
        public RecordingWriter GrokWriter { get; } = new("grok-result");
        public RecordingDirectoryDeleter DirectoryDeleter { get; } = new();
        public List<string> ReparsePaths { get; } = [];

        public LocalSessionOperations CreateOperations(IConversationReader? codexReader = null) => new(
            CodexPaths,
            GrokPaths,
            ActiveState,
            DirectoryDeleter,
            CodexWriter,
            GrokWriter,
            codexReader ?? new CodexConversationReader(),
            new GrokConversationReader());

        public ManagedSession Session(ManagedAgent agent, string id, string path) =>
            new(agent, id, Path.GetFullPath(path), id, DateTimeOffset.UtcNow, IsActive: false, CanRead: true);

        public async Task<string> WriteCodexAsync(
            string id,
            string title,
            string user,
            string assistant,
            string? directory = null)
        {
            directory ??= Path.Combine(CodexPaths.Sessions, "2026", "08", "09");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"rollout-{id}.jsonl");
            var lines = new[]
            {
                JsonSerializer.Serialize(new
                {
                    type = "session_meta",
                    payload = new { id, timestamp = "2026-08-09T08:00:00Z", cwd = WorkingDirectory, title }
                }),
                JsonSerializer.Serialize(new
                {
                    type = "response_item",
                    payload = new
                    {
                        type = "message", role = "user", timestamp = "2026-08-09T09:00:00Z",
                        content = new[] { new { type = "input_text", text = user } }
                    }
                }),
                JsonSerializer.Serialize(new
                {
                    type = "response_item",
                    payload = new
                    {
                        type = "message", role = "assistant", timestamp = "2026-08-09T10:00:00Z",
                        content = new[] { new { type = "output_text", text = assistant } }
                    }
                })
            };
            await File.WriteAllTextAsync(path, string.Join('\n', lines) + "\n", Utf8);
            return path;
        }

        public async Task<string> WriteGrokAsync(
            string id,
            string title,
            string user,
            string assistant)
        {
            var session = GrokPaths.SessionDirectory(WorkingDirectory, id);
            Directory.CreateDirectory(session);
            var turns = new[]
            {
                JsonSerializer.Serialize(new
                {
                    role = "user", content = new[] { new { type = "input_text", text = user } }
                }),
                JsonSerializer.Serialize(new
                {
                    role = "assistant", content = new[] { new { type = "output_text", text = assistant } }
                })
            };
            await File.WriteAllTextAsync(Path.Combine(session, "chat_history.jsonl"),
                string.Join('\n', turns) + "\n", Utf8);
            await File.WriteAllTextAsync(Path.Combine(session, "summary.json"),
                JsonSerializer.Serialize(new
                {
                    info = new
                    {
                        id, cwd = WorkingDirectory, title,
                        created_at = "2026-08-09T08:00:00Z", updated_at = "2026-08-09T10:00:00Z"
                    }
                }), Utf8);
            return session;
        }

        public async Task<IReadOnlyDictionary<string, byte[]>> WriteAndSnapshotSyncArtifactsAsync()
        {
            var syncRoot = Path.Combine(Root, "sync-state");
            var paths = new Dictionary<string, string>
            {
                ["state"] = Path.Combine(syncRoot, "state", "local-state.json"),
                ["tombstone"] = Path.Combine(syncRoot, "state", "tombstones", "deleted.json"),
                ["manifest"] = Path.Combine(syncRoot, "repository", "index.json"),
                ["conflict"] = Path.Combine(syncRoot, "conflicts", "conflict-1", "record.json")
            };
            foreach (var pair in paths)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(pair.Value)!);
                await File.WriteAllBytesAsync(pair.Value, Encoding.UTF8.GetBytes(pair.Key + "-before"));
            }
            artifactPaths = paths;
            return await SnapshotAsync(paths);
        }

        public async Task AssertSyncArtifactsEqualAsync(IReadOnlyDictionary<string, byte[]> expected)
        {
            var actual = await SnapshotAsync(artifactPaths);
            Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
            foreach (var key in expected.Keys) Assert.Equal(expected[key], actual[key]);
        }

        private static async Task<IReadOnlyDictionary<string, byte[]>> SnapshotAsync(
            IReadOnlyDictionary<string, string> paths)
        {
            var result = new Dictionary<string, byte[]>();
            foreach (var pair in paths) result[pair.Key] = await File.ReadAllBytesAsync(pair.Value);
            return result;
        }

        public ValueTask DisposeAsync()
        {
            foreach (var link in ReparsePaths)
            {
                if ((Directory.Exists(link) || File.Exists(link)) &&
                    File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint))
                    File.Delete(link);
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
        public List<(ManagedAgent Agent, string SessionId, string NativePath)> Checks { get; } = [];

        public Task<bool> IsActiveAsync(
            ManagedAgent agent,
            string sessionId,
            string nativePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Checks.Add((agent, sessionId, nativePath));
            return Task.FromResult(ActiveIds.Contains(sessionId));
        }
    }

    private sealed class RecordingWriter(string defaultSessionId) : IConversationWriter
    {
        public List<PortableConversation> Conversations { get; } = [];
        public ConversationWriteResult Result { get; set; } = new(defaultSessionId, defaultSessionId + "-path");

        public Task<ConversationWriteResult> WriteAsync(
            PortableConversation conversation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Conversations.Add(conversation);
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingDirectoryDeleter : IManagedSessionDirectoryDeleter
    {
        public List<(string Root, string Target)> Deletions { get; } = [];

        public Task DeleteAsync(string sessionsRoot, string sessionDirectory, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sessionsRoot));
            var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sessionDirectory));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Deletion target escaped its exact test root.");
            Deletions.Add((root, target));
            Directory.Delete(target, recursive: true);
            return Task.CompletedTask;
        }
    }

    private sealed class MutatingReader(IConversationReader inner, string path) : IConversationReader
    {
        public bool Mutated { get; private set; }

        public async Task<PortableConversation> ReadAsync(string nativePath, CancellationToken cancellationToken)
        {
            var result = await inner.ReadAsync(nativePath, cancellationToken);
            await File.AppendAllTextAsync(path, "\n", cancellationToken);
            Mutated = true;
            return result;
        }
    }
}
