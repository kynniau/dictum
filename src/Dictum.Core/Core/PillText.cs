namespace Dictum.Core;

/// <summary>
/// Pill wording (SPEC §7, as redrawn in docs/design/Pill.dc.html) and how long each transient state stays up. The pill
/// shows <see cref="For"/> as its label, plus <see cref="Detail"/> beside it in a lighter weight.
/// </summary>
public static class PillText
{
    public static string For(PillView view, TimeSpan elapsed) => view switch
    {
        PillView.RecordingHold => "Recording · hold",
        PillView.RecordingHandsFree => "Hands-free",
        PillView.Transcribing => "Transcribing…",
        PillView.Polishing => "Polishing…",
        PillView.PolishingSkipped => "Polishing… skipped (long dictation)",
        PillView.Done => "Pasted",
        PillView.Cancelled => "Cancelled",
        PillView.Busy => "Busy",
        PillView.MicError => "Mic error",
        _ => "",
    };

    /// <summary>The lighter text after the label: the hands-free timer, or how many words were pasted.</summary>
    public static string? Detail(PillState state, TimeSpan elapsed) => state.View switch
    {
        PillView.RecordingHandsFree => Timer(elapsed),
        PillView.Done when state.Words > 0 => state.Words == 1 ? "1 word" : $"{state.Words} words",
        _ => null,
    };

    /// <summary>The small hint after the hands-free timer.</summary>
    public const string HandsFreeHint = "tap to stop · Esc cancels";

    /// <summary>"0:42", "12:05": minutes and seconds since capture began.</summary>
    public static string Timer(TimeSpan elapsed) => $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";

    /// <summary>Milliseconds a transient view stays before hiding (or, for Busy, reverting); null = until replaced.</summary>
    public static int? FlashMs(PillView view) => view switch
    {
        PillView.Done => 1000, // "Fades after a second" (Pill.dc.html)
        PillView.Cancelled => 900,
        PillView.Busy => 700,
        PillView.MicError => 1800,
        _ => null,
    };

    public static bool IsRecording(PillView view) => view is PillView.RecordingHold or PillView.RecordingHandsFree;

    public static bool IsProcessing(PillView view) => view is PillView.Transcribing or PillView.Polishing or PillView.PolishingSkipped;
}
