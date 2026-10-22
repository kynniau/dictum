using Dictum.Llm;

namespace Dictum.Core;

/// <summary>A step of one recording's journey that <see cref="DictationPipeline.DemonstrateAsync"/> reports as it finishes.</summary>
public enum PipelineStep
{
    /// <summary>The journey stopped here: the recording gate, or no words heard (<see cref="PipelineProgress.Reason"/> says which).</summary>
    Discarded,

    /// <summary>The speech model's raw text (empty when it heard no words), timed as the log times it.</summary>
    Transcribed,

    /// <summary>The clean-up in use has run: its text, its result (accepted, or why not) and its time, as logged.</summary>
    CleanedUp,

    /// <summary>After corrections, the profanity filter and snippets: what a dictation would paste.</summary>
    Finished,
}

/// <summary>
/// One step's result (SPEC §29). Every figure is the pipeline's own: <see cref="Ms"/> is the very value the log line for
/// that step carries, never a second clock.
/// </summary>
/// <param name="Text">The step's text: the raw transcript, the cleaned text, or the finished text; empty for Discarded.</param>
/// <param name="Ms">Milliseconds the step took (Transcribed, CleanedUp); null where the pipeline does not time the step.</param>
/// <param name="Reason">Why the journey stopped (Discarded): the recording gate's reason or <see cref="DictationPipeline.NoSpeech"/>.</param>
/// <param name="Raw">The raw transcript the clean-up was given (CleanedUp).</param>
/// <param name="Cleanup">The clean-up's result (CleanedUp): accepted, or the validator's or provider's reason it was not.</param>
/// <param name="Cleaner">The clean-up that ran (CleanedUp): whether a model took part, its status line, whether it sends text.</param>
public sealed record PipelineProgress(PipelineStep Step, string Text, long? Ms = null, string? Reason = null, string? Raw = null,
    CleanupResult? Cleanup = null, TranscriptCleaner? Cleaner = null);
