using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Model;

namespace CodexHistorySync.Core.IO;

public interface IAtomicFileSystem
{
    Task WriteTemporaryAsync(string path, Stream content, CancellationToken ct);
    Task ReplaceAsync(string temporaryPath, string destinationPath, CancellationToken ct);
    Task<bool> ReplaceIfUnchangedAsync(string temporaryPath, string destinationPath, ContentHash expectedDestinationHash, Func<bool>? mutationAllowed, CancellationToken ct);
    Task DeleteAsync(string path, CancellationToken ct);
    Task<bool> DeleteIfUnchangedAsync(string path, ContentHash expectedHash, Func<bool>? mutationAllowed, CancellationToken ct);
}

public sealed class AtomicFileSystem : IAtomicFileSystem
{
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

    public async Task<bool> ReplaceIfUnchangedAsync(string temporaryPath, string destinationPath, ContentHash expectedDestinationHash, Func<bool>? mutationAllowed, CancellationToken ct)
    {
        var temporary = Path.GetFullPath(temporaryPath);
        var destination = Path.GetFullPath(destinationPath);
        if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(temporary), Path.GetDirectoryName(destination)))
            throw new ArgumentException("Atomic replacement requires a sibling temporary file.", nameof(temporaryPath));
        await using (var existing = TryOpenForIdentity(destination))
            if (existing is null || !BackupHashEquals(await HashAsync(existing, ct).ConfigureAwait(false), expectedDestinationHash)) return false;
        ct.ThrowIfCancellationRequested();
        if (mutationAllowed is not null && !mutationAllowed()) throw new InvalidOperationException("The atomic mutation guard rejected replacement.");
        var quarantine = SiblingArtifact(destination, "displaced");
        var rejected = SiblingArtifact(destination, "rejected");
        var incomingHash = await HashPathAsync(temporary, ct).ConfigureAwait(false);
        var captured = false;
        try
        {
            File.Replace(temporary, destination, quarantine);
            captured = true;
            if (BackupHashEquals(await HashPathAsync(quarantine, CancellationToken.None).ConfigureAwait(false), expectedDestinationHash))
            {
                File.Delete(quarantine);
                return true;
            }
            File.Replace(quarantine, destination, rejected);
            captured = false;
            if (BackupHashEquals(await HashPathAsync(rejected, CancellationToken.None).ConfigureAwait(false), incomingHash)) File.Delete(rejected);
            return false;
        }
        catch (FileNotFoundException) when (!captured) { return false; }
        catch
        {
            if (captured && File.Exists(quarantine)) RestoreCaptured(quarantine, destination);
            throw;
        }
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
        ct.ThrowIfCancellationRequested();
        if (mutationAllowed is not null && !mutationAllowed()) throw new InvalidOperationException("The atomic mutation guard rejected deletion.");
        var quarantine = SiblingArtifact(destination, "deleted");
        File.Move(destination, quarantine);
        var capturedMatches = BackupHashEquals(await HashPathAsync(quarantine, CancellationToken.None).ConfigureAwait(false), expectedHash);
        if (capturedMatches)
        {
            File.Delete(quarantine);
            return !File.Exists(destination);
        }
        if (!File.Exists(destination)) File.Move(quarantine, destination);
        else
        {
            var appeared = SiblingArtifact(destination, "appeared");
            File.Replace(quarantine, destination, appeared);
        }
        return false;
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

    private static void RestoreCaptured(string captured, string destination)
    {
        if (!File.Exists(destination)) File.Move(captured, destination);
        else File.Replace(captured, destination, SiblingArtifact(destination, "preserved-concurrent"));
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

    public static void EnsureOutsideCodex(string candidate, CodexPaths paths, string parameterName)
    {
        foreach (var synchronizedPath in new[] { paths.Home, paths.Sessions, paths.ArchivedSessions, paths.Attachments })
            if (CodexPaths.IsPathWithin(candidate, synchronizedPath) || CodexPaths.IsPathWithin(synchronizedPath, candidate))
                throw new ArgumentException("The storage path must not overlap synchronized Codex paths.", parameterName);
    }

    public static string EnsureSessionDestination(string candidate, ObjectKind kind, CodexPaths paths, string parameterName)
    {
        var canonical = Canonicalize(candidate, parameterName, requireFullyQualified: true);
        var root = kind switch
        {
            ObjectKind.ActiveSession => paths.Sessions,
            ObjectKind.ArchivedSession => paths.ArchivedSessions,
            _ => throw new ArgumentException("Only active and archived sessions can be written as JSONL history.", parameterName)
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
