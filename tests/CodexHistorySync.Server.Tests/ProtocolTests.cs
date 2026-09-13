using System.Net;
using System.Net.Http.Json;
using CodexHistorySync.Remote;

namespace CodexHistorySync.Server.Tests;

public sealed class ProtocolTests
{
    [Theory]
    [InlineData("http://100.64.0.1:8080/v1/repositories/personal")]
    [InlineData("http://100.127.255.254/v1/repositories/a/")]
    [InlineData("https://xeon.example.ts.net/v1/repositories/a")]
    [InlineData("http://[fd7a:115c:a1e0::1]:8080/v1/repositories/a")]
    [InlineData("http://127.0.0.1:8080/v1/repositories/a")]
    [InlineData("http://[::1]:8080/v1/repositories/a")]
    public void AllowsExplicitPrivateEndpoints(string value) => Assert.NotNull(new StoreEndpoint(value));

    [Theory]
    [InlineData("http://100.63.255.255/v1/repositories/a")]
    [InlineData("http://100.128.0.1/v1/repositories/a")]
    [InlineData("http://192.168.1.2/v1/repositories/a")]
    [InlineData("http://0.0.0.0/v1/repositories/a")]
    [InlineData("https://example.org/v1/repositories/a")]
    [InlineData("http://xeon.example.ts.net/v1/repositories/a")]
    [InlineData("http://user:secret@127.0.0.1/v1/repositories/a")]
    [InlineData("http://127.0.0.1/v1/repositories/a?token=secret")]
    [InlineData("http://127.0.0.1/v1/repositories/a#fragment")]
    [InlineData("http://127.0.0.1/v1/repositories/A")]
    [InlineData("http://127.0.0.1/v1/repositories/a//")]
    [InlineData("http://127.0.0.1/v1/repositories/%61")]
    [InlineData("http://127.0.0.1/v1/x/../repositories/a")]
    public void RejectsUnsafeEndpoints(string value) => Assert.Throws<InvalidDataException>(() => new StoreEndpoint(value));

    [Fact]
    public async Task BoundedReadsRejectExcessAndObserveCancellation()
    {
        await Assert.ThrowsAsync<StorePayloadTooLargeException>(() => StoreProtocol.ReadBoundedAsync(new MemoryStream(new byte[9]), 8, default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StoreProtocol.ReadBoundedAsync(new MemoryStream(new byte[9]), 8, cancellation.Token));
    }

    [Fact]
    public async Task ClientNeverSendsAuthorizationAndRejectsRedirectOrWrongProtocol()
    {
        using var client = new StoreClient(new StoreEndpoint("http://127.0.0.1/v1/repositories/test"), new Reply(request =>
        {
            Assert.Null(request.Headers.Authorization);
            return new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Headers = { Location = new Uri("https://example.org/secret") } };
        }));
        var error = await Assert.ThrowsAsync<IOException>(() => client.CheckInfoAsync(default));
        Assert.DoesNotContain("secret", error.Message);
        using var wrongVersion = new StoreClient(client.Endpoint, new Reply(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = JsonContent.Create(new StoreInfo(99)) }));
        await Assert.ThrowsAsync<InvalidDataException>(() => wrongVersion.CheckInfoAsync(default));
    }

    [Fact]
    public async Task ClientRejectsChangedCiphertextAndDuplicateReferences()
    {
        var bytes = new byte[32];
        "CHS1"u8.CopyTo(bytes);
        using var client = new StoreClient(new StoreEndpoint("http://127.0.0.1/v1/repositories/test"),
            new Reply(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.ReadBlobAsync(new string('a', 64), default));
        var item = new StoreObject(new string('b', 64), StoreProtocol.Hash(bytes));
        using var duplicate = new StoreClient(client.Endpoint, new Reply(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = JsonContent.Create(new StoreSnapshot(new string('a', 32), bytes, [item, item])) }));
        await Assert.ThrowsAsync<InvalidDataException>(() => duplicate.ReadSnapshotAsync(default));
    }

    private sealed class Reply(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
}
