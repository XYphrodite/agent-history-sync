using System.Security.Cryptography;
using CodexHistorySync.Core.IO;
using CodexHistorySync.Core.Model;
using Xunit.Sdk;

namespace CodexHistorySync.Core.Tests.Codex;

public sealed class CodexHistoryWriterTestsAtomicSafety : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"codex-history-sync-atomic-{Guid.NewGuid():N}");

    [Fact]
    public async Task DeleteIfUnchangedAsync_WhenFailureOccursAfterCapture_RestoresOriginalAndExposesConcurrentEvidence()
    {
        Directory.CreateDirectory(_root);
        var destination = Path.Combine(_root, "chat.jsonl");
        var original = "original"u8.ToArray();
        await File.WriteAllBytesAsync(destination, original);
        var hooks = new TestHooks
        {
            AfterDeleteCapture = (_, path) =>
            {
                File.WriteAllText(path, "concurrent");
                throw new IOException("Injected post-capture failure.");
            }
        };
        var fileSystem = new AtomicFileSystem(hooks);

        var error = await Assert.ThrowsAsync<AtomicMutationException>(() =>
            fileSystem.DeleteIfUnchangedAsync(destination, Hash(original), () => true, CancellationToken.None));

        Assert.Equal(original, await File.ReadAllBytesAsync(destination));
        var evidence = Assert.Single(error.PreservedPaths);
        Assert.Equal("concurrent", await File.ReadAllTextAsync(evidence));
        AssertArtifactsAccountedFor(destination, error.PreservedPaths);
    }

    [Fact]
    public async Task DeleteIfUnchangedAsync_WhenCleanupFails_RestoresOriginalWithoutHiddenArtifacts()
    {
        Directory.CreateDirectory(_root);
        var destination = Path.Combine(_root, "chat.jsonl");
        var original = "original"u8.ToArray();
        await File.WriteAllBytesAsync(destination, original);
        var hooks = new TestHooks { BeforeArtifactCleanup = _ => throw new IOException("Injected cleanup failure.") };
        var fileSystem = new AtomicFileSystem(hooks);

        var error = await Assert.ThrowsAsync<AtomicMutationException>(() =>
            fileSystem.DeleteIfUnchangedAsync(destination, Hash(original), () => true, CancellationToken.None));

        Assert.Equal(original, await File.ReadAllBytesAsync(destination));
        Assert.Empty(error.PreservedPaths);
        AssertArtifactsAccountedFor(destination, []);
    }

    [Fact]
    public async Task DeleteIfUnchangedAsync_WhenCancelledAfterCapture_RecoversNonCancellably()
    {
        Directory.CreateDirectory(_root);
        var destination = Path.Combine(_root, "cancelled-delete.jsonl");
        var original = "original"u8.ToArray();
        await File.WriteAllBytesAsync(destination, original);
        using var cancellation = new CancellationTokenSource();
        var hooks = new TestHooks
        {
            AfterDeleteCapture = (_, _) =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }
        };

        var error = await Assert.ThrowsAsync<AtomicMutationException>(() => new AtomicFileSystem(hooks)
            .DeleteIfUnchangedAsync(destination, Hash(original), () => true, CancellationToken.None));

        Assert.IsType<OperationCanceledException>(error.InnerException);
        Assert.Equal(original, await File.ReadAllBytesAsync(destination));
        AssertArtifactsAccountedFor(destination, error.PreservedPaths);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PublishAsync_WhenStagingChangesAfterInitialHash_RejectsWithoutMutatingDestination(bool existingDestination, bool replaceStagingPath)
    {
        Directory.CreateDirectory(_root);
        var destination = Path.Combine(_root, $"chat-{existingDestination}.jsonl");
        var temporary = Path.Combine(_root, $".chat-{existingDestination}.tmp");
        var incoming = "authenticated"u8.ToArray();
        var original = "original"u8.ToArray();
        await File.WriteAllBytesAsync(temporary, incoming);
        if (existingDestination) await File.WriteAllBytesAsync(destination, original);
        var hooks = new TestHooks
        {
            AfterSourceHash = path =>
            {
                if (replaceStagingPath) File.Move(path, path + ".superseded");
                File.WriteAllText(path, "tampered");
            }
        };
        var fileSystem = new AtomicFileSystem(hooks);

        await Assert.ThrowsAsync<InvalidDataException>(() => fileSystem.PublishAsync(
            temporary,
            destination,
            Hash(incoming),
            existingDestination ? Hash(original) : null,
            () => true,
            CancellationToken.None));

        if (existingDestination) Assert.Equal(original, await File.ReadAllBytesAsync(destination));
        else Assert.False(File.Exists(destination));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishAsync_WhenGuardRejectsAfterHash_DoesNotPublish(bool existingDestination)
    {
        Directory.CreateDirectory(_root);
        var destination = Path.Combine(_root, $"guard-{existingDestination}.jsonl");
        var temporary = Path.Combine(_root, $".guard-{existingDestination}.tmp");
        var incoming = "authenticated"u8.ToArray();
        var original = "original"u8.ToArray();
        await File.WriteAllBytesAsync(temporary, incoming);
        if (existingDestination) await File.WriteAllBytesAsync(destination, original);
        var hashCompleted = false;
        var fileSystem = new AtomicFileSystem(new TestHooks { AfterSourceHash = _ => hashCompleted = true });

        await Assert.ThrowsAsync<InvalidOperationException>(() => fileSystem.PublishAsync(
            temporary, destination, Hash(incoming), existingDestination ? Hash(original) : null,
            () => !hashCompleted, CancellationToken.None));

        if (existingDestination) Assert.Equal(original, await File.ReadAllBytesAsync(destination));
        else Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task DeleteIfUnchangedAsync_WhenGuardRejectsAfterHash_DoesNotDelete()
    {
        Directory.CreateDirectory(_root);
        var destination = Path.Combine(_root, "guard-delete.jsonl");
        var original = "original"u8.ToArray();
        await File.WriteAllBytesAsync(destination, original);
        var hashCompleted = false;
        var fileSystem = new AtomicFileSystem(new TestHooks { AfterDestinationHash = _ => hashCompleted = true });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fileSystem.DeleteIfUnchangedAsync(destination, Hash(original), () => !hashCompleted, CancellationToken.None));

        Assert.Equal(original, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task PublishAsync_WhenMutationBoundaryDetectsReparse_DoesNotPublish()
    {
        Directory.CreateDirectory(_root);
        var destination = Path.Combine(_root, "reparse.jsonl");
        var temporary = Path.Combine(_root, ".reparse.tmp");
        var incoming = "authenticated"u8.ToArray();
        await File.WriteAllBytesAsync(temporary, incoming);
        var fileSystem = new AtomicFileSystem(new TestHooks { BeforeMutationPathValidation = _ => throw new ArgumentException("Injected reparse boundary.") });

        await Assert.ThrowsAsync<ArgumentException>(() =>
            fileSystem.PublishAsync(temporary, destination, Hash(incoming), null, () => true, CancellationToken.None));

        Assert.False(File.Exists(destination));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishAsync_WhenFailureOccursAfterNamespaceMutation_RecoversOrExposesAllEvidence(bool existingDestination)
    {
        Directory.CreateDirectory(_root);
        var destination = Path.Combine(_root, $"post-mutation-{existingDestination}.jsonl");
        var temporary = Path.Combine(_root, $".post-mutation-{existingDestination}.tmp");
        var incoming = "incoming"u8.ToArray();
        var original = "original"u8.ToArray();
        await File.WriteAllBytesAsync(temporary, incoming);
        if (existingDestination) await File.WriteAllBytesAsync(destination, original);
        var fileSystem = new AtomicFileSystem(new TestHooks { AfterPublishMutation = _ => throw new IOException("Injected post-mutation failure.") });

        var error = await Assert.ThrowsAsync<AtomicMutationException>(() => fileSystem.PublishAsync(
            temporary, destination, Hash(incoming), existingDestination ? Hash(original) : null, () => true, CancellationToken.None));

        if (existingDestination) Assert.Equal(original, await File.ReadAllBytesAsync(destination));
        else Assert.False(File.Exists(destination));
        Assert.All(error.PreservedPaths, path => Assert.True(File.Exists(path)));
        AssertArtifactsAccountedFor(destination, error.PreservedPaths);
    }

    [Fact]
    public async Task PublishAsync_WhenStagingIsSymbolicLink_RejectsWithoutPublishing()
    {
        Directory.CreateDirectory(_root);
        var outside = Path.Combine(_root, "outside.bin");
        var temporary = Path.Combine(_root, ".linked.tmp");
        var destination = Path.Combine(_root, "linked.jsonl");
        var bytes = "authenticated"u8.ToArray();
        await File.WriteAllBytesAsync(outside, bytes);
        try { File.CreateSymbolicLink(temporary, outside); }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            throw SkipException.ForSkip($"Symbolic-link creation is unavailable: {exception.GetType().Name}");
        }

        await Assert.ThrowsAsync<ArgumentException>(() => new AtomicFileSystem().PublishAsync(
            temporary, destination, Hash(bytes), null, () => true, CancellationToken.None));

        Assert.False(File.Exists(destination));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(outside));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static ContentHash Hash(byte[] bytes) => new(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());

    private static void AssertArtifactsAccountedFor(string destination, IReadOnlyCollection<string> preserved)
    {
        var suffixes = new[] { ".tmp", ".displaced", ".rejected", ".deleted", ".appeared", ".preserved-concurrent" };
        var artifacts = Directory.EnumerateFiles(Path.GetDirectoryName(destination)!)
            .Where(path => suffixes.Any(suffix => path.EndsWith(suffix, StringComparison.Ordinal)))
            .ToArray();
        Assert.Equal(preserved.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), artifacts.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
    }

    private sealed class TestHooks : IAtomicFileSystemHooks
    {
        public Action<string>? AfterSourceHash { get; init; }
        public Action<string>? AfterDestinationHash { get; init; }
        public Action<string, string>? AfterDeleteCapture { get; init; }
        public Action<string>? BeforeArtifactCleanup { get; init; }
        public Action<string>? BeforeMutationPathValidation { get; init; }
        public Action<string>? AfterPublishMutation { get; init; }

        void IAtomicFileSystemHooks.OnAfterSourceHash(string path) => AfterSourceHash?.Invoke(path);
        void IAtomicFileSystemHooks.OnAfterDestinationHash(string path) => AfterDestinationHash?.Invoke(path);
        void IAtomicFileSystemHooks.OnAfterDeleteCapture(string quarantinePath, string destinationPath) => AfterDeleteCapture?.Invoke(quarantinePath, destinationPath);
        void IAtomicFileSystemHooks.OnBeforeArtifactCleanup(string path) => BeforeArtifactCleanup?.Invoke(path);
        void IAtomicFileSystemHooks.OnBeforeMutationPathValidation(string path) => BeforeMutationPathValidation?.Invoke(path);
        void IAtomicFileSystemHooks.OnAfterPublishMutation(string destinationPath) => AfterPublishMutation?.Invoke(destinationPath);
    }
}
