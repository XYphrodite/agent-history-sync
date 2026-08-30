using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using CodexHistorySync.Core.Update;

namespace CodexHistorySync.Cli;

/// <summary>
/// Reads releases from the public source repository over HTTPS, the same endpoint
/// <c>scripts/install.ps1</c> uses. The repository is fixed in code on purpose: a configurable
/// download origin would be the shortest path from a stray environment variable to an
/// executable of somebody else's choosing.
/// </summary>
internal sealed class GitHubReleaseSource : IReleaseSource, IDisposable
{
    private const string Repository = "XYphrodite/agent-history-sync";
    private const string ExecutableAsset = "agent-sync.exe";
    private const string ChecksumAsset = "agent-sync.exe.sha256";
    private const int MaximumTextAsset = 4096;

    private readonly HttpClient client;

    public GitHubReleaseSource()
    {
        client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("agent-history-sync-update", "1.0"));
    }

    public async Task<ReleaseDescriptor> ResolveAsync(string? tag, CancellationToken cancellationToken)
    {
        // The tag reaches a URL, so it is rebuilt from parsed numbers instead of being pasted in.
        string address;
        if (tag is null)
        {
            address = $"https://api.github.com/repos/{Repository}/releases/latest";
        }
        else
        {
            if (!ReleaseVersion.TryParse(tag, out var requested))
                throw new InvalidDataException("The requested version is not a supported release tag.");
            address = $"https://api.github.com/repos/{Repository}/releases/tags/v{requested}";
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidDataException("The release could not be read from GitHub.");

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;

        if (!root.TryGetProperty("tag_name", out var tagName) || tagName.GetString() is not { } resolvedTag)
            throw new InvalidDataException("The release carries no tag.");
        if (!ReleaseVersion.TryParse(resolvedTag, out var version))
            throw new InvalidDataException("The published release tag is not a supported version.");

        return new ReleaseDescriptor(resolvedTag, version,
            AssetUrl(root, ExecutableAsset), AssetUrl(root, ChecksumAsset));
    }

    public async Task DownloadAsync(Uri address, string destinationPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        using var response = await client
            .GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidDataException("The release asset could not be downloaded.");

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ReadTextAsync(Uri address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);

        using var response = await client.GetAsync(address, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidDataException("The release asset could not be downloaded.");

        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (content.Length > MaximumTextAsset)
            throw new InvalidDataException("The release checksum asset is unexpectedly large.");
        return content;
    }

    public void Dispose() => client.Dispose();

    private static Uri AssetUrl(JsonElement release, string name)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The release carries no assets.");

        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var assetName) ||
                !string.Equals(assetName.GetString(), name, StringComparison.Ordinal))
                continue;
            if (!asset.TryGetProperty("browser_download_url", out var url) || url.GetString() is not { } value)
                break;

            // The response is JSON from the network: it selects which GitHub URL is fetched,
            // it does not get to choose the host.
            if (!Uri.TryCreate(value, UriKind.Absolute, out var address) ||
                address.Scheme != Uri.UriSchemeHttps ||
                !(address.Host is "github.com" || address.Host.EndsWith(".githubusercontent.com", StringComparison.Ordinal)))
                throw new InvalidDataException("The release asset address is not a GitHub download URL.");

            return address;
        }

        throw new InvalidDataException("The release does not carry the expected assets.");
    }
}
