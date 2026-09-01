using CodexHistorySync.Core.Management;

namespace CodexHistorySync.Core.Annotations;

/// <summary>Who wrote the annotation, which decides whether it may be replaced without asking.</summary>
public enum SessionAnnotationSource
{
    /// <summary>A suggester produced it; regenerating it costs the user nothing.</summary>
    Generated,

    /// <summary>A person typed it; it is overwritten only after an explicit confirmation.</summary>
    Edited
}

/// <summary>
/// A session is addressed by its agent as well as its id: two agents may hold the same id, and
/// <see cref="ManagedSession.SessionId"/> alone is not unique across the four homes.
/// </summary>
public readonly record struct SessionAnnotationKey(ManagedAgent Agent, string SessionId);

/// <summary>
/// A title and a description of this machine's own, kept outside every agent home.
/// <paramref name="DigestHash"/> is the hash of the conversation the text was made from, so a
/// session that has grown since can be shown as stale instead of quietly misdescribed.
/// </summary>
public sealed record SessionAnnotation(
    string Title,
    string? Description,
    SessionAnnotationSource Source,
    string DigestHash,
    string? Model,
    DateTimeOffset UpdatedAt)
{
    /// <summary>The bound the session catalog already renders titles at.</summary>
    public const int MaximumTitleLength = 80;

    /// <summary>Two wrapped rows in the content pane header, and nothing longer.</summary>
    public const int MaximumDescriptionLength = 300;
}
