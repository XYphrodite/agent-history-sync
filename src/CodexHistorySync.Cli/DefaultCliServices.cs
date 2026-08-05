using System.Security.Cryptography;
using System.Text.Json;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Crypto;
using CodexHistorySync.Core.Model;
using CodexHistorySync.Core.State;
using CodexHistorySync.Core.Sync;

namespace CodexHistorySync.Cli;

public sealed record CliLocalConfiguration(
    int SchemaVersion,
    string RepositoryId,
    string DeviceId,
    string RemoteUrl,
    string LastSuccessfulRevision);

public sealed record CliPublishedInitialization(byte[] Manifest, byte[] Index, string Revision);
public sealed record CliRemoteSetup(byte[] Manifest, byte[] Index, string Revision);

public interface ICliRepositoryGateway
{
    Task<CliGateResult> VerifyInitializationTargetAsync(string remoteUrl, CancellationToken cancellationToken);
    Task<CliGateResult> VerifyPrivateAsync(string remoteUrl, CancellationToken cancellationToken);
    Task<CliPublishedInitialization> PublishInitializationAsync(string remoteUrl, string repositoryId,
        byte[] manifest, byte[] encryptedIndex, CancellationToken cancellationToken);
    Task<CliRemoteSetup> ReadSetupAsync(string remoteUrl, CancellationToken cancellationToken);
    Task<string> ReadCurrentRevisionAsync(string remoteUrl, CancellationToken cancellationToken);
}

public interface ICliLocalRepository
{
    Task SaveKeyAsync(string repositoryId, ReadOnlyMemory<byte> key, CancellationToken cancellationToken);
    Task<byte[]?> LoadKeyAsync(string repositoryId, CancellationToken cancellationToken);
    Task SaveConfigurationAsync(CliLocalConfiguration configuration, CancellationToken cancellationToken);
    Task<CliLocalConfiguration> LoadConfigurationAsync(CancellationToken cancellationToken);
    Task SaveInitialStateAsync(string repositoryId, CancellationToken cancellationToken);
}

public interface ICliSyncRuntime
{
    Task<CliGateResult> ProbeCompatibilityAsync(CancellationToken cancellationToken);
    Task<CliJoinPlan> PreviewJoinAsync(CliLocalConfiguration configuration, ReadOnlyMemory<byte> key,
        CliRemoteSetup setup, CancellationToken cancellationToken);
    Task<SyncResult> SynchronizeAsync(CliLocalConfiguration configuration, ReadOnlyMemory<byte> key,
        SyncMode mode, CancellationToken cancellationToken);
    Task<CliStatusReport> GetStatusAsync(CliLocalConfiguration configuration, ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken);
    Task<CliDoctorReport> RunDoctorAsync(CliLocalConfiguration? configuration, ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<CliConflictInfo>> ListConflictsAsync(CliLocalConfiguration configuration,
        CancellationToken cancellationToken);
    Task<CliResolutionResult> ResolveAsync(CliLocalConfiguration configuration, ReadOnlyMemory<byte> key, string conflictId,
        CliResolution resolution, string? exportDirectory, CancellationToken cancellationToken);
}

public sealed record CliRepositoryManifest(int SchemaVersion, string RepositoryId,
    Argon2Parameters Argon2Parameters, byte[] Authenticator);

public sealed record CliManifestAuthentication(CliRepositoryManifest Manifest, byte[] MasterKey);

public static class RepositoryManifestAuthenticator
{
    private const int SchemaVersion = 1;
    private static readonly byte[] AuthenticationLabel = "CodexHistorySync/manifest-authentication/v1"u8.ToArray();
    private static readonly EnvelopeMetadata IndexMetadata = new(1, new LogicalObjectId("__repository_index__"), ObjectKind.RepositoryIndex);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<(byte[] Manifest, byte[] MasterKey)> CreateAsync(string repositoryId,
        ReadOnlyMemory<char> passphrase, RepositoryCrypto crypto, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        ArgumentNullException.ThrowIfNull(crypto);
        var parameters = new Argon2Parameters(RandomNumberGenerator.GetBytes(RepositoryCrypto.RepositorySaltSize),
            RepositoryCrypto.DefaultMemoryKiB, RepositoryCrypto.DefaultIterations, RepositoryCrypto.DefaultParallelism);
        var masterKey = await crypto.DeriveMasterKeyAsync(passphrase, parameters, cancellationToken).ConfigureAwait(false);
        byte[]? authenticationKey = null;
        try
        {
            var unsigned = CanonicalPayload(repositoryId, parameters);
            authenticationKey = DeriveAuthenticationKey(masterKey);
            var authenticator = HMACSHA256.HashData(authenticationKey, unsigned);
            var manifest = JsonSerializer.SerializeToUtf8Bytes(
                new CliRepositoryManifest(SchemaVersion, repositoryId, parameters, authenticator), JsonOptions);
            return (manifest, masterKey);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(masterKey);
            throw;
        }
        finally
        {
            if (authenticationKey is not null) CryptographicOperations.ZeroMemory(authenticationKey);
        }
    }

