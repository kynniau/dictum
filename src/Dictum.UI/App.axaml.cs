using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Dictum.UI;

/// <summary>
/// The Avalonia application: the Fluent theme and Dictum's tokens. It has no main window: the tray icon and its panel are
/// the app, so the lifetime shuts down only when Quit asks it to. <see cref="Started"/> runs once the framework is up
/// (the platform's entry point builds the shell there); a headless test session never sets it.
/// </summary>
public partial class App : Application
{
    /// <summary>Runs on the UI thread once Avalonia has started, with the desktop lifetime to shut down through.</summary>
    public Action<IClassicDesktopStyleApplicationLifetime>? Started { get; init; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Started?.Invoke(desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
