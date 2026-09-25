using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CodexHistorySync.Core.Annotations;
using CodexHistorySync.Core.Management;
using CodexHistorySync.Core.Viewing;
using CodexHistorySync.Desktop;

namespace CodexHistorySync.Desktop.Tests;

public sealed class SessionManagerTests
{
    [AvaloniaFact]
    public async Task OfflineMuseCanBeCopiedAndExplainsWhyDeletionIsUnavailable()
    {
        var source = Session("first", "Offline Muse") with
        {
            Agent = ManagedAgent.Muse,
            DiskSession = new CodexHistorySync.Core.Muse.MuseDiskSession(null!,
                new CodexHistorySync.Core.Muse.MuseDiskEntry("session.jsonl", 1, DateTimeOffset.UtcNow), ["first"])
        };
        var operations = new FakeOperations();
        using var model = CreateModel(operations, suppliedCatalog: new SingleSessionCatalog(source));
        await model.RefreshAsync();
        Assert.True(model.CanCopy);
        Assert.False(model.CanDelete);
        Assert.True(model.HasDeleteUnavailableReason);
        Assert.Contains("starting WSL", model.DeleteUnavailableReason);
        await model.CopyAsync(ManagedAgent.Grok, Path.GetTempPath());
        Assert.Single(operations.Copied);
        Assert.Equal(Path.GetTempPath(), Assert.Single(operations.CopyWorkingDirectories));
        Assert.Empty(operations.Deleted);
    }

    [AvaloniaFact]
    public async Task SlowMuseDoesNotKeepTheManagerListEmpty()
    {
        var catalog = new IncrementalDesktopCatalog();
        using var model = CreateModel(suppliedCatalog: catalog);
        var refresh = model.RefreshAsync();
        await UntilAsync(() => model.Sessions.Count == 1);
        Assert.True(model.IsRefreshing);
        Assert.Contains("Muse", model.Status);
        await model.SelectAsync(model.Sessions[0]);
        catalog.Release.TrySetResult();
        await refresh;
        Assert.Equal(2, model.Sessions.Count);
        Assert.Equal("ready", model.Selected!.SessionId);
        Assert.False(model.IsRefreshing);
    }

    [AvaloniaFact]
    public async Task EmptyManagerExplainsThatNoHistoriesWereFound()
    {
        using var model = CreateModel(suppliedCatalog: new EmptyDesktopCatalog());
        await model.RefreshAsync();
        Assert.Empty(model.Sessions);
        Assert.Equal("No local agent histories found.", model.Status);
    }

    [AvaloniaFact]
    public async Task RefreshLoadsSessionsAndKeepsSelectionAfterFilter()
    {
        using var model = CreateModel();
        await model.RefreshAsync();

        Assert.Equal(2, model.Sessions.Count);
        Assert.Equal(2, model.SessionCount);
        Assert.NotNull(model.Selected);
        Assert.Equal("first", model.Selected!.SessionId);
        Assert.True(model.HasTrace);

        model.Filter = "second";
        await model.FilterAsync(debounce: false);
        Assert.Single(model.Sessions);
        Assert.Equal("second", model.Sessions[0].SessionId);
        // Selection cleared because previous does not match filter
        Assert.Null(model.Selected);
        Assert.Empty(model.Entries);

        model.Filter = string.Empty;
        await model.FilterAsync(debounce: false);
        Assert.Equal(2, model.Sessions.Count);
    }

    [AvaloniaFact]
    public async Task CopyDelegatesToOperationsAndRefreshes()
    {
        var operations = new FakeOperations();
        using var model = CreateModel(operations: operations);
        await model.RefreshAsync();
        var first = model.Selected!;
        Assert.True(model.CanCopy);

        await model.CopyAsync(ManagedAgent.Grok);

        Assert.Single(operations.Copied);
        Assert.Equal(first.SessionId, operations.Copied[0].Source.SessionId);
        Assert.Equal(ManagedAgent.Grok, operations.Copied[0].Target);
        Assert.Contains("Copied to Grok", model.Status);
    }

    [AvaloniaFact]
    public async Task DeleteRemovesSessionAndClearsPreviewWhenActive()
    {
        var operations = new FakeOperations();
        using var model = CreateModel(operations: operations, activeId: "first");
        await model.RefreshAsync();

        // Active session cannot be copied or deleted
        Assert.False(model.CanCopy);
        Assert.False(model.CanDelete);
        Assert.True(model.IsSelectedActive);

        // Select second (readable, inactive)
        await model.SelectAsync(model.Sessions[1]);
        Assert.True(model.CanDelete);
        Assert.False(model.IsSelectedActive);

        await model.DeleteAsync();
        Assert.Single(operations.Deleted);
        Assert.Equal("second", operations.Deleted[0].SessionId);
    }

    [AvaloniaFact]
    public async Task UnreadableSessionShowsBadgeAndCannotBeCopied()
    {
        using var model = CreateModel(unreadableId: "first");
        await model.RefreshAsync();

        Assert.True(model.IsSelectedUnreadable);
        Assert.False(model.CanCopy);
        Assert.False(model.CanDelete);
        Assert.Contains("Cannot read this session", model.Status);
    }

    [AvaloniaFact]
    public async Task SearchFindsPreviewMatches()
    {
        using var model = CreateModel();
        await model.RefreshAsync();
        model.Query = "hello";
        await model.SearchAsync();

        Assert.True(model.HasMatches);
        Assert.Equal("1 / 1", model.MatchCount);
    }

