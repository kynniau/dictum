using Avalonia.Controls;
using Avalonia.Threading;
using Dictum.Platform;
using Dictum.UI.Views;

namespace Dictum.UI.Shell;

public sealed partial class AppShell
{
    private FlyoutWindow? _flyout;
    private SettingsWindow? _settingsWindow;
    private CalibrationWindow? _calibration;
    private FixLastWindow? _fixLast;
    private AddWordWindow? _addWord;
    private AddSnippetWindow? _addSnippet;
    private long _flyoutClosedAt;

    /// <summary>A click on the tray icon: opens the panel beside it, or closes it if it is open.</summary>
    public void ToggleFlyout(ScreenPoint? anchor)
    {
        if (_flyout is not null)
        {
            _flyout.Close();
            return;
        }
        // The click that dismissed the panel (it lost focus to the taskbar) must not open it again at once.
        // simplification: a fixed 300 ms window tells that click from a new one. Ceiling: a deliberate second click within
        // 300 ms of the panel closing is ignored. Trigger to retire: a report of clicks being ignored; then compare the
        // tray message's own timestamp with the deactivation instead.
        if (Environment.TickCount64 - _flyoutClosedAt < 300) return;
        _flyout = new FlyoutWindow(this);
        _flyout.Closed += (_, _) =>
        {
            _flyout = null;
            _flyoutClosedAt = Environment.TickCount64;
        };
        _flyout.ShowNear(anchor);
    }

    /// <summary>Settings, opened on purpose: it takes focus. <paramref name="page"/> picks the section ("Storage", "Clean-up"…).</summary>
    public void OpenSettings(string? page = null)
    {
        _flyout?.Close();
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(this);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        if (page is not null) _settingsWindow.ShowPage(page);
        _settingsWindow.Activate(); // the opener activates it; the window never activates itself
    }

    /// <summary>Calibrate my voice (SPEC §16.5): opened on purpose, so it takes focus.</summary>
    public void OpenCalibration()
    {
        _flyout?.Close();
        if (_calibration is null)
        {
            _calibration = new CalibrationWindow(this);
            _calibration.Closed += (_, _) => _calibration = null;
            _calibration.Show();
        }
        _calibration.Activate();
    }

    /// <summary>
    /// Fix last dictation (SPEC §16.3). From the fix_hotkey it waits until no dictation is recording or processing, because a
    /// window taking focus then would receive the paste.
    /// </summary>
    public void OpenFixLast(bool fromHotkey = false)
    {
        if (fromHotkey && !DictationIdle)
        {
            _log.Info("fix_hotkey ignored: a dictation is in progress.");
            return;
        }
        _flyout?.Close();
        if (Personal.Last is not { } last)
        {
            Notify("Dictum", "Nothing has been dictated since Dictum started (the last dictation is kept in memory only).", warning: false);
            return;
        }
        if (_fixLast is null)
        {
            _fixLast = new FixLastWindow(this, last);
            _fixLast.Closed += (_, _) => _fixLast = null;
            _fixLast.Show();
        }
        _fixLast.Activate();
    }

    /// <summary>Dictionary → Add word… (SPEC §16.2): a small window opened on purpose.</summary>
    public void OpenAddWord()
    {
        _flyout?.Close();
        if (_addWord is null)
        {
            _addWord = new AddWordWindow(this);
            _addWord.Closed += (_, _) => _addWord = null;
            _addWord.Show();
        }
        _addWord.Activate();
    }

    /// <summary>Tray panel → Snippets, or "Add a snippet…" in the dictionary window (SPEC §16.6).</summary>
    public void OpenAddSnippet()
    {
        _flyout?.Close();
        if (_addSnippet is null)
        {
            _addSnippet = new AddSnippetWindow(this);
            _addSnippet.Closed += (_, _) => _addSnippet = null;
            _addSnippet.Show();
        }
        _addSnippet.Activate();
    }

    private void CloseWindows()
    {
        foreach (var window in new Window?[] { _flyout, _settingsWindow, _calibration, _fixLast, _addWord, _addSnippet })
        {
            try
            {
                window?.Close();
            }
            catch (InvalidOperationException)
            {
                // already closing
            }
        }
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread now or soon, never waiting.</summary>
    internal static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }
}