    public static async Task<CliManifestAuthentication> AuthenticateAsync(byte[] manifestBytes,
        ReadOnlyMemory<char> passphrase, RepositoryCrypto crypto, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifestBytes);
        ArgumentNullException.ThrowIfNull(crypto);
        CliRepositoryManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<CliRepositoryManifest>(manifestBytes, JsonOptions)
                ?? throw new CryptographicException("Repository manifest is empty.");
            ValidateManifest(manifest);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidDataException)
        {
            throw new CryptographicException("Repository manifest is malformed.", exception);
        }

        var masterKey = await crypto.DeriveMasterKeyAsync(passphrase, manifest.Argon2Parameters, cancellationToken).ConfigureAwait(false);
        byte[]? authenticationKey = null;
        try
        {
            authenticationKey = DeriveAuthenticationKey(masterKey);
            var expected = HMACSHA256.HashData(authenticationKey, CanonicalPayload(manifest.RepositoryId, manifest.Argon2Parameters));
            if (!CryptographicOperations.FixedTimeEquals(expected, manifest.Authenticator))
                throw new CryptographicException("Repository manifest authentication failed.");
            return new CliManifestAuthentication(manifest, masterKey);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(masterKey);
            throw;
        }
        finally
        {
            if (authenticationKey is not null) CryptographicOperations.ZeroMemory(authenticationKey);
        }
    }

    public static async Task<int> AuthenticateIndexAsync(byte[] encryptedIndex, string repositoryId,
        ReadOnlyMemory<byte> masterKey, RepositoryCrypto crypto, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(encryptedIndex);
        await using var source = new MemoryStream(encryptedIndex, writable: false);
        await using var plaintext = new MemoryStream();
        await crypto.DecryptAsync(source, plaintext, masterKey, IndexMetadata, cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(plaintext.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.GetProperty("schemaVersion").GetInt32() != SchemaVersion ||
                !StringComparer.Ordinal.Equals(root.GetProperty("repositoryId").GetString(), repositoryId))
                throw new CryptographicException("Repository index identity is invalid.");
            return root.GetProperty("objects").GetArrayLength();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new CryptographicException("Repository index is malformed.", exception);
        }
    }

    public static async Task<byte[]> CreateEmptyIndexAsync(string repositoryId, ReadOnlyMemory<byte> masterKey,
        RepositoryCrypto crypto, CancellationToken cancellationToken)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = SchemaVersion, repositoryId, objects = Array.Empty<object>() }, JsonOptions);
        await using var source = new MemoryStream(plaintext, writable: false);
        await using var destination = new MemoryStream();
        await crypto.EncryptAsync(source, destination, masterKey, IndexMetadata, cancellationToken).ConfigureAwait(false);
        CryptographicOperations.ZeroMemory(plaintext);
        return destination.ToArray();
    }

    private static void ValidateManifest(CliRepositoryManifest manifest)
    {
        if (manifest.SchemaVersion != SchemaVersion) throw new InvalidDataException("Repository manifest schema is unsupported.");
        if (string.IsNullOrWhiteSpace(manifest.RepositoryId) || manifest.RepositoryId is "." or ".." ||
            manifest.RepositoryId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || manifest.RepositoryId.Contains('/') || manifest.RepositoryId.Contains('\\'))
            throw new InvalidDataException("Repository manifest ID is invalid.");
        ArgumentNullException.ThrowIfNull(manifest.Argon2Parameters);
        if (manifest.Authenticator is null || manifest.Authenticator.Length != 32)
            throw new InvalidDataException("Repository manifest authenticator is invalid.");
    }

    private static byte[] CanonicalPayload(string repositoryId, Argon2Parameters parameters) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = SchemaVersion,
            repositoryId,
            salt = Convert.ToBase64String(parameters.Salt),
            memoryKiB = parameters.MemoryKiB,
            iterations = parameters.Iterations,
            parallelism = parameters.Parallelism
        }, JsonOptions);

    private static byte[] DeriveAuthenticationKey(ReadOnlySpan<byte> masterKey)
    {
        var key = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, key, ReadOnlySpan<byte>.Empty, AuthenticationLabel);
        return key;
    }
}

public sealed class DefaultCliServices : ICliServices
{
    private readonly ICliRepositoryGateway gateway;
    private readonly ICliLocalRepository local;
    private readonly ICliSyncRuntime runtime;
    private readonly RepositoryCrypto crypto;
    private readonly Dictionary<string, PendingJoin> pendingJoins = new(StringComparer.Ordinal);

