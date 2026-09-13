using System.Globalization;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security;
using System.Text;
using System.Text.Json;
using CodexHistorySync.Cli.Management;
using CodexHistorySync.Cli.Search;
using CodexHistorySync.Cli.Mcp;
using CodexHistorySync.Core.Annotations;
using CodexHistorySync.Core.Claude;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Continue;
using CodexHistorySync.Core.Conversion;
using CodexHistorySync.Core.Crypto;
using CodexHistorySync.Core.Grok;
using CodexHistorySync.Core.Management;
using CodexHistorySync.Core.Search;
using CodexHistorySync.Core.Model;
using CodexHistorySync.Core.Providers;
using CodexHistorySync.Core.State;
using CodexHistorySync.Core.Sync;
using CodexHistorySync.Git;
using CodexHistorySync.Windows;
using Spectre.Console;

namespace CodexHistorySync.Cli;

public sealed class GitHubCliRepositoryGateway : ICliRepositoryGateway
{
    private const string ManifestFileName = "codex-history-sync.json";
    private readonly GitHubVisibilityVerifier visibilityVerifier;
    private readonly GitCommand git;
    private readonly GitCommand gh;

    public GitHubCliRepositoryGateway(string gitExecutable = "git", string ghExecutable = "gh")
    {
        visibilityVerifier = new GitHubVisibilityVerifier(ghExecutable);
        git = new GitCommand(gitExecutable);
        gh = new GitCommand(ghExecutable);
    }

    public async Task<CliGateResult> VerifyPrivateAsync(string remoteUrl, CancellationToken cancellationToken)
    {
        var result = await visibilityVerifier.VerifyPrivateAsync(ParseRepository(remoteUrl), cancellationToken).ConfigureAwait(false);
        // The verifier already distinguishes an unauthenticated gh from a repository that is
        // genuinely public, and says which. Dropping that left both looking identical.
        return new CliGateResult(result.IsPrivate, "private-visibility", result.Diagnostic);
    }

    public async Task<CliGateResult> VerifyInitializationTargetAsync(string remoteUrl, CancellationToken cancellationToken)
    {
        var visibility = await VerifyPrivateAsync(remoteUrl, cancellationToken).ConfigureAwait(false);
        if (!visibility.Passed) return visibility;
        var refs = await git.RunAsync(["ls-remote", remoteUrl],
            Environment.CurrentDirectory, cancellationToken).ConfigureAwait(false);
        await RequireSuccessAsync(refs, "Unable to inspect the initialization repository.").ConfigureAwait(false);
        return new CliGateResult(string.IsNullOrWhiteSpace(refs.StandardOutput), "empty-private-repository");
    }

    public async Task<CliPublishedInitialization> PublishInitializationAsync(string remoteUrl, string repositoryId,
        byte[] manifest, byte[] encryptedIndex, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(encryptedIndex);
        var ownedTemporary = OwnedTemporaryDirectory.Create(Path.GetTempPath(), "codex-history-sync-init-");
        var temporaryRoot = ownedTemporary.RootPath;
        var clone = Path.Combine(temporaryRoot, "repository");
        try
        {
            await RequireSuccessAsync(await git.RunAsync(["clone", "--no-checkout", "--origin", "origin", remoteUrl, clone], temporaryRoot, cancellationToken),
                "Unable to clone the private initialization repository.").ConfigureAwait(false);
            var refs = await git.RunAsync(["ls-remote", "origin"], clone, cancellationToken).ConfigureAwait(false);
            await RequireSuccessAsync(refs, "Unable to inspect the initialization repository.").ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(refs.StandardOutput)) throw new InvalidOperationException("Initialization requires an empty private repository.");
            await RequireSuccessAsync(await git.RunAsync(["checkout", "--orphan", "main"], clone, cancellationToken), "Unable to create the initialization branch.").ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(clone, ManifestFileName), manifest, cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(clone, "repository.chs"), encryptedIndex, cancellationToken).ConfigureAwait(false);
            await RequireSuccessAsync(await git.RunAsync(["config", "user.email", "codex-history-sync@localhost"], clone, cancellationToken), "Unable to configure Git identity.").ConfigureAwait(false);
            await RequireSuccessAsync(await git.RunAsync(["config", "user.name", "Agent History Sync"], clone, cancellationToken), "Unable to configure Git identity.").ConfigureAwait(false);
            await RequireSuccessAsync(await git.RunAsync(["add", "--", ManifestFileName, "repository.chs"], clone, cancellationToken), "Unable to stage initialization metadata.").ConfigureAwait(false);
            await RequireSuccessAsync(await git.RunAsync(["commit", "--no-gpg-sign", "-m", "Initialize encrypted Codex history"], clone, cancellationToken), "Unable to commit initialization metadata.").ConfigureAwait(false);
            await RequireSuccessAsync(await git.RunAsync(["push", "origin", "HEAD:main"], clone, cancellationToken), "Unable to publish initialization metadata.").ConfigureAwait(false);
            var revision = await git.RunAsync(["rev-parse", "HEAD"], clone, cancellationToken).ConfigureAwait(false);
            await RequireSuccessAsync(revision, "Unable to resolve initialization revision.").ConfigureAwait(false);
            return new CliPublishedInitialization(manifest.ToArray(), encryptedIndex.ToArray(), revision.StandardOutput.Trim());
        }
        finally
        {
            // Publication is authoritative. A failed ownership/safety check deliberately leaves the tree for
            // later or manual recovery and never replaces the primary result.
            ownedTemporary.TryDelete();
        }
    }

    public async Task<CliRemoteSetup> ReadSetupAsync(string remoteUrl, CancellationToken cancellationToken)
    {
        var repository = ParseRepository(remoteUrl);
        var revision = await ReadCurrentRevisionAsync(remoteUrl, cancellationToken).ConfigureAwait(false);
        var manifest = await ReadGitHubFileAsync(repository, ManifestFileName, revision, cancellationToken).ConfigureAwait(false);
        var index = await ReadGitHubFileAsync(repository, "repository.chs", revision, cancellationToken).ConfigureAwait(false);
        return new CliRemoteSetup(manifest, index, revision);
    }

    public async Task<string> ReadCurrentRevisionAsync(string remoteUrl, CancellationToken cancellationToken)
    {
        var repository = ParseRepository(remoteUrl);
        var revision = await gh.RunAsync(["api", $"repos/{repository}/git/ref/heads/main", "--jq", ".object.sha"],
            Environment.CurrentDirectory, cancellationToken).ConfigureAwait(false);
        await RequireSuccessAsync(revision, "Unable to read the repository revision.").ConfigureAwait(false);
        var value = revision.StandardOutput.Trim();
        if (value.Length is not (40 or 64) || value.Any(character => character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f')))
            throw new InvalidDataException("GitHub returned an invalid repository revision.");
        return value;
    }

    private async Task<byte[]> ReadGitHubFileAsync(string repository, string fileName, string revision,
        CancellationToken cancellationToken)
    {
        var result = await gh.RunAsync(["api", $"repos/{repository}/contents/{fileName}?ref={revision}", "--jq", ".content"], Environment.CurrentDirectory, cancellationToken).ConfigureAwait(false);
        await RequireSuccessAsync(result, "Unable to read encrypted repository setup metadata.").ConfigureAwait(false);
        try { return Convert.FromBase64String(string.Concat(result.StandardOutput.Where(character => !char.IsWhiteSpace(character)))); }
        catch (FormatException exception) { throw new InvalidDataException("GitHub returned malformed setup metadata.", exception); }
    }

    private static string ParseRepository(string remoteUrl)
    {
        var canonical = DefaultCliServices.CanonicalRemoteUrl(remoteUrl);
        var uri = new Uri(canonical);
        var path = uri.AbsolutePath.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        if (path.Split('/').Length != 2) throw new CliGateException("GitHub repository URL must identify owner/repository.");
        return path;
    }

    private static Task RequireSuccessAsync(GitCommandResult result, string message)
    {
        if (result.ExitCode == 0 && !result.TimedOut) return Task.CompletedTask;
        // Git already said what went wrong - no credential helper, no network, no such ref - and
        // dropping it left a sentence that fits every one of those equally. The stream is redacted
        // where it is captured, so the credential-bearing parts of a URL are already gone.
        var detail = result.TimedOut ? "The Git command timed out." : result.StandardError.Trim();
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail) ? message : message + " " + detail);
    }
}


