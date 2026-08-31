using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Model;

namespace CodexHistorySync.Core.IO;

public interface IAtomicFileSystem
{
    Task WriteTemporaryAsync(string path, Stream content, CancellationToken ct);
    Task PublishAsync(string temporaryPath, string destinationPath, ContentHash expectedSourceHash, ContentHash? expectedDestinationHash, Func<bool>? mutationAllowed, CancellationToken ct);
    Task ReplaceAsync(string temporaryPath, string destinationPath, CancellationToken ct);
    Task<bool> ReplaceIfUnchangedAsync(string temporaryPath, string destinationPath, ContentHash expectedDestinationHash, Func<bool>? mutationAllowed, CancellationToken ct);
    Task DeleteAsync(string path, CancellationToken ct);
    Task<bool> DeleteIfUnchangedAsync(string path, ContentHash expectedHash, Func<bool>? mutationAllowed, CancellationToken ct);
}

public sealed class AtomicMutationException : IOException
{
    public AtomicMutationException(string message, Exception innerException, IReadOnlyList<string> preservedPaths)
        : base(message, innerException) => PreservedPaths = preservedPaths;

    public IReadOnlyList<string> PreservedPaths { get; }
}

internal interface IAtomicFileSystemHooks
{
    void OnAfterSourceHash(string path);
    void OnAfterDestinationHash(string path);
    void OnAfterDeleteCapture(string quarantinePath, string destinationPath);
    void OnBeforeArtifactCleanup(string path);
    void OnBeforeMutationPathValidation(string path);
    void OnAfterPublishMutation(string destinationPath);
}

public sealed class AtomicFileSystem : IAtomicFileSystem
{
    private readonly IAtomicFileSystemHooks _hooks;

