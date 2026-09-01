using CodexHistorySync.Core.Annotations;

namespace CodexHistorySync.Core.Management;

/// <summary>
/// Puts this machine's own titles and descriptions onto a scanned snapshot. It decorates the
/// catalog instead of reaching into the four sources, so their priority chains keep deciding what
/// an agent calls a session and this layer only fills the silence: a title the agent never gave.
/// </summary>
public sealed class AnnotatedSessionCatalog(
    ILocalSessionCatalog catalog,
    ISessionAnnotationStore annotations) : ILocalSessionCatalog
{
    private readonly ILocalSessionCatalog _catalog =
        catalog ?? throw new ArgumentNullException(nameof(catalog));

    private readonly ISessionAnnotationStore _annotations =
        annotations ?? throw new ArgumentNullException(nameof(annotations));

    public async Task<SessionCatalogSnapshot> ScanAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _catalog.ScanAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyDictionary<SessionAnnotationKey, SessionAnnotation> stored;
        try
        {
            stored = await _annotations.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException
                                              or UnauthorizedAccessException)
        {
            // A damaged sidecar costs the user their titles, never their session list.
            return snapshot;
        }

        if (stored.Count == 0) return snapshot;

        return new SessionCatalogSnapshot(
            Overlay(snapshot.Codex, stored),
            Overlay(snapshot.Grok, stored),
            Overlay(snapshot.Claude, stored),
            Overlay(snapshot.Continue, stored))
        {
            ConfiguredAgents = snapshot.ConfiguredAgents
        };
    }

    private static IReadOnlyList<ManagedSession> Overlay(
        IReadOnlyList<ManagedSession> sessions,
        IReadOnlyDictionary<SessionAnnotationKey, SessionAnnotation> annotations)
    {
        if (sessions.Count == 0) return sessions;

        var overlaid = new ManagedSession[sessions.Count];
        for (var index = 0; index < sessions.Count; index++)
        {
            var session = sessions[index];
            overlaid[index] =
                annotations.TryGetValue(new SessionAnnotationKey(session.Agent, session.SessionId), out var annotation)
                    ? session with
                    {
                        // The agent's own name always wins; the description rides along either way.
                        Title = session.TitleSource == ManagedTitleSource.Official
                            ? session.Title
                            : annotation.Title,
                        Annotation = annotation
                    }
                    : session;
        }

        return overlaid;
    }
}
