namespace CodexHistorySync.Core.Model;

public enum ObjectKind
{
    ActiveSession,
    ArchivedSession,
    Attachment,
    Tombstone
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
