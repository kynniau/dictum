using Avalonia.Controls;
using Avalonia.Threading;

namespace Dictum.UI.Views;

/// <summary>
/// Synchronous dialogs for a tray app with no main window: the window is shown and a nested dispatcher frame runs until it
/// closes (Avalonia's ShowDialog needs an owner window and returns a task; the model prompts are synchronous by contract).
/// </summary>
internal static class Modal
{
    public static void Run(Window window)
    {
        var frame = new DispatcherFrame();
        window.Closed += (_, _) => frame.Continue = false;
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.Show();
        window.Activate(); // opened by the user's action or at first run: it takes focus
        Dispatcher.UIThread.PushFrame(frame);
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
