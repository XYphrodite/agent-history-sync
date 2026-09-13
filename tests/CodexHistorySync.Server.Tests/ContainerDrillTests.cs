using System.Security.Cryptography;
using System.Text;
using CodexHistorySync.Cli;
using CodexHistorySync.Core.Crypto;
using CodexHistorySync.Core.Model;
using CodexHistorySync.Remote;

namespace CodexHistorySync.Server.Tests;

public sealed class ContainerDrillFactAttribute : FactAttribute
{
    public ContainerDrillFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AGENT_SYNC_DRILL_URL")))
            Skip = "Optional Docker backup/restore drill: see docs/server.md.";
    }
}

public sealed class ContainerSyncFactAttribute : FactAttribute
{
    public ContainerSyncFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AGENT_SYNC_SYNC_DRILL_URL")))
            Skip = "Optional two-profile sync against a disposable external API: set AGENT_SYNC_SYNC_DRILL_URL.";
    }
}

public sealed class ContainerRestoreSyncFactAttribute : FactAttribute
{
    public ContainerRestoreSyncFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AGENT_SYNC_RESTORE_SYNC_DRILL_URL")))
            Skip = "Optional fresh-profile import from a restored two-profile drill: set AGENT_SYNC_RESTORE_SYNC_DRILL_URL.";
    }
}

/// <summary>Run once before backup and again against the isolated restored database.</summary>
public sealed class ContainerDrillTests
{
    private const string Passphrase = "SYNTHETIC-DRILL-PASSPHRASE-ONLY";
    private const string Canary = "SLICE3-BACKUP-PRIVATE-MESSAGE-45adcb";

    [ContainerSyncFact]
    public Task ContainerTwoClientsConvergeForAllScannableKinds() =>
        ServerSyncTests.AssertTwoClientsConvergeAsync(new StoreEndpoint(
            Environment.GetEnvironmentVariable("AGENT_SYNC_SYNC_DRILL_URL")!));

    [ContainerRestoreSyncFact]
    public Task ContainerRestoredRepositoryImportsIntoFreshProfile() =>
        ServerSyncTests.AssertRestoredProfileAsync(new StoreEndpoint(
            Environment.GetEnvironmentVariable("AGENT_SYNC_RESTORE_SYNC_DRILL_URL")!));

    [ContainerDrillFact]
    public async Task ContainerRoundTripAuthenticatesEncryptedSetupAndBlob()
    {
        using var client = new StoreClient(new StoreEndpoint(Environment.GetEnvironmentVariable("AGENT_SYNC_DRILL_URL")!));
        await client.CheckInfoAsync(default);
        var crypto = new RepositoryCrypto();
        var setup = await client.ReadSetupAsync(default);
        var id = new LogicalObjectId(new string('a', 64));
        var metadata = new EnvelopeMetadata(1, id, ObjectKind.ActiveSession);
        if (Environment.GetEnvironmentVariable("AGENT_SYNC_DRILL_PHASE") == "seed")
        {
            Assert.Null(setup); // Never overwrite an existing repository during the drill.
            var repositoryId = Guid.NewGuid().ToString("N");
            var created = await RepositoryManifestAuthenticator.CreateAsync(repositoryId, Passphrase.AsMemory(), crypto, default);
            try
            {
                var index = await RepositoryManifestAuthenticator.CreateEmptyIndexAsync(repositoryId, created.MasterKey, crypto, default);
                setup = await client.InitializeAsync(new(created.Manifest, index), default);
                using var input = new MemoryStream(Encoding.UTF8.GetBytes(Canary));
                using var encrypted = new MemoryStream();
                await crypto.EncryptAsync(input, encrypted, created.MasterKey, metadata, default);
                var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agent-sync-drill-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                try
                {
                    var path = System.IO.Path.Combine(directory, "blob.chs");
                    await File.WriteAllBytesAsync(path, encrypted.ToArray());
                    var hash = await client.UploadBlobAsync(path, default);
                    Assert.True((await client.PublishAsync(new(setup.Revision, null, [new(id.Value, hash)]), default)).Published);
                }
                finally { Directory.Delete(directory, recursive: true); }
            }
            finally { CryptographicOperations.ZeroMemory(created.MasterKey); }
        }
        Assert.NotNull(setup);
        var authenticated = await RepositoryManifestAuthenticator.AuthenticateAsync(setup.Manifest, Passphrase.AsMemory(), crypto, default);
        try
        {
            await RepositoryManifestAuthenticator.AuthenticateIndexAsync(setup.Index, authenticated.Manifest.RepositoryId, authenticated.MasterKey, crypto, default);
            var snapshot = await client.ReadSnapshotAsync(default);
            var reference = Assert.Single(snapshot.Objects);
            Assert.Equal(id.Value, reference.ObjectId);
            using var encrypted = new MemoryStream(await client.ReadBlobAsync(reference.Hash, default));
            using var plaintext = new MemoryStream();
            await crypto.DecryptAsync(encrypted, plaintext, authenticated.MasterKey, metadata, default);
            Assert.Equal(Canary, Encoding.UTF8.GetString(plaintext.ToArray()));
        }
        finally { CryptographicOperations.ZeroMemory(authenticated.MasterKey); }
        if (Environment.GetEnvironmentVariable("AGENT_SYNC_DRILL_LARGE") == "1")
        {
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agent-sync-load-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var path = System.IO.Path.Combine(directory, "large.chs");
                await File.WriteAllBytesAsync(path, PostgresApiTests.Envelope(StoreProtocol.MaximumBlobBytes));
                var hashes = await Task.WhenAll(client.UploadBlobAsync(path, default), client.UploadBlobAsync(path, default));
                Assert.Equal(hashes[0], hashes[1]);
                Assert.Equal(StoreProtocol.MaximumBlobBytes, (await client.ReadBlobAsync(hashes[0], default)).Length);
            }
            finally { Directory.Delete(directory, recursive: true); }
        }
    }
}