    public AtomicFileSystem() : this(NoopAtomicFileSystemHooks.Instance) { }
    internal AtomicFileSystem(IAtomicFileSystemHooks hooks) => _hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));

    public async Task WriteTemporaryAsync(string path, Stream content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await content.CopyToAsync(destination, ct).ConfigureAwait(false);
        await destination.FlushAsync(ct).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
    }

    public Task ReplaceAsync(string temporaryPath, string destinationPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var temporary = Path.GetFullPath(temporaryPath);
        var destination = Path.GetFullPath(destinationPath);
        if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(temporary), Path.GetDirectoryName(destination)))
            throw new ArgumentException("Atomic replacement requires a sibling temporary file.", nameof(temporaryPath));
        File.Move(temporary, destination);
        return Task.CompletedTask;
    }

    public async Task PublishAsync(string temporaryPath, string destinationPath, ContentHash expectedSourceHash, ContentHash? expectedDestinationHash, Func<bool>? mutationAllowed, CancellationToken ct)
    {
        var temporary = Path.GetFullPath(temporaryPath);
        var destination = Path.GetFullPath(destinationPath);
        EnsureSiblings(temporary, destination);
        if (!BackupHashEquals(await HashPathAsync(temporary, ct).ConfigureAwait(false), expectedSourceHash)) throw new InvalidDataException("Staged content does not match its authenticated hash.");
        _hooks.OnAfterSourceHash(temporary);
        if (expectedDestinationHash is { } expectedDestination)
        {
            if (!File.Exists(destination) || !BackupHashEquals(await HashPathAsync(destination, ct).ConfigureAwait(false), expectedDestination))
                throw new IOException("The destination changed after backup.");
            _hooks.OnAfterDestinationHash(destination);
        }

        _hooks.OnBeforeMutationPathValidation(destination);
        PathSafety.RejectReparsePoints(temporary, nameof(temporaryPath));
        PathSafety.RejectReparsePoints(destination, nameof(destinationPath));
        if (!BackupHashEquals(await HashPathAsync(temporary, ct).ConfigureAwait(false), expectedSourceHash)) throw new InvalidDataException("Staged content changed before publication.");
        if (expectedDestinationHash is { } finalExpectedDestination && (!File.Exists(destination) || !BackupHashEquals(await HashPathAsync(destination, ct).ConfigureAwait(false), finalExpectedDestination)))
            throw new IOException("The destination changed immediately before publication.");
        PathSafety.RejectReparsePoints(temporary, nameof(temporaryPath));
        PathSafety.RejectReparsePoints(destination, nameof(destinationPath));
        ct.ThrowIfCancellationRequested();
        if (mutationAllowed is not null && !mutationAllowed()) throw new InvalidOperationException("The atomic mutation guard rejected publication.");

        if (expectedDestinationHash is null)
        {
            var published = false;
            try
            {
                File.Move(temporary, destination);
                published = true;
                _hooks.OnAfterPublishMutation(destination);
                if (!BackupHashEquals(await HashPathAsync(destination, CancellationToken.None).ConfigureAwait(false), expectedSourceHash))
                    throw new InvalidDataException("Published content failed authenticated hash verification.");
            }
            catch (Exception exception)
            {
                if (!published) throw;
                var evidence = SiblingArtifact(destination, "rejected");
                try { if (File.Exists(destination)) File.Move(destination, evidence); }
                catch { }
                throw new AtomicMutationException("New-file publication failed verification.", exception, ExistingArtifacts(evidence, destination));
            }
            return;
        }

        var displaced = SiblingArtifact(destination, "displaced");
        var captured = false;
        try
        {
            File.Replace(temporary, destination, displaced);
            captured = true;
            _hooks.OnAfterPublishMutation(destination);
            if (!BackupHashEquals(await HashPathAsync(displaced, CancellationToken.None).ConfigureAwait(false), expectedDestinationHash.Value) ||
                !BackupHashEquals(await HashPathAsync(destination, CancellationToken.None).ConfigureAwait(false), expectedSourceHash))
                throw new InvalidDataException("Atomic replacement failed authenticated hash verification.");
            _hooks.OnBeforeArtifactCleanup(displaced);
            File.Delete(displaced);
            captured = false;
        }
        catch (Exception exception)
        {
            var preserved = captured ? RecoverCaptured(displaced, destination) : ExistingArtifacts(displaced);
            throw new AtomicMutationException("Atomic replacement failed and recovery was attempted.", exception, preserved);
        }
    }

    public async Task<bool> ReplaceIfUnchangedAsync(string temporaryPath, string destinationPath, ContentHash expectedDestinationHash, Func<bool>? mutationAllowed, CancellationToken ct)
    {
        try
        {
            var sourceHash = await HashPathAsync(temporaryPath, ct).ConfigureAwait(false);
            await PublishAsync(temporaryPath, destinationPath, sourceHash, expectedDestinationHash, mutationAllowed, ct).ConfigureAwait(false);
            return true;
        }
        catch (IOException exception) when (exception is not AtomicMutationException) { return false; }
    }

    public Task DeleteAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        File.Delete(path);
        return Task.CompletedTask;
    }

    public async Task<bool> DeleteIfUnchangedAsync(string path, ContentHash expectedHash, Func<bool>? mutationAllowed, CancellationToken ct)
    {
        var destination = Path.GetFullPath(path);
        await using (var existing = TryOpenForIdentity(destination))
            if (existing is null || !BackupHashEquals(await HashAsync(existing, ct).ConfigureAwait(false), expectedHash)) return false;
        _hooks.OnAfterDestinationHash(destination);
        _hooks.OnBeforeMutationPathValidation(destination);
        PathSafety.RejectReparsePoints(destination, nameof(path));
        if (!BackupHashEquals(await HashPathAsync(destination, ct).ConfigureAwait(false), expectedHash)) return false;
        PathSafety.RejectReparsePoints(destination, nameof(path));
        ct.ThrowIfCancellationRequested();
        if (mutationAllowed is not null && !mutationAllowed()) throw new InvalidOperationException("The atomic mutation guard rejected deletion.");
        var quarantine = SiblingArtifact(destination, "deleted");
        var captured = false;
        try
        {
            File.Move(destination, quarantine);
            captured = true;
            _hooks.OnAfterDeleteCapture(quarantine, destination);
            if (!BackupHashEquals(await HashPathAsync(quarantine, CancellationToken.None).ConfigureAwait(false), expectedHash))
                throw new InvalidDataException("Captured deletion target failed hash verification.");
            _hooks.OnBeforeArtifactCleanup(quarantine);
            File.Delete(quarantine);
            captured = false;
            return !File.Exists(destination);
        }
        catch (Exception exception)
        {
            var preserved = captured ? RecoverCaptured(quarantine, destination) : ExistingArtifacts(quarantine);
            throw new AtomicMutationException("Atomic deletion failed and recovery was attempted.", exception, preserved);
        }
    }

    private static FileStream? TryOpenForIdentity(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static async Task<ContentHash> HashAsync(Stream stream, CancellationToken ct) =>
        new(Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant());

    private static bool BackupHashEquals(ContentHash left, ContentHash right) =>
        StringComparer.OrdinalIgnoreCase.Equals(left.Hex, right.Hex);

    private static async Task<ContentHash> HashPathAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await HashAsync(stream, ct).ConfigureAwait(false);
    }

    private static string SiblingArtifact(string path, string kind) =>
        Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.{kind}");

    private static IReadOnlyList<string> RecoverCaptured(string captured, string destination)
    {
        var preserved = new List<string>();
        try
        {
            if (!File.Exists(destination)) File.Move(captured, destination);
            else
            {
                var concurrent = SiblingArtifact(destination, "preserved-concurrent");
                File.Replace(captured, destination, concurrent);
                if (File.Exists(concurrent)) preserved.Add(concurrent);
            }
        }
        catch
        {
            if (File.Exists(captured)) preserved.Add(captured);
            if (File.Exists(destination)) preserved.Add(destination);
        }
        return preserved;
    }

    private static IReadOnlyList<string> ExistingArtifacts(params string[] paths) => paths.Where(File.Exists).ToArray();

    private static void EnsureSiblings(string temporary, string destination)
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(temporary), Path.GetDirectoryName(destination)))
            throw new ArgumentException("Atomic publication requires a sibling temporary file.", nameof(temporary));
    }

    private sealed class NoopAtomicFileSystemHooks : IAtomicFileSystemHooks
    {
        public static readonly NoopAtomicFileSystemHooks Instance = new();
        public void OnAfterSourceHash(string path) { }
        public void OnAfterDestinationHash(string path) { }
        public void OnAfterDeleteCapture(string quarantinePath, string destinationPath) { }
        public void OnBeforeArtifactCleanup(string path) { }
        public void OnBeforeMutationPathValidation(string path) { }
        public void OnAfterPublishMutation(string destinationPath) { }
    }
}

