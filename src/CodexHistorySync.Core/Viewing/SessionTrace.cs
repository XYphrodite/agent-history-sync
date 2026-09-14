using CodexHistorySync.Core.Management;

namespace CodexHistorySync.Core.Viewing;

/// <summary>The visible kinds of transcript records; reasoning and system instructions are not exposed.</summary>
public enum TraceEntryKind { User, Assistant, ToolCall, ToolResult, Notification }

/// <summary>A visible record in native order. Tool calls and results retain their correlation ID.</summary>
public sealed record TraceEntry(
    int Index, TraceEntryKind Kind, string Label, string Text,
    DateTimeOffset? Timestamp = null, string? CallId = null)
{
    public bool IsTool => Kind is TraceEntryKind.ToolCall or TraceEntryKind.ToolResult;
}

/// <summary>A read-only view of one conversation and any recoverable read warnings.</summary>
public sealed record SessionTrace(ManagedSession Session, IReadOnlyList<TraceEntry> Entries, IReadOnlyList<string> Warnings);

/// <summary>A parent conversation and its recursively nested child conversations.</summary>
public sealed record SessionThread(ManagedSession Session, IReadOnlyList<SessionThread> Children)
{
    public IEnumerable<SessionThread> DescendantsAndSelf()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var node in child.DescendantsAndSelf()) yield return node;
    }
}

/// <summary>Reads user-visible messages and tool records from local history.</summary>
public interface ISessionTraceReader
{
    Task<SessionTrace> ReadAsync(ManagedSession session, CancellationToken cancellationToken);
}

/// <summary>Finds only the explicitly related local children of a selected Codex chat.</summary>
public interface ISessionFamilyReader
{
    Task<SessionThread> ReadAsync(ManagedSession parent, CancellationToken cancellationToken);
}

/// <summary>One literal text match, retaining its conversation and entry for navigation.</summary>
public sealed record TraceSearchMatch(ManagedSession Session, int EntryIndex, int Offset, string Preview);

/// <summary>Literal, culture-independent search across messages and tool input/output.</summary>
public static class SessionTraceSearch
{
    public const int MaximumMatches = 10000;
    public static IReadOnlyList<TraceSearchMatch> Find(
        IEnumerable<SessionTrace> traces, string? query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var results = new List<TraceSearchMatch>();
        foreach (var trace in traces)
        foreach (var entry in trace.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var offset = 0; offset <= entry.Text.Length - query.Length;)
            {
                var found = entry.Text.IndexOf(query, offset, StringComparison.OrdinalIgnoreCase);
                if (found < 0) break;
                var start = Math.Max(0, found - 40);
                var preview = entry.Text.Substring(start, Math.Min(160, entry.Text.Length - start))
                    .Replace('\r', ' ').Replace('\n', ' ');
                results.Add(new TraceSearchMatch(trace.Session, entry.Index, found, preview));
                if (results.Count >= MaximumMatches) return results;
                offset = found + query.Length;
            }
        }
        return results;
    }
}
