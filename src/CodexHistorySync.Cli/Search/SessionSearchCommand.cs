using CodexHistorySync.Core.Management;
using CodexHistorySync.Core.Search;

namespace CodexHistorySync.Cli.Search;

public interface ISessionSearchCommand
{
    Task<int> SearchAsync(string query, CancellationToken cancellationToken);
}

/// <summary>
/// Local-only corpus search. It never constructs Git, GitHub, or the sync engine.
/// </summary>
public sealed class SessionSearchCommand(
    ILocalSessionCatalog catalog,
    ISessionSearchIndex index,
    ISessionContentReader reader,
    ICliConsole console) : ISessionSearchCommand
{
    private readonly ILocalSessionCatalog catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly ISessionSearchIndex index = index ?? throw new ArgumentNullException(nameof(index));
    private readonly ISessionContentReader reader = reader ?? throw new ArgumentNullException(nameof(reader));
    private readonly ICliConsole console = console ?? throw new ArgumentNullException(nameof(console));

    public async Task<int> SearchAsync(string query, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var snapshot = await catalog.ScanAsync(cancellationToken).ConfigureAwait(false);
        await index.EnsureCurrentAsync(snapshot, reader, cancellationToken).ConfigureAwait(false);
        var hits = await index.SearchAsync(query, SessionSearchIndex.DefaultSearchLimit, cancellationToken)
            .ConfigureAwait(false);
        if (hits.Count == 0) return 1;

        foreach (var hit in hits)
        {
            console.WriteLine(
                $"{AgentToken(hit.Agent)} {SafeToken(hit.SessionId)} {SafeText(hit.Title)}");
            if (!string.IsNullOrWhiteSpace(hit.Snippet))
                console.WriteLine("  " + SafeText(hit.Snippet));
        }

        return 0;
    }

    private static string AgentToken(ManagedAgent agent) => agent switch
    {
        ManagedAgent.Codex => "codex",
        ManagedAgent.Grok => "grok",
        ManagedAgent.Claude => "claude",
        ManagedAgent.Continue => "continue",
        _ => "unknown"
    };

    private static string SafeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unknown";
        return new string(value.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':' ? character : '_').ToArray());
    }

    private static string SafeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unknown";
        var trimmed = value.Length <= 200 ? value : value[..200];
        return new string(trimmed.Select(character => char.IsControl(character) ? '_' : character).ToArray());
    }
}
