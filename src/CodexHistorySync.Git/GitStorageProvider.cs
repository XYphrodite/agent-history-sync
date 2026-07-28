using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CodexHistorySync.Core.Model;
using CodexHistorySync.Core.Providers;
using CodexHistorySync.Core.Sync;

namespace CodexHistorySync.Git;

public sealed class GitStorageProvider : IStorageProvider
{
    private static readonly Regex RepositoryIdPattern = new("^[A-Za-z0-9_-]{1,128}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ObjectIdPattern = new("^[a-f0-9]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly string _repositoryId;
    private readonly string _remoteUrl;
    private readonly string _storageRoot;
    private readonly string _clonePath;
    private readonly GitCommand _git;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GitStorageProvider(
        string repositoryId,
        string remoteUrl,
        string? storageRoot = null,
        string gitExecutable = "git",
        TimeSpan? commandTimeout = null)
    {
        var safeRepositoryId = repositoryId ?? throw new ArgumentNullException(nameof(repositoryId));
        if (!RepositoryIdPattern.IsMatch(safeRepositoryId))
            throw new ArgumentException("Repository ID contains unsupported characters.", nameof(repositoryId));
        var safeRemoteUrl = remoteUrl ?? throw new ArgumentNullException(nameof(remoteUrl));
        if (string.IsNullOrWhiteSpace(safeRemoteUrl)) throw new ArgumentException("Remote URL is required.", nameof(remoteUrl));

        _repositoryId = safeRepositoryId;
        _remoteUrl = safeRemoteUrl;
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) ??
            throw new InvalidOperationException("Local application data directory is unavailable.");
        _storageRoot = Path.GetFullPath(storageRoot ?? Path.Combine(
            localAppData,
            "CodexHistorySync", "repositories"));
        _clonePath = Path.Combine(_storageRoot, _repositoryId, "git");
        AssertContained(_clonePath, _storageRoot);
        EnsureNotInsideGitWorktree(_storageRoot);
        _git = new GitCommand(gitExecutable, commandTimeout);
    }

    public async Task<RemoteSnapshot> ReadSnapshotAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureCloneAsync(ct).ConfigureAwait(false);
            var revision = await FetchRevisionAsync(ct).ConfigureAwait(false);
            EnsureSafeDirectory(_clonePath);
            var objects = new Dictionary<LogicalObjectId, ObjectVersion>();
            var objectsPath = Path.Combine(_clonePath, "objects");
            if (!Directory.Exists(objectsPath)) return new RemoteSnapshot(revision, objects);
            EnsureSafeDirectory(objectsPath);
            foreach (var file in EnumerateSafeObjectFiles(objectsPath))
            {
                EnsureSafeFile(file);
                var relative = Path.GetRelativePath(objectsPath, file).Replace(Path.DirectorySeparatorChar, '/');
                var segments = relative.Split('/');
                if (segments.Length != 2 || segments[0].Length != 2 || !ObjectIdPattern.IsMatch(segments[0] + Path.GetFileNameWithoutExtension(segments[1])))
                    throw new InvalidDataException("Repository contains an invalid encrypted object path.");
                var id = new LogicalObjectId(segments[0] + Path.GetFileNameWithoutExtension(segments[1]));
                objects[id] = new ObjectVersion(id, ObjectKind.ActiveSession, new ContentHash(string.Empty), revision, IsDeleted: false);
            }
            return new RemoteSnapshot(revision, objects);
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

            await ResetDedicatedCloneAsync(remoteRevision, ct).ConfigureAwait(false);
            foreach (var change in request.Changes)
            {
                await ApplyChangeAsync(change, ct).ConfigureAwait(false);
            }

            var staged = await RunGitAsync(["diff", "--cached", "--quiet"], ct).ConfigureAwait(false);
            if (staged.ExitCode == 0) return new PublishResult(true, remoteRevision);
            if (staged.ExitCode != 1) ThrowGitFailure("Unable to inspect staged encrypted objects.", staged);

            var commit = await RunGitAsync(["commit", "--no-gpg-sign", "-m", request.CommitMessage], ct).ConfigureAwait(false);
            if (commit.ExitCode != 0) ThrowGitFailure("Unable to commit encrypted objects in the dedicated clone.", commit);

            var push = await RunGitAsync(["push", "origin", "HEAD:main"], ct).ConfigureAwait(false);
            if (push.ExitCode == 0)
            {
                var revision = await ResolveRevisionAsync("HEAD", ct).ConfigureAwait(false);
                return new PublishResult(true, revision);
            }

