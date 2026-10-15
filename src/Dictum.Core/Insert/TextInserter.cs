using Dictum.Core;
using Dictum.Settings;

namespace Dictum.Insert;

/// <summary>Text clipboard access. Snapshots cover text formats only (images/files are not restored).</summary>
public interface IClipboard
{
    ClipboardSnapshot Snapshot();

    /// <summary>Sets the clipboard to <paramref name="text"/>, retrying while another app holds it open.</summary>
    /// <param name="keepOutOfHistory">Also mark the item so Windows keeps it out of clipboard history (Win+V) and cloud clipboard sync.</param>
    void SetText(string text, bool keepOutOfHistory);

    void Restore(ClipboardSnapshot snapshot);
}

public sealed record ClipboardSnapshot(IReadOnlyDictionary<string, object> Formats)
{
    public static readonly ClipboardSnapshot Empty = new(new Dictionary<string, object>());
}

/// <summary>Synthetic keyboard input. All injected events carry Dictum's marker so our hook ignores them.</summary>
public interface IInputSender
{
    /// <summary>Sends key-ups for any Ctrl/Shift/Alt/Win the system believes is down.</summary>
    void ReleaseModifiers();

    /// <summary>Ctrl down, V down, V up, Ctrl up.</summary>
    void SendCtrlV();

    /// <summary>KEYEVENTF_UNICODE typing in small batches; "\n" becomes Enter.</summary>
    Task TypeTextAsync(string text);
}

/// <summary>
/// Inserts text at the cursor of whatever window is in the foreground at insert time (SPEC §6).
/// Never forces the foreground. The shell runs it on the UI thread, one paste at a time.
/// </summary>
public sealed class TextInserter
{
    private readonly IClipboard _clipboard;
    private readonly IInputSender _input;
    private readonly Func<int, Task> _delay;
    private readonly ILog _log;

    public TextInserter(IClipboard clipboard, IInputSender input, ILog log, Func<int, Task>? delay = null)
    {
        _clipboard = clipboard;
        _input = input;
        _log = log;
        _delay = delay ?? (ms => Task.Delay(ms));
    }

    /// <summary>
    /// Inserts <paramref name="text"/>. <paramref name="cancellationToken"/> is checked immediately before the
    /// first keystroke; once input has been sent the insert completes.
    /// </summary>
    public async Task InsertAsync(string text, InsertSettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(text)) return;
        var payload = settings.TrailingSpace && !char.IsWhiteSpace(text[^1]) ? text + " " : text;

        if (settings.Method == InsertSettings.Type)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _input.ReleaseModifiers();
            await _input.TypeTextAsync(payload);
            return;
        }

        var snapshot = settings.RestoreClipboard ? TrySnapshot() : null;
        cancellationToken.ThrowIfCancellationRequested();
        _input.ReleaseModifiers();
        _clipboard.SetText(payload, keepOutOfHistory: !settings.ClipboardHistory);
        _input.SendCtrlV();
        await _delay(settings.PasteDelayMs);
        if (snapshot is not null)
        {
            try
            {
                _clipboard.Restore(snapshot);
            }
            catch (Exception ex)
            {
                _log.Warn($"Clipboard restore failed: {ex.GetType().Name}. The transcript was left on the clipboard.");
            }
        }
    }

    /// <summary>A clipboard another app holds open (or odd data in it) must never cost the user their transcript.</summary>
    private ClipboardSnapshot? TrySnapshot()
    {
        try
        {
            return _clipboard.Snapshot();
        }
        catch (Exception ex)
        {
            _log.Warn($"Clipboard snapshot failed ({ex.GetType().Name}); pasting without restoring the previous clipboard.");
            return null;
        }
    }
}
