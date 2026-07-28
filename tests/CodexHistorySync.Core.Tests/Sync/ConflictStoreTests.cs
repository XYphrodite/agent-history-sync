using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Crypto;
using CodexHistorySync.Core.Model;
using CodexHistorySync.Core.Sync;

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

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private Fixture CreateFixture()
    {
        var home = Path.Combine(_root, "codex");
        Directory.CreateDirectory(home);
        var paths = CodexPaths.Resolve(home);
        var crypto = new RepositoryCrypto();
        var key = RandomNumberGenerator.GetBytes(RepositoryCrypto.MasterKeySize);
        var metadata = new EnvelopeMetadata(1, new LogicalObjectId("object-1"), ObjectKind.ActiveSession);
        return new Fixture(paths, crypto, key, metadata, new ConflictStore("repo", Path.Combine(_root, "local"), paths));
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
}
