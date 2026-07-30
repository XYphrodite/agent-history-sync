using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Crypto;
using CodexHistorySync.Core.IO;
using CodexHistorySync.Core.Model;
using CodexHistorySync.Core.Sync;
using Xunit.Sdk;

namespace CodexHistorySync.Core.Tests.Sync;

public sealed class ConflictStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"codex-history-sync-conflicts-{Guid.NewGuid():N}");

    [Fact]
    public async Task PreserveAndListAsync_RetainsBothEncryptedVersionsAndProvenance()
    {
        var fixture = CreateFixture();
        var provenance = Provenance();
        var local = await EncryptAsync(fixture, "{\"thread\":\"same-id\",\"side\":\"local\"}\n");
        var remote = await EncryptAsync(fixture, "{\"thread\":\"same-id\",\"side\":\"remote\"}\n");

        var preserved = await fixture.Store.PreserveAsync(provenance, new MemoryStream(local), new MemoryStream(remote), CancellationToken.None);
        var listed = Assert.Single(await fixture.Store.ListAsync(CancellationToken.None));

        Assert.Equal(preserved.Id, listed.Id);
        Assert.Equal(provenance, listed.Provenance);
        Assert.Equal(local, await File.ReadAllBytesAsync(listed.LocalEncryptedPath));
        Assert.Equal(remote, await File.ReadAllBytesAsync(listed.RemoteEncryptedPath));
    }

    [Fact]
    public async Task PreserveAsync_SameContentFingerprintAcrossCallsReturnsExistingRecord()
    {
        var fixture = CreateFixture();
        var provenance = Provenance();

        var first = await fixture.Store.PreserveAsync(provenance,
            new MemoryStream(await EncryptAsync(fixture, "local\n")),
            new MemoryStream(await EncryptAsync(fixture, "remote\n")), CancellationToken.None);
        var second = await fixture.Store.PreserveAsync(provenance with
        {
            LocalTimestampUtc = provenance.LocalTimestampUtc.AddHours(1),
            RemoteTimestampUtc = provenance.RemoteTimestampUtc.AddHours(1)
        }, new MemoryStream(await EncryptAsync(fixture, "local\n")),
            new MemoryStream(await EncryptAsync(fixture, "remote\n")), CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        Assert.Single(await fixture.Store.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ResolveExportBothAsync_ExportsExactPlaintextWithoutRewritingIds()
    {
        var fixture = CreateFixture();
        const string localText = "{\"type\":\"session_meta\",\"payload\":{\"id\":\"same-thread\"},\"side\":\"local\"}\n";
        const string remoteText = "{\"type\":\"session_meta\",\"payload\":{\"id\":\"same-thread\"},\"side\":\"remote\"}\n";
        var conflict = await fixture.Store.PreserveAsync(
            Provenance(),
            new MemoryStream(await EncryptAsync(fixture, localText)),
            new MemoryStream(await EncryptAsync(fixture, remoteText)),
            CancellationToken.None);
        var destination = Path.GetFullPath(Path.Combine(_root, "exports", "chosen-conflict"));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        var result = await fixture.Store.ResolveAsync(conflict.Id, ConflictResolution.ExportBoth, destination, fixture.Crypto, fixture.Key, CancellationToken.None);

        Assert.Equal(localText, await File.ReadAllTextAsync(result.LocalPlaintextPath!));
        Assert.Equal(remoteText, await File.ReadAllTextAsync(result.RemotePlaintextPath!));
        Assert.DoesNotContain(Directory.EnumerateFiles(destination), path => Path.GetFileName(path).Contains("same-thread", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResolveExportBothAsync_RefusesOverwriteTraversalAndCodexDestination()
    {
        var fixture = CreateFixture();
        var encrypted = await EncryptAsync(fixture, "{}\n");
        var conflict = await fixture.Store.PreserveAsync(Provenance(), new MemoryStream(encrypted), new MemoryStream(encrypted), CancellationToken.None);
        var existing = Path.GetFullPath(Path.Combine(_root, "existing"));
        Directory.CreateDirectory(existing);

        await Assert.ThrowsAsync<IOException>(() => fixture.Store.ResolveAsync(conflict.Id, ConflictResolution.ExportBoth, existing, fixture.Crypto, fixture.Key, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Store.ResolveAsync(conflict.Id, ConflictResolution.ExportBoth, Path.Combine(_root, "exports", "..", "escape"), fixture.Crypto, fixture.Key, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Store.ResolveAsync(conflict.Id, ConflictResolution.ExportBoth, Path.Combine(fixture.Paths.Sessions, "export"), fixture.Crypto, fixture.Key, CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreserveAsync_WhenEitherEnvelopeFails_LeavesNoPartialRecordOrStagingDirectory(bool failFirst)
    {
        var fixture = CreateFixture();
        var encrypted = await EncryptAsync(fixture, "{}\n");

        await Assert.ThrowsAsync<IOException>(() => fixture.Store.PreserveAsync(
            Provenance(),
            failFirst ? new FailingReadStream(encrypted) : new MemoryStream(encrypted),
            failFirst ? new MemoryStream(encrypted) : new FailingReadStream(encrypted),
            CancellationToken.None));

        Assert.Empty(await fixture.Store.ListAsync(CancellationToken.None));
        AssertNoStagingDirectories(fixture.Store.RootPath);
    }

    [Fact]
    public async Task PreserveAsync_WhenCancelledAfterFirstEnvelope_LeavesNoPartialRecordOrStagingDirectory()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = CreateFixture(hooks: new TestConflictHooks { AfterFirstEnvelope = cancellation.Cancel });
        var encrypted = await EncryptAsync(fixture, "{}\n");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Store.PreserveAsync(
            Provenance(), new MemoryStream(encrypted), new MemoryStream(encrypted), cancellation.Token));

        Assert.Empty(await fixture.Store.ListAsync(CancellationToken.None));
        AssertNoStagingDirectories(fixture.Store.RootPath);
    }

    [Fact]
    public async Task PreserveAsync_WhenDirectoryPublicationFails_LeavesNoVisibleRecordOrStagingDirectory()
    {
        var fixture = CreateFixture(publisher: new FailingDirectoryPublisher());
        var encrypted = await EncryptAsync(fixture, "{}\n");

        await Assert.ThrowsAsync<IOException>(() => fixture.Store.PreserveAsync(
            Provenance(), new MemoryStream(encrypted), new MemoryStream(encrypted), CancellationToken.None));

        Assert.Empty(await fixture.Store.ListAsync(CancellationToken.None));
        AssertNoStagingDirectories(fixture.Store.RootPath);
    }

    [Fact]
    public async Task ResolveExportBothAsync_WhenSecondEnvelopeIsInvalid_LeavesNoPlaintextOrStagingDirectory()
    {
        var fixture = CreateFixture();
        var valid = await EncryptAsync(fixture, "{\"side\":\"local\"}\n");
        var invalid = valid.ToArray();
        invalid[^1] ^= 0x40;
        var conflict = await fixture.Store.PreserveAsync(Provenance(), new MemoryStream(valid), new MemoryStream(invalid), CancellationToken.None);
        var destination = Path.GetFullPath(Path.Combine(_root, "exports", "invalid-remote"));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        await Assert.ThrowsAnyAsync<CryptographicException>(() => fixture.Store.ResolveAsync(
            conflict.Id, ConflictResolution.ExportBoth, destination, fixture.Crypto, fixture.Key, CancellationToken.None));

        Assert.False(Directory.Exists(destination));
        AssertNoStagingDirectories(Path.GetDirectoryName(destination)!);
    }

    [Fact]
    public async Task ResolveExportBothAsync_WhenCancelledAfterFirstPlaintext_LeavesNoPlaintextOrStagingDirectory()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = CreateFixture(hooks: new TestConflictHooks { AfterFirstPlaintext = cancellation.Cancel });
        var encrypted = await EncryptAsync(fixture, "{}\n");
        var conflict = await fixture.Store.PreserveAsync(Provenance(), new MemoryStream(encrypted), new MemoryStream(encrypted), CancellationToken.None);
        var destination = Path.GetFullPath(Path.Combine(_root, "exports", "cancelled"));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Store.ResolveAsync(
            conflict.Id, ConflictResolution.ExportBoth, destination, fixture.Crypto, fixture.Key, cancellation.Token));

        Assert.False(Directory.Exists(destination));
        AssertNoStagingDirectories(Path.GetDirectoryName(destination)!);
    }

    [Fact]
    public async Task ResolveExportBothAsync_WhenDirectoryPublicationFails_LeavesNoPlaintextOrStagingDirectory()
    {
        var fixture = CreateFixture(publisher: new FailingDirectoryPublisher(failOnCall: 2));
        var encrypted = await EncryptAsync(fixture, "{}\n");
        var conflict = await fixture.Store.PreserveAsync(Provenance(), new MemoryStream(encrypted), new MemoryStream(encrypted), CancellationToken.None);
        var destination = Path.GetFullPath(Path.Combine(_root, "exports", "publish-failure"));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        await Assert.ThrowsAsync<IOException>(() => fixture.Store.ResolveAsync(
            conflict.Id, ConflictResolution.ExportBoth, destination, fixture.Crypto, fixture.Key, CancellationToken.None));

        Assert.False(Directory.Exists(destination));
        AssertNoStagingDirectories(Path.GetDirectoryName(destination)!);
    }

    [Fact]
    public async Task PreserveAsync_WhenCancelledAfterSecondEnvelope_DoesNotPublish()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = CreateFixture(hooks: new TestConflictHooks { BeforePreservePublication = cancellation.Cancel });
        var encrypted = await EncryptAsync(fixture, "{}\n");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Store.PreserveAsync(
            Provenance(), new MemoryStream(encrypted), new MemoryStream(encrypted), cancellation.Token));

        Assert.Empty(await fixture.Store.ListAsync(CancellationToken.None));
        AssertNoStagingDirectories(fixture.Store.RootPath);
    }

    [Fact]
    public async Task ResolveExportBothAsync_WhenCancelledAfterSecondPlaintext_DoesNotPublish()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = CreateFixture(hooks: new TestConflictHooks { BeforeExportPublication = cancellation.Cancel });
        var encrypted = await EncryptAsync(fixture, "{}\n");
        var conflict = await fixture.Store.PreserveAsync(Provenance(), new MemoryStream(encrypted), new MemoryStream(encrypted), CancellationToken.None);
        var destination = Path.GetFullPath(Path.Combine(_root, "exports", "cancelled-after-second"));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Store.ResolveAsync(
            conflict.Id, ConflictResolution.ExportBoth, destination, fixture.Crypto, fixture.Key, cancellation.Token));

        Assert.False(Directory.Exists(destination));
        AssertNoStagingDirectories(Path.GetDirectoryName(destination)!);
    }

    [Fact]
    public async Task ResolveExportBothAsync_WhenPrimaryAndCleanupFail_ExposesPlaintextStagingEvidence()
    {
        var fixture = CreateFixture(cleaner: new FailingStagingDirectoryCleaner());
        var valid = await EncryptAsync(fixture, "{}\n");
        var invalid = valid.ToArray();
        invalid[^1] ^= 0x20;
        var conflict = await fixture.Store.PreserveAsync(Provenance(), new MemoryStream(valid), new MemoryStream(invalid), CancellationToken.None);
        var destination = Path.GetFullPath(Path.Combine(_root, "exports", "cleanup-failure"));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        var error = await Assert.ThrowsAsync<AtomicMutationException>(() => fixture.Store.ResolveAsync(
            conflict.Id, ConflictResolution.ExportBoth, destination, fixture.Crypto, fixture.Key, CancellationToken.None));

        var staging = Assert.Single(error.PreservedPaths);
        Assert.True(Directory.Exists(staging));
        Assert.Contains(Directory.EnumerateFiles(staging), path => path.EndsWith(".local.jsonl", StringComparison.Ordinal));
        Assert.IsType<AggregateException>(error.InnerException);
    }

    [Fact]
    public async Task ListAsync_RejectsSymbolicLinkConflictRecordDirectory()
    {
        var fixture = CreateFixture();
        var encrypted = await EncryptAsync(fixture, "{}\n");
        var conflict = await fixture.Store.PreserveAsync(Provenance(), new MemoryStream(encrypted), new MemoryStream(encrypted), CancellationToken.None);
        var outside = Path.Combine(_root, "outside-conflict-record");
        Directory.Move(conflict.DirectoryPath, outside);
        try { Directory.CreateSymbolicLink(conflict.DirectoryPath, outside); }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            throw SkipException.ForSkip($"Symbolic-link creation is unavailable: {exception.GetType().Name}");
        }

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Store.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RetireAsync_WhenAtomicRenameFails_LeavesLiveEvidenceForRetry()
    {
        var fixture = CreateFixture(retirement: new FailingRetirementFileSystem(failMove: true));
        var encrypted = await EncryptAsync(fixture, "{}\n");
        var conflict = await fixture.Store.PreserveAsync(Provenance(), new MemoryStream(encrypted),
            new MemoryStream(encrypted), CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(() => fixture.Store.RetireAsync(conflict.Id, CancellationToken.None));

        Assert.Single(await fixture.Store.ListAsync(CancellationToken.None));
        var retry = new ConflictStore("repo", Path.Combine(_root, "local"), fixture.Paths);
        await retry.RetireAsync(conflict.Id, CancellationToken.None);
        Assert.Empty(await retry.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RetireAsync_DeleteFailureIsResolvedAndRestartCleansRetiredArtifact()
    {
        var fixture = CreateFixture(retirement: new FailingRetirementFileSystem(failDelete: true));
        var encrypted = await EncryptAsync(fixture, "{}\n");
        var conflict = await fixture.Store.PreserveAsync(Provenance(), new MemoryStream(encrypted),
            new MemoryStream(encrypted), CancellationToken.None);

        await fixture.Store.RetireAsync(conflict.Id, CancellationToken.None);

        Assert.Empty(await fixture.Store.ListAsync(CancellationToken.None));
        Assert.Single(Directory.EnumerateDirectories(fixture.Store.RootPath),
            path => Path.GetFileName(path).Contains(".resolved-", StringComparison.Ordinal));
        var restarted = new ConflictStore("repo", Path.Combine(_root, "local"), fixture.Paths);
        Assert.Empty(await restarted.ListAsync(CancellationToken.None));
        Assert.DoesNotContain(Directory.EnumerateDirectories(restarted.RootPath),
            path => Path.GetFileName(path).Contains(".resolved-", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private Fixture CreateFixture(IAtomicDirectoryPublisher? publisher = null, IConflictStoreHooks? hooks = null,
        IStagingDirectoryCleaner? cleaner = null, IConflictRetirementFileSystem? retirement = null)
    {
        var home = Path.Combine(_root, "codex");
        Directory.CreateDirectory(home);
        var paths = CodexPaths.Resolve(home);
        var crypto = new RepositoryCrypto();
        var key = RandomNumberGenerator.GetBytes(RepositoryCrypto.MasterKeySize);
        var metadata = new EnvelopeMetadata(1, new LogicalObjectId("object-1"), ObjectKind.ActiveSession);
        return new Fixture(paths, crypto, key, metadata, new ConflictStore("repo", Path.Combine(_root, "local"),
            paths, publisher, hooks, cleaner, retirement));
    }

    private static ConflictProvenance Provenance() => new(
        new EnvelopeMetadata(1, new LogicalObjectId("object-1"), ObjectKind.ActiveSession),
        new ContentHash("local-hash"), new ContentHash("remote-hash"), new ContentHash("baseline-hash"),
        "local-device", "remote-device",
        new DateTimeOffset(2026, 7, 28, 10, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 7, 28, 11, 0, 0, TimeSpan.Zero));

    private static async Task<byte[]> EncryptAsync(Fixture fixture, string text)
    {
        await using var output = new MemoryStream();
        await fixture.Crypto.EncryptAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), output, fixture.Key, fixture.Metadata, CancellationToken.None);
        return output.ToArray();
    }

    private sealed record Fixture(CodexPaths Paths, RepositoryCrypto Crypto, byte[] Key, EnvelopeMetadata Metadata, ConflictStore Store);

    private static void AssertNoStagingDirectories(string root)
    {
        if (!Directory.Exists(root)) return;
        Assert.DoesNotContain(Directory.EnumerateDirectories(root), path => Path.GetFileName(path).EndsWith(".tmp", StringComparison.Ordinal));
    }

    private sealed class FailingDirectoryPublisher(int failOnCall = 1) : IAtomicDirectoryPublisher
    {
        private int _calls;
        public void Publish(string stagingPath, string destinationPath)
        {
            _calls++;
            if (_calls == failOnCall) throw new IOException("Injected directory publication failure.");
            Directory.Move(stagingPath, destinationPath);
        }
    }

    private sealed class TestConflictHooks : IConflictStoreHooks
    {
        public Action? AfterFirstEnvelope { get; init; }
        public Action? AfterFirstPlaintext { get; init; }
        public Action? BeforePreservePublication { get; init; }
        public Action? BeforeExportPublication { get; init; }
        void IConflictStoreHooks.OnAfterFirstEnvelope() => AfterFirstEnvelope?.Invoke();
        void IConflictStoreHooks.OnAfterFirstPlaintext() => AfterFirstPlaintext?.Invoke();
        void IConflictStoreHooks.OnBeforePreservePublication() => BeforePreservePublication?.Invoke();
        void IConflictStoreHooks.OnBeforeExportPublication() => BeforeExportPublication?.Invoke();
    }

    private sealed class FailingStagingDirectoryCleaner : IStagingDirectoryCleaner
    {
        public void Delete(string path) => throw new IOException("Injected staging cleanup failure.");
    }

    private sealed class FailingRetirementFileSystem(bool failMove = false, bool failDelete = false)
        : IConflictRetirementFileSystem
    {
        public void Move(string source, string destination)
        {
            if (failMove) throw new IOException("Injected retirement rename failure.");
            Directory.Move(source, destination);
        }

        public void Delete(string path)
        {
            if (failDelete) throw new IOException("Injected retired cleanup failure.");
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class FailingReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        private bool _failed;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_failed) return ValueTask.FromException<int>(new IOException("Injected stream failure."));
            _failed = true;
            var count = Math.Min(buffer.Length, Math.Max(1, (int)(Length / 2)));
            return base.ReadAsync(buffer[..count], cancellationToken);
        }
    }
}