internal static class PathSafety
{
    public static string Canonicalize(string path, string parameterName, bool requireFullyQualified = false)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A path is required.", parameterName);
        if (requireFullyQualified && !Path.IsPathFullyQualified(path)) throw new ArgumentException("The path must be fully qualified.", parameterName);
        if (path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries).Any(segment => segment == ".."))
            throw new ArgumentException("Traversal path segments are not allowed.", parameterName);
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        RejectReparsePoints(canonical, parameterName);
        return canonical;
    }

    public static void EnsureOutsideCodex(string candidate, CodexPaths paths, string parameterName, Grok.GrokPaths? grokPaths = null,
        Claude.ClaudePaths? claudePaths = null, Continue.ContinuePaths? continuePaths = null)
    {
        foreach (var synchronizedPath in new[] { paths.Home, paths.Sessions, paths.ArchivedSessions, paths.Attachments })
            if (CodexPaths.IsPathWithin(candidate, synchronizedPath) || CodexPaths.IsPathWithin(synchronizedPath, candidate))
                throw new ArgumentException("The storage path must not overlap synchronized Codex paths.", parameterName);
        if (grokPaths is not null)
        {
            foreach (var synchronizedPath in new[] { grokPaths.Home, grokPaths.Sessions })
                if (CodexPaths.IsPathWithin(candidate, synchronizedPath) || CodexPaths.IsPathWithin(synchronizedPath, candidate))
                    throw new ArgumentException("The storage path must not overlap synchronized Grok paths.", parameterName);
        }
        if (claudePaths is not null)
        {
            foreach (var synchronizedPath in new[] { claudePaths.Home, claudePaths.Projects })
                if (CodexPaths.IsPathWithin(candidate, synchronizedPath) || CodexPaths.IsPathWithin(synchronizedPath, candidate))
                    throw new ArgumentException("The storage path must not overlap synchronized Claude paths.", parameterName);
        }
        if (continuePaths is not null)
        {
            foreach (var synchronizedPath in new[] { continuePaths.Home, continuePaths.Sessions })
                if (CodexPaths.IsPathWithin(candidate, synchronizedPath) || CodexPaths.IsPathWithin(synchronizedPath, candidate))
                    throw new ArgumentException("The storage path must not overlap synchronized Continue paths.", parameterName);
        }
    }

    public static string EnsureSessionDestination(string candidate, ObjectKind kind, CodexPaths paths, string parameterName,
        Grok.GrokPaths? grokPaths = null, Claude.ClaudePaths? claudePaths = null, Continue.ContinuePaths? continuePaths = null)
    {
        var canonical = Canonicalize(candidate, parameterName, requireFullyQualified: true);
        if (kind == ObjectKind.ContinueSession)
        {
            if (continuePaths is null)
                throw new ArgumentException("Continue paths are required for Continue session destinations.", parameterName);
            // sessions/<uuid>.json exactly: the directory is flat, and the shared index lives in it.
            if (!StringComparer.OrdinalIgnoreCase.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(canonical) ?? string.Empty),
                    Path.TrimEndingDirectorySeparator(continuePaths.Sessions)))
                throw new ArgumentException("The destination is outside the synchronized Continue sessions directory.", parameterName);
            if (Continue.ContinuePaths.IsIndexFile(canonical))
                throw new ArgumentException("The Continue session index is not a session destination.", parameterName);
            if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetExtension(canonical), ".json") ||
                !Guid.TryParseExact(Path.GetFileNameWithoutExtension(canonical), "D", out _))
                throw new ArgumentException("Continue session destinations must be named <uuid>.json.", parameterName);
            return canonical;
        }

        if (kind == ObjectKind.ClaudeSession)
        {
            if (claudePaths is null)
                throw new ArgumentException("Claude paths are required for Claude session destinations.", parameterName);
            // projects/<project>/<uuid>.jsonl exactly: the project directory is one level under the
            // projects root and the transcript sits directly inside it. Anything deeper is not Claude's layout.
            var projectDirectory = Path.GetDirectoryName(canonical);
            if (projectDirectory is null ||
                !StringComparer.OrdinalIgnoreCase.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(projectDirectory) ?? string.Empty),
                    Path.TrimEndingDirectorySeparator(claudePaths.Projects)))
                throw new ArgumentException("The destination is outside the synchronized Claude projects directory.", parameterName);
            if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetExtension(canonical), ".jsonl") ||
                !Guid.TryParseExact(Path.GetFileNameWithoutExtension(canonical), "D", out _))
                throw new ArgumentException("Claude session destinations must be named <uuid>.jsonl.", parameterName);
            return canonical;
        }

        if (kind == ObjectKind.GrokSession)
        {
            if (grokPaths is null)
                throw new ArgumentException("Grok paths are required for Grok session destinations.", parameterName);
            if (!CodexPaths.IsPathWithin(canonical, grokPaths.Sessions) ||
                StringComparer.OrdinalIgnoreCase.Equals(canonical, Path.TrimEndingDirectorySeparator(grokPaths.Sessions)))
                throw new ArgumentException("The destination is outside the synchronized Grok sessions directory.", parameterName);
            if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(canonical), "chat_history.jsonl"))
                throw new ArgumentException("Grok session destinations must be chat_history.jsonl.", parameterName);
            return canonical;
        }

        var root = kind switch
        {
            ObjectKind.ActiveSession => paths.Sessions,
            ObjectKind.ArchivedSession => paths.ArchivedSessions,
            _ => throw new ArgumentException("Only active, archived, Grok, and Claude sessions can be written as history.", parameterName)
        };
        if (!CodexPaths.IsPathWithin(canonical, root) || StringComparer.OrdinalIgnoreCase.Equals(canonical, Path.TrimEndingDirectorySeparator(root)))
            throw new ArgumentException("The destination is outside its synchronized Codex directory.", parameterName);
        if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetExtension(canonical), ".jsonl"))
            throw new ArgumentException("Session destinations must use the .jsonl extension.", parameterName);
        return canonical;
    }

    public static string ValidateFileComponent(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.Contains('/') || value.Contains('\\'))
            throw new ArgumentException("The value is not a safe file-name component.", parameterName);
        return value;
    }

    public static void RejectReparsePoints(string path, string parameterName)
    {
        FileSystemInfo? current = File.Exists(path) ? new FileInfo(path) : new DirectoryInfo(Directory.Exists(path) ? path : Path.GetDirectoryName(path)!);
        while (current is not null)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new ArgumentException("Reparse points are not allowed in managed filesystem paths.", parameterName);
            current = current switch
            {
                FileInfo file => file.Directory,
                DirectoryInfo directory => directory.Parent,
                _ => null
            };
        }
    }
}
