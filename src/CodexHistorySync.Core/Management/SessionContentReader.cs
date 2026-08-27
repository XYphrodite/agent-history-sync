using CodexHistorySync.Core.Conversion;

namespace CodexHistorySync.Core.Management;

public interface ISessionContentReader
{
    Task<PortableConversation> ReadAsync(ManagedSession session, CancellationToken cancellationToken);
}

/// <summary>
/// Reads a catalog row's conversation through its own agent's reader, so the viewer has one
/// call for three native formats. Read-only: nothing here touches an agent home.
/// </summary>
public sealed class SessionContentReader : ISessionContentReader
{
    private readonly IConversationReader codexReader;
    private readonly IConversationReader grokReader;
    private readonly IConversationReader claudeReader;

    public SessionContentReader()
        : this(new CodexConversationReader(), new GrokConversationReader(), new ClaudeConversationReader())
    {
    }

    internal SessionContentReader(
        IConversationReader codexReader,
        IConversationReader grokReader,
        IConversationReader claudeReader)
    {
        this.codexReader = codexReader ?? throw new ArgumentNullException(nameof(codexReader));
        this.grokReader = grokReader ?? throw new ArgumentNullException(nameof(grokReader));
        this.claudeReader = claudeReader ?? throw new ArgumentNullException(nameof(claudeReader));
    }

    public Task<PortableConversation> ReadAsync(ManagedSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();
        // The catalog already decided this row cannot be parsed; failing here keeps the reason
        // identical to the one the copy path reports instead of surfacing a format error.
        if (!session.CanRead) throw new InvalidDataException("The session is not readable.");
        if (string.IsNullOrWhiteSpace(session.NativePath))
            throw new InvalidDataException("The selected session identity is invalid.");

        return ReaderFor(session.Agent).ReadAsync(session.NativePath, cancellationToken);
    }

    private IConversationReader ReaderFor(ManagedAgent agent) => agent switch
    {
        ManagedAgent.Codex => codexReader,
        ManagedAgent.Grok => grokReader,
        ManagedAgent.Claude => claudeReader,
        _ => throw new InvalidDataException("The selected agent is invalid.")
    };
}
