using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace CodexHistorySync.Desktop;

/// <summary>Application lifetime for the graphical manager, hosted by the CLI executable.</summary>
public sealed class SessionManagerApp : Application
{
    private DesktopSessionServices? services;

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && services is not null)
        {
            desktop.MainWindow = new SessionManagerWindow(new SessionManagerModel(services));
            desktop.Exit += (_, _) => (services.SearchIndex as IDisposable)?.Dispose();
        }
        base.OnFrameworkInitializationCompleted();
    }

    public static int Run(DesktopSessionServices services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        cancellationToken.ThrowIfCancellationRequested();
        using var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(() =>
        {
            if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
        }));
        return AppBuilder.Configure(() => new SessionManagerApp { services = services })
            .UsePlatformDetect().WithInterFont()
            .StartWithClassicDesktopLifetime([], ShutdownMode.OnMainWindowClose);
    }
}
