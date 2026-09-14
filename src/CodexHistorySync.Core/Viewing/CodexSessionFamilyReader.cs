using System.Collections.Concurrent;
using System.Text.Json;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Management;

namespace CodexHistorySync.Core.Viewing;

/// <summary>Discovers subagents from explicit native metadata, without changing the sync/catalog filters.</summary>
public sealed class CodexSessionFamilyReader(CodexPaths? paths) : ISessionFamilyReader
{
    private readonly SystemSessionCatalogIo io = new();
    private readonly ConcurrentDictionary<string, CachedMetadata> cache = new(StringComparer.Ordinal);

    public async Task<SessionThread> ReadAsync(ManagedSession parent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parent);
        cancellationToken.ThrowIfCancellationRequested();
        if (parent.Agent != ManagedAgent.Codex || paths is null) return new SessionThread(parent, []);

        var rows = new ConcurrentBag<ChildMetadata>();
        var files = new[] { paths.Sessions, paths.ArchivedSessions }
            .SelectMany(root => io.EnumerateFiles(root, "*.jsonl").Select(path => (Path: path, Root: root))).ToArray();
        await Parallel.ForEachAsync(files, new ParallelOptions
        {
            CancellationToken = cancellationToken, MaxDegreeOfParallelism = 8
        }, async (candidate, token) =>
        {
            if (!ManagedSessionPathPolicy.TryResolveConcreteTarget(candidate.Path, candidate.Root, false, out var resolved)) return;
            try
            {
                var info = new FileInfo(resolved);
                var stamp = (info.Length, info.LastWriteTimeUtc);
                if (cache.TryGetValue(resolved, out var cached) && cached.Stamp == stamp)
                {
                    if (cached.Metadata is not null) rows.Add(cached.Metadata);
                    return;
                }
                var prefix = await io.ReadPrefixAsync(resolved, 256 * 1024, token).ConfigureAwait(false);
                ChildMetadata? metadata = null;
                var lines = prefix.Text.Split('\n');
                for (var index = 0; index < Math.Min(64, lines.Length); index++)
                {
                    if (index == lines.Length - 1 && !prefix.IsComplete) break;
                    if (string.IsNullOrWhiteSpace(lines[index])) continue;
                    using var document = JsonDocument.Parse(lines[index]);
                    var root = document.RootElement;
                    if (SessionTraceReader.Text(root, "type") != "session_meta" ||
                        !root.TryGetProperty("payload", out var payload)) continue;
                    metadata = ReadMetadata(payload, resolved, info.LastWriteTimeUtc);
                    break;
                }
                cache[resolved] = new CachedMetadata(stamp, metadata);
                if (metadata is not null) rows.Add(metadata);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or System.Text.DecoderFallbackException)
            {
                // Inaccessible or incomplete metadata cannot establish a parent-child relationship.
            }
        }).ConfigureAwait(false);

        var children = rows.GroupBy(row => row.Session.SessionId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1).Select(group => group.Single())
            .Where(row => row.ParentId is not null)
            .GroupBy(row => row.ParentId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(row => row.Session.LastModifiedAt)
                .ThenBy(row => row.Session.SessionId, StringComparer.Ordinal).ToArray(), StringComparer.OrdinalIgnoreCase);
        return Build(parent, children, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private static ChildMetadata? ReadMetadata(JsonElement payload, string path, DateTimeOffset modified)
    {
        if (payload.ValueKind != JsonValueKind.Object) return null;
        var subagent = default(JsonElement);
        if (payload.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object)
            source.TryGetProperty("subagent", out subagent);
        var marked = string.Equals(SessionTraceReader.Text(payload, "thread_source"), "subagent", StringComparison.OrdinalIgnoreCase)
            || subagent.ValueKind == JsonValueKind.Object;
        var spawn = default(JsonElement);
        if (subagent.ValueKind == JsonValueKind.Object) subagent.TryGetProperty("thread_spawn", out spawn);
        var id = SessionTraceReader.Text(payload, "id");
        if (!SafeId(id)) return null;
        var directParent = SessionTraceReader.Text(payload, "parent_thread_id");
        var spawnedParent = SessionTraceReader.Text(spawn, "parent_thread_id");
        var parent = directParent ?? spawnedParent;
        if (!marked || !SafeId(parent) || string.Equals(id, parent, StringComparison.OrdinalIgnoreCase) ||
            directParent is not null && spawnedParent is not null && !string.Equals(directParent, spawnedParent, StringComparison.OrdinalIgnoreCase))
            parent = null;
        var nickname = SessionTraceReader.Text(payload, "agent_nickname") ?? SessionTraceReader.Text(spawn, "agent_nickname");
        var role = SessionTraceReader.Text(payload, "agent_role") ?? SessionTraceReader.Text(spawn, "agent_role");
        var title = SessionTraceReader.Text(payload, "title") ?? SessionTraceReader.Text(payload, "thread_name") ?? nickname ?? id!;
        if (!string.IsNullOrWhiteSpace(role)) title += " · " + role;
        return new ChildMetadata(parent, new ManagedSession(ManagedAgent.Codex, id!, path, title, modified, false, true));
    }

    private static SessionThread Build(ManagedSession session, IReadOnlyDictionary<string, ChildMetadata[]> children, HashSet<string> ancestors)
    {
        if (ancestors.Count >= 64 || !ancestors.Add(session.SessionId)) return new SessionThread(session, []);
        var result = children.TryGetValue(session.SessionId, out var related)
            ? related.Where(child => !ancestors.Contains(child.Session.SessionId))
                .Select(child => Build(child.Session, children, ancestors)).ToArray()
            : [];
        ancestors.Remove(session.SessionId);
        return new SessionThread(session, result);
    }

    private static bool SafeId(string? id) => !string.IsNullOrEmpty(id) && id.Length <= 200 &&
        char.IsAsciiLetterOrDigit(id[0]) && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private sealed record ChildMetadata(string? ParentId, ManagedSession Session);
    private sealed record CachedMetadata((long Length, DateTime Modified) Stamp, ChildMetadata? Metadata);
}
