namespace CodexHistorySync.Core.Model;

public enum ObjectKind
{
    ActiveSession,
    ArchivedSession,
    Attachment,
    Tombstone,
    RepositoryIndex,
    /// <summary>Grok CLI session package (chat_history + summary under ~/.grok/sessions).</summary>
    GrokSession,
    /// <summary>
    /// Claude Code session package (one transcript under ~/.claude/projects only).
    /// Appended last on purpose: the value is persisted as an integer in the encrypted index and
    /// an undefined value fails the whole index, so existing members must keep their numbers and
    /// every machine must be upgraded before the first push carrying this kind (design D4).
    /// </summary>
    ClaudeSession
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
