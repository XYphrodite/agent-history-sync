using System.Text;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Model;

namespace CodexHistorySync.Core.Claude;

/// <summary>
/// Turns ~/.claude/projects/&lt;segment&gt;/memory/*.md into publishable objects, one file each.
///
/// Like every other scanner here it would rather report uncertainty than a deletion: a file it
/// could not read is not a memory that is gone, and publishing the difference as a tombstone
/// would erase that record on every other machine.
/// </summary>
public sealed class ClaudeMemoryScanner
{
    public Task<SessionScanResult> ScanDetailedAsync(ClaudePaths paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var objects = new List<LocalObject>();
        var objectsById = new Dictionary<LogicalObjectId, LocalObject>();
        var uncertain = new HashSet<ObjectKind>();
        var duplicates = new HashSet<LogicalObjectId>();

        if (!Directory.Exists(paths.Projects))
        {
            uncertain.Add(ObjectKind.ClaudeMemory);
            return Task.FromResult(new SessionScanResult(objects, uncertain, duplicates));
        }

        string[] projects;
        try
        {
            projects = Directory.GetDirectories(paths.Projects, "*",
                new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            uncertain.Add(ObjectKind.ClaudeMemory);
            return Task.FromResult(new SessionScanResult(objects, uncertain, duplicates));
        }

        var complete = true;
        foreach (var projectDirectory in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CodexPaths.IsPathWithin(projectDirectory, paths.Projects)) continue;
            var memoryDirectory = Path.Combine(projectDirectory, "memory");
            if (!Directory.Exists(memoryDirectory)) continue;

            string[] files;
            try
            {
                files = Directory.GetFiles(memoryDirectory, "*.md",
                    new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                complete = false;
                continue;
            }

            foreach (var path in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!CodexPaths.IsPathWithin(path, memoryDirectory)) continue;
                if (!StringComparer.OrdinalIgnoreCase.Equals(
                        Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(path) ?? string.Empty),
                        Path.TrimEndingDirectorySeparator(memoryDirectory)))
                    continue;

                var item = ReadStable(path, ref complete);
                if (item is null) continue;
                if (objectsById.TryAdd(item.Id, item)) objects.Add(item);
                else
                {
                    duplicates.Add(item.Id);
                    uncertain.Add(ObjectKind.ClaudeMemory);
                    if (objectsById.Remove(item.Id, out var prior)) objects.Remove(prior);
                }
            }
        }

        if (!complete) uncertain.Add(ObjectKind.ClaudeMemory);
        return Task.FromResult(new SessionScanResult(objects, uncertain, duplicates));
    }

    private static LocalObject? ReadStable(string path, ref bool complete)
    {
        try
        {
            var before = new FileInfo(path);
            if (!before.Exists) return null;
            var (length, written) = (before.Length, before.LastWriteTimeUtc);

            var body = File.ReadAllText(path, new UTF8Encoding(false, true));

            var after = new FileInfo(path);
            after.Refresh();
            if (!after.Exists || after.Length != length || after.LastWriteTimeUtc != written)
            {
                complete = false;
                return null;
            }

            var (project, name) = ClaudeMemoryPackage.ReadLocation(path);
            var package = ClaudeMemoryPackage.Build(project, name, body);
            return new LocalObject(
                new LogicalObjectId(ClaudeMemoryPackage.ToLogicalId(project, name)),
                ObjectKind.ClaudeMemory,
                Path.GetFullPath(path),
                ClaudeMemoryPackage.HashPackage(package),
                package.LongLength,
                new DateTimeOffset(written, TimeSpan.Zero));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
                                              or DecoderFallbackException or ArgumentException or NotSupportedException)
        {
            complete = false;
            return null;
        }
    }
}