    public DefaultCliServices(ICliRepositoryGateway gateway, ICliLocalRepository local, ICliSyncRuntime runtime, RepositoryCrypto crypto)
    {
        this.gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        this.local = local ?? throw new ArgumentNullException(nameof(local));
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.crypto = crypto ?? throw new ArgumentNullException(nameof(crypto));
    }

    public Task<CliGateResult> VerifyPrivateRepositoryAsync(string remoteUrl, CancellationToken cancellationToken) =>
        gateway.VerifyPrivateAsync(remoteUrl, cancellationToken);

    public Task<CliGateResult> VerifyInitializationTargetAsync(string remoteUrl, CancellationToken cancellationToken) =>
        gateway.VerifyInitializationTargetAsync(CanonicalRemoteUrl(remoteUrl), cancellationToken);

    public async Task<CliInitializationResult> InitializeAsync(string remoteUrl, ReadOnlyMemory<char> passphrase,
        CancellationToken cancellationToken)
    {
        remoteUrl = CanonicalRemoteUrl(remoteUrl);
        var repositoryId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var deviceId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var created = await RepositoryManifestAuthenticator.CreateAsync(repositoryId, passphrase, crypto, cancellationToken).ConfigureAwait(false);
        try
        {
            var index = await RepositoryManifestAuthenticator.CreateEmptyIndexAsync(repositoryId, created.MasterKey, crypto, cancellationToken).ConfigureAwait(false);
            var published = await gateway.PublishInitializationAsync(remoteUrl, repositoryId, created.Manifest, index, cancellationToken).ConfigureAwait(false);
            await local.SaveKeyAsync(repositoryId, created.MasterKey, cancellationToken).ConfigureAwait(false);
            await local.SaveConfigurationAsync(new CliLocalConfiguration(1, repositoryId, deviceId, remoteUrl, published.Revision), cancellationToken).ConfigureAwait(false);
            await local.SaveInitialStateAsync(repositoryId, cancellationToken).ConfigureAwait(false);
            return new CliInitializationResult(repositoryId);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(created.MasterKey);
        }
    }

    public async Task<CliAuthenticatedRepository> AuthenticateRepositoryAsync(string remoteUrl,
        ReadOnlyMemory<char> passphrase, CancellationToken cancellationToken)
    {
        remoteUrl = CanonicalRemoteUrl(remoteUrl);
        var setup = await gateway.ReadSetupAsync(remoteUrl, cancellationToken).ConfigureAwait(false);
        CliManifestAuthentication? authentication = null;
        try
        {
            authentication = await RepositoryManifestAuthenticator.AuthenticateAsync(setup.Manifest, passphrase, crypto, cancellationToken).ConfigureAwait(false);
            await RepositoryManifestAuthenticator.AuthenticateIndexAsync(setup.Index, authentication.Manifest.RepositoryId,
                authentication.MasterKey, crypto, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (authentication is not null) CryptographicOperations.ZeroMemory(authentication.MasterKey);
            if (exception is OperationCanceledException) throw;
            if (exception is CryptographicException or InvalidDataException)
                throw new CliGateException("Repository authentication failed.", exception);
            throw;
        }
        var repository = new CliAuthenticatedRepository(authentication.Manifest.RepositoryId, setup.Revision);
        if (pendingJoins.Remove(repository.RepositoryId, out var previous)) CryptographicOperations.ZeroMemory(previous.MasterKey);
        pendingJoins[repository.RepositoryId] = new PendingJoin(remoteUrl, setup, authentication.MasterKey,
            new CliLocalConfiguration(1, repository.RepositoryId, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), remoteUrl, setup.Revision));
        return repository;
    }

    public Task<CliGateResult> ProbeCompatibilityAsync(CliAuthenticatedRepository repository, CancellationToken cancellationToken)
    {
        _ = GetPending(repository);
        return runtime.ProbeCompatibilityAsync(cancellationToken);
    }

