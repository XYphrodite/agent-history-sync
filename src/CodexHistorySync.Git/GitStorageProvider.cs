using System.Text;
using System.Text.RegularExpressions;
using CodexHistorySync.Core.Model;
using CodexHistorySync.Core.Providers;

namespace CodexHistorySync.Git;

public enum GitRemoteKind
{
    Local,
    GitHub
}

public interface IGitPublicationHook
{
    Task AfterStagingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    Task BeforePushAsync(CancellationToken cancellationToken);
}

public interface IGitPushTransport
{
    Task<GitCommandResult> PushAsync(
        GitCommand git,
        string workingDirectory,
        string expectedRemoteRevision,
        CancellationToken cancellationToken);
}

/// <summary>
/// Publishes a single-snapshot history: each successful push replaces <c>main</c> with an orphan
/// commit via force-with-lease so prior encrypted blobs become unreachable on the remote.
/// </summary>
public sealed class GitPushTransport : IGitPushTransport
{
    public Task<GitCommandResult> PushAsync(
        GitCommand git,
        string workingDirectory,
        string expectedRemoteRevision,
        CancellationToken cancellationToken)
    {
        // Empty expected revision = first publish to an empty remote (or CAS already matched empty).
        if (string.IsNullOrEmpty(expectedRemoteRevision))
            return git.RunAsync(["push", "--force", "origin", "HEAD:main"], workingDirectory, cancellationToken);

        return git.RunAsync(
            ["push", "--force-with-lease=refs/heads/main:" + expectedRemoteRevision, "origin", "HEAD:main"],
            workingDirectory,
            cancellationToken);
    }
}

public sealed class GitStorageProvider : IStorageProvider
{
    private static readonly Regex RepositoryIdPattern = new("^[A-Za-z0-9_-]{1,128}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ObjectIdPattern = new("^[a-f0-9]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly string _repositoryId;
    private readonly string _remoteUrl;
    private readonly GitRemoteKind _remoteKind;
    private readonly string _storageRoot;
    private readonly string _clonePath;
    private readonly GitCommand _git;
    private readonly IGitHubVisibilityVerifier _visibilityVerifier;
    private readonly IGitPublicationHook? _publicationHook;
    private readonly IGitPushTransport _pushTransport;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GitStorageProvider(
        string repositoryId,
        string remoteUrl,
        GitRemoteKind remoteKind,
        string? storageRoot = null,
        string gitExecutable = "git",
        TimeSpan? commandTimeout = null,
        IGitHubVisibilityVerifier? visibilityVerifier = null,
        IGitPublicationHook? publicationHook = null,
        IGitPushTransport? pushTransport = null)
    {
        _repositoryId = repositoryId ?? throw new ArgumentNullException(nameof(repositoryId));
        if (!RepositoryIdPattern.IsMatch(_repositoryId))
            throw new ArgumentException("Repository ID contains unsupported characters.", nameof(repositoryId));
        _remoteUrl = remoteUrl ?? throw new ArgumentNullException(nameof(remoteUrl));
        if (string.IsNullOrWhiteSpace(_remoteUrl)) throw new ArgumentException("Remote URL is required.", nameof(remoteUrl));
        if (!Enum.IsDefined(remoteKind)) throw new ArgumentOutOfRangeException(nameof(remoteKind));
        _remoteKind = remoteKind;
        if (_remoteKind == GitRemoteKind.Local && !IsLocalRemote(_remoteUrl))
            throw new ArgumentException("Local remotes must be absolute filesystem paths or file URLs.", nameof(remoteUrl));
        if (_remoteKind == GitRemoteKind.GitHub) _ = ParseGitHubRepository(_remoteUrl);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) ??
            throw new InvalidOperationException("Local application data directory is unavailable.");
        _storageRoot = Path.GetFullPath(storageRoot ?? Path.Combine(localAppData, "CodexHistorySync", "repositories"));
        _clonePath = Path.Combine(_storageRoot, _repositoryId, "git");
        AssertContained(_clonePath, _storageRoot);
        EnsureNotInsideGitWorktree(_storageRoot);
        _git = new GitCommand(gitExecutable, commandTimeout);
        _visibilityVerifier = visibilityVerifier ?? new GitHubVisibilityVerifier();
        _publicationHook = publicationHook;
        _pushTransport = pushTransport ?? new GitPushTransport();
    }