public sealed class FileCliLocalRepository : ICliLocalRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string configurationPath;
    private readonly IKeyStore keyStore;
    private readonly LocalStateStore stateStore;

    public FileCliLocalRepository(string localAppData, IKeyStore keyStore)
    {
        if (string.IsNullOrWhiteSpace(localAppData)) throw new ArgumentException("Local application data is required.", nameof(localAppData));
        this.keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        stateStore = new LocalStateStore(localAppData);
        configurationPath = Path.Combine(Path.GetFullPath(localAppData), "CodexHistorySync", "config.json");
    }

    public Task SaveKeyAsync(string repositoryId, ReadOnlyMemory<byte> key, CancellationToken cancellationToken) =>
        keyStore.SaveAsync(repositoryId, key, cancellationToken);

    public Task<byte[]?> LoadKeyAsync(string repositoryId, CancellationToken cancellationToken) =>
        keyStore.LoadAsync(repositoryId, cancellationToken);

    public async Task SaveConfigurationAsync(CliLocalConfiguration configuration, CancellationToken cancellationToken)
    {
        Validate(configuration);
        var directory = Path.GetDirectoryName(configurationPath)!;
        Directory.CreateDirectory(directory);
        RejectReparsePoints(directory);
        var temporary = Path.Combine(directory, ".config." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(output, configuration, JsonOptions, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(true);
            }
            RejectReparsePoints(temporary);
            File.Move(temporary, configurationPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<CliLocalConfiguration> LoadConfigurationAsync(CancellationToken cancellationToken)
    {
        // A machine that never joined has no configuration, which is a step not taken rather than
        // a failure to read one. Said plainly here, it does not surface as a missing-path error.
        if (!File.Exists(configurationPath)) throw new CliNotJoinedException();
        RejectReparsePoints(configurationPath);
        await using var input = new FileStream(configurationPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var configuration = await JsonSerializer.DeserializeAsync<CliLocalConfiguration>(input, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Local configuration is empty.");
        Validate(configuration);
        return configuration;
    }

    public Task SaveInitialStateAsync(string repositoryId, CancellationToken cancellationToken) =>
        stateStore.SaveAsync(new DeviceState(LocalStateStore.CurrentSchemaVersion, repositoryId, []), cancellationToken);

    private static void Validate(CliLocalConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.SchemaVersion != 1) throw new InvalidDataException("Local configuration schema is unsupported.");
        if (string.IsNullOrWhiteSpace(configuration.RepositoryId) || string.IsNullOrWhiteSpace(configuration.DeviceId))
            throw new InvalidDataException("Local configuration identity is invalid.");
        if (!StringComparer.Ordinal.Equals(configuration.RemoteUrl, DefaultCliServices.CanonicalRemoteUrl(configuration.RemoteUrl)))
            throw new InvalidDataException("Local configuration remote URL is not canonical.");
    }

    private static void RejectReparsePoints(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Directory.GetParent(current)?.FullName)
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Local configuration path contains a reparse point.");
        }
    }
}

