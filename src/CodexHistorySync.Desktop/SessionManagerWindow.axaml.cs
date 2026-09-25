using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CodexHistorySync.Core.Management;
using CodexHistorySync.Core.Viewing;

namespace CodexHistorySync.Desktop;

public sealed partial class SessionManagerWindow : Window
{
    private readonly SessionManagerModel model;
    private bool initialized;

    public SessionManagerWindow(SessionManagerModel model)
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
        if (!initialized || sender is not ListBox { SelectedItem: ManagedSession session }) return;
        await model.SelectAsync(session);
    }
    private async void OnSearch(object? sender, RoutedEventArgs e) => await model.SearchAsync();
    private async void OnSearchKey(object? sender, KeyEventArgs e)
    { if (e.Key == Key.Enter) { e.Handled = true; await model.SearchAsync(); } }
    private async void OnPreviousMatch(object? sender, RoutedEventArgs e) => await model.NextMatchAsync(-1);
    private async void OnNextMatch(object? sender, RoutedEventArgs e) => await model.NextMatchAsync(1);
    private async void OnMatchSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: TraceSearchMatch match }) await model.GoToMatchAsync(match);
    }
    private void OnPreviousPage(object? sender, RoutedEventArgs e) { if (sender is Control { DataContext: TraceEntryModel entry }) entry.MovePage(-1); }
    private void OnNextPage(object? sender, RoutedEventArgs e) { if (sender is Control { DataContext: TraceEntryModel entry }) entry.MovePage(1); }
    private async void OnCopyEntry(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Control { DataContext: TraceEntryModel entry } && Clipboard is { } clipboard)
                await clipboard.SetTextAsync(entry.Entry.Text);
        }
        catch (Exception ex) when (PlatformFailure(ex)) { model.ReportFailure("Could not copy text", ex); }
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        var selected = model.Selected;
        if (selected is null || !model.CanCopy) return;
        var targets = model.AvailableTargets;
        ManagedAgent? chosen = null;
        string? workingDirectory = null;
        if (targets.Count == 1 && selected.Agent != ManagedAgent.Muse) chosen = targets[0];
        else if (targets.Count > 0)
        {
            var dialog = new Window
            {
                Title = "Copy to…",
                Width = 560,
                Height = selected.Agent == ManagedAgent.Muse ? 440 : 240,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = Avalonia.Media.Brush.Parse("#1D2024")
            };
            var combo = new ComboBox { ItemsSource = targets.Select(t => t.ToString()).ToArray(), SelectedIndex = 0, Margin = new Thickness(0, 0, 0, 16), HorizontalAlignment = HorizontalAlignment.Stretch };
            var project = new TextBox { Watermark = "Project folder on this computer", HorizontalAlignment = HorizontalAlignment.Stretch };
            if (selected.Agent == ManagedAgent.Muse)
            {
                try { project.Text = await model.GetCopyWorkingDirectoryAsync(); }
                catch (OperationCanceledException) { return; }
                if (model.Selected != selected) return;
            }
            var validation = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            var ok = new Button { Content = "Copy", HorizontalAlignment = HorizontalAlignment.Right };
            ok.Click += (_, _) =>
            {
                if (selected.Agent == ManagedAgent.Muse)
                {
                    workingDirectory = project.Text?.Trim();
                    if (string.IsNullOrWhiteSpace(workingDirectory) || !Path.IsPathFullyQualified(workingDirectory) || !Directory.Exists(workingDirectory))
                    { validation.Text = "Select an existing project folder on this computer."; return; }
                }
                dialog.Close(combo.SelectedIndex >= 0 ? targets[combo.SelectedIndex] : null);
            };
            var cancel = new Button { Content = "Cancel", Margin = new Thickness(8, 0, 0, 0) };
            cancel.Click += (_, _) => dialog.Close(null);
            var content = new StackPanel { Margin = new Thickness(24), Spacing = 12, Children =
            {
                new TextBlock { Text = $"Copy \"{selected.Title}\" to:", TextWrapping = Avalonia.Media.TextWrapping.Wrap }, combo
            } };
            if (selected.Agent == ManagedAgent.Muse)
            {
                var browse = new Button { Content = "Choose folder…" };
                browse.Click += async (_, _) =>
                {
                    try
                    {
                        var folders = await dialog.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                        { Title = "Project folder for the copied conversation", AllowMultiple = false });
                        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } directory) project.Text = directory;
                    }
                    catch (Exception ex) when (PlatformFailure(ex)) { validation.Text = ex.Message; }
                };
                content.Children.Add(new TextBlock { Text = "Project folder for the copy (the original session is unchanged):", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
                content.Children.Add(project);
                content.Children.Add(browse);
                content.Children.Add(validation);
            }
            content.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } });
            dialog.Content = content;
            chosen = await dialog.ShowDialog<ManagedAgent?>(this);
        }
        if (chosen is not null && model.Selected == selected) await model.CopyAsync(chosen.Value, workingDirectory);
    }

    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        var selected = model.Selected;
        if (selected is null || !model.CanDelete) return;
        var dialog = Dialog("Delete local conversation?", 520, 240);
        var confirm = new Button { Content = "Delete this conversation", HorizontalAlignment = HorizontalAlignment.Right };
        confirm.Click += (_, _) => dialog.Close(true);
        dialog.Content = new StackPanel { Margin = new Thickness(24), Spacing = 16, Children =
        {
            new TextBlock { Text = selected.Title, FontSize = 16, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            new TextBlock { Text = "This deletes the local transcript only. A later sync may restore it. This cannot be undone.", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Foreground = Avalonia.Media.Brush.Parse("#A5A8AF") },
            confirm
        } };
        if (await dialog.ShowDialog<bool>(this)) await model.DeleteAsync();
    }

    private async void OnWindowKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
        { this.FindControl<TextBox>("SearchBox")!.Focus(); e.Handled = true; }
        else if (e.Key == Key.F3) { e.Handled = true; await model.NextMatchAsync(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1); }
        else if (e.Key == Key.F5) { e.Handled = true; await model.RefreshAsync(); }
        else if (e.Key == Key.C && !e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Meta) && model.CanCopy)
        { e.Handled = true; OnCopy(sender, new RoutedEventArgs()); }
        else if (e.Key == Key.Delete && model.CanDelete)
        { e.Handled = true; OnDelete(sender, new RoutedEventArgs()); }
    }

    private static Window Dialog(string title, int width, int height) => new()
    { Title = title, Width = width, Height = height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };

    private static bool PlatformFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or
        InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.COMException;
}
