using System.Collections.ObjectModel;
using System.Linq;
using CodexHistorySync.Core.Annotations;
using CodexHistorySync.Core.Management;
using CodexHistorySync.Core.Viewing;

namespace CodexHistorySync.Desktop;

/// <summary>Desktop model for --manage: flat list by agent, preview, copy and delete.</summary>
public sealed class SessionManagerModel : ObservableModel, IDisposable
{
    private readonly DesktopSessionServices services;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? selectionWork;
    private CancellationTokenSource? filterWork;
    private CancellationTokenSource? searchWork;
    private List<ManagedSession> allSessions = [];
    private ManagedSession? selected;
    private SessionTrace? trace;
    private readonly Dictionary<string, SessionTrace> traceCache = new(StringComparer.Ordinal);
    private string status = "Loading local sessions…";
    private string filter = string.Empty;
    private string agent = "All agents";
    private string query = string.Empty;
    private bool loading;
    private bool refreshing;
    private bool disposed;
    private int matchIndex = -1;

    public SessionManagerModel(DesktopSessionServices services)
    {
        this.services = services ?? throw new ArgumentNullException(nameof(services));
    }

    public ObservableCollection<ManagedSession> Sessions { get; } = [];
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

    public string Status { get => status; private set => Set(ref status, value); }
    public bool IsLoading { get => loading; private set => Set(ref loading, value); }
    public bool IsRefreshing => refreshing;
    public ManagedSession? Selected => selected;
    public string Title => selected?.Title ?? "Select a session";
    public string Subtitle => selected is null ? "Choose a conversation to manage."
        : $"{selected.Agent}  /  {selected.SessionId}";
    public string Description => selected?.Annotation?.Description ?? string.Empty;
    public bool HasSelection => selected is not null;
    public bool HasTrace => trace is not null;
    public bool CanCopy => selected is not null && !selected.IsActive && selected.CanRead && services.Operations is not null && AvailableTargets.Count > 0;
    public bool CanDelete => selected is not null && !selected.IsActive && selected.CanRead && services.Operations is not null;
    public IReadOnlyList<ManagedAgent> AvailableTargets
    {
        get
        {
            if (selected is null || services.Operations is null) return [];
            try { return services.Operations.AvailableCopyTargets(selected); }
            catch { return []; }
        }
    }
    public string CopyLabel
    {
        get
        {
            var targets = AvailableTargets;
            return targets.Count switch
            {
                0 => "Copy",
                1 => $"Copy to {targets[0]}",
                _ => "Copy to…"
            };
        }
    }
    public string ActiveBadge => selected?.IsActive == true ? "● Active — close the agent to copy or delete" : string.Empty;
    public string UnreadableBadge => selected?.CanRead == false ? "Unreadable" : string.Empty;
    public bool IsSelectedActive => selected?.IsActive == true;
    public bool IsSelectedUnreadable => selected is not null && !selected.CanRead;
    public bool IsSelectedReadable => selected is not null && selected.CanRead && !selected.IsActive;
    public bool HasMatches => Matches.Count > 0;
    public string MatchCount => Matches.Count == 0 ? "No matches" : $"{Math.Max(0, matchIndex + 1)} / {Matches.Count}";
    public int SessionCount => allSessions.Count;
    public int FilteredCount => Sessions.Count;
    public string FilteredLabel => $"{FilteredCount} shown ({SessionCount} total)";

    public event Action<TraceEntryModel>? RevealEntry;

    public void ReportFailure(string action, Exception exception) => Status = action + ": " + exception.Message;