    public async Task<RemoteSnapshot> ReadSnapshotAsync(CancellationToken ct)
    {
        var metadata = await ReadSnapshotMetadataAsync(ct).ConfigureAwait(false);
        var objects = new List<EncryptedRemoteObject>(metadata.EffectiveObjectReferences.Count);
        foreach (var objectId in metadata.EffectiveObjectReferences)
            objects.Add(new EncryptedRemoteObject(objectId, await ReadObjectAsync(metadata, objectId, ct).ConfigureAwait(false)));
        return metadata with { Objects = objects };
    }

    public async Task<RemoteSnapshot> ReadSnapshotMetadataAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureCloneAsync(ct).ConfigureAwait(false);
            var revision = await FetchRevisionAsync(ct).ConfigureAwait(false);
            await MaterializeRevisionAsync(revision, ct).ConfigureAwait(false);

            byte[]? indexCiphertext = null;
            var indexPath = Path.Combine(_clonePath, "repository.chs");
            if (File.Exists(indexPath))
            {
                EnsureSafeFile(indexPath);
                indexCiphertext = await File.ReadAllBytesAsync(indexPath, ct).ConfigureAwait(false);
            }

            var objectReferences = new List<LogicalObjectId>();
            var objectsPath = Path.Combine(_clonePath, "objects");
            if (Directory.Exists(objectsPath))
            {
                EnsureSafeDirectory(objectsPath);
                foreach (var file in EnumerateSafeObjectFiles(objectsPath))
                {
                    var relative = Path.GetRelativePath(objectsPath, file).Replace(Path.DirectorySeparatorChar, '/');
                    var segments = relative.Split('/');
                    var opaqueId = segments.Length == 2 && segments[0].Length == 2
                        ? segments[0] + Path.GetFileNameWithoutExtension(segments[1])
                        : string.Empty;
                    if (!ObjectIdPattern.IsMatch(opaqueId))
                        throw new InvalidDataException("Repository contains an invalid encrypted object path.");
                    objectReferences.Add(new LogicalObjectId(opaqueId));
                }
            }

