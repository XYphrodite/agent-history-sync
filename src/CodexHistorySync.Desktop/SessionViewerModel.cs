using System.Collections.ObjectModel;
using CodexHistorySync.Core.Annotations;
using CodexHistorySync.Core.Management;
using CodexHistorySync.Core.Viewing;

namespace CodexHistorySync.Desktop;

/// <summary>Selection, family discovery and search state independent of window controls.</summary>
public sealed class SessionViewerModel(DesktopSessionServices services) : ObservableModel, IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? selectionWork;
    private CancellationTokenSource? searchWork;
    private CancellationTokenSource? filterWork;
    private CancellationTokenSource? familyWork;
    private List<SessionNode> allSessions = [];
    private SessionNode? selected;
    private SessionThread? family;
    private SessionTrace? trace;
    private readonly Dictionary<string, SessionTrace> traceCache = new(StringComparer.Ordinal);
    private string status = "Loading local conversations…";
    private string filter = string.Empty;
    private string agent = "All agents";
    private string query = string.Empty;
    private bool showSubagents;
    private bool familySearch = true;
    private bool loading;
    private bool refreshing;
    private bool disposed;
    private int matchIndex = -1;

    public ObservableCollection<SessionNode> Sessions { get; } = [];
    public ObservableCollection<TraceEntryModel> Entries { get; } = [];
    public ObservableCollection<TraceSearchMatch> Matches { get; } = [];
    public IReadOnlyList<string> Agents { get; } = ["All agents", "Codex", "Grok", "Claude", "Continue", "Kimi", "Muse"];
    public string BuildLabel => services.BuildLabel;
    public string Agent { get => agent; set => Set(ref agent, value); }
    public string Filter { get => filter; set => Set(ref filter, value); }
    public string Query
    {
        get => query;
        set { if (Set(ref query, value)) { searchWork?.Cancel(); ClearMatches(); } }
    }
    public bool FamilySearch { get => familySearch; set => Set(ref familySearch, value); }
    public string Status { get => status; private set => Set(ref status, value); }
    public bool IsLoading { get => loading; private set => Set(ref loading, value); }
    public bool IsRefreshing => refreshing;
    public bool ShowSubagents { get => showSubagents; private set => Set(ref showSubagents, value); }
    public SessionNode? Selected => selected;
    public string Title => selected?.Session.Annotation?.Title ?? selected?.Title ?? "Your conversation archive";
    public string Subtitle => selected is null ? "Choose a conversation to start reading."
        : $"{selected.Session.Agent}  /  {selected.Session.SessionId}" + (selected.Session.IsReadOnly ? "  /  WSL disk · read-only" : "");
    public string Description => selected?.Session.Annotation?.Description ?? string.Empty;
    public bool HasSelection => selected is not null;
    public bool HasTrace => trace is not null;
    public bool CanShowSubagents => selected?.Session.Agent is ManagedAgent.Codex or ManagedAgent.Muse;
    public bool CanDelete => selected?.Parent is null && selected is not null && !selected.Session.IsReadOnly && services.Operations is not null;
    public bool CanGenerateTitle => services.TitleSuggester?.IsConfigured == true && selected is not null;
    public bool HasMatches => Matches.Count > 0;
    public string MatchCount => Matches.Count == 0 ? "No matches" : $"{Math.Max(0, matchIndex + 1)} / {Matches.Count}";
    public event Action<TraceEntryModel>? RevealEntry;
    public event Action<SessionNode>? RevealNode;
    public void ReportFailure(string action, Exception exception) => Status = action + ": " + exception.Message;

    public async Task RefreshAsync()
    {
        if (refreshing) return;
        refreshing = true;
        Changed(nameof(IsRefreshing));
        var previous = selected?.Root.Session;
        var previousSelection = selected?.Session;
        var wasShown = ShowSubagents;
        try
        {
            Status = "Reading local session catalog…";
            SessionCatalogSnapshot? snapshot = null;
            traceCache.Clear();
            family = null;
            await foreach (var update in services.Catalog.ScanIncrementallyAsync(lifetime.Token))
            {
                snapshot = update;
                var pendingRows = allSessions.Where(node => update.PendingAgents.Contains(node.Session.Agent));
                allSessions = update.ConfiguredAgents.SelectMany(update.For).Select(session => new SessionNode(session))
                    .Concat(pendingRows).OrderByDescending(node => node.Session.LastModifiedAt).ToList();
                await FilterAsync(debounce: false);
                Status = update.LoadingMessage ?? $"{allSessions.Count} local conversations";
            }
            if (snapshot is null) return;
            previous = selected?.Root.Session ?? previous;
            var replacement = previous is null ? Sessions.FirstOrDefault()
                : Sessions.FirstOrDefault(node => Same(node.Session, previous));
            await SelectAsync(replacement);
            if (wasShown && replacement is not null && previous is not null && Same(replacement.Session, previous))
            {
                await SetSubagentsAsync(true);
                var previousChild = Nodes(replacement).FirstOrDefault(node => previousSelection is not null && Same(node.Session, previousSelection));
                if (previousChild is not null) { await SelectAsync(previousChild); replacement = previousChild; }
            }
            if (replacement is not null) RevealNode?.Invoke(replacement);
            else Status = $"{allSessions.Count} local conversations";
            if (snapshot.LoadingMessage is { } message) Status = message;
            if (services.SearchIndex is not null)
            {
                await Task.Run(() => services.SearchIndex.EnsureCurrentAsync(snapshot, services.Conversations, lifetime.Token), lifetime.Token);
                if (!string.IsNullOrWhiteSpace(Filter)) await FilterAsync(debounce: false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (ReadFailure(exception)) { Status = "Could not refresh sessions: " + exception.Message; }
        finally { refreshing = false; Changed(nameof(IsRefreshing)); }
    }

    public async Task FilterAsync(bool debounce = true)
    {
        var token = Restart(ref filterWork);
        try
        {
            if (debounce) await Task.Delay(220, token);
            var text = Filter.Trim();
            var rows = allSessions.Where(node => Agent == "All agents" || node.Session.Agent.ToString() == Agent).ToArray();
            var matching = new HashSet<(ManagedAgent, string)>();
            if (text.Length > 0 && services.SearchIndex is not null)
            {
                var hits = await services.SearchIndex.SearchAsync(text, 200, token);
                foreach (var hit in hits) matching.Add((hit.Agent, hit.SessionId));
            }
            token.ThrowIfCancellationRequested();
            Replace(Sessions, rows.Where(node => text.Length == 0 || node.Title.Contains(text, StringComparison.OrdinalIgnoreCase)
                || node.Session.SessionId.Contains(text, StringComparison.OrdinalIgnoreCase)
                || matching.Contains((node.Session.Agent, node.Session.SessionId))));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (ReadFailure(exception)) { Status = "Search unavailable: " + exception.Message; }
    }

    public async Task SelectAsync(SessionNode? node)
    {
        if (ReferenceEquals(selected, node)) return;
        var token = Restart(ref selectionWork);
        if (!ReferenceEquals(selected?.Root, node?.Root))
        {
            searchWork?.Cancel(); familyWork?.Cancel();
            if (selected is not null) { selected.Root.Children.Clear(); selected.Root.IsExpanded = false; }
            family = null; traceCache.Clear();
            ShowSubagents = false;
            ClearMatches();
        }
        selected = node;
        trace = null;
        Entries.Clear();
        NotifySelection();
        if (node is null) { IsLoading = false; return; }
        IsLoading = true;
        Status = "Reading conversation…";
        try
        {
            var loaded = await ReadTraceAsync(node.Session, token);
            token.ThrowIfCancellationRequested();
            trace = loaded;
            Replace(Entries, loaded.Entries.Select(entry => new TraceEntryModel(entry)));
            Changed(nameof(HasTrace));
            Status = loaded.Warnings.Count > 0 ? string.Join(" ", loaded.Warnings) : $"{Entries.Count} messages and tool records";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (ReadFailure(exception)) { if (!token.IsCancellationRequested) Status = "Cannot read this session: " + exception.Message; }
        finally { if (!token.IsCancellationRequested) IsLoading = false; }
    }

    public async Task SetSubagentsAsync(bool show)
    {
        if (selected is null || !CanShowSubagents) return;
        var root = selected.Root;
        var token = Restart(ref familyWork);
        ShowSubagents = show;
        if (!show)
        {
            if (selected.Parent is not null) await SelectAsync(root);
            root.Children.Clear(); root.IsExpanded = false;
            return;
        }
        try
        {
            Status = "Finding this conversation’s subagents…";
            var tree = await ReadFamilyAsync(root, token);
            token.ThrowIfCancellationRequested();
            Populate(root, tree);
            root.IsExpanded = true;
            Status = $"{tree.DescendantsAndSelf().Count() - 1} local subagents";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (ReadFailure(exception)) { if (!token.IsCancellationRequested) Status = "Could not read subagents: " + exception.Message; }
    }

    public async Task SearchAsync()
    {
        var token = Restart(ref searchWork);
        ClearMatches();
        if (selected is null || string.IsNullOrWhiteSpace(Query)) return;
        var target = selected;
        var searchText = Query;
        try
        {
            Status = "Searching messages and tool input/output…";
            var sessions = FamilySearch && target.Session.Agent == ManagedAgent.Codex
                ? (await ReadFamilyAsync(target.Root, token)).DescendantsAndSelf().Select(node => node.Session).ToArray()
                : [target.Session];
            var found = new List<TraceSearchMatch>();
            var unreadable = 0;
            foreach (var session in sessions)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var document = await ReadTraceAsync(session, token);
                    var hits = await Task.Run(() => SessionTraceSearch.Find([document], searchText, token), token);
                    found.AddRange(hits.Take(SessionTraceSearch.MaximumMatches - found.Count));
                    if (found.Count >= SessionTraceSearch.MaximumMatches) break;
                }
                catch (Exception exception) when (ReadFailure(exception)) { unreadable++; }
            }
            token.ThrowIfCancellationRequested();
            Replace(Matches, found);
            Changed(nameof(HasMatches)); Changed(nameof(MatchCount));
            Status = (found.Count >= SessionTraceSearch.MaximumMatches ? "Showing the first 10,000 matches; narrow your search" : $"{Matches.Count} matches in {sessions.Length} conversation(s)")
                + (unreadable > 0 ? $"; {unreadable} could not be read" : string.Empty);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (ReadFailure(exception)) { if (!token.IsCancellationRequested) Status = "Search failed: " + exception.Message; }
    }

    public async Task GoToMatchAsync(TraceSearchMatch match)
    {
        if (selected is null) return;
        var root = selected.Root;
        var token = searchWork?.Token ?? lifetime.Token;
        if (!Same(match.Session, root.Session))
        {
            if (!ShowSubagents) await SetSubagentsAsync(true);
        }
        if (token.IsCancellationRequested || !ReferenceEquals(selected?.Root, root)) return;
        var node = Nodes(root).FirstOrDefault(item => Same(item.Session, match.Session));
        if (node is null) return;
        for (var ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent) ancestor.IsExpanded = true;
        await SelectAsync(node);
        if (!ReferenceEquals(selected, node)) return;
        RevealNode?.Invoke(node);
        var entry = Entries.FirstOrDefault(item => item.Entry.Index == match.EntryIndex);
        if (entry is null) return;
        matchIndex = Matches.IndexOf(match);
        Changed(nameof(MatchCount));
        entry.FocusMatch(match.Offset, Query.Length);
        RevealEntry?.Invoke(entry);
    }

    public Task NextMatchAsync(int delta) => Matches.Count == 0 ? Task.CompletedTask
        : GoToMatchAsync(Matches[matchIndex < 0 ? delta > 0 ? 0 : Matches.Count - 1
            : (matchIndex + delta + Matches.Count) % Matches.Count]);

    public async Task ExportAsync(string directory, bool includeFamily)
    {
        var target = selected;
        var current = trace;
        if (target is null || current is null) return;
        try
        {
            Status = "Exporting Markdown…";
            var exporter = new SessionTraceExporter(services.Traces);
            var tree = includeFamily ? await ReadFamilyAsync(target.Root, lifetime.Token) : null;
            var path = await Task.Run(() => tree is not null
                ? exporter.ExportFamilyAsync(tree, directory, lifetime.Token)
                : exporter.ExportAsync(current, directory, lifetime.Token), lifetime.Token);
            Status = "Exported: " + path;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (ReadFailure(exception)) { Status = "Export failed: " + exception.Message; }
    }

    public async Task SaveAnnotationAsync(string title, string description)
    {
        var target = selected;
        if (target is null) return;
        try
        {
            var conversation = await services.Conversations.ReadAsync(target.Session, lifetime.Token);
            var annotation = new SessionAnnotation(title.Trim(), description.Trim(), SessionAnnotationSource.Edited,
                SessionDigest.Build(conversation).Hash, null, DateTimeOffset.UtcNow);
            await services.Annotations.SaveAsync(new SessionAnnotationKey(target.Session.Agent, target.Session.SessionId), annotation, lifetime.Token);
            target.SetAnnotation(annotation);
            NotifySelection(); Status = "Title and description saved.";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (ReadFailure(exception)) { Status = "Could not save title: " + exception.Message; }
    }

    public async Task<SessionAnnotationDraft?> GenerateTitleAsync()
    {
        var target = selected;
        if (target is null || services.TitleSuggester is null) return null;
        try
        {
            Status = "Asking the configured title model…";
            var conversation = await services.Conversations.ReadAsync(target.Session, lifetime.Token);
            var draft = await services.TitleSuggester.SuggestAsync(SessionDigest.Build(conversation), lifetime.Token);
            Status = draft is null ? "Could not generate a title." : "Review the suggested title before saving.";
            return ReferenceEquals(target, selected) ? draft : null;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return null; }
        catch (Exception exception) when (ReadFailure(exception)) { Status = "Title generation failed: " + exception.Message; return null; }
    }

    public async Task DeleteAsync(SessionNode target)
    {
        if (target.Parent is not null || target.Session.IsReadOnly || services.Operations is null) return;
        try
        {
            await services.Operations.DeleteAsync(target.Session, lifetime.Token);
            await services.Annotations.DeleteAsync(new SessionAnnotationKey(target.Session.Agent, target.Session.SessionId), lifetime.Token);
            await RefreshAsync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (ReadFailure(exception) || exception is ManagedSessionOperationException)
        { Status = "Could not delete session: " + exception.Message; }
    }

    private async Task<SessionTrace> ReadTraceAsync(ManagedSession session, CancellationToken token)
    {
        if (traceCache.TryGetValue(session.NativePath, out var cached)) return cached;
        var document = await Task.Run(() => services.Traces.ReadAsync(session, token), token);
        token.ThrowIfCancellationRequested();
        // Bound retained transcripts while navigating; a family search may reread an evicted file.
        if (traceCache.Count >= 6) traceCache.Remove(traceCache.Keys.First());
        traceCache[session.NativePath] = document;
        return document;
    }

    private async Task<SessionThread> ReadFamilyAsync(SessionNode root, CancellationToken token)
    {
        if (family is not null && Same(family.Session, root.Session)) return family;
        var loaded = await Task.Run(() => services.Families.ReadAsync(root.Session, token), token);
        token.ThrowIfCancellationRequested();
        if (ReferenceEquals(selected?.Root, root)) family = loaded;
        return loaded;
    }

    private static void Populate(SessionNode node, SessionThread tree)
    {
        node.Children.Clear();
        foreach (var child in tree.Children)
        {
            var item = new SessionNode(child.Session, node);
            Populate(item, child);
            node.Children.Add(item);
        }
    }
    private void NotifySelection()
    {
        foreach (var name in new[] { nameof(Selected), nameof(Title), nameof(Subtitle), nameof(Description), nameof(HasSelection),
            nameof(HasTrace), nameof(CanShowSubagents), nameof(CanDelete), nameof(CanGenerateTitle) }) Changed(name);
    }
    private void ClearMatches()
    {
        Matches.Clear(); matchIndex = -1;
        Changed(nameof(HasMatches)); Changed(nameof(MatchCount));
    }
    private CancellationToken Restart(ref CancellationTokenSource? source)
    {
        source?.Cancel(); source?.Dispose();
        source = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        return source.Token;
    }
    private static void Replace<T>(ObservableCollection<T> collection, IEnumerable<T> items)
    { collection.Clear(); foreach (var item in items) collection.Add(item); }
    private static IEnumerable<SessionNode> Nodes(SessionNode root)
    {
        yield return root;
        foreach (var child in root.Children) foreach (var node in Nodes(child)) yield return node;
    }
    private static bool Same(ManagedSession left, ManagedSession right) => left.Agent == right.Agent && left.SessionId == right.SessionId;
    // InvalidDataException derives from SystemException, not IOException.
    private static bool ReadFailure(Exception exception) => exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException
        or InvalidOperationException or System.Text.DecoderFallbackException or System.Text.Json.JsonException or Microsoft.Data.Sqlite.SqliteException;
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        selectionWork?.Dispose(); searchWork?.Dispose(); filterWork?.Dispose(); familyWork?.Dispose(); lifetime.Dispose();
    }
}
