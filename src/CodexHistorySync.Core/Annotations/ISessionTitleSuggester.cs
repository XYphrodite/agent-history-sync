namespace CodexHistorySync.Core.Annotations;

/// <summary>What a suggester came back with, before anyone decides to keep it.</summary>
public sealed record SessionAnnotationDraft(string Title, string Description, string Model);

/// <summary>
/// Where titling may send a session and what it may ask for. <see cref="Endpoint"/> has no default
/// on purpose: with nothing configured the feature stays inert and no session text leaves the
/// machine.
/// </summary>
public sealed record SessionTitleOptions(string? Endpoint, string Model = "qwen3:8b", string Language = "auto")
{
    /// <summary>Measured faster and more specific than gpt-oss:20b on real sessions.</summary>
    public const string DefaultModel = "qwen3:8b";

    /// <summary>A host that is not listening must be given up on, not waited for.</summary>
    public static TimeSpan ConnectTimeout => TimeSpan.FromSeconds(2);

    /// <summary>A small GPU takes tens of seconds on a long session; beyond two minutes it is stuck.</summary>
    public static TimeSpan RequestTimeout => TimeSpan.FromSeconds(120);
}

public interface ISessionTitleSuggester
{
    /// <summary>False when no endpoint is configured, which makes every suggestion a no-op.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Returns a draft, or null for every failure there is: not configured, nothing to send, the
    /// endpoint down, the answer unusable, or the caller no longer waiting.
    /// </summary>
    Task<SessionAnnotationDraft?> SuggestAsync(SessionDigestResult digest, CancellationToken cancellationToken);

    /// <summary>
    /// Why the last suggestion came back empty, for a diagnostic command to print. The screen
    /// deliberately does not show it: a failure there costs a keystroke, not an investigation.
    /// </summary>
    string? LastFailure { get; }
}
