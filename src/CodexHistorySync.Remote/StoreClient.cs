using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace CodexHistorySync.Remote;

public sealed class StoreClient : IDisposable
{
    private readonly HttpClient http;
    public StoreEndpoint Endpoint { get; }
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public StoreClient(StoreEndpoint endpoint, HttpMessageHandler? handler = null)
    {
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
    }

    public async Task CheckInfoAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint.Origin + "/v1/info");
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        RequireSuccess(response);
        var info = await JsonAsync<StoreInfo>(response, 4096, ct).ConfigureAwait(false);
        if (info.ProtocolVersion != StoreProtocol.Version) throw new InvalidDataException("Unsupported server protocol.");
    }

    public async Task<StoreSetup?> ReadSetupAsync(CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, "/setup");
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        RequireSuccess(response);
        var setup = await JsonAsync<StoreSetup>(response, StoreProtocol.MaximumJsonBytes, ct).ConfigureAwait(false);
        if (!StoreProtocol.IsRevision(setup.Revision) || setup.Manifest is not { Length: > 0 and <= 65536 })
            throw new InvalidDataException("Invalid server setup.");
        StoreProtocol.RequireEnvelope(setup.Index, StoreProtocol.MaximumIndexBytes);
        return setup;
    }

    public async Task<StoreSetup> InitializeAsync(StoreInitialization initialization, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Put, "");
        request.Content = JsonContent.Create(initialization);
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        RequireSuccess(response);
        var setup = await JsonAsync<StoreSetup>(response, StoreProtocol.MaximumJsonBytes, ct).ConfigureAwait(false);
        if (!StoreProtocol.IsRevision(setup.Revision) || !setup.Manifest.AsSpan().SequenceEqual(initialization.Manifest) ||
            !setup.Index.AsSpan().SequenceEqual(initialization.Index))
            throw new InvalidDataException("Server initialization was not confirmed.");
        return setup;
    }

    public async Task<StoreSnapshot> ReadSnapshotAsync(CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, "/snapshot");
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        RequireSuccess(response);
        var snapshot = await JsonAsync<StoreSnapshot>(response, StoreProtocol.MaximumJsonBytes, ct).ConfigureAwait(false);
        if (!StoreProtocol.IsRevision(snapshot.Revision) || snapshot.Objects is null || snapshot.Objects.Count > StoreProtocol.MaximumObjects)
            throw new InvalidDataException("Invalid server snapshot.");
        StoreProtocol.RequireEnvelope(snapshot.Index, StoreProtocol.MaximumIndexBytes);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in snapshot.Objects)
            if (item is null || !StoreProtocol.IsHash(item.ObjectId) || !StoreProtocol.IsHash(item.Hash) || !ids.Add(item.ObjectId))
                throw new InvalidDataException("Invalid server snapshot references.");
        return snapshot;
    }

    public async Task<byte[]> ReadBlobAsync(string hash, CancellationToken ct)
    {
        RequireHash(hash);
        using var request = Request(HttpMethod.Get, "/blobs/" + hash);
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        RequireSuccess(response);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var bytes = await StoreProtocol.ReadBoundedAsync(stream, StoreProtocol.MaximumBlobBytes, ct).ConfigureAwait(false);
        if (StoreProtocol.Hash(bytes) != hash) throw new InvalidDataException("Server ciphertext checksum mismatch.");
        StoreProtocol.RequireEnvelope(bytes, StoreProtocol.MaximumBlobBytes);
        return bytes;
    }

    public async Task<string> UploadBlobAsync(string path, CancellationToken ct)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var bytes = await StoreProtocol.ReadBoundedAsync(input, StoreProtocol.MaximumBlobBytes, ct).ConfigureAwait(false);
        return await UploadBlobAsync(bytes, ct).ConfigureAwait(false);
    }

    public async Task<string> UploadBlobAsync(byte[] bytes, CancellationToken ct)
    {
        StoreProtocol.RequireEnvelope(bytes, StoreProtocol.MaximumBlobBytes);
        var hash = StoreProtocol.Hash(bytes);
        using var request = Request(HttpMethod.Put, "/blobs/" + hash);
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        RequireSuccess(response);
        return hash;
    }

    public async Task<StorePublicationResult> PublishAsync(StorePublication publication, CancellationToken ct)
    {
        StoreProtocol.Validate(publication);
        using var request = Request(HttpMethod.Post, "/publish");
        request.Content = JsonContent.Create(publication);
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Conflict) RequireSuccess(response);
        var result = await JsonAsync<StorePublicationResult>(response, 4096, ct).ConfigureAwait(false);
        if (!StoreProtocol.IsRevision(result.CurrentRevision) || result.Published != response.IsSuccessStatusCode)
            throw new InvalidDataException("Invalid server publication response.");
        return result;
    }

    private HttpRequestMessage Request(HttpMethod method, string path) => new(method, Endpoint.RepositoryUri.AbsoluteUri + path);
    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    private static void RequireHash(string hash)
    {
        if (!StoreProtocol.IsHash(hash)) throw new InvalidDataException("Invalid ciphertext hash.");
    }
    private static void RequireSuccess(HttpResponseMessage response)
    {
        // Never reflect response bodies or redirect locations in diagnostics.
        if (!response.IsSuccessStatusCode) throw new IOException($"Storage server refused the request (HTTP {(int)response.StatusCode}).");
    }
    private static async Task<T> JsonAsync<T>(HttpResponseMessage response, int maximum, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var bytes = await StoreProtocol.ReadBoundedAsync(stream, maximum, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes, JsonOptions) ?? throw new InvalidDataException("Empty server response.");
    }
    public void Dispose() => http.Dispose();
}
