using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CodexHistorySync.Core.Annotations;
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

    private static SessionViewerModel CreateModel(FakeTraces? traces = null)
    {
        return new SessionViewerModel(new DesktopSessionServices(new FakeCatalog(), traces ?? new FakeTraces(),
            new FakeFamilies(), new SessionContentReader(), new FakeAnnotations()));
    }

    private static ManagedSession Session(string id, string title) => new(ManagedAgent.Codex, id, id + ".jsonl", title,
        new DateTimeOffset(2026, 9, 14, id == "parent" ? 18 : 17, 0, 0, TimeSpan.Zero), false, true);

    private sealed class FakeCatalog : ILocalSessionCatalog
    {
        public Task<SessionCatalogSnapshot> ScanAsync(CancellationToken cancellationToken) => Task.FromResult(
            new SessionCatalogSnapshot([Session("parent", "Find the project’s current task"), Session("second", "Review release notes")], []));
    }
    private sealed class FakeFamilies : ISessionFamilyReader
    {
        public Task<SessionThread> ReadAsync(ManagedSession parent, CancellationToken cancellationToken) => Task.FromResult(
            new SessionThread(parent, parent.SessionId == "parent" ? [new SessionThread(Session("reviewer", "Averroes · explorer"), [])] : []));
    }
    private sealed class FakeTraces : ISessionTraceReader
    {
        public bool Block { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<SessionTrace> ReadAsync(ManagedSession session, CancellationToken cancellationToken)
        {
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
}