            var refreshed = await FetchRevisionAsync(ct).ConfigureAwait(false);
            await ResetDedicatedCloneAsync(refreshed, ct).ConfigureAwait(false);
            if (!StringComparer.Ordinal.Equals(refreshed, request.ExpectedRevision) || IsNonFastForward(push))
                return new PublishResult(false, refreshed);
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
            return;
        }

        var repositoryDirectory = Path.GetDirectoryName(_clonePath)!;
        Directory.CreateDirectory(repositoryDirectory);
        EnsureSafeDirectory(repositoryDirectory);
        var clone = await _git.RunAsync(["clone", "--no-checkout", "--origin", "origin", _remoteUrl, _clonePath], _storageRoot, ct).ConfigureAwait(false);
        if (clone.ExitCode != 0) ThrowGitFailure("Unable to create the dedicated encrypted-history clone.", clone);
        EnsureSafeDirectory(_clonePath);
        await File.WriteAllTextAsync(Path.Combine(_clonePath, ".codex-history-sync-repository-id"), _repositoryId, Encoding.UTF8, ct).ConfigureAwait(false);
        await ConfigureIdentityAsync(ct).ConfigureAwait(false);
    }

    private async Task ConfigureIdentityAsync(CancellationToken ct)
    {
        var email = await RunGitAsync(["config", "user.email", "codex-history-sync@localhost"], ct).ConfigureAwait(false);
        if (email.ExitCode != 0) ThrowGitFailure("Unable to configure the dedicated clone identity.", email);
        var name = await RunGitAsync(["config", "user.name", "Codex History Sync"], ct).ConfigureAwait(false);
        if (name.ExitCode != 0) ThrowGitFailure("Unable to configure the dedicated clone identity.", name);
    }

    private async Task<string> FetchRevisionAsync(CancellationToken ct)
    {
        var fetch = await RunGitAsync(["fetch", "--no-tags", "origin", "main"], ct).ConfigureAwait(false);
        if (fetch.ExitCode != 0 && !LooksLikeEmptyRemote(fetch)) ThrowGitFailure("Unable to fetch origin/main.", fetch);
        return await ResolveRevisionAsync("refs/remotes/origin/main", ct, allowMissing: true).ConfigureAwait(false);
    }

    private async Task ResetDedicatedCloneAsync(string remoteRevision, CancellationToken ct)
    {
        EnsureSafeDirectory(_clonePath);
        EnsureOwnedClone();
        if (remoteRevision.Length == 0)
        {
            var checkout = await RunGitAsync(["checkout", "--orphan", "main"], ct).ConfigureAwait(false);
            if (checkout.ExitCode != 0 && !checkout.StandardError.Contains("already exists", StringComparison.OrdinalIgnoreCase))
                ThrowGitFailure("Unable to initialize the dedicated clone branch.", checkout);
        }
        else
        {
            var reset = await RunGitAsync(["reset", "--hard", "refs/remotes/origin/main"], ct).ConfigureAwait(false);
            if (reset.ExitCode != 0) ThrowGitFailure("Unable to reset the dedicated clone.", reset);
        }
        var clean = await RunGitAsync(["clean", "-fdx"], ct).ConfigureAwait(false);
        if (clean.ExitCode != 0) ThrowGitFailure("Unable to clean the dedicated clone.", clean);
    }

    private async Task ApplyChangeAsync(EncryptedObjectChange change, CancellationToken ct)
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
            if (remove.ExitCode != 0) ThrowGitFailure("Unable to remove encrypted object from the dedicated clone.", remove);
            return;
        }

        if (string.IsNullOrWhiteSpace(change.CiphertextPath)) throw new ArgumentException("Ciphertext path is required for additions.", nameof(change));
        var source = Path.GetFullPath(change.CiphertextPath);
        EnsureSafeFile(source);
        var destinationDirectory = Path.GetDirectoryName(destination)!;
        if (Directory.Exists(destinationDirectory)) EnsureSafeDirectory(destinationDirectory);
        else Directory.CreateDirectory(destinationDirectory);
        EnsureSafeDirectory(destinationDirectory);
        if (File.Exists(destination)) EnsureSafeFile(destination);
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        await using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await input.CopyToAsync(output, ct).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);
        }
        EnsureSafeFile(destination);
        var add = await RunGitAsync(["add", "--", relative.Replace('\\', '/')], ct).ConfigureAwait(false);
        if (add.ExitCode != 0) ThrowGitFailure("Unable to stage encrypted object in the dedicated clone.", add);
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
        var marker = Path.Combine(_clonePath, ".codex-history-sync-repository-id");
        EnsureSafeFile(marker);
        var identity = File.ReadAllText(marker, Encoding.UTF8).Trim();
        if (!StringComparer.Ordinal.Equals(identity, _repositoryId))
            throw new InvalidOperationException("Refusing to reset a clone not owned by this Codex History Sync repository.");
    }

    private static bool LooksLikeEmptyRemote(GitCommandResult result) =>
        result.StandardError.Contains("couldn't find remote ref main", StringComparison.OrdinalIgnoreCase) ||
        result.StandardError.Contains("could not find remote branch main", StringComparison.OrdinalIgnoreCase);

    private static bool IsNonFastForward(GitCommandResult result) =>
        result.StandardError.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase) ||
        result.StandardError.Contains("fetch first", StringComparison.OrdinalIgnoreCase) ||
        result.StandardError.Contains("[rejected]", StringComparison.OrdinalIgnoreCase);

    private static void ThrowGitFailure(string message, GitCommandResult result) =>
        throw new InvalidOperationException($"{message} {GitCommand.Redact(result.StandardError)}".Trim());

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
        foreach (var childDirectory in Directory.EnumerateDirectories(directory))
        {
            EnsureSafeDirectory(childDirectory);
            foreach (var file in EnumerateSafeObjectFiles(childDirectory)) yield return file;
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
        var current = Path.GetFullPath(path);
        while (true)
        {
            if (File.Exists(current) || Directory.Exists(current)) AssertNoReparsePoint(current);
            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || StringComparer.OrdinalIgnoreCase.Equals(parent, current)) return;
            current = parent;
        }
    }

    private static void AssertNoReparsePoint(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Symbolic links and reparse points are not permitted at Git storage boundaries.");
    }
}
