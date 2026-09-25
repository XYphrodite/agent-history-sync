using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CodexHistorySync.Core.Annotations;
using CodexHistorySync.Core.Codex;
using CodexHistorySync.Core.Management;
using CodexHistorySync.Core.Viewing;
using CodexHistorySync.Desktop;

[assembly: AvaloniaTestApplication(typeof(CodexHistorySync.Desktop.Tests.TestAppBuilder))]

namespace CodexHistorySync.Desktop.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<SessionViewerApp>()
        .UseSkia().WithInterFont().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class SessionViewerTests
{
    [AvaloniaFact]
    public async Task SlowMuseDoesNotKeepTheViewerListEmpty()
    {
        var catalog = new IncrementalDesktopCatalog();
        using var model = CreateModel(catalog: catalog);
        var refresh = model.RefreshAsync();
        for (var i = 0; i < 200 && model.Sessions.Count == 0; i++)
        { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.Single(model.Sessions);
        Assert.True(model.IsRefreshing);
        Assert.Contains("Muse", model.Status);
        catalog.Release.TrySetResult();
        await refresh;
        Assert.Equal(2, model.Sessions.Count);
        await model.SelectAsync(model.Sessions.Single(node => node.Session.Agent == ManagedAgent.Muse));
        Assert.True(model.CanShowSubagents);
    }

    [AvaloniaFact]
    public async Task EmptyViewerExplainsThatNoHistoriesWereFound()
    {
        using var model = CreateModel(catalog: new EmptyDesktopCatalog());
        await model.RefreshAsync();
        Assert.Empty(model.Sessions);
        Assert.Equal("No local agent histories found.", model.Status);
    }

    [AvaloniaFact]
    public async Task UnreadableFirstSessionShowsErrorAndAnotherConversationCanBeOpened()
    {
        using var model = CreateModel(catalog: new FakeCatalog(unreadableParent: true));

        await model.RefreshAsync();

        Assert.Equal("parent", model.Selected!.Session.SessionId);
        Assert.False(model.Selected.Session.CanRead);
        Assert.False(model.IsLoading);
        Assert.False(model.IsRefreshing);
        Assert.False(model.HasTrace);
        Assert.Empty(model.Entries);
        Assert.Equal("Cannot read this session: The session is not readable.", model.Status);

        await model.SelectAsync(model.Sessions[1]);

        Assert.Equal("second", model.Selected!.Session.SessionId);
        Assert.True(model.HasTrace);
        Assert.Single(model.Entries);
    }

    [AvaloniaFact]
    public async Task InvalidDataAfterSelectionClearsOldTranscriptAndReportsTheError()
    {
        using var model = CreateModel(new FakeTraces { InvalidSessionId = "second" });
        await model.RefreshAsync();
        Assert.True(model.HasTrace);

        await model.SelectAsync(model.Sessions[1]);

        Assert.False(model.HasTrace);
        Assert.Empty(model.Entries);
        Assert.False(model.IsLoading);
        Assert.Contains("Cannot read this session: Invalid synthetic session.", model.Status);

        await model.SelectAsync(model.Sessions[0]);
        Assert.True(model.HasTrace);
    }

    [AvaloniaFact]
    public async Task FamilySearchSkipsInvalidSubagentAndKeepsResultsFromReadableParent()
    {
        using var model = CreateModel(new FakeTraces { InvalidSessionId = "reviewer" });
        await model.RefreshAsync();
        model.Query = "current task";

        await model.SearchAsync();

        Assert.Equal("parent", Assert.Single(model.Matches).Session.SessionId);
        Assert.Contains("1 could not be read", model.Status);
        Assert.True(model.HasTrace);
    }

    [AvaloniaFact]
    public async Task FamilyExportReportsInvalidSubagentWithoutPublishingPartialFiles()
    {
        using var model = CreateModel(new FakeTraces { InvalidSessionId = "reviewer" });
        await model.RefreshAsync();
        var directory = Path.Combine(Path.GetTempPath(), "agent-sync-viewer-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await model.ExportAsync(directory, includeFamily: true);

            Assert.Equal("Export failed: Invalid synthetic session.", model.Status);
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
            Assert.True(model.HasTrace);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [AvaloniaFact]
    public async Task ClickingTheLineAboveTheTitleCopiesTheSessionId()
    {
        using var model = CreateModel();
        var window = new SessionViewerWindow(model);
        window.Show();
        try
        {
            await UntilAsync(() => model.Selected?.Session.SessionId == "parent");
            window.FindControl<Button>("CopySessionId")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => model.Status == "Session id copied.");
            Assert.Equal("parent", await window.Clipboard!.TryGetTextAsync());
            Dispatcher.UIThread.RunJobs();
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task WindowStaysOpenWithUnreadableFirstSessionAndCanSelectHealthyConversation()
    {
        using var model = CreateModel(catalog: new FakeCatalog(unreadableParent: true));
        var window = new SessionViewerWindow(model);
        window.Show();
        try
        {
            await UntilAsync(() => !model.IsRefreshing && model.Selected is not null);
            Assert.True(window.IsVisible);
            Assert.False(model.HasTrace);
            Assert.Contains("Cannot read this session", model.Status);

            window.FindControl<TreeView>("SessionTree")!.SelectedItem = model.Sessions[1];
            await UntilAsync(() => model.Selected?.Session.SessionId == "second" && model.HasTrace);

            Assert.True(window.IsVisible);
            Assert.Single(model.Entries);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task WindowSurvivesUnreadableDuplicatesDiscoveredByTheRealCatalog()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agent-sync-viewer-catalog-" + Guid.NewGuid().ToString("N"));
        var sessions = Path.Combine(directory, "sessions");
        Directory.CreateDirectory(sessions);
        try
        {
            const string duplicate = """
                {"type":"session_meta","payload":{"id":"duplicate","title":"Duplicate session","timestamp":"2026-09-14T19:00:00Z"}}
                """;
            const string healthy = """
                {"type":"session_meta","payload":{"id":"healthy","title":"Healthy session","timestamp":"2026-09-14T18:00:00Z"}}
                {"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"Readable synthetic message."}]}}
                """;
            await File.WriteAllTextAsync(Path.Combine(sessions, "duplicate-one.jsonl"), duplicate);
            await File.WriteAllTextAsync(Path.Combine(sessions, "duplicate-two.jsonl"), duplicate);
            await File.WriteAllTextAsync(Path.Combine(sessions, "healthy.jsonl"), healthy);
            var catalog = new LocalSessionCatalog(CodexPaths.Resolve(directory), null, new NoActiveSessions());
            using var model = new SessionViewerModel(new DesktopSessionServices(catalog, new SessionTraceReader(),
                new FakeFamilies(), new SessionContentReader(), new FakeAnnotations()));
            var window = new SessionViewerWindow(model);
            window.Show();
            try
            {
                await UntilAsync(() => !model.IsRefreshing && model.Selected is not null);
                Assert.Equal(3, model.Sessions.Count);
                Assert.Equal("duplicate", model.Selected!.Session.SessionId);
                Assert.False(model.Selected.Session.CanRead);
                Assert.Contains("Cannot read this session", model.Status);
                Assert.True(window.IsVisible);

                window.FindControl<TreeView>("SessionTree")!.SelectedItem = model.Sessions.Single(node => node.Session.CanRead);
                await UntilAsync(() => model.HasTrace);

                Assert.Equal("Readable synthetic message.", Assert.Single(model.Entries).Text);
                Assert.True(window.IsVisible);
            }
            finally { window.Close(); }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [AvaloniaFact]
    public async Task SubagentsAreOptInAndResetWhenAnotherParentIsSelected()
    {
        using var model = CreateModel();
        await model.RefreshAsync();
        var parent = model.Selected!;
        Assert.False(model.ShowSubagents);
        Assert.Empty(parent.Children);
        await model.SetSubagentsAsync(true);
        var child = Assert.Single(parent.Children);
        await model.SelectAsync(child);
        Assert.True(model.ShowSubagents);
        Assert.Equal("reviewer", model.Selected!.Session.SessionId);
        await model.SelectAsync(model.Sessions[1]);
        Assert.False(model.ShowSubagents);
        Assert.Empty(parent.Children);
    }

    [AvaloniaFact]
    public async Task FamilySearchNavigatesToHiddenWorkerToolOutputAndHighlightsItsText()
    {
        using var model = CreateModel();
        await model.RefreshAsync();
        model.Query = "authorization";
        await model.SearchAsync();
        var match = Assert.Single(model.Matches);
        Assert.False(model.ShowSubagents);
        TraceEntryModel? revealed = null;
        model.RevealEntry += entry => revealed = entry;
        await model.GoToMatchAsync(match);
        Assert.True(model.ShowSubagents);
        Assert.Equal("reviewer", model.Selected!.Session.SessionId);
        Assert.NotNull(revealed);
        Assert.True(revealed.IsExpanded);
        Assert.Equal("authorization", revealed.Text[revealed.SelectionStart..revealed.SelectionEnd]);
    }

    [AvaloniaFact]
    public async Task RefreshPreservesSelectedWorkerAndExpandedFamily()
    {
        using var model = CreateModel();
        await model.RefreshAsync();
        await model.SetSubagentsAsync(true);
        await model.SelectAsync(model.Selected!.Children[0]);
        await model.RefreshAsync();
        Assert.True(model.ShowSubagents);
        Assert.Equal("reviewer", model.Selected!.Session.SessionId);
        Assert.Single(model.Selected.Root.Children);
    }

    [AvaloniaFact]
    public async Task CancelledSlowSelectionCannotReplaceTheNewlySelectedConversation()
    {
        var traceReader = new FakeTraces();
        using var model = CreateModel(traceReader);
        await model.RefreshAsync();
        traceReader.Block = true;
        var second = model.Sessions[1];
        var waiting = model.SelectAsync(second);
        await traceReader.Entered.Task;
        await model.SelectAsync(model.Sessions[0]);
        traceReader.Release.SetResult();
        await waiting;
        Assert.Equal("parent", model.Selected!.Session.SessionId);
        Assert.Equal("User", model.Entries[0].Label);
    }

    [Fact]
    public void LargeEntryNavigationShowsThePageContainingTheMatch()
    {
        var text = new string('x', TraceEntryModel.PageSize + 50) + "needle";
        var item = new TraceEntryModel(new TraceEntry(0, TraceEntryKind.ToolResult, "Tool result", text));
        Assert.Equal(TraceEntryModel.PageSize, item.Text.Length);
        item.FocusMatch(TraceEntryModel.PageSize + 50, 6);
        Assert.Equal("2 / 2", item.PageLabel);
        Assert.Equal("needle", item.Text[item.SelectionStart..item.SelectionEnd]);
        Assert.True(item.IsExpanded);
    }

    [AvaloniaFact]
    public async Task WindowRendersTreeAndCheckboxLoadsOnlySelectedFamily()
    {
        using var model = CreateModel();
        await model.RefreshAsync();
        var window = new SessionViewerWindow(model);
        window.Show();
        try
        {
            // Opening triggers a real refresh; wait for that short synthetic operation to finish.
            await UntilAsync(() => !model.IsRefreshing && model.Selected is not null && model.Entries.Count > 0);
            var tree = window.FindControl<TreeView>("SessionTree")!;
            tree.SelectedItem = model.Sessions[0];
            var toggle = window.FindControl<CheckBox>("SubagentsToggle")!;
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => model.Selected!.Root.Children.Count == 1);
            Assert.True(model.ShowSubagents);
            Assert.True(toggle.IsChecked);
            Assert.True(model.Selected!.Root.IsExpanded);
            tree.SelectedItem = model.Selected.Root.Children[0];
            await UntilAsync(() => model.Selected!.Session.SessionId == "reviewer" && model.Entries.Count > 0);
            Assert.True(model.Entries[0].IsTechnical);
            Assert.False(model.Entries[0].IsExpanded);

            // Optional synthetic screenshot for design review, never real conversation content.
            if (Environment.GetEnvironmentVariable("AGENT_SYNC_TEST_SCREENSHOT") is { Length: > 0 } path)
            {
                model.Entries[0].IsExpanded = true;
                using var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
                frame.Save(path);
            }
        }
        finally { window.Close(); }
    }

    private static async Task UntilAsync(Func<bool> predicate)
    {
        for (var attempt = 0; attempt < 200 && !predicate(); attempt++)
        { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.True(predicate(), "The window did not reach the expected state.");
    }

    private static SessionViewerModel CreateModel(FakeTraces? traces = null, ILocalSessionCatalog? catalog = null)
    {
        return new SessionViewerModel(new DesktopSessionServices(catalog ?? new FakeCatalog(), traces ?? new FakeTraces(),
            new FakeFamilies(), new SessionContentReader(), new FakeAnnotations()));
    }

    private static ManagedSession Session(string id, string title) => new(ManagedAgent.Codex, id, id + ".jsonl", title,
        new DateTimeOffset(2026, 9, 14, id == "parent" ? 18 : 17, 0, 0, TimeSpan.Zero), false, true);

    private sealed class FakeCatalog(bool unreadableParent = false) : ILocalSessionCatalog
    {
        public Task<SessionCatalogSnapshot> ScanAsync(CancellationToken cancellationToken) => Task.FromResult(
            new SessionCatalogSnapshot([Session("parent", "Find the project’s current task") with { CanRead = !unreadableParent },
                Session("second", "Review release notes")], []));
    }
    private sealed class FakeFamilies : ISessionFamilyReader
    {
        public Task<SessionThread> ReadAsync(ManagedSession parent, CancellationToken cancellationToken) => Task.FromResult(
            new SessionThread(parent, parent.SessionId == "parent" ? [new SessionThread(Session("reviewer", "Averroes · explorer"), [])] : []));
    }
    private sealed class FakeTraces : ISessionTraceReader
    {
        public bool Block { get; set; }
        public string? InvalidSessionId { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<SessionTrace> ReadAsync(ManagedSession session, CancellationToken cancellationToken)
        {
            if (!session.CanRead) return await new SessionTraceReader().ReadAsync(session, cancellationToken);
            if (session.SessionId == InvalidSessionId) throw new InvalidDataException("Invalid synthetic session.");
            if (Block && session.SessionId == "second") { Entered.SetResult(); await Release.Task; }
            return session.SessionId == "reviewer"
                ? new SessionTrace(session, [new TraceEntry(0, TraceEntryKind.ToolCall, "exec_command",
                    "rg -n authorization src\n\nReviewing the permission checks for the selected operation.", DateTimeOffset.UtcNow, "call-review"),
                    new TraceEntry(1, TraceEntryKind.ToolResult, "exec_command · result", "src/Controllers/ExampleController.cs:42\nRequireAuthenticatedUser();", DateTimeOffset.UtcNow, "call-review"),
                    new TraceEntry(2, TraceEntryKind.Assistant, "Assistant", "The review is complete. The route checks the authenticated caller before dispatching the operation.")], [])
                : new SessionTrace(session, [new TraceEntry(0, TraceEntryKind.User, "User", "Find the current task and inspect the relevant code.")], []);
        }
    }
    private sealed class FakeAnnotations : ISessionAnnotationStore
    {
        public Task<IReadOnlyDictionary<SessionAnnotationKey, SessionAnnotation>> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<SessionAnnotationKey, SessionAnnotation>>(new Dictionary<SessionAnnotationKey, SessionAnnotation>());
        public Task SaveAsync(SessionAnnotationKey key, SessionAnnotation annotation, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(SessionAnnotationKey key, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoActiveSessions : IManagedSessionActiveState
    {
        public Task<IReadOnlySet<string>> GetActiveSessionIdsAsync(ManagedAgent agent, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
        public Task<bool> IsActiveAsync(ManagedAgent agent, string sessionId, string nativePath, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }
}
