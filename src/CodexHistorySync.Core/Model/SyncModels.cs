namespace CodexHistorySync.Core.Model;

public enum ObjectKind
{
    ActiveSession,
    ArchivedSession,
    Attachment,
    Tombstone,
    RepositoryIndex,
    /// <summary>Grok CLI session package (chat_history + summary under ~/.grok/sessions).</summary>
    GrokSession
}

public readonly record struct LogicalObjectId(string Value);

public readonly record struct ContentHash(string Hex);

public sealed record LocalObject(
    LogicalObjectId Id,
    ObjectKind Kind,
    string SourcePath,
    ContentHash Hash,
    long Length,
    DateTimeOffset LastWriteTimeUtc);
