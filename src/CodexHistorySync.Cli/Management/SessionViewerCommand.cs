namespace CodexHistorySync.Cli.Management;

public enum SessionViewerCommand
{
    None,
    MoveUp,
    MoveDown,
    PageUp,
    PageDown,
    Home,
    End,
    FocusList,
    FocusContent,
    /// <summary>Find inside the open session. `/` means this while the text has focus.</summary>
    Search,
    /// <summary>Narrow the list by title. `/` means this while the list has focus.</summary>
    FilterList,
    NextMatch,
    Export,

    /// <summary>Ask the configured model to name the open session.</summary>
    GenerateAnnotation,

    /// <summary>Type a title and a description for the open session.</summary>
    EditAnnotation,

    Delete,
    Refresh,
    Exit
}
