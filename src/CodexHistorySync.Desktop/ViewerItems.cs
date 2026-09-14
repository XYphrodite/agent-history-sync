using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CodexHistorySync.Core.Management;
using CodexHistorySync.Core.Viewing;

namespace CodexHistorySync.Desktop;

public abstract class ObservableModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Changed(name);
        return true;
    }
}

public sealed class SessionNode(ManagedSession session, SessionNode? parent = null) : ObservableModel
{
    private bool expanded;
    public ManagedSession Session { get; private set; } = session;
    public SessionNode? Parent { get; } = parent;
    public SessionNode Root => Parent?.Root ?? this;
    public ObservableCollection<SessionNode> Children { get; } = [];
    public string Title => Session.Title;
    public string Detail => Parent is null
        ? $"{Session.Agent}  ·  {Session.LastModifiedAt.LocalDateTime:g}" : $"Subagent  ·  {Session.SessionId[..Math.Min(8, Session.SessionId.Length)]}";
    public bool IsExpanded { get => expanded; set => Set(ref expanded, value); }
    public void SetAnnotation(Core.Annotations.SessionAnnotation annotation)
    {
        Session = Session with { Annotation = annotation,
            Title = Session.TitleSource == ManagedTitleSource.Official ? Session.Title : annotation.Title };
        Changed(nameof(Title));
    }
}

/// <summary>Small text pages avoid creating an enormous text layout for one tool result.</summary>
public sealed class TraceEntryModel(TraceEntry entry) : ObservableModel
{
    public const int PageSize = 16000;
    private int page;
    private bool expanded;
    private int selectionStart;
    private int selectionEnd;
    public TraceEntry Entry { get; } = entry;
    public string Label => Entry.Label;
    public string Time => Entry.Timestamp?.LocalDateTime.ToString("HH:mm:ss") ?? string.Empty;
    public string? CallId => Entry.CallId;
    public bool IsTechnical => Entry.IsTool || Entry.Kind == TraceEntryKind.Notification;
    public bool IsMessage => !IsTechnical;
    public bool IsExpanded { get => expanded; set => Set(ref expanded, value); }
    public string Text => Entry.Text.Substring(page * PageSize, Math.Min(PageSize, Entry.Text.Length - page * PageSize));
    public int PageCount => Math.Max(1, (Entry.Text.Length + PageSize - 1) / PageSize);
    public bool HasPages => PageCount > 1;
    public string PageLabel => $"{page + 1} / {PageCount}";
    public bool HasPrevious => page > 0;
    public bool HasNext => page + 1 < PageCount;
    public int SelectionStart { get => selectionStart; private set => Set(ref selectionStart, value); }
    public int SelectionEnd { get => selectionEnd; private set => Set(ref selectionEnd, value); }
    public void MovePage(int delta) => SetPage(Math.Clamp(page + delta, 0, PageCount - 1));
    public void FocusMatch(int offset, int length)
    {
        SetPage(offset / PageSize);
        IsExpanded = true;
        SelectionStart = offset % PageSize;
        SelectionEnd = Math.Min(Text.Length, SelectionStart + length);
    }
    private void SetPage(int value)
    {
        page = value;
        SelectionStart = SelectionEnd = 0;
        Changed(nameof(Text)); Changed(nameof(PageLabel)); Changed(nameof(HasPrevious)); Changed(nameof(HasNext));
    }
}
