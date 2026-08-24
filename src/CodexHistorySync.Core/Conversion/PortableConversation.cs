namespace CodexHistorySync.Core.Conversion;

public enum ConversationAgent { Codex, Grok, Claude }

public enum ConversationRole { User, Assistant }

public sealed record PortableTurn(ConversationRole Role, string Text);

public sealed record PortableConversation(
    ConversationAgent SourceAgent,
    string SourceSessionId,
    string Title,
    string? WorkingDirectory,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastModifiedAt,
    IReadOnlyList<PortableTurn> Turns);

public interface IConversationReader
{
    Task<PortableConversation> ReadAsync(string nativePath, CancellationToken cancellationToken);
}