    public async Task RefreshAsync()
    {
        if (refreshing) return;
        refreshing = true;
        Changed(nameof(IsRefreshing));
        var previous = selected;
        try
        {
            Status = "Reading local session catalog…";
            var snapshot = await Task.Run(() => services.Catalog.ScanAsync(lifetime.Token), lifetime.Token);
            traceCache.Clear();
            allSessions = snapshot.ConfiguredAgents.SelectMany(snapshot.For)
                .OrderByDescending(s => s.LastModifiedAt).ThenBy(s => s.SessionId, StringComparer.OrdinalIgnoreCase).ToList();
            await FilterAsync(debounce: false);
            var replacement = previous is null ? Sessions.FirstOrDefault()
                : Sessions.FirstOrDefault(s => s.Agent == previous.Agent && s.SessionId == previous.SessionId);
            var statusBeforeSelect = Status;
            await SelectAsync(replacement);
            if (replacement is null) Status = $"{SessionCount} local sessions — no match for filter";
            else if (Status == "Reading conversation…" || Status == statusBeforeSelect) Status = $"{FilteredCount} of {SessionCount} sessions";
            if (services.SearchIndex is not null)
            {
                try
                {
                    await Task.Run(() => services.SearchIndex.EnsureCurrentAsync(snapshot, services.Conversations, lifetime.Token), lifetime.Token);
                    if (!string.IsNullOrWhiteSpace(Filter)) await FilterAsync(debounce: false);
                }
                catch (Exception ex) when (ReadFailure(ex)) { }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ReadFailure(ex)) { Status = "Could not refresh sessions: " + ex.Message; }
        finally { refreshing = false; Changed(nameof(IsRefreshing)); Changed(nameof(SessionCount)); Changed(nameof(FilteredCount)); Changed(nameof(FilteredLabel)); }
    }

    public async Task FilterAsync(bool debounce = true)
    {
        var token = Restart(ref filterWork);
        try
        {
            if (debounce) await Task.Delay(220, token);
            var text = Filter.Trim();
            var rows = allSessions.Where(s => Agent == "All agents" || s.Agent.ToString() == Agent).ToArray();
            var matching = new HashSet<(ManagedAgent, string)>();
            if (text.Length > 0 && services.SearchIndex is not null)
            {
                try
                {
                    var hits = await services.SearchIndex.SearchAsync(text, 200, token);
                    foreach (var hit in hits) matching.Add((hit.Agent, hit.SessionId));
                }
                catch (Exception ex) when (ReadFailure(ex)) { }
            }
            token.ThrowIfCancellationRequested();
            var filtered = rows.Where(s => text.Length == 0
                || s.Title.Contains(text, StringComparison.OrdinalIgnoreCase)
                || s.SessionId.Contains(text, StringComparison.OrdinalIgnoreCase)
                || matching.Contains((s.Agent, s.SessionId))).ToArray();
            Replace(Sessions, filtered);
            // Keep selection if it survives filter, else clear preview
            if (selected is not null && !filtered.Any(s => s.Agent == selected.Agent && s.SessionId == selected.SessionId))
            {
                await SelectAsync(null);
            }
            Changed(nameof(FilteredCount));
            Changed(nameof(SessionCount));
            Changed(nameof(FilteredLabel));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ReadFailure(ex)) { Status = "Filter unavailable: " + ex.Message; }
    }

    public async Task SelectAsync(ManagedSession? session)
    {
        if (ReferenceEquals(selected, session)) return;
        var token = Restart(ref selectionWork);
        selected = session;
        trace = null;
        Entries.Clear();
        ClearMatches();
        NotifySelection();
        if (session is null) { IsLoading = false; return; }
        IsLoading = true;
        Status = "Reading conversation…";
        try
        {
            var loaded = await ReadTraceAsync(session, token);
            token.ThrowIfCancellationRequested();
            trace = loaded;
            Replace(Entries, loaded.Entries.Select(e => new TraceEntryModel(e)));
            Changed(nameof(HasTrace));
            Status = loaded.Warnings.Count > 0 ? string.Join(" ", loaded.Warnings) : $"{Entries.Count} messages and tool records";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ReadFailure(ex)) { if (!token.IsCancellationRequested) Status = "Cannot read this session: " + ex.Message; }
        finally { if (!token.IsCancellationRequested) IsLoading = false; }
    }

    public async Task CopyAsync(ManagedAgent target)
    {
        var source = selected;
        if (source is null || services.Operations is null) return;
        try
        {
            Status = $"Copying to {target}…";
            var newId = await services.Operations.CopyAsync(source, target, lifetime.Token);
            await RefreshAsync();
            Status = $"Copied to {target} ({newId}).";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (ManagedSessionOperationException ex)
        {
            Status = ex.Reason switch
            {
                ManagedSessionFailureReason.Active => "Active sessions cannot be copied.",
                ManagedSessionFailureReason.Unreadable => "This session cannot be copied.",
                ManagedSessionFailureReason.Changed => "The session changed. Copy was cancelled.",
                ManagedSessionFailureReason.DestinationUnavailable => "No other agent is available to copy this session into.",
                ManagedSessionFailureReason.Incompatible => "Codex rejected the copied session.",
                _ => "Copy failed: " + ex.Message
            };
        }
        catch (Exception ex) when (ReadFailure(ex) || ex is InvalidOperationException or InvalidDataException) { Status = "Copy failed: " + ex.Message; }
    }

    public async Task DeleteAsync()
    {
        var target = selected;
        if (target is null || services.Operations is null) return;
        try
        {
            Status = "Deleting…";
            await services.Operations.DeleteAsync(target, lifetime.Token);
            try { await services.Annotations.DeleteAsync(new SessionAnnotationKey(target.Agent, target.SessionId), lifetime.Token); }
            catch { }
            await RefreshAsync();
            Status = "Deleted.";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ReadFailure(ex) || ex is ManagedSessionOperationException) { Status = "Could not delete session: " + ex.Message; }
    }

    public async Task SearchAsync()
    {
        var token = Restart(ref searchWork);
        ClearMatches();
        if (selected is null || string.IsNullOrWhiteSpace(Query) || trace is null) return;
        try
        {
            Status = "Searching messages and tool input/output…";
            var found = await Task.Run(() => SessionTraceSearch.Find([trace], Query, token), token);
            token.ThrowIfCancellationRequested();
            Replace(Matches, found);
            Changed(nameof(HasMatches)); Changed(nameof(MatchCount));
            Status = $"{Matches.Count} matches";
            if (Matches.Count > 0)
            {
                matchIndex = 0; Changed(nameof(MatchCount));
                await GoToMatchAsync(Matches[0]);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ReadFailure(ex)) { if (!token.IsCancellationRequested) Status = "Search failed: " + ex.Message; }
    }

    public async Task GoToMatchAsync(TraceSearchMatch match)
    {
        if (trace is null) return;
        var entry = Entries.FirstOrDefault(e => e.Entry.Index == match.EntryIndex);
        if (entry is null) return;
        matchIndex = Matches.IndexOf(match);
        Changed(nameof(MatchCount));
        entry.FocusMatch(match.Offset, Query.Length);
        RevealEntry?.Invoke(entry);
        await Task.CompletedTask;
    }

    public Task NextMatchAsync(int delta) => Matches.Count == 0 ? Task.CompletedTask
        : GoToMatchAsync(Matches[matchIndex < 0 ? delta > 0 ? 0 : Matches.Count - 1
            : (matchIndex + delta + Matches.Count) % Matches.Count]);

    private async Task<SessionTrace> ReadTraceAsync(ManagedSession session, CancellationToken token)
    {
        if (traceCache.TryGetValue(session.NativePath, out var cached)) return cached;
        var document = await Task.Run(() => services.Traces.ReadAsync(session, token), token);
        token.ThrowIfCancellationRequested();
        if (traceCache.Count >= 6) traceCache.Remove(traceCache.Keys.First());
        traceCache[session.NativePath] = document;
        return document;
    }

    private void NotifySelection()
    {
        foreach (var name in new[] { nameof(Selected), nameof(Title), nameof(Subtitle), nameof(Description), nameof(HasSelection),
            nameof(HasTrace), nameof(CanCopy), nameof(CanDelete), nameof(CopyLabel), nameof(ActiveBadge), nameof(UnreadableBadge),
            nameof(IsSelectedActive), nameof(IsSelectedUnreadable), nameof(IsSelectedReadable), nameof(AvailableTargets) }) Changed(name);
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

    private static bool ReadFailure(Exception ex) => ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException
        or InvalidOperationException or System.Text.DecoderFallbackException or System.Text.Json.JsonException or Microsoft.Data.Sqlite.SqliteException;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        selectionWork?.Dispose(); searchWork?.Dispose(); filterWork?.Dispose(); lifetime.Dispose();
    }
}
