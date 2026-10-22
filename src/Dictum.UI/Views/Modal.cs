using Avalonia.Controls;
using Avalonia.Threading;

namespace Dictum.UI.Views;

/// <summary>
/// Synchronous dialogs for a tray app with no main window: the window is shown and a nested dispatcher frame runs until it
/// closes (the model prompts are synchronous by contract, and Avalonia's ShowDialog returns a task).
/// </summary>
internal static class Modal
{
    /// <summary>
    /// The window whose button's action is running now (<see cref="Ui.Button"/> sets it for the length of the action), or null.
    /// Run consumes it, so a job the dispatcher runs inside the dialog's frame (a prompt from another thread) never sees it.
    /// </summary>
    private static Window? _opener;

    /// <summary>
    /// Runs <paramref name="action"/>, a click on a button in <paramref name="opener"/>, so that a dialog it opens is owned by
    /// that window (<see cref="Run"/>).
    /// </summary>
    public static void From(Window? opener, Action action)
    {
        var previous = _opener;
        _opener = opener;
        try
        {
            action();
        }
        finally
        {
            _opener = previous;
        }
    }

    /// <summary>
    /// Shows <paramref name="window"/> and waits, with the UI running, until it closes. Opened by a click on a window's button,
    /// it is that window's modal dialog (ShowDialog, centred on it): Avalonia raises Click inside the button's pointer-released
    /// handler, before the opener releases the mouse capture, and a window shown on its own would get no mouse input until it
    /// closed (probe, 23/10/2026: GetCapture() was still the opener inside the frame). Disabling the owner for the dialog
    /// releases the capture, keeps a second click on the opener from opening another dialog, and gives the owner back its
    /// focus when the dialog closes. With no opener (first run, a prompt from the tray or another thread), or when the opener
    /// is the tray panel (it closes itself when it loses focus, which would close its dialog too), the window is shown on its
    /// own, centred on the screen.
    /// </summary>
    public static void Run(Window window)
    {
        var opener = _opener;
        var owner = opener is { IsVisible: true } and not FlyoutWindow ? opener : null;
        if (owner?.OwnedWindows.FirstOrDefault(w => w is DialogWindow { IsVisible: true }) is { } open)
        {
            open.Activate(); // a second click on the opener while its dialog is up (the disabled owner stops the mouse; this stops any other way in) answers as Cancel
            return;
        }
        var frame = new DispatcherFrame();
        window.Closed += (_, _) => frame.Continue = false;
        if (owner is not null)
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            _ = window.ShowDialog(owner);
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.Show();
            window.Activate(); // opened by the user's action or at first run: it takes focus
        }
        _opener = null;
        try
        {
            Dispatcher.UIThread.PushFrame(frame);
        }
        finally
        {
            _opener = opener; // a second dialog from the same click (the two warnings of Remove Dictum completely)
        }
    }

    /// <summary>Runs <paramref name="ask"/> on the UI thread, waiting for it from any other thread.</summary>
    public static T OnUi<T>(Func<T> ask) => Dispatcher.UIThread.CheckAccess() ? ask() : Dispatcher.UIThread.Invoke(ask);

    /// <summary>Waits for <paramref name="task"/> while the UI keeps running (the system file chooser).</summary>
    public static T Wait<T>(Task<T> task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => Dispatcher.UIThread.Post(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.UIThread.PushFrame(frame);
        }
        return task.GetAwaiter().GetResult();
    }
}
