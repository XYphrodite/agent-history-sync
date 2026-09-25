using CodexHistorySync.Core.Conversion;
using CodexHistorySync.Core.Management;
using CodexHistorySync.Core.Viewing;

namespace CodexHistorySync.Core.Muse;

internal sealed record MuseDiskSession(MuseWslDisk Disk, MuseDiskEntry Entry, string[] Ids);

internal sealed class MuseWslDisk(string distribution, string image, string? home, IMuseArchive archive, Func<bool> isStopped)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private IReadOnlyList<SessionCatalogCandidate> sessions = [];
    private (long Size, DateTime Modified)? stamp;

    public async Task<IReadOnlyList<SessionCatalogCandidate>> ScanAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var before = CheckDisk();
            if (stamp == before) return sessions.Where(row => row.DiskSession!.Ids.Length == 1).ToArray();
            var entries = MuseDiskEntry.ParseListing(await archive.ListAsync(ct).ConfigureAwait(false));
            var rows = new List<SessionCatalogCandidate>();
            var titles = new Dictionary<string, IReadOnlyDictionary<string, MuseIndexedTitle>>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                if (!MuseDiskEntry.TrySession(entry.Path, home, out var museHome, out var ids)) continue;
                if (!titles.TryGetValue(museHome, out var index))
                {
                    index = await ReadIndexAsync(entries, museHome, ct).ConfigureAwait(false);
                    titles.Add(museHome, index);
                }
                var id = ids[^1];
                var title = index.GetValueOrDefault(id);
                var normalized = string.IsNullOrWhiteSpace(title?.Title) ? null
                    : string.Join(' ', title.Title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                if (normalized?.Length > 80) normalized = normalized[..80];
                var native = @"\\wsl$\" + distribution + "\\" + entry.Path[..^"/session.jsonl".Length].Replace('/', '\\');
                rows.Add(new SessionCatalogCandidate(id, native, normalized ?? id, entry.Modified, entry.Size > 0,
                    normalized is null ? ManagedTitleSource.SessionId : title!.Official ? ManagedTitleSource.Official : ManagedTitleSource.Fallback)
                { DiskSession = new MuseDiskSession(this, entry, ids) });
            }
            if (CheckDisk() != before) throw new IOException("The WSL disk changed. Refresh the session list.");
            var duplicates = rows.GroupBy(row => row.SessionId, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            sessions = rows.Select(row => duplicates.Contains(row.SessionId) ? row with { CanRead = false } : row).ToArray();
            stamp = before;
            return sessions.Where(row => row.DiskSession!.Ids.Length == 1).ToArray();
        }
        finally { gate.Release(); }
    }

    private async Task<IReadOnlyDictionary<string, MuseIndexedTitle>> ReadIndexAsync(IReadOnlyList<MuseDiskEntry> entries, string museHome, CancellationToken ct)
    {
        var database = entries.FirstOrDefault(entry => entry.Path == museHome + "/session-index.db");
        if (database is null || database.Size > 64 * 1024 * 1024) return new Dictionary<string, MuseIndexedTitle>();
        var temporary = Directory.CreateTempSubdirectory("agent-sync-muse-index-");
        try
        {
            await ExtractAsync(database, Path.Combine(temporary.FullName, "session-index.db"), ct).ConfigureAwait(false);
            var wal = entries.FirstOrDefault(entry => entry.Path == museHome + "/session-index.db-wal");
            if (wal is not null)
            {
                if (wal.Size > 64 * 1024 * 1024) return new Dictionary<string, MuseIndexedTitle>();
                await ExtractAsync(wal, Path.Combine(temporary.FullName, "session-index.db-wal"), ct).ConfigureAwait(false);
            }
            return MuseSessionIndex.Read(temporary.FullName, ct);
        }
        catch (IOException) { return new Dictionary<string, MuseIndexedTitle>(); }
        finally { temporary.Delete(recursive: true); }
    }

    public async Task<PortableConversation> ReadAsync(ManagedSession session, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var before = CheckDisk();
            if (stamp != before) throw new IOException("The WSL disk changed. Refresh the session list.");
            var source = session.DiskSession ?? throw new InvalidDataException("Missing WSL session location.");
            if (!ReferenceEquals(source.Disk, this) || !sessions.Any(row => row.CanRead && row.DiskSession == source))
                throw new InvalidDataException("The selected WSL session is not in the catalog.");
            var temporary = Directory.CreateTempSubdirectory("agent-sync-muse-session-");
            try
            {
                var directory = Path.Combine(temporary.FullName, session.SessionId);
                Directory.CreateDirectory(directory);
                await ExtractAsync(source.Entry, Path.Combine(directory, MusePaths.SessionFileName), ct).ConfigureAwait(false);
                var conversation = await new MuseConversationReader().ReadAsync(directory, ct).ConfigureAwait(false);
                if (CheckDisk() != before) throw new IOException("The WSL disk changed while reading. Refresh the session list.");
                return conversation with { Title = session.Title, CreatedAt = source.Entry.Modified, LastModifiedAt = source.Entry.Modified };
            }
            finally { temporary.Delete(recursive: true); }
        }
        finally { gate.Release(); }
    }

    public async Task<SessionThread> ReadFamilyAsync(ManagedSession parent, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (stamp != CheckDisk()) throw new IOException("The WSL disk changed. Refresh the session list.");
            return Build(parent);
        }
        finally { gate.Release(); }

        SessionThread Build(ManagedSession node)
        {
            ct.ThrowIfCancellationRequested();
            var prefix = node.DiskSession!.Entry.Path[..^"session.jsonl".Length] + "subagent/";
            var depth = node.DiskSession.Ids.Length + 1;
            var children = sessions.Where(row => row.DiskSession!.Ids.Length == depth &&
                row.DiskSession.Entry.Path.StartsWith(prefix, StringComparison.Ordinal))
                .OrderByDescending(row => row.LastModifiedAt)
                .Select(row => Build(new ManagedSession(ManagedAgent.Muse, row.SessionId, row.NativePath,
                    row.Title, row.LastModifiedAt, false, row.CanRead, row.TitleSource) { DiskSession = row.DiskSession })).ToArray();
            return new SessionThread(node, children);
        }
    }

    private async Task ExtractAsync(MuseDiskEntry entry, string destination, CancellationToken ct)
    {
        await using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await archive.ExtractAsync(entry.Path, file, entry.Size, ct).ConfigureAwait(false);
        if (file.Length != entry.Size) throw new IOException("7-Zip returned an incomplete WSL disk entry.");
    }

    private (long Size, DateTime Modified) CheckDisk()
    {
        if (!isStopped()) throw new IOException("WSL is running or its state is unknown. Reopen the app to read the live history.");
        var info = new FileInfo(image);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("The WSL disk is unavailable.");
        return (info.Length, info.LastWriteTimeUtc);
    }
}
