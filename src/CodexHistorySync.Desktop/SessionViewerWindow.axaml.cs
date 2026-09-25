using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CodexHistorySync.Core.Annotations;
using CodexHistorySync.Core.Viewing;

namespace CodexHistorySync.Desktop;

public sealed partial class SessionViewerWindow : Window
{
    private readonly SessionViewerModel model;
    private bool initialized;

    public SessionViewerWindow(SessionViewerModel model)
    {
        this.model = model;
        AvaloniaXamlLoader.Load(this);
        DataContext = model;
        model.RevealEntry += entry => Dispatcher.UIThread.Post(() =>
        {
            var transcript = this.FindControl<ListBox>("Transcript")!;
            transcript.SelectedItem = entry;
            transcript.ScrollIntoView(entry);
        }, DispatcherPriority.Loaded);
        model.RevealNode += node => this.FindControl<TreeView>("SessionTree")!.SelectedItem = node;
        Opened += OnOpened;
        Closed += (_, _) => model.Dispose();
        KeyDown += OnWindowKey;
        initialized = true;
    }

    private async void OnOpened(object? sender, EventArgs e) => await model.RefreshAsync();
    private async void OnRefresh(object? sender, RoutedEventArgs e) => await model.RefreshAsync();
    private async void OnFilterChanged(object? sender, TextChangedEventArgs e) { if (initialized) await model.FilterAsync(); }
    private async void OnAgentChanged(object? sender, SelectionChangedEventArgs e) { if (initialized) await model.FilterAsync(false); }
    private async void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!initialized || sender is not TreeView tree || tree.SelectedItem is not SessionNode node) return;
        await model.SelectAsync(node);
    }
    private async void OnSubagents(object? sender, RoutedEventArgs e) => await model.SetSubagentsAsync(!model.ShowSubagents);
    private async void OnSearch(object? sender, RoutedEventArgs e) => await model.SearchAsync();
    private async void OnSearchKey(object? sender, KeyEventArgs e)
    { if (e.Key == Key.Enter) { e.Handled = true; await model.SearchAsync(); } }
    private async void OnPreviousMatch(object? sender, RoutedEventArgs e) => await model.NextMatchAsync(-1);
    private async void OnNextMatch(object? sender, RoutedEventArgs e) => await model.NextMatchAsync(1);
    private async void OnMatchSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: TraceSearchMatch match }) await model.GoToMatchAsync(match);
    }
    private async void OnExport(object? sender, RoutedEventArgs e) => await ChooseExportAsync(false);
    private async void OnExportFamily(object? sender, RoutedEventArgs e) => await ChooseExportAsync(true);
    private async Task ChooseExportAsync(bool family)
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            { Title = family ? "Export conversation and subagents" : "Export conversation", AllowMultiple = false });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } directory) await model.ExportAsync(directory, family);
        }
        catch (Exception exception) when (PlatformFailure(exception)) { model.ReportFailure("Could not select export folder", exception); }
    }
    private async void OnCopySessionId(object? sender, RoutedEventArgs e)
    {
        var id = model.Selected?.Session.SessionId;
        if (string.IsNullOrEmpty(id)) return;
        try
        {
            if (Clipboard is not { } clipboard) throw new InvalidOperationException("Clipboard is unavailable.");
            await clipboard.SetTextAsync(id);
            model.ReportSessionIdCopied();
        }
        catch (Exception exception) when (PlatformFailure(exception)) { model.ReportFailure("Could not copy session id", exception); }
    }
    private async void OnCopyEntry(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Control { DataContext: TraceEntryModel entry } && Clipboard is { } clipboard)
                await clipboard.SetTextAsync(entry.Entry.Text);
        }
        catch (Exception exception) when (PlatformFailure(exception)) { model.ReportFailure("Could not copy text", exception); }
    }
    private void OnPreviousPage(object? sender, RoutedEventArgs e) { if (sender is Control { DataContext: TraceEntryModel entry }) entry.MovePage(-1); }
    private void OnNextPage(object? sender, RoutedEventArgs e) { if (sender is Control { DataContext: TraceEntryModel entry }) entry.MovePage(1); }
    private async void OnEditTitle(object? sender, RoutedEventArgs e) => await EditTitleAsync(null);
    private async void OnSuggestTitle(object? sender, RoutedEventArgs e)
    {
        var draft = await model.GenerateTitleAsync();
        if (draft is not null) await EditTitleAsync(draft);
    }
    private async Task EditTitleAsync(SessionAnnotationDraft? draft)
    {
        if (model.Selected is null) return;
        var title = new TextBox { Text = draft?.Title ?? model.Title, MaxLength = SessionAnnotation.MaximumTitleLength };
        var description = new TextBox { Text = draft?.Description ?? model.Description, MaxLength = SessionAnnotation.MaximumDescriptionLength,
            AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Height = 100 };
        var dialog = Dialog("Title and description", 560, 300);
        var save = new Button { Content = "Save", HorizontalAlignment = HorizontalAlignment.Right };
        save.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(title.Text)) dialog.Close(true); };
        dialog.Content = new StackPanel { Margin = new Thickness(24), Spacing = 12, Children =
        { new TextBlock { Text = "Title" }, title, new TextBlock { Text = "Description" }, description, save } };
        if (await dialog.ShowDialog<bool>(this)) await model.SaveAnnotationAsync(title.Text ?? string.Empty, description.Text ?? string.Empty);
    }
    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        var selected = model.Selected;
        if (selected is null || !model.CanDelete) return;
        var dialog = Dialog("Delete local conversation?", 500, 250);
        var confirm = new Button { Content = "Delete this conversation", HorizontalAlignment = HorizontalAlignment.Right };
        confirm.Click += (_, _) => dialog.Close(true);
        dialog.Content = new StackPanel { Margin = new Thickness(24), Spacing = 20, Children =
        {
            new TextBlock { Text = selected.Title, FontSize = 18, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            new TextBlock { Text = "This deletes the selected local transcript. Subagent files are kept. A later sync may restore it.",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap }, confirm
        } };
        if (await dialog.ShowDialog<bool>(this)) await model.DeleteAsync(selected);
    }
    private async void OnWindowKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
        { this.FindControl<TextBox>("SearchBox")!.Focus(); e.Handled = true; }
        else if (e.Key == Key.F3) { e.Handled = true; await model.NextMatchAsync(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1); }
        else if (e.Key == Key.F5) { e.Handled = true; await model.RefreshAsync(); }
    }
    private static Window Dialog(string title, int width, int height) => new()
    { Title = title, Width = width, Height = height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };

    private static bool PlatformFailure(Exception exception) => exception is IOException or UnauthorizedAccessException or
        InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.COMException;
}
