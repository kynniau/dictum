using Dictum.Insert;
using Dictum.Mac.Interop;

namespace Dictum.Mac.Input;

/// <summary>
/// Posts keyboard events with Core Graphics: Cmd+V for the paste, key-ups for held modifiers, and Unicode typing.
/// <para>
/// Every event comes from a private event source (kCGEventSourceStatePrivate), so its flags are exactly the ones set here
/// and not merged with modifiers the user is still holding (recalled, not verified): Cmd+V stays Cmd+V while the hotkey's
/// Option is down, and typed text never turns into shortcuts. Events are posted at kCGHIDEventTap, the point where hardware
/// events enter the window server, so they pass through every session tap including Dictum's own, which skip them by
/// <see cref="EventTap.InjectedMarker"/> in kCGEventSourceUserData. Posting there needs no root (only creating a tap there
/// does, CGEvent.h) but does need Accessibility; without it macOS drops the events silently (recalled), so each send
/// checks first and logs instead.
/// </para>
/// </summary>
internal sealed class CGEventSender(ILog log, Func<bool>? trusted = null) : IInputSender
{
    /// <summary>
    /// UTF-16 units per typed event.
    /// simplification: 20 units per event. Ceiling: apps are recalled to read at most 20 characters from one event's Unicode
    /// string, so a larger chunk could drop text; 20 makes long text take many events. Trigger to retire: a measurement on
    /// real hardware showing a larger chunk arrives whole in common apps.
    /// </summary>
    internal const int ChunkUnits = 20;

    private readonly Func<bool> _trusted = trusted ?? AX.IsProcessTrusted;

    /// <summary>One keyboard event to post: a Mac key code, down or up, the flags it carries, and optional text replacing what the key would type.</summary>
    internal readonly record struct KeyEvent(ushort Key, bool Down, ulong Flags, string? Text = null);

    public void ReleaseModifiers()
    {
        var down = KeyCodeMap.ModifierKeys.Where(KeyState.IsMacKeyDown).ToList();
        if (down.Count > 0) Post([.. down.Select(key => new KeyEvent(key, false, 0))], "release modifiers");
    }

    public void SendCtrlV() => Post(CmdV, "Cmd+V");

    /// <summary>Command down, V down, V up, Command up; Command's flag on all but the last.</summary>
    internal static readonly KeyEvent[] CmdV =
    [
        new(KeyCodeMap.MacCommand, true, CG.FlagCommand),
        new(KeyCodeMap.MacV, true, CG.FlagCommand),
        new(KeyCodeMap.MacV, false, CG.FlagCommand),
        new(KeyCodeMap.MacCommand, false, 0),
    ];

    public async Task TypeTextAsync(string text)
    {
        foreach (var batch in TypingEvents(text))
        {
            if (!Post(batch, "typing")) return;
            await Task.Delay(5); // lets slow apps drain their input queue, as the Windows sender does
        }
    }

    /// <summary>
    /// The events that type <paramref name="text"/>, in batches: each batch is one chunk of at most <see cref="ChunkUnits"/>
    /// UTF-16 units as a key down and up carrying that text (key code 0, no flags), or a Return down and up for a line
    /// break ("\r\n", "\r" and "\n" each count once). A surrogate pair is never split across chunks.
    /// </summary>
    internal static IEnumerable<KeyEvent[]> TypingEvents(string text)
    {
        var chunk = new System.Text.StringBuilder(ChunkUnits);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') continue;
            if (c is '\n' or '\r')
            {
                if (chunk.Length > 0) yield return Chunk(chunk);
                yield return [new(KeyCodeMap.MacReturn, true, 0), new(KeyCodeMap.MacReturn, false, 0)];
                continue;
            }
            if (chunk.Length == ChunkUnits || (chunk.Length == ChunkUnits - 1 && char.IsHighSurrogate(c))) yield return Chunk(chunk);
            chunk.Append(c);
        }
        if (chunk.Length > 0) yield return Chunk(chunk);
    }

    private static KeyEvent[] Chunk(System.Text.StringBuilder chunk)
    {
        var text = chunk.ToString();
        chunk.Clear();
        return [new(0, true, 0, text), new(0, false, 0, text)];
    }

    /// <summary>Posts the events in order; false (and a log line) when nothing could be posted.</summary>
    private unsafe bool Post(IReadOnlyList<KeyEvent> events, string what)
    {
        if (!_trusted())
        {
            // PROPOSED (log line, 22/10/2026).
            log.Warn($"{what} not sent: Dictum is not allowed to control this Mac yet (System Settings > Privacy & Security > Accessibility).");
            return false;
        }
        var source = CG.EventSourceCreate(CG.SourceStatePrivate);
        try
        {
            for (var i = 0; i < events.Count; i++)
            {
                var e = events[i];
                var ev = CG.EventCreateKeyboardEvent(source, e.Key, e.Down);
                if (ev == 0)
                {
                    log.Warn($"Posting ({what}) stopped after {i} of {events.Count} events: CGEventCreateKeyboardEvent returned null.");
                    return i > 0;
                }
                try
                {
                    CG.EventSetFlags(ev, e.Flags);
                    if (e.Text is { } text)
                    {
                        fixed (char* p = text) CG.EventKeyboardSetUnicodeString(ev, (nuint)text.Length, p);
                    }
                    CG.EventSetIntegerValueField(ev, CG.FieldSourceUserData, EventTap.InjectedMarker);
                    CG.EventPost(CG.HidEventTap, ev);
                }
                finally
                {
                    CF.Release(ev);
                }
            }
            return true;
        }
        finally
        {
            if (source != 0) CF.Release(source);
        }
    }
}
