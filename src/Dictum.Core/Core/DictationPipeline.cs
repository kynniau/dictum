using System.Diagnostics;
using Dictum.Audio;
using Dictum.Llm;
using Dictum.Settings;
using Dictum.Transcribe;

namespace Dictum.Core;

/// <summary>
/// One recording's journey (SPEC §4): gate → transcribe → optional cleanup → insert. In hands-free each part of the
/// recording takes the same journey (<see cref="HandsFreePart"/>): whisper and the clean-up also hear the closing words of
/// what the last part said as context (never its finished text: corrections, the profanity filter and snippets run after),
/// and the part is typed after the last one by the boundary rule (<see cref="SegmentJoin"/>).
/// Everything honours the session's CancellationToken; a cancelled session inserts nothing.
/// </summary>
public sealed class DictationPipeline
{
    private readonly Func<CancellationToken, Task<ITranscriber>> _transcriber;
    private readonly Func<TranscriptCleaner> _cleaner;
    private readonly Func<string, CancellationToken, Task> _insert;
    private readonly Func<AppSettings> _settings;
    private readonly ILog _log;
    private readonly Func<string, string> _finish;
    private readonly Action<string> _inserted;
    private readonly Func<IEnumerable<string>> _names;

    /// <param name="transcriber">Returns the loaded engine, waiting for a model load still in progress.</param>
    /// <param name="cleaner">Current cleanup configuration (rebuilt on settings reload).</param>
    /// <param name="insert">Inserts text at the cursor; checks the token before the first keystroke.</param>
    /// <param name="finish">The last step before the paste (SPEC §22 order: corrections, the profanity filter, then snippets); identity if null.</param>
    /// <param name="inserted">Told the text that was pasted (kept in memory for "Fix last dictation"; in hands-free, each part's own text).</param>
    /// <param name="names">The personal dictionary's terms: a hands-free part that goes on with a sentence keeps the capital of one spelt with a capital; none if null.</param>
    public DictationPipeline(
        Func<CancellationToken, Task<ITranscriber>> transcriber,
        Func<TranscriptCleaner> cleaner,
        Func<string, CancellationToken, Task> insert,
        Func<AppSettings> settings,
        ILog log,
        Func<string, string>? finish = null,
        Action<string>? inserted = null,
        Func<IEnumerable<string>>? names = null)
    {
        _transcriber = transcriber;
        _cleaner = cleaner;
        _insert = insert;
        _settings = settings;
        _log = log;
        _finish = finish ?? (text => text);
        _inserted = inserted ?? (_ => { });
        _names = names ?? (() => []);
    }

    /// <param name="insertedWords">Told how many words were pasted (for the "Pasted · 38 words" pill).</param>
    /// <param name="part">A hands-free part (its context and the boundary with the last part typed), or null for a whole recording.</param>
    public async Task<ProcessingOutcome> RunAsync(CapturedAudio audio, CancellationToken token, Action<PillView> showPill, Action<int>? insertedWords = null,
        HandsFreePart? part = null)
    {
        var settings = _settings();
        var reject = RecordingGate.RejectReason(audio, settings.MinRecordingMs, settings.SilenceThreshold);
        if (reject is not null)
        {
            _log.Info(part is null ? $"Recording discarded: {reject}." : $"Segment {part.Number} discarded: {reject}.");
            return ProcessingOutcome.Discarded;
        }

        var sw = Stopwatch.StartNew();
        var engine = await _transcriber(token).ConfigureAwait(false);
        var carried = part?.Carried; // the last part's closing words: context for whisper and the clean-up, never typed again
        var raw = carried is null
            ? await engine.TranscribeAsync(audio.Samples, token).ConfigureAwait(false)
            : await engine.TranscribeAsync(audio.Samples, carried, token).ConfigureAwait(false);
        var transcribeMs = sw.ElapsedMilliseconds;
        token.ThrowIfCancellationRequested();
        if (raw.Length == 0)
        {
            _log.Info($"Transcribed {audio.Duration.TotalSeconds:0.0} s of audio in {transcribeMs} ms: no speech recognised.");
            return ProcessingOutcome.Discarded;
        }
        _log.Info($"Transcribed {audio.Duration.TotalSeconds:0.0} s of audio in {transcribeMs} ms ({raw.Length} chars).");
        if (settings.LogTranscripts) _log.Info($"Raw transcript: {raw}");

        var cleaner = _cleaner();
        if (cleaner.UsesModel && !token.IsCancellationRequested) showPill(cleaner.SkipReason(raw) is null ? PillView.Polishing : PillView.PolishingSkipped);
        sw.Restart();
        var cleaned = await cleaner.CleanAsync(raw, token, carried).ConfigureAwait(false);
        var text = cleaned.Text;
        _log.Info($"Cleanup by {(cleaned.Cleaned ? "the model" : "the rule-based tidy")} in {sw.ElapsedMilliseconds} ms.");
        if (settings.LogTranscripts) _log.Info($"Cleaned transcript: {text}");
        // The boundary rule decides the first letter before corrections, the profanity filter and snippets run, so a snippet
        // is still pasted exactly as written; the separator goes in front of the finished text.
        var separator = "";
        if (part?.Before is { } before && text.Length > 0)
        {
            (separator, text) = SegmentJoin.Join(before, raw, text, settings.Insert.TrailingSpace, _names());
        }
        var spoken = text; // what was said: the next part's carried words come from this, never from the finished text below
        var finished = _finish(text);
        if (finished != text)
        {
            _log.Info("Corrections, the profanity filter or snippets changed the text."); // never the text itself
            if (settings.LogTranscripts) _log.Info($"Final text: {finished}");
            text = finished;
        }

        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text)) return ProcessingOutcome.Discarded;
        await _insert(separator + text, token).ConfigureAwait(false);
        _log.Info($"Inserted {separator.Length + text.Length} chars via {settings.Insert.Method}.");
        _inserted(text);
        if (part is not null) (part.Typed, part.Spoken) = (text, spoken);
        insertedWords?.Invoke(text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);
        return ProcessingOutcome.Inserted;
    }
}
