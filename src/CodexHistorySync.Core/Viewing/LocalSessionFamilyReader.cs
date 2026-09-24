using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Management;
using CodexHistorySync.Core.Muse;

namespace CodexHistorySync.Core.Viewing;

/// <summary>Loads children only for the conversation explicitly opened by the user.</summary>
public sealed class LocalSessionFamilyReader(CodexPaths? codexPaths, MusePaths? musePaths) : ISessionFamilyReader
{
    private readonly CodexSessionFamilyReader codex = new(codexPaths);
    private readonly MuseSessionCatalogSource? muse = musePaths is null ? null : new(musePaths, new SystemSessionCatalogIo());

    public async Task<SessionThread> ReadAsync(ManagedSession parent, CancellationToken cancellationToken)
    {
        if (parent.Agent != ManagedAgent.Muse) return await codex.ReadAsync(parent, cancellationToken).ConfigureAwait(false);
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