    public async Task<CliJoinPlan> PlanJoinAsync(CliAuthenticatedRepository repository, CancellationToken cancellationToken)
    {
        var pending = GetPending(repository);
        var currentRevision = await gateway.ReadCurrentRevisionAsync(pending.RemoteUrl, cancellationToken).ConfigureAwait(false);
        if (!StringComparer.Ordinal.Equals(currentRevision, pending.Setup.Revision))
            throw new CliGateException("The repository changed after join authentication; retry the join.");
        return await runtime.PreviewJoinAsync(pending.Configuration, pending.MasterKey, pending.Setup,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<SyncResult> ApplyJoinAsync(CliAuthenticatedRepository repository, CliJoinPlan plan, CancellationToken cancellationToken)
    {
        var pending = GetPending(repository);
        try
        {
            var currentRevision = await gateway.ReadCurrentRevisionAsync(pending.RemoteUrl, cancellationToken).ConfigureAwait(false);
            if (!StringComparer.Ordinal.Equals(currentRevision, pending.Setup.Revision))
                throw new CliGateException("The repository changed after join authentication; retry the join.");
            await local.SaveKeyAsync(repository.RepositoryId, pending.MasterKey, cancellationToken).ConfigureAwait(false);
            await local.SaveConfigurationAsync(pending.Configuration, cancellationToken).ConfigureAwait(false);
            await local.SaveInitialStateAsync(repository.RepositoryId, cancellationToken).ConfigureAwait(false);
            var result = await runtime.SynchronizeAsync(pending.Configuration, pending.MasterKey, SyncMode.Pull, cancellationToken).ConfigureAwait(false);
            await local.SaveConfigurationAsync(pending.Configuration with { LastSuccessfulRevision = result.RemoteRevision }, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally { AbortPending(repository.RepositoryId); }
    }

    public Task AbortJoinAsync(CliAuthenticatedRepository repository, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AbortPending(repository.RepositoryId);
        return Task.CompletedTask;
    }

    public async Task<SyncResult> SynchronizeAsync(SyncMode mode, CancellationToken cancellationToken)
    {
        var (configuration, key) = await LoadAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await runtime.SynchronizeAsync(configuration, key, mode, cancellationToken).ConfigureAwait(false);
            await local.SaveConfigurationAsync(configuration with { LastSuccessfulRevision = result.RemoteRevision }, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public async Task<CliStatusReport> GetStatusAsync(CancellationToken cancellationToken)
    {
        var (configuration, key) = await LoadAsync(cancellationToken).ConfigureAwait(false);
        try { return await runtime.GetStatusAsync(configuration, key, cancellationToken).ConfigureAwait(false); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public async Task<CliDoctorReport> RunDoctorAsync(CancellationToken cancellationToken)
    {
        try
        {
            var (configuration, key) = await LoadAsync(cancellationToken).ConfigureAwait(false);
            try { return await runtime.RunDoctorAsync(configuration, key, cancellationToken).ConfigureAwait(false); }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or InvalidOperationException)
        {
            return await runtime.RunDoctorAsync(null, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<CompatibilityResult> ProbeCompatibilitySessionAsync(string sourceSession, string codexExecutable,
        CancellationToken cancellationToken) =>
        new CodexCompatibilityProbe().ProbeAsync(codexExecutable, sourceSession, cancellationToken);

    public async Task<IReadOnlyList<CliConflictInfo>> ListConflictsAsync(CancellationToken cancellationToken)
    {
        var configuration = await local.LoadConfigurationAsync(cancellationToken).ConfigureAwait(false);
        return await runtime.ListConflictsAsync(configuration, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CliResolutionResult> ResolveAsync(string conflictId, CliResolution resolution, string? exportDirectory,
        CancellationToken cancellationToken)
    {
        var (configuration, key) = await LoadAsync(cancellationToken).ConfigureAwait(false);
        try { return await runtime.ResolveAsync(configuration, key, conflictId, resolution, exportDirectory, cancellationToken).ConfigureAwait(false); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private void AbortPending(string repositoryId)
    {
        if (pendingJoins.Remove(repositoryId, out var pending)) CryptographicOperations.ZeroMemory(pending.MasterKey);
    }

    private PendingJoin GetPending(CliAuthenticatedRepository repository) =>
        pendingJoins.TryGetValue(repository.RepositoryId, out var pending) && StringComparer.Ordinal.Equals(pending.Setup.Revision, repository.RemoteRevision)
            ? pending : throw new CliGateException("Authenticated join context is unavailable.");

    private async Task<(CliLocalConfiguration Configuration, byte[] Key)> LoadAsync(CancellationToken cancellationToken)
    {
        var configuration = await local.LoadConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var key = await local.LoadKeyAsync(configuration.RepositoryId, cancellationToken).ConfigureAwait(false)
            ?? throw new CliGateException("The repository key is unavailable.");
        return (configuration, key);
    }

    internal static string CanonicalRemoteUrl(string remoteUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteUrl);
        if (!Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            throw new CliGateException("Only HTTPS GitHub repository URLs are supported.");
        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty,
            Host = "github.com",
            Scheme = Uri.UriSchemeHttps,
            Port = -1
        };
        return builder.Uri.AbsoluteUri;
    }

    private sealed record PendingJoin(string RemoteUrl, CliRemoteSetup Setup, byte[] MasterKey, CliLocalConfiguration Configuration);
}