            return new RemoteSnapshot(revision, indexCiphertext, [], objectReferences);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<byte[]> ReadObjectAsync(RemoteSnapshot snapshot, LogicalObjectId objectId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureOwnedClone();
            var current = await ResolveRevisionAsync("HEAD", ct, allowMissing: true).ConfigureAwait(false);
            if (!StringComparer.Ordinal.Equals(current, snapshot.Revision))
                throw new InvalidOperationException("The materialized repository revision changed before object access.");
            var value = objectId.Value ?? string.Empty;
            if (!ObjectIdPattern.IsMatch(value)) throw new InvalidDataException("The encrypted object ID is invalid.");
            var path = Path.Combine(_clonePath, "objects", value[..2], value[2..] + ".chs");
            AssertContained(path, _clonePath);
            EnsureSafeFile(path);
            return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PublishResult> TryPublishAsync(PublishRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Changes);
        if (string.IsNullOrWhiteSpace(request.CommitMessage)) throw new ArgumentException("Commit message is required.", nameof(request));

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureCloneAsync(ct).ConfigureAwait(false);
            var remoteRevision = await FetchRevisionAsync(ct).ConfigureAwait(false);
            if (!StringComparer.Ordinal.Equals(request.ExpectedRevision, remoteRevision))
                return new PublishResult(false, remoteRevision);

            await MaterializeRevisionAsync(remoteRevision, ct).ConfigureAwait(false);
            if (request.Index is not null) await ApplyIndexChangeAsync(request.Index, ct).ConfigureAwait(false);
            foreach (var change in request.Changes) await ApplyObjectChangeAsync(change, ct).ConfigureAwait(false);
            if (_publicationHook is not null) await _publicationHook.AfterStagingAsync(ct).ConfigureAwait(false);

            var staged = await RunGitAsync(["diff", "--cached", "--quiet"], ct).ConfigureAwait(false);
            if (staged.ExitCode == 0)
            {
                if (_publicationHook is not null) await _publicationHook.BeforePushAsync(ct).ConfigureAwait(false);
                var refreshed = await FetchRevisionAsync(ct).ConfigureAwait(false);
                if (!StringComparer.Ordinal.Equals(refreshed, request.ExpectedRevision))
                {
                    await MaterializeRevisionAsync(refreshed, ct).ConfigureAwait(false);
                    return new PublishResult(false, refreshed);
                }
                return new PublishResult(true, refreshed);
            }
            if (staged.ExitCode != 1) ThrowGitFailure("Unable to inspect staged encrypted objects.", staged);

            var commit = await RunGitAsync(["commit", "--no-gpg-sign", "-m", request.CommitMessage], ct).ConfigureAwait(false);
            if (commit.ExitCode != 0) ThrowGitFailure("Unable to commit encrypted objects in the dedicated clone.", commit);

            // Rewrite as a parentless commit so force-push leaves only the current tree on main.
            var candidateRevision = await ReplaceHistoryWithOrphanSnapshotAsync(request.CommitMessage, ct)
                .ConfigureAwait(false);
            if (_publicationHook is not null) await _publicationHook.BeforePushAsync(ct).ConfigureAwait(false);

            var push = await _pushTransport.PushAsync(_git, _clonePath, request.ExpectedRevision, ct)
                .ConfigureAwait(false);
            if (push.ExitCode == 0)
                return new PublishResult(true, candidateRevision);

            var current = await FetchRevisionAsync(ct).ConfigureAwait(false);
            if (StringComparer.Ordinal.Equals(current, candidateRevision))
                return new PublishResult(true, candidateRevision);
            await MaterializeRevisionAsync(current, ct).ConfigureAwait(false);
            if (!StringComparer.Ordinal.Equals(current, request.ExpectedRevision))
                return new PublishResult(false, current);
            ThrowGitFailure("Unable to push encrypted objects.", push);
            throw new InvalidOperationException("Unreachable.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureCloneAsync(CancellationToken ct)
    {
        EnsureSafePathComponents(_storageRoot);
        if (Directory.Exists(_clonePath))
        {
            EnsureSafeDirectory(_clonePath);
            EnsureOwnedClone();
            await ConfigureIdentityAsync(ct).ConfigureAwait(false);
            return;
        }

        if (_remoteKind == GitRemoteKind.GitHub)
        {
            var repository = ParseGitHubRepository(_remoteUrl);
            var visibility = await _visibilityVerifier.VerifyPrivateAsync(repository, ct).ConfigureAwait(false);
            if (!visibility.IsPrivate) throw new InvalidOperationException(visibility.Diagnostic);
        }

        var repositoryDirectory = Path.GetDirectoryName(_clonePath)!;
        Directory.CreateDirectory(repositoryDirectory);
        EnsureSafeDirectory(repositoryDirectory);
        var clone = await _git.RunAsync(["clone", "--no-checkout", "--origin", "origin", _remoteUrl, _clonePath], _storageRoot, ct).ConfigureAwait(false);
        if (clone.ExitCode != 0) ThrowGitFailure("Unable to create the dedicated encrypted-history clone.", clone);
        EnsureSafeDirectory(_clonePath);
        await ConfigureIdentityAsync(ct).ConfigureAwait(false);
        var metadataDirectory = Path.Combine(_clonePath, ".git", "codex-history-sync");
        EnsureSafePathComponents(metadataDirectory);
        Directory.CreateDirectory(metadataDirectory);
        EnsureSafeDirectory(metadataDirectory);
        var marker = Path.Combine(metadataDirectory, "repository-id");
        await File.WriteAllTextAsync(marker, _repositoryId, Encoding.UTF8, ct).ConfigureAwait(false);
        EnsureSafeFile(marker);
    }

    private async Task ConfigureIdentityAsync(CancellationToken ct)
    {
        // Large first-time history packs need a bigger HTTP buffer; GitHub often resets the default.
        var postBuffer = await RunGitAsync(["config", "http.postBuffer", "524288000"], ct).ConfigureAwait(false);
        if (postBuffer.ExitCode != 0) ThrowGitFailure("Unable to configure the dedicated clone HTTP buffer.", postBuffer);
        var httpVersion = await RunGitAsync(["config", "http.version", "HTTP/1.1"], ct).ConfigureAwait(false);
        if (httpVersion.ExitCode != 0) ThrowGitFailure("Unable to configure the dedicated clone HTTP version.", httpVersion);
        var email = await RunGitAsync(["config", "user.email", "codex-history-sync@localhost"], ct).ConfigureAwait(false);
        if (email.ExitCode != 0) ThrowGitFailure("Unable to configure the dedicated clone identity.", email);
        var name = await RunGitAsync(["config", "user.name", "Codex History Sync"], ct).ConfigureAwait(false);
        if (name.ExitCode != 0) ThrowGitFailure("Unable to configure the dedicated clone identity.", name);
    }

    private async Task<string> ReplaceHistoryWithOrphanSnapshotAsync(string commitMessage, CancellationToken ct)
    {
        var tree = await RunGitAsync(["write-tree"], ct).ConfigureAwait(false);
        if (tree.ExitCode != 0) ThrowGitFailure("Unable to resolve the publish tree.", tree);
        var treeSha = tree.StandardOutput.Trim();
        if (treeSha.Length == 0) throw new InvalidDataException("The publish tree is empty.");

        var orphan = await RunGitAsync(["commit-tree", treeSha, "-m", commitMessage], ct).ConfigureAwait(false);
        if (orphan.ExitCode != 0) ThrowGitFailure("Unable to create the single-snapshot publish commit.", orphan);
        var orphanSha = orphan.StandardOutput.Trim();
        if (orphanSha.Length == 0) throw new InvalidDataException("The single-snapshot publish commit is missing.");

        var update = await RunGitAsync(["update-ref", "refs/heads/main", orphanSha], ct).ConfigureAwait(false);
        if (update.ExitCode != 0) ThrowGitFailure("Unable to point main at the single-snapshot commit.", update);
        var reset = await RunGitAsync(["reset", "--hard", orphanSha], ct).ConfigureAwait(false);
        if (reset.ExitCode != 0) ThrowGitFailure("Unable to check out the single-snapshot commit.", reset);
        return orphanSha;
    }

    private async Task<string> FetchRevisionAsync(CancellationToken ct)
    {
        EnsureOwnedClone();
        var clear = await RunGitAsync(["update-ref", "-d", "refs/remotes/origin/main"], ct).ConfigureAwait(false);
        if (clear.ExitCode != 0) ThrowGitFailure("Unable to clear the remote-tracking revision.", clear);
        var fetch = await RunGitAsync(
            ["fetch", "--prune", "--no-tags", "origin", "+refs/heads/main:refs/remotes/origin/main"],
            ct).ConfigureAwait(false);
        if (fetch.ExitCode != 0 && !LooksLikeEmptyRemote(fetch)) ThrowGitFailure("Unable to fetch origin/main.", fetch);
        return await ResolveRevisionAsync("refs/remotes/origin/main", ct, allowMissing: true).ConfigureAwait(false);
    }

    private async Task MaterializeRevisionAsync(string revision, CancellationToken ct)
    {
        EnsureSafeDirectory(_clonePath);
        EnsureOwnedClone();
        GitCommandResult materialize;
        if (revision.Length == 0)
        {
            materialize = await RunGitAsync(["symbolic-ref", "HEAD", "refs/heads/main"], ct).ConfigureAwait(false);
            if (materialize.ExitCode == 0)
                materialize = await RunGitAsync(["update-ref", "-d", "refs/heads/main"], ct).ConfigureAwait(false);
            if (materialize.ExitCode == 0)
                materialize = await RunGitAsync(["rm", "-rf", "--ignore-unmatch", "--", "."], ct).ConfigureAwait(false);
        }
        else
        {
            materialize = await RunGitAsync(["reset", "--hard", "refs/remotes/origin/main"], ct).ConfigureAwait(false);
            if (materialize.ExitCode == 0)
                materialize = await RunGitAsync(["checkout", "-B", "main", "refs/remotes/origin/main"], ct).ConfigureAwait(false);
            if (materialize.ExitCode == 0)
                materialize = await RunGitAsync(["reset", "--hard", "refs/remotes/origin/main"], ct).ConfigureAwait(false);
        }
        if (materialize.ExitCode != 0) ThrowGitFailure("Unable to materialize the fetched revision.", materialize);
        EnsureOwnedClone();
        var clean = await RunGitAsync(["clean", "-fdx"], ct).ConfigureAwait(false);
        if (clean.ExitCode != 0) ThrowGitFailure("Unable to clean the dedicated clone.", clean);
        EnsureOwnedClone();
    }

    private async Task ApplyIndexChangeAsync(EncryptedIndexChange change, CancellationToken ct)
    {
        var destination = Path.Combine(_clonePath, "repository.chs");
        if (change.Delete)
        {
            if (File.Exists(destination)) EnsureSafeFile(destination);
            var remove = await RunGitAsync(["rm", "--ignore-unmatch", "--", "repository.chs"], ct).ConfigureAwait(false);
            if (remove.ExitCode != 0) ThrowGitFailure("Unable to remove encrypted repository index.", remove);
            return;
        }
        await CopyAndStageAsync(change.CiphertextPath, destination, "repository.chs", ct).ConfigureAwait(false);
    }

    private async Task ApplyObjectChangeAsync(EncryptedObjectChange change, CancellationToken ct)
    {
        var objectId = change.ObjectId.Value ?? string.Empty;
        if (!ObjectIdPattern.IsMatch(objectId))
            throw new ArgumentException("Encrypted object IDs must be 64 lowercase hexadecimal characters.", nameof(change));
        var relative = Path.Combine("objects", objectId[..2], objectId[2..] + ".chs");
        var destination = Path.Combine(_clonePath, relative);
        AssertContained(destination, _clonePath);
        if (change.Delete)
        {
            if (File.Exists(destination)) EnsureSafeFile(destination);
            var remove = await RunGitAsync(["rm", "--ignore-unmatch", "--", relative.Replace('\\', '/')], ct).ConfigureAwait(false);
            if (remove.ExitCode != 0) ThrowGitFailure("Unable to remove encrypted object.", remove);
            return;
        }
        await CopyAndStageAsync(change.CiphertextPath, destination, relative.Replace('\\', '/'), ct).ConfigureAwait(false);
    }

    private async Task CopyAndStageAsync(string sourcePath, string destination, string gitPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentException("Ciphertext path is required.", nameof(sourcePath));
        var source = Path.GetFullPath(sourcePath);
        EnsureSafeFile(source);
        var destinationDirectory = Path.GetDirectoryName(destination)!;
        EnsureSafePathComponents(destinationDirectory);
        Directory.CreateDirectory(destinationDirectory);
        EnsureSafeDirectory(destinationDirectory);
        if (File.Exists(destination)) EnsureSafeFile(destination);
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        await using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await input.CopyToAsync(output, ct).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);
        }
        EnsureSafeFile(destination);
        var add = await RunGitAsync(["add", "--", gitPath], ct).ConfigureAwait(false);
        if (add.ExitCode != 0) ThrowGitFailure("Unable to stage encrypted content.", add);
    }

