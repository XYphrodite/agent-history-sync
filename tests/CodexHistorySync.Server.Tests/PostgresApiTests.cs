using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexHistorySync.Core.Model;
using CodexHistorySync.Core.Providers;
using CodexHistorySync.Remote;
using Npgsql;

namespace CodexHistorySync.Server.Tests;

public sealed class PostgresApiTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    internal static byte[] Envelope(int length = 64)
    {
        var bytes = RandomNumberGenerator.GetBytes(length);
        "CHS1"u8.CopyTo(bytes);
        return bytes;
    }

    internal static StoreInitialization Setup() => new(JsonSerializer.SerializeToUtf8Bytes(new
    {
        schemaVersion = 1, repositoryId = Guid.NewGuid().ToString("N"),
        argon2Parameters = new { salt = new byte[16], memoryKiB = 65536, iterations = 3, parallelism = 1 },
        authenticator = new byte[32]
    }), Envelope());

    [PostgresFact]
    public async Task ConcurrentInitAndPublishHaveOneWinnerAndAtomicIndexAndReferences()
    {
        var endpoint = fixture.Endpoint();
        using var http = new HttpClient();
        var initializations = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => http.PutAsJsonAsync(endpoint.RepositoryUri, Setup())));
        Assert.Single(initializations, x => x.StatusCode == HttpStatusCode.Created);
        Assert.Single(initializations, x => x.StatusCode == HttpStatusCode.Conflict);
        foreach (var response in initializations) response.Dispose();
        using var client = new StoreClient(endpoint);
        await client.CheckInfoAsync(Ct);
        var setup = (await client.ReadSetupAsync(Ct))!;
        var blobs = new[] { Envelope(), Envelope() };
        var hashes = blobs.Select(x => StoreProtocol.Hash(x)).ToArray();
        for (var i = 0; i < 2; i++) await fixture.Store.UploadBlobAsync(endpoint.Name, hashes[i], blobs[i], Ct);
        var ids = new[] { new string('a', 64), new string('b', 64) };
        var publications = blobs.Select((index, i) => new StorePublication(setup.Revision, index, [new StoreChange(ids[i], hashes[i])])).ToArray();
        var results = await Task.WhenAll(publications.Select(x => client.PublishAsync(x, Ct)));
        var winner = Array.FindIndex(results, x => x.Published);
        Assert.Single(results, x => x.Published);
        Assert.Equal(results[0].CurrentRevision, results[1].CurrentRevision);
        var snapshot = await client.ReadSnapshotAsync(Ct);
        Assert.Equal(blobs[winner], snapshot.Index);
        Assert.Equal(new StoreObject(ids[winner], hashes[winner]), Assert.Single(snapshot.Objects));
        Assert.Equal(snapshot.Revision, results[winner].CurrentRevision);
    }

    [PostgresFact]
    public async Task MissingBlobRollsBackDeletesAndIndexAndCannotReferenceAnotherRepository()
    {
        var endpoint = fixture.Endpoint();
        var other = fixture.Endpoint();
        using var client = new StoreClient(endpoint);
        var setup = await client.InitializeAsync(Setup(), Ct);
        await fixture.Store.InitializeAsync(other.Name, Setup(), Ct);
        var bytes = Envelope();
        var hash = StoreProtocol.Hash(bytes);
        await fixture.Store.UploadBlobAsync(other.Name, hash, bytes, Ct);
        var id = new string('a', 64);
        var error = await Assert.ThrowsAsync<IOException>(() => client.PublishAsync(new(setup.Revision, Envelope(), [new(id, hash)]), Ct));
        Assert.Contains("400", error.Message);
        var before = await client.ReadSnapshotAsync(Ct);
        Assert.Equal(setup.Revision, before.Revision);
        Assert.Equal(setup.Index, before.Index);
        Assert.Empty(before.Objects);
        await fixture.Store.UploadBlobAsync(endpoint.Name, hash, bytes, Ct);
        var published = await client.PublishAsync(new(setup.Revision, null, [new(id, hash)]), Ct);
        await Assert.ThrowsAsync<IOException>(() => client.PublishAsync(new(published.CurrentRevision, Envelope(),
            [new(id, null), new(new string('b', 64), new string('f', 64))]), Ct));
        var after = await client.ReadSnapshotAsync(Ct);
        Assert.Equal(published.CurrentRevision, after.Revision);
        Assert.Equal(setup.Index, after.Index);
        Assert.Equal(id, Assert.Single(after.Objects).ObjectId);
    }

    [PostgresFact]
    public async Task PinnedSnapshotSurvivesReplacementDeletionAndApiRestart()
    {
        var endpoint = fixture.Endpoint();
        using var client = new StoreClient(endpoint);
        var setup = await client.InitializeAsync(Setup(), Ct);
        var first = Envelope();
        var second = Envelope();
        var id = new LogicalObjectId(new string('a', 64));
        var directory = Directory.CreateTempSubdirectory("agent-sync-pinned-test-").FullName;
        try
        {
            var firstPath = Path.Combine(directory, "first.chs");
            var secondPath = Path.Combine(directory, "second.chs");
            await File.WriteAllBytesAsync(firstPath, first);
            await File.WriteAllBytesAsync(secondPath, second);
            using var provider = new HttpStorageProvider(new StoreClient(endpoint), RandomNumberGenerator.GetBytes(32));
            var head = await provider.TryPublishAsync(new PublishRequest(setup.Revision, null, [new(id, firstPath, false)], "test"), Ct);
            var hash = Assert.Single((await client.ReadSnapshotAsync(Ct)).Objects).Hash;
            var firstSealed = await client.ReadBlobAsync(hash, Ct);
            Assert.NotEqual(StoreProtocol.Hash(first), hash);
            var pinned = await provider.ReadSnapshotMetadataAsync(Ct);
            using (var wrongKey = new HttpStorageProvider(new StoreClient(endpoint), RandomNumberGenerator.GetBytes(32)))
            {
                var wrongSnapshot = await wrongKey.ReadSnapshotMetadataAsync(Ct);
                await Assert.ThrowsAnyAsync<CryptographicException>(() => wrongKey.ReadObjectAsync(wrongSnapshot, id, Ct));
            }
            head = await provider.TryPublishAsync(new PublishRequest(head.CurrentRevision, null, [new(id, secondPath, false)], "test"), Ct);
            var secondHash = Assert.Single((await client.ReadSnapshotAsync(Ct)).Objects).Hash;
            var secondSealed = await client.ReadBlobAsync(secondHash, Ct);
            await client.PublishAsync(new(head.CurrentRevision, null, [new(id.Value, null)]), Ct);
            Assert.Equal(first, await provider.ReadObjectAsync(pinned, id, Ct));
            Assert.Empty((await client.ReadSnapshotAsync(Ct)).Objects);
            await fixture.RestartAsync();
            using var restarted = new StoreClient(fixture.Endpoint(endpoint.Name));
            Assert.Equal(firstSealed, await restarted.ReadBlobAsync(hash, Ct));
            Assert.Equal(secondSealed, await restarted.ReadBlobAsync(secondHash, Ct));
            Assert.Empty((await restarted.ReadSnapshotAsync(Ct)).Objects);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [PostgresFact]
    public async Task CancellationWhileWaitingForRepositoryLockDoesNotAdvanceHead()
    {
        var endpoint = fixture.Endpoint();
        var setup = (await fixture.Store.InitializeAsync(endpoint.Name, Setup(), Ct))!;
        await using var connection = await fixture.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("SELECT revision FROM repositories WHERE name = $1 FOR UPDATE", connection);
        command.Parameters.AddWithValue(endpoint.Name);
        await command.ExecuteScalarAsync();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Store.PublishAsync(endpoint.Name,
            new(setup.Revision, Envelope(), []), cancel.Token));
        await transaction.RollbackAsync();
        Assert.Equal(setup.Revision, (await fixture.Store.ReadSetupAsync(endpoint.Name, Ct))!.Revision);
    }

    [PostgresFact]
    public async Task LostPublishResponseCanBeReconciledFromHeadWithoutDoublePublication()
    {
        var endpoint = fixture.Endpoint();
        using var client = new StoreClient(endpoint);
        var setup = await client.InitializeAsync(Setup(), Ct);
        using var losing = new StoreClient(endpoint, new LoseResponse());
        var change = new StorePublication(setup.Revision, Envelope(), []);
        await Assert.ThrowsAsync<IOException>(() => losing.PublishAsync(change, Ct));
        var head = await client.ReadSnapshotAsync(Ct);
        Assert.NotEqual(setup.Revision, head.Revision);
        Assert.Equal(change.Index, head.Index);
        var retry = await client.PublishAsync(change, Ct);
        Assert.False(retry.Published);
        Assert.Equal(head.Revision, retry.CurrentRevision);
    }

    [PostgresFact]
    public async Task BrowserHostTypeAndMalformedPayloadsAreRejectedWithoutLeakingDetails()
    {
        var endpoint = fixture.Endpoint();
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
        foreach (var header in new[] { "Origin", "Sec-Fetch-Site", "Host" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, endpoint.RepositoryUri) { Content = JsonContent.Create(Setup()) };
            request.Headers.TryAddWithoutValidation(header, header == "Sec-Fetch-Site" ? "cross-site" : header == "Host" ? "evil.example" : "https://evil.example");
            using var response = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        }
        using var wrongType = await http.PutAsync(endpoint.RepositoryUri, new StringContent("SECRET-CANARY", Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongType.StatusCode);
        using var malformed = await http.PutAsync(endpoint.RepositoryUri, new StringContent("{SECRET-CANARY", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.DoesNotContain("SECRET-CANARY", await malformed.Content.ReadAsStringAsync());
        using var missing = await http.PutAsJsonAsync(endpoint.RepositoryUri, new StoreInitialization("{}"u8.ToArray(), Envelope()));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        using var client = new StoreClient(endpoint);
        Assert.Null(await client.ReadSetupAsync(Ct));
        await client.InitializeAsync(Setup(), Ct);
        using var badHash = await http.PutAsync(endpoint.RepositoryUri + "/blobs/" + new string('a', 64), Binary(Envelope()));
        Assert.Equal(HttpStatusCode.BadRequest, badHash.StatusCode);
        using var missingBlob = await http.GetAsync(endpoint.RepositoryUri + "/blobs/" + new string('a', 64));
        Assert.Equal(HttpStatusCode.NotFound, missingBlob.StatusCode);
    }

    [PostgresFact]
    public async Task MaximumReferenceCountIsNotTruncatedAndOverflowRollsBack()
    {
        var endpoint = fixture.Endpoint();
        using var client = new StoreClient(endpoint);
        var setup = await client.InitializeAsync(Setup(), Ct);
        var blob = Envelope();
        var hash = StoreProtocol.Hash(blob);
        await fixture.Store.UploadBlobAsync(endpoint.Name, hash, blob, Ct);
        var refs = Enumerable.Range(0, StoreProtocol.MaximumObjects).Select(i => new StoreChange(i.ToString("x64"), hash)).ToArray();
        var result = await client.PublishAsync(new(setup.Revision, null, refs), Ct);
        Assert.Equal(StoreProtocol.MaximumObjects, (await client.ReadSnapshotAsync(Ct)).Objects.Count);
        await Assert.ThrowsAsync<IOException>(() => client.PublishAsync(new(result.CurrentRevision, Envelope(),
            [new(new string('f', 64), hash)]), Ct));
        var after = await client.ReadSnapshotAsync(Ct);
        Assert.Equal(result.CurrentRevision, after.Revision);
        Assert.Equal(setup.Index, after.Index);
        Assert.Equal(StoreProtocol.MaximumObjects, after.Objects.Count);
    }

    [PostgresFact]
    public async Task ChunkedJsonAndBlobBodiesCannotBypassSizeLimits()
    {
        var endpoint = fixture.Endpoint();
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        using var json = await http.PutAsync(endpoint.RepositoryUri, new RepeatedContent(StoreProtocol.MaximumJsonBytes + 1L, "application/json"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, json.StatusCode);
        using var blob = await http.PutAsync(endpoint.RepositoryUri + "/blobs/" + new string('a', 64),
            new RepeatedContent(StoreProtocol.MaximumBlobBytes + 1L, "application/octet-stream"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, blob.StatusCode);
        using var client = new StoreClient(endpoint);
        Assert.Null(await client.ReadSetupAsync(Ct));
    }

    [PostgresFact]
    public async Task TwoMaximumSizeUploadsRoundTripWithoutPublishingPartialReferences()
    {
        var endpoint = fixture.Endpoint();
        using var client = new StoreClient(endpoint);
        await client.InitializeAsync(Setup(), Ct);
        var bytes = Envelope(StoreProtocol.MaximumBlobBytes);
        var hash = StoreProtocol.Hash(bytes);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        var replies = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => http.PutAsync(endpoint.RepositoryUri + "/blobs/" + hash, Binary(bytes))));
        foreach (var response in replies) { Assert.Equal(HttpStatusCode.NoContent, response.StatusCode); response.Dispose(); }
        Assert.Equal(bytes, await client.ReadBlobAsync(hash, Ct));
        Assert.Empty((await client.ReadSnapshotAsync(Ct)).Objects);
    }

    private static ByteArrayContent Binary(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return content;
    }

    [PostgresFact]
    public async Task ConcurrencyLimitRejectsExcessRequestsAndReleasesPermits()
    {
        var endpoint = fixture.Endpoint();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var sockets = new[] { new TcpClient(), new TcpClient() };
        var limited = false;
        var statuses = new HashSet<int>();
        var rawResponses = new List<string>();
        try
        {
            foreach (var socket in sockets)
            {
                await socket.ConnectAsync("127.0.0.1", endpoint.RepositoryUri.Port);
                var headers = $"PUT {endpoint.RepositoryUri.AbsolutePath} HTTP/1.1\r\nHost: {endpoint.RepositoryUri.Authority}\r\nContent-Type: application/json\r\nContent-Length: 2\r\nExpect: 100-continue\r\n\r\n";
                await socket.GetStream().WriteAsync(Encoding.ASCII.GetBytes(headers));
                // Kestrel sends 100 only when the handler starts reading the body, after taking
                // its permit. A polling GET before both PUTs enter can itself reject the second PUT.
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var buffer = new byte[4096];
                var read = await socket.GetStream().ReadAsync(buffer, deadline.Token);
                Assert.StartsWith("HTTP/1.1 100 Continue", Encoding.ASCII.GetString(buffer, 0, read));
            }
            for (var attempt = 0; attempt < 100 && !limited; attempt++)
            {
                using var response = await http.GetAsync(endpoint.RepositoryUri + "/setup");
                statuses.Add((int)response.StatusCode);
                limited = response.StatusCode == HttpStatusCode.TooManyRequests;
                if (!limited) await Task.Delay(25);
            }
        }
        finally
        {
            foreach (var socket in sockets)
            {
                await socket.GetStream().WriteAsync("{}"u8.ToArray());
                var buffer = new byte[4096];
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var read = await socket.GetStream().ReadAsync(buffer, deadline.Token);
                rawResponses.Add(Encoding.ASCII.GetString(buffer, 0, read));
                socket.Dispose();
            }
        }
        Assert.True(limited, $"GET statuses: {string.Join(',', statuses)}; PUT: {string.Join(';', rawResponses)}");
        using var after = await http.GetAsync(endpoint.RepositoryUri + "/setup");
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
    }

    private sealed class LoseResponse() : DelegatingHandler(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var response = await base.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            throw new IOException("Synthetic lost response after commit.");
        }
    }

    private sealed class RepeatedContent : HttpContent
    {
        private readonly long length;
        public RepeatedContent(long length, string type)
        {
            this.length = length;
            Headers.ContentType = new MediaTypeHeaderValue(type);
        }
        protected override bool TryComputeLength(out long value) { value = 0; return false; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var buffer = Enumerable.Repeat((byte)' ', 81920).ToArray();
            for (long offset = 0; offset < length; offset += buffer.Length)
                await stream.WriteAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - offset)));
        }
    }
}
