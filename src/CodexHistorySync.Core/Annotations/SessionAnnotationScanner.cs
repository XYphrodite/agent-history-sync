using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Model;

namespace CodexHistorySync.Core.Annotations;

/// <summary>
/// Turns the annotation directory into publishable objects, one per annotated session.
///
/// Like every other scanner here it would rather report uncertainty than a deletion: an
/// annotation it could not read is not an annotation that is gone, and publishing the difference
/// as a tombstone would erase a title on every other machine.
/// </summary>
public sealed class SessionAnnotationScanner
{
    public Task<SessionScanResult> ScanDetailedAsync(string annotationsDirectory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(annotationsDirectory);

        var objects = new List<LocalObject>();
        var uncertain = new HashSet<ObjectKind>();
        var duplicates = new HashSet<LogicalObjectId>();
        var seen = new HashSet<LogicalObjectId>();

        if (!Directory.Exists(annotationsDirectory))
        {
            // Nothing stored yet is a confirmed absence, not an unreadable one.
            return Task.FromResult(new SessionScanResult(objects, uncertain, duplicates));
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(annotationsDirectory, "*.json");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            uncertain.Add(ObjectKind.SessionAnnotations);
            return Task.FromResult(new SessionScanResult(objects, uncertain, duplicates));
        }

        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReadStable(path, uncertain) is not { } observed) continue;

            if (!seen.Add(observed.Id)) duplicates.Add(observed.Id);
            objects.Add(observed);
        }

        return Task.FromResult(new SessionScanResult(objects, uncertain, duplicates));
    }

    private static LocalObject? ReadStable(string path, HashSet<ObjectKind> uncertain)
    {
        try
        {
            var before = new FileInfo(path);
            if (!before.Exists) return null;
            var (length, written) = (before.Length, before.LastWriteTimeUtc);

            var bytes = File.ReadAllBytes(path);

            var after = new FileInfo(path);
            if (!after.Exists || after.Length != length || after.LastWriteTimeUtc != written)
            {
                // Being written right now. Say so rather than publish half of it.
                uncertain.Add(ObjectKind.SessionAnnotations);
                return null;
            }

            if (!SessionAnnotationPackage.TryReadPackage(bytes, out var key, out _)) return null;

            // The name is the address: a file that disagrees with its own name does not say which
            // session it belongs to, and is left alone rather than published under a guess.
            if (!string.Equals(SessionAnnotationStore.FileName(key), Path.GetFileName(path),
                    StringComparison.OrdinalIgnoreCase))
                return null;

            return new LocalObject(
                new LogicalObjectId(SessionAnnotationPackage.ToLogicalId(key)),
                ObjectKind.SessionAnnotations,
                Path.GetFullPath(path),
                SessionAnnotationPackage.HashPackage(bytes),
                bytes.LongLength,
                new DateTimeOffset(written, TimeSpan.Zero));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                             or ArgumentException or NotSupportedException)
        {
            uncertain.Add(ObjectKind.SessionAnnotations);
            return null;
        }
    }
}
