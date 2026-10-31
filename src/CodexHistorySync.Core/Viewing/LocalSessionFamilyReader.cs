using CodexHistorySync.Core.Antigravity;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Management;
using CodexHistorySync.Core.Muse;

namespace CodexHistorySync.Core.Viewing;

/// <summary>Loads children only for the conversation explicitly opened by the user.</summary>
public sealed class LocalSessionFamilyReader(CodexPaths? codexPaths, MusePaths? musePaths, AntigravityPaths? antigravityPaths = null) : ISessionFamilyReader
{
    private readonly CodexSessionFamilyReader codex = new(codexPaths);
    private readonly MuseSessionCatalogSource? muse = musePaths is null ? null : new(musePaths, new SystemSessionCatalogIo());

    public async Task<SessionThread> ReadAsync(ManagedSession parent, CancellationToken cancellationToken)
    {
        if (parent.Agent == ManagedAgent.Antigravity)
        {
            var paths = antigravityPaths;
            if (paths is null)
            {
                if (AntigravityPaths.TryParseAnchor(parent.NativePath, out var home, out _))
                {
                    paths = new AntigravityPaths(home);
                }
                else if (parent.NativePath.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
                {
                    var parentDir = Path.GetDirectoryName(parent.NativePath);
                    if (parentDir is not null && string.Equals(Path.GetFileName(parentDir), "conversations", StringComparison.OrdinalIgnoreCase))
                    {
                        var root = Path.GetDirectoryName(parentDir);
                        if (root is not null) paths = new AntigravityPaths(root);
                    }
                }
            }

            if (paths is null || !File.Exists(paths.ConversationSummariesDbPath))
                return new SessionThread(parent, []);

            IReadOnlyList<AntigravitySummaryRow> summaries;
            try
            {
                summaries = AntigravityDatabase.ReadSummaries(paths.ConversationSummariesDbPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
            {
                return new SessionThread(parent, []);
            }

            var childrenByParent = summaries
                .Where(row => !string.IsNullOrEmpty(row.ParentConversationId))
                .GroupBy(row => row.ParentConversationId!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { parent.SessionId };

            SessionThread BuildThread(ManagedSession current, int depth)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (depth >= 32) return new SessionThread(current, []);

                if (!childrenByParent.TryGetValue(current.SessionId, out var childRows))
                    return new SessionThread(current, []);

                var children = new List<SessionThread>();
                foreach (var childRow in childRows.OrderByDescending(row => row.UpdatedAt > DateTimeOffset.MinValue ? row.UpdatedAt : row.CreatedAt))
                {
                    if (!visited.Add(childRow.ConversationId)) continue;

                    var canRead = File.Exists(paths.ConversationDatabasePath(childRow.ConversationId));
                    var timestamp = childRow.UpdatedAt > DateTimeOffset.MinValue ? childRow.UpdatedAt : childRow.CreatedAt;
                    var titleSource = string.IsNullOrWhiteSpace(childRow.Title) ? ManagedTitleSource.SessionId : ManagedTitleSource.Official;
                    var childSession = new ManagedSession(
                        ManagedAgent.Antigravity,
                        childRow.ConversationId,
                        paths.AnchorPath(childRow.ConversationId),
                        string.IsNullOrWhiteSpace(childRow.Title) ? childRow.ConversationId : childRow.Title,
                        timestamp,
                        false,
                        canRead,
                        titleSource);

                    children.Add(BuildThread(childSession, depth + 1));
                }

                return new SessionThread(current, children);
            }

            return BuildThread(parent, 0);
        }

        if (parent.Agent != ManagedAgent.Muse) return await codex.ReadAsync(parent, cancellationToken).ConfigureAwait(false);
        if (parent.DiskSession is { } archive) return await archive.Disk.ReadFamilyAsync(parent, cancellationToken).ConfigureAwait(false);
        if (musePaths is null || muse is null ||
            !ManagedSessionPathPolicy.TryResolveConcreteTarget(parent.NativePath, musePaths.Sessions, true, out var resolved))
            return new SessionThread(parent, []);
        using var limiter = new SessionCatalogReadLimiter(8);
        return await ReadChildrenAsync(parent with { NativePath = resolved }, 0).ConfigureAwait(false);

        async Task<SessionThread> ReadChildrenAsync(ManagedSession session, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (depth >= 32) return new SessionThread(session, []);
            var directories = MuseSessionDiscovery.ChildDirectories(Path.Combine(session.NativePath, "subagent"))
                .Where(path => Guid.TryParse(Path.GetFileName(path), out _)).ToArray();
            var candidates = await muse.ReadDirectoriesAsync(directories, musePaths.Sessions,
                new Dictionary<string, MuseIndexedTitle>(), limiter, cancellationToken).ConfigureAwait(false);
            var children = new List<SessionThread>();
            foreach (var child in candidates.OrderByDescending(row => row.LastModifiedAt))
                children.Add(await ReadChildrenAsync(new ManagedSession(ManagedAgent.Muse, child.SessionId,
                    child.NativePath, child.Title, child.LastModifiedAt, session.IsActive, child.CanRead, child.TitleSource), depth + 1).ConfigureAwait(false));
            return new SessionThread(session, children);
        }
    }
}
