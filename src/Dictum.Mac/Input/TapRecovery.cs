namespace Dictum.Mac.Input;

/// <summary>
/// What a tap does when <see cref="TapLiveness"/> says keys stopped reaching it (orchestrator decision, 29/10/2026 22:16 AEDT).
/// The counts alone cannot tell a revoked grant from keys another tap or macOS took (a healthy listener was removed at 21:50:19
/// that day), and a probe Dictum posts itself is delivered to its own tap even while macOS holds the hardware keys (22:11:55), so
/// the tap confirms the only way that proved honest on this Mac: it removes itself, which frees the keys, then asks for a fresh tap.
/// <c>CGEventTapCreate</c> was refused after a real revocation while the Accessibility check still said allowed
/// (dictum-20261029.log 08:10:24), so a refusal is the grant gone, and a fresh tap is a false positive to keep.
/// </summary>
internal sealed class TapRecovery
{
    /// <summary>
    /// simplification: a second trigger this soon after a fresh tap is taken as the grant gone without asking again. Ceiling: a
    /// revoked process whose create still succeeds holds the keys for up to two trigger cycles (about 2 s) before it stops. Trigger
    /// to retire: the per-tick diagnostic (DICTUM_TAP_DIAG) showing a signal that flips reliably at revocation.
    /// </summary>
    internal static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    private long _recreatedMs = long.MinValue;

    /// <summary>On a trigger: whether to ask for a fresh tap, or to stay removed and report the grant gone.</summary>
    public bool MayRecreate(long nowMs) => _recreatedMs == long.MinValue || nowMs - _recreatedMs > (long)Window.TotalMilliseconds;

    /// <summary>A fresh tap was created: the next trigger inside <see cref="Window"/> is not asked again.</summary>
    public void Recreated(long nowMs) => _recreatedMs = nowMs;
}

/// <summary>
/// How many events Dictum's own taps have swallowed (the hotkey, when it is set to be suppressed; the fix-last hotkey). The HID
/// system counted them and no app ever receives them, so the session counter stays flat for them: without this, two quick
/// hands-free dictations with no typing between read as keys held (merge lens on fe03270, 29/10/2026 23:50 AEDT). One per
/// platform, shared by every tap, because the session counter is one count for all of them.
/// </summary>
internal sealed class SwallowedEvents
{
    private long _count;

    public long Count => Interlocked.Read(ref _count);

    public void Add(int events) => Interlocked.Add(ref _count, events);
}

/// <summary>
/// How many key events Dictum has posted itself (the paste's Cmd-V, typed text, released modifiers). The HID system counts them
/// with the hardware keys, and they need not reach Dictum's own tap, so the liveness check subtracts them (a healthy listener
/// read as starved one second after a paste, 29/10/2026 22:10:12 AEDT). One per platform, shared by the sender and every tap.
/// </summary>
internal sealed class PostedEvents
{
    private long _count;

    public long Count => Interlocked.Read(ref _count);

    public void Add(int events) => Interlocked.Add(ref _count, events);
}
