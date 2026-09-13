using CodexHistorySync.Remote;

namespace CodexHistorySync.Cli;

/// <summary>Selects the transport without creating or invoking Git for server repositories.</summary>
public sealed class StorageRepositoryGateway : ICliRepositoryGateway
{
    private readonly Lazy<ICliRepositoryGateway> git;
    private readonly ICliRepositoryGateway server = new ServerRepositoryGateway();

    public StorageRepositoryGateway(Func<ICliRepositoryGateway>? gitFactory = null) =>
        git = new Lazy<ICliRepositoryGateway>(gitFactory ?? (() => new GitHubCliRepositoryGateway()));

    private ICliRepositoryGateway Select(string url) => StoreEndpoint.IsServerUrl(url) ? server : git.Value;
    public Task<CliGateResult> VerifyInitializationTargetAsync(string url, CancellationToken ct) => Select(url).VerifyInitializationTargetAsync(url, ct);
    public Task<CliGateResult> VerifyPrivateAsync(string url, CancellationToken ct) => Select(url).VerifyPrivateAsync(url, ct);
    public Task<CliPublishedInitialization> PublishInitializationAsync(string url, string id, byte[] manifest, byte[] index, CancellationToken ct) =>
        Select(url).PublishInitializationAsync(url, id, manifest, index, ct);
    public Task<CliRemoteSetup> ReadSetupAsync(string url, CancellationToken ct) => Select(url).ReadSetupAsync(url, ct);
    public Task<string> ReadCurrentRevisionAsync(string url, CancellationToken ct) => Select(url).ReadCurrentRevisionAsync(url, ct);
}

public sealed class ServerRepositoryGateway : ICliRepositoryGateway
{
    public async Task<CliGateResult> VerifyPrivateAsync(string remoteUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new StoreClient(new StoreEndpoint(remoteUrl));
            await client.CheckInfoAsync(cancellationToken).ConfigureAwait(false);
            // Reachability is not proof of Tailscale ACL/firewall isolation.
            return new CliGateResult(true, "server-access");
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException or ArgumentException or
            System.Text.Json.JsonException || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new CliGateResult(false, "server-access", "Cannot reach a compatible storage server. Check its URL, Tailscale, allowed hosts and readiness.");
        }
    }

    public async Task<CliGateResult> VerifyInitializationTargetAsync(string remoteUrl, CancellationToken cancellationToken)
    {
        var access = await VerifyPrivateAsync(remoteUrl, cancellationToken).ConfigureAwait(false);
        if (!access.Passed) return access;
        using var client = new StoreClient(new StoreEndpoint(remoteUrl));
        return new CliGateResult(await client.ReadSetupAsync(cancellationToken).ConfigureAwait(false) is null,
            "empty-server-repository", "Initialization requires a new repository name.");
    }

    public async Task<CliPublishedInitialization> PublishInitializationAsync(string remoteUrl, string repositoryId,
        byte[] manifest, byte[] encryptedIndex, CancellationToken cancellationToken)
    {
        using var client = new StoreClient(new StoreEndpoint(remoteUrl));
        var setup = await client.InitializeAsync(new StoreInitialization(manifest, encryptedIndex), cancellationToken).ConfigureAwait(false);
        return new CliPublishedInitialization(setup.Manifest, setup.Index, setup.Revision);
    }

    public async Task<CliRemoteSetup> ReadSetupAsync(string remoteUrl, CancellationToken cancellationToken)
    {
        using var client = new StoreClient(new StoreEndpoint(remoteUrl));
        var setup = await client.ReadSetupAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new CliGateException("The server repository does not exist. Initialize it on the first device.");
        return new CliRemoteSetup(setup.Manifest, setup.Index, setup.Revision);
    }

    public async Task<string> ReadCurrentRevisionAsync(string remoteUrl, CancellationToken cancellationToken) =>
        (await ReadSetupAsync(remoteUrl, cancellationToken).ConfigureAwait(false)).Revision;
}