    private async Task<GitCommandResult> RunGitAsync(IReadOnlyList<string> arguments, CancellationToken ct) =>
        await _git.RunAsync(arguments, _clonePath, ct).ConfigureAwait(false);

    private async Task<string> ResolveRevisionAsync(string reference, CancellationToken ct, bool allowMissing = false)
    {
        var result = await RunGitAsync(["rev-parse", "--verify", reference], ct).ConfigureAwait(false);
        if (result.ExitCode != 0 && allowMissing) return string.Empty;
        if (result.ExitCode != 0) ThrowGitFailure("Unable to resolve Git revision.", result);
        return result.StandardOutput.Trim();
    }

    private void EnsureOwnedClone()
    {
        var marker = Path.Combine(_clonePath, ".git", "codex-history-sync", "repository-id");
        EnsureSafeFile(marker);
        if (!StringComparer.Ordinal.Equals(File.ReadAllText(marker, Encoding.UTF8).Trim(), _repositoryId))
            throw new InvalidOperationException("Refusing to modify a clone not owned by this repository.");
    }

    private static string ParseGitHubRepository(string remoteUrl)
    {
        string path;
        if (Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri) &&
            uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            path = uri.AbsolutePath;
        else
        {
            var match = Regex.Match(remoteUrl, @"^(?:git@)?github\.com:(?<path>[^?#]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) throw new ArgumentException("GitHub remote must identify github.com/owner/repository.", nameof(remoteUrl));
            path = match.Groups["path"].Value;
        }
        var repository = path.Trim('/');
        if (repository.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repository = repository[..^4];
        if (repository.Split('/').Length != 2) throw new ArgumentException("GitHub remote must identify owner/repository.", nameof(remoteUrl));
        return repository;
    }

    private static bool IsLocalRemote(string remoteUrl) =>
        Path.IsPathFullyQualified(remoteUrl) ||
        (Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri) && uri.IsFile);

    private static bool LooksLikeEmptyRemote(GitCommandResult result) =>
        result.StandardError.Contains("couldn't find remote ref", StringComparison.OrdinalIgnoreCase) ||
        result.StandardError.Contains("could not find remote branch", StringComparison.OrdinalIgnoreCase);

    private static void ThrowGitFailure(string message, GitCommandResult result)
    {
        var detail = result.TimedOut
            ? "The Git command timed out."
            : GitCommand.Redact(result.StandardError);
        throw new InvalidOperationException($"{message} {detail}".Trim());
    }

    private static void AssertContained(string child, string parent)
    {
        var fullChild = Path.GetFullPath(child);
        var fullParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar;
        if (!fullChild.StartsWith(fullParent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Path escapes the dedicated clone boundary.");
    }

    private static void EnsureSafeDirectory(string path)
    {
        EnsureSafePathComponents(path);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("Expected dedicated-clone directory was not found.");
    }

    private static void EnsureSafeFile(string path)
    {
        EnsureSafePathComponents(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Expected regular file was not found.", path);
    }

    private static IEnumerable<string> EnumerateSafeObjectFiles(string directory)
    {
        EnsureSafeDirectory(directory);
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            EnsureSafeDirectory(child);
            foreach (var file in EnumerateSafeObjectFiles(child)) yield return file;
        }
        foreach (var file in Directory.EnumerateFiles(directory, "*.chs", SearchOption.TopDirectoryOnly))
        {
            EnsureSafeFile(file);
            yield return file;
        }
    }

    private static void EnsureNotInsideGitWorktree(string path)
    {
        for (var current = Path.GetFullPath(path); ;)
        {
            var gitMetadata = Path.Combine(current, ".git");
            if (Directory.Exists(gitMetadata) || File.Exists(gitMetadata))
                throw new ArgumentException("Dedicated Git storage cannot be located inside a source Git worktree.", nameof(path));
            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || StringComparer.OrdinalIgnoreCase.Equals(parent, current)) return;
            current = parent;
        }
    }

    private static void EnsureSafePathComponents(string path)
    {
        for (var current = Path.GetFullPath(path); ;)
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Symbolic links and reparse points are not permitted at Git storage boundaries.");
            }
            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || StringComparer.OrdinalIgnoreCase.Equals(parent, current)) return;
            current = parent;
        }
    }
}