    [AvaloniaFact]
    public async Task ClickingTheLineAboveTheTitleCopiesTheSessionId()
    {
        using var model = CreateModel();
        var window = new SessionManagerWindow(model);
        window.Show();
        try
        {
            await UntilAsync(() => model.Selected?.SessionId == "first");
            window.FindControl<Button>("CopySessionId")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => model.Status == "Session id copied.");
            Assert.Equal("first", await window.Clipboard!.TryGetTextAsync());
            Dispatcher.UIThread.RunJobs();
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task WindowShowsManagerAndCanSelectSecondSession()
    {
        using var model = CreateModel();
        var window = new SessionManagerWindow(model);
        window.Show();
        try
        {
            await UntilAsync(() => !model.IsRefreshing && model.Selected is not null && model.Entries.Count > 0);
            Assert.True(window.IsVisible);
            Assert.Equal("first", model.Selected!.SessionId);

            window.FindControl<ListBox>("SessionList")!.SelectedItem = model.Sessions[1];
            await UntilAsync(() => model.Selected!.SessionId == "second" && model.Entries.Count > 0);
            Assert.True(window.IsVisible);
            Assert.Equal("second", model.Selected!.SessionId);
        }
        finally { window.Close(); }
    }

    private static async Task UntilAsync(Func<bool> predicate)
    {
        for (var attempt = 0; attempt < 200 && !predicate(); attempt++)
        { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.True(predicate(), "Window did not reach expected state.");
    }

    private static SessionManagerModel CreateModel(FakeOperations? operations = null, string? activeId = null, string? unreadableId = null, ILocalSessionCatalog? suppliedCatalog = null)
    {
        var catalog = suppliedCatalog ?? new FakeCatalog(activeId, unreadableId);
        var traces = new FakeTraces(unreadableId);
        var ops = operations ?? new FakeOperations();
        var annotations = new FakeAnnotations();
        var services = new DesktopSessionServices(catalog, traces, new FakeFamilies(), new SessionContentReader(), annotations, ops, null, null, "test build");
        return new SessionManagerModel(services);
    }

    private static ManagedSession Session(string id, string title, bool isActive = false, bool canRead = true) =>
        new(ManagedAgent.Codex, id, id + ".jsonl", title, new DateTimeOffset(2026, 9, 14, id == "first" ? 18 : 17, 0, 0, TimeSpan.Zero), isActive, canRead);

    private sealed class FakeCatalog(string? activeId, string? unreadableId) : ILocalSessionCatalog
    {
        public Task<SessionCatalogSnapshot> ScanAsync(CancellationToken ct) => Task.FromResult(
            new SessionCatalogSnapshot(
                [Session("first", "First session", isActive: activeId == "first", canRead: unreadableId != "first"),
                 Session("second", "Second session", isActive: activeId == "second", canRead: unreadableId != "second")],
                [], [], [], [], []));
    }

    private sealed class FakeTraces(string? unreadableId) : ISessionTraceReader
    {
        public Task<SessionTrace> ReadAsync(ManagedSession session, CancellationToken ct)
        {
            if (unreadableId == session.SessionId || !session.CanRead)
                throw new InvalidDataException("The session is not readable.");
            var text = session.SessionId == "first" ? "hello world from first" : "second session content";
            return Task.FromResult(new SessionTrace(session, [new TraceEntry(0, TraceEntryKind.User, "User", text)], []));
        }
    }

    private sealed class FakeFamilies : ISessionFamilyReader
    {
        public Task<SessionThread> ReadAsync(ManagedSession parent, CancellationToken ct) => Task.FromResult(new SessionThread(parent, []));
    }

    private sealed class FakeAnnotations : ISessionAnnotationStore
    {
        public Task<IReadOnlyDictionary<SessionAnnotationKey, SessionAnnotation>> LoadAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<SessionAnnotationKey, SessionAnnotation>>(new Dictionary<SessionAnnotationKey, SessionAnnotation>());
        public Task SaveAsync(SessionAnnotationKey key, SessionAnnotation annotation, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAsync(SessionAnnotationKey key, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeOperations : ILocalSessionOperations
    {
        public List<string?> CopyWorkingDirectories { get; } = [];
        public List<(ManagedSession Source, ManagedAgent Target)> Copied { get; } = [];
        public List<ManagedSession> Deleted { get; } = [];
        public IReadOnlyList<ManagedAgent> AvailableCopyTargets(ManagedSession source) =>
            source.CanRead && !source.IsActive ? [ManagedAgent.Grok, ManagedAgent.Claude] : [];
        public Task<string> CopyAsync(ManagedSession source, CancellationToken ct) => CopyAsync(source, ManagedAgent.Grok, ct);
        public Task<string> CopyAsync(ManagedSession source, ManagedAgent target, string? workingDirectory, CancellationToken ct)
        {
            CopyWorkingDirectories.Add(workingDirectory);
            return CopyAsync(source, target, ct);
        }
        public Task<string> CopyAsync(ManagedSession source, ManagedAgent target, CancellationToken ct)
        {
            Copied.Add((source, target));
            return Task.FromResult("new-id");
        }
        public Task DeleteAsync(ManagedSession source, CancellationToken ct)
        {
            Deleted.Add(source);
            return Task.CompletedTask;
        }
    }

    private sealed class SingleSessionCatalog(ManagedSession source) : ILocalSessionCatalog
    {
        public Task<SessionCatalogSnapshot> ScanAsync(CancellationToken ct) =>
            Task.FromResult(new SessionCatalogSnapshot([], [], [], [], [], [source]) { ConfiguredAgents = [ManagedAgent.Muse] });
    }
}
