using CodexHistorySync.Core.Crypto;
using CodexHistorySync.Core.Model;
using CodexHistorySync.Core.State;
using CodexHistorySync.Core.Sync;

namespace CodexHistorySync.Core.Tests.State;

public sealed class LocalStateStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"CodexHistorySync-{Guid.NewGuid():N}");

    [Fact]
    public async Task SaveAndLoadAsync_RoundTripsDeviceState()
    {
        var store = new LocalStateStore(_root);
        var state = new DeviceState(
            SchemaVersion: 1,
            RepositoryId: "repository-1",
            Objects: [Version("session-1", "abc")]);

        await store.SaveAsync(state, CancellationToken.None);
        var loaded = await store.LoadAsync("repository-1", CancellationToken.None);

        Assert.Equal(state.SchemaVersion, loaded.SchemaVersion);
        Assert.Equal(state.RepositoryId, loaded.RepositoryId);
        Assert.Equal(state.Objects, loaded.Objects);
        Assert.Equal(new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff },
            new RepositoryManifest(1, new Argon2Parameters(Convert.FromHexString("00112233445566778899AABBCCDDEEFF"), 65_536, 3, 2)).Argon2Parameters.Salt);
        Assert.Equal(Path.Combine(_root, "CodexHistorySync", "repositories", "repository-1", "state.json"), store.GetStatePath("repository-1"));
    }

    [Fact]
    public async Task LoadAsync_RejectsUnknownSchemaBeforeReturningState()
    {
        var store = new LocalStateStore(_root);
        var path = store.GetStatePath("repository-1");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{\"schemaVersion\":2,\"repositoryId\":\"repository-1\",\"objects\":[]}");

        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync("repository-1", CancellationToken.None));
    }

    [Fact]
    public async Task LoadAsync_RejectsDuplicateObjectIdsBeforeReturningState()
    {
        var store = new LocalStateStore(_root);
        var path = store.GetStatePath("repository-1");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{\"schemaVersion\":1,\"repositoryId\":\"repository-1\",\"objects\":[{\"id\":{\"value\":\"same\"},\"kind\":0,\"plaintextHash\":{\"hex\":\"a\"},\"revision\":\"a\",\"isDeleted\":false},{\"id\":{\"value\":\"same\"},\"kind\":0,\"plaintextHash\":{\"hex\":\"b\"},\"revision\":\"b\",\"isDeleted\":false}]}");

        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync("repository-1", CancellationToken.None));
    }

    [Fact]
    public async Task SaveAsync_WhenReplacingStateFails_PreservesExistingStateAndCleansTemporaryFile()
    {
        var repositoryId = "repository-1";
        var baseline = new DeviceState(1, repositoryId, [Version("session-1", "baseline")]);
        var updated = new DeviceState(1, repositoryId, [Version("session-1", "updated")]);
        var store = new LocalStateStore(_root);
        await store.SaveAsync(baseline, CancellationToken.None);
        var failingStore = new LocalStateStore(_root, new FailingStateFileReplacer());

        await Assert.ThrowsAsync<IOException>(() => failingStore.SaveAsync(updated, CancellationToken.None));

        var loaded = await store.LoadAsync(repositoryId, CancellationToken.None);
        Assert.Equal(baseline.Objects, loaded.Objects);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(store.GetStatePath(repositoryId))!, "*.tmp"));
    }

    [Fact]
    public async Task SaveAsync_WhenCreatingStateFails_LeavesNoDestinationOrTemporaryFile()
    {
        var repositoryId = "repository-1";
        var store = new LocalStateStore(_root, new FailingStateFileReplacer());

        await Assert.ThrowsAsync<IOException>(() =>
            store.SaveAsync(new DeviceState(1, repositoryId, [Version("session-1", "new")]), CancellationToken.None));

        Assert.False(File.Exists(store.GetStatePath(repositoryId)));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(store.GetStatePath(repositoryId))!, "*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static ObjectVersion Version(string id, string hash) =>
        new(new LogicalObjectId(id), ObjectKind.ActiveSession, new ContentHash(hash), hash, false);

    private sealed class FailingStateFileReplacer : IStateFileReplacer
    {
        public void Replace(string sourcePath, string destinationPath) =>
            throw new IOException("Injected state-file replacement failure.");
    }
}
