namespace Dictum.Hotkey;

public enum HotkeySignal { None, Down, Up, Escape }

/// <param name="TimestampMs">Environment.TickCount64 when the listener saw the key (the state machine's clock).</param>
/// <param name="Stamp">Stopwatch.GetTimestamp() at the same moment (0 when unknown), for capture latency on the audio clock.</param>
public readonly record struct HookEvent(HotkeySignal Signal, long TimestampMs, long Stamp = 0);

/// <param name="Suppress">Swallow this key event (do not pass it on to the focused app).</param>
/// <param name="Signal">What to post to the state machine.</param>
/// <param name="NeedsMenuMask">Inject the menu-mask key now so a held Win/Alt does not open Start or a menu bar.</param>
public readonly record struct KeyDecision(bool Suppress, HotkeySignal Signal, bool NeedsMenuMask)
{
    public static readonly KeyDecision Pass = new(false, HotkeySignal.None, false);

    /// <summary>Whether the listener passes this key on to the focused app rather than swallowing it. SPEC §3: Esc always reaches the app.</summary>
    public bool PassesThrough => !Suppress || Signal == HotkeySignal.Escape;
}

/// <summary>
/// Pure virtual-key matching for the low-level hook. Fed every physical key event; decides whether the
/// event completes or releases the hotkey and whether to swallow it. No I/O and no blocking, so it is
/// safe to call from inside the hook callback.
/// Suppression rules (SPEC §3): Esc is never suppressed; a single-key hotkey is suppressed when
/// <c>suppress_hotkey</c> is on; in a chord only the final non-modifier key is suppressed, never a bare
/// modifier, and only while the chord is complete. A mouse-button trigger is suppressed whatever
/// <c>suppress_hotkey</c> says: its click reaching the app under the pointer on release (a context menu, a browser
/// Back) would wreck the dictation. Auto-repeat never produces a signal.
/// A listener switched off (<c>enabled</c> false) starts no new press, but a press already under way finishes as it
/// began: its auto-repeat and release stay swallowed if its press was, and its release reports Up once. The listener
/// can go off mid-press (a mouse hotkey's readiness gate closes as a dictation ends, which can be while its stop tap
/// is still down), and until the state machine hears Up it believes the key is held and takes the next press for
/// auto-repeat.
/// </summary>
public sealed class HotkeyMatcher
{
    private readonly HashSet<int> _down = [];
    private readonly Func<int, bool>? _isPhysicallyDown;
    private bool _active; // Down reported, Up not yet: owed whether or not the listener is still on (a lost key-up drops it)
    private int _activeTriggerVk;
    private bool _triggerSuppressed;

    /// <param name="isPhysicallyDown">
    /// Optional probe (GetAsyncKeyState in production) used to drop modifiers we believe are held but are not,
    /// e.g. after a key-up was lost to the secure desktop. Never called for the key in the current event.
    /// </param>
    public HotkeyMatcher(HotkeySpec spec, bool suppress, Func<int, bool>? isPhysicallyDown = null)
    {
        Spec = spec;
        SuppressEnabled = suppress;
        _isPhysicallyDown = isPhysicallyDown;
    }

    public HotkeySpec Spec { get; }
    public bool SuppressEnabled { get; }

    /// <param name="eventTimeMs">The event's own time (KBDLLHOOKSTRUCT.time, ms), or -1 when unknown (no late-repeat check).</param>
    public KeyDecision OnKey(int vk, bool isDown, bool enabled, long eventTimeMs = -1)
    {
        var wasDown = _down.Contains(vk);
        if (isDown) _down.Add(vk); else _down.Remove(vk);
        var repeat = isDown && wasDown;
        if (repeat && LateRepeat(vk, eventTimeMs))
        {
            // Its key-up was lost (released on the secure desktop: UAC, Win+L), so this is a fresh press.
            repeat = false;
            if (vk == _activeTriggerVk) (_active, _triggerSuppressed) = (false, false);
        }
        if (isDown && eventTimeMs >= 0) _lastDownMs[vk] = eventTimeMs;

        if (vk == Vk.Escape)
        {
            // Passive watch: always passed through so the focused app still gets Esc.
            return new KeyDecision(false, isDown && !repeat && enabled ? HotkeySignal.Escape : HotkeySignal.None, false);
        }

        var isTrigger = Spec.Trigger.Contains(vk);
        var isChordModifier = Spec.Modifiers.Any(m => m.Contains(vk));

        if (isDown)
        {
            if (repeat) return new KeyDecision(_triggerSuppressed && vk == _activeTriggerVk, HotkeySignal.None, false);
            // Switched off: no new press. A press under way is finished below whether the listener is on or off.
            if (!enabled || _active || !Completes(vk, isTrigger, isChordModifier)) return KeyDecision.Pass;

            _active = true;
            _activeTriggerVk = vk;
            // Single key: suppressible even if it is a modifier (default RightCtrl). Chord: only a non-modifier trigger.
            // A mouse button is always swallowed (class summary).
            var suppress = (SuppressEnabled || Vk.IsMouseButton(vk)) && isTrigger && (!Spec.IsChord || !Spec.Trigger.IsModifier);
            _triggerSuppressed = suppress;
            var mask = !suppress && ChordHoldsWinOrAlt();
            return new KeyDecision(suppress, HotkeySignal.Down, mask);
        }

        if (_triggerSuppressed && vk == _activeTriggerVk)
        {
            _triggerSuppressed = false;
            var signal = _active ? HotkeySignal.Up : HotkeySignal.None;
            _active = false;
            return new KeyDecision(true, signal, false);
        }

        if (_active && (vk == _activeTriggerVk || (Spec.IsChord && (isTrigger || isChordModifier))))
        {
            // Any chord key released ends the hold.
            _active = false;
            return new KeyDecision(false, HotkeySignal.Up, false);
        }
        return KeyDecision.Pass;
    }

    private bool Completes(int vk, bool isTrigger, bool isChordModifier)
    {
        if (!Spec.IsChord) return isTrigger;
        if (Spec.AllModifiers) return (isTrigger || isChordModifier) && Spec.AllKeys.All(g => Held(g, vk));
        return isTrigger && Spec.Modifiers.All(g => Held(g, vk));
    }

    private bool Held(KeyGroup group, int currentVk)
    {
        foreach (var v in group.Vks)
        {
            if (v == currentVk) return true;
            if (!_down.Contains(v)) continue;
            if (_isPhysicallyDown is null || _isPhysicallyDown(v)) return true;
            _down.Remove(v); // stale: we missed its key-up
        }
        return false;
    }

    private bool ChordHoldsWinOrAlt() => Spec.AllKeys.SelectMany(g => g.Vks).Any(Vk.IsWinOrAlt);

    // simplification: a key-down for a key we think is held counts as a fresh press when its previous down event is more
    // than 1.2 s old. Ceiling: Windows' longest keyboard delay setting is 1 s (recalled, not verified) and auto-repeat then
    // follows every ~33 ms, so a real repeat is never that late; a held key whose repeat stopped because another key was
    // pressed sends no more downs, so it cannot trip this. Trigger to retire: a report of a phantom press on a slow machine,
    // or a hook API that reports lost key-ups; then probe GetAsyncKeyState for the trigger as Held does for modifiers.
    private const long LateRepeatMs = 1_200;

    private readonly Dictionary<int, long> _lastDownMs = [];

    private bool LateRepeat(int vk, long eventTimeMs) =>
        eventTimeMs >= 0 && _lastDownMs.TryGetValue(vk, out var last) && unchecked((uint)eventTimeMs - (uint)last) > LateRepeatMs;
}
