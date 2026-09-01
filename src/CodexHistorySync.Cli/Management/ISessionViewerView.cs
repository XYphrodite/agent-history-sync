using CodexHistorySync.Core.Annotations;
using CodexHistorySync.Core.Management;

namespace CodexHistorySync.Cli.Management;

/// <summary>A title and a description as they were typed, before anything validates them.</summary>
public sealed record SessionAnnotationEdit(string Title, string? Description);

public interface ISessionViewerView
{
    Task RunDisplayAsync(Func<CancellationToken, Task> interaction, CancellationToken cancellationToken);
    void Render(SessionViewerState state);
    SessionViewerCommand ReadCommand(CancellationToken cancellationToken);
    string ReadSearchQuery(SessionViewerState state, CancellationToken cancellationToken);

    /// <summary>Reads the list's title filter, live, the same way the find prompt reads its query.</summary>
    string ReadListFilter(SessionViewerState state, CancellationToken cancellationToken);
    bool ConfirmLocalDelete(ManagedSession session, CancellationToken cancellationToken);

    /// <summary>
    /// Reads a title and then a description, seeded with what is already there. Null means the
    /// edit was abandoned, and so does an empty title.
    /// </summary>
    SessionAnnotationEdit? ReadAnnotation(
        ManagedSession session,
        SessionAnnotation? current,
        CancellationToken cancellationToken);

    /// <summary>Asked only before a title someone typed by hand would be replaced.</summary>
    bool ConfirmAnnotationOverwrite(ManagedSession session, CancellationToken cancellationToken);
    void ShowMessage(string message, bool isError);

    /// <summary>Rows the content pane can show, so the state clamps scrolling to what fits.</summary>
    int ContentRows { get; }

    /// <summary>Columns the content pane can show, so the document wraps to what fits.</summary>
    int ContentWidth { get; }

    /// <summary>
    /// True while another keystroke is already waiting. Reading a session costs tens to hundreds
    /// of milliseconds, so the loop skips it while the selection is still moving.
    /// </summary>
    bool IsInputPending { get; }
}
