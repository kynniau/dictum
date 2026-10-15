using System.Diagnostics;
using Dictum.Audio;
using Dictum.Llm;
using Dictum.Settings;
using Dictum.Transcribe;

namespace Dictum.Core;

/// <summary>
/// One recording's journey (SPEC §4): gate → transcribe → optional cleanup → insert.
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

    /// <param name="transcriber">Returns the loaded engine, waiting for a model load still in progress.</param>
    /// <param name="cleaner">Current cleanup configuration (rebuilt on settings reload).</param>
    /// <param name="insert">Inserts text at the cursor; checks the token before the first keystroke.</param>
    /// <param name="finish">The last step before the paste (SPEC §22 order: corrections, then the profanity filter); identity if null.</param>
    /// <param name="inserted">Told the text that was pasted (kept in memory for "Fix last dictation").</param>
    public DictationPipeline(
        Func<CancellationToken, Task<ITranscriber>> transcriber,
        Func<TranscriptCleaner> cleaner,
        Func<string, CancellationToken, Task> insert,
        Func<AppSettings> settings,
        ILog log,
        Func<string, string>? finish = null,
        Action<string>? inserted = null)
    {
        _transcriber = transcriber;
        _cleaner = cleaner;
        _insert = insert;
        _settings = settings;
        _log = log;
        _finish = finish ?? (text => text);
        _inserted = inserted ?? (_ => { });
    }

    /// <param name="insertedWords">Told how many words were pasted (for the "Pasted · 38 words" pill).</param>
    public async Task<ProcessingOutcome> RunAsync(CapturedAudio audio, CancellationToken token, Action<PillView> showPill, Action<int>? insertedWords = null)
    {
        var settings = _settings();
        var reject = RecordingGate.RejectReason(audio, settings.MinRecordingMs, settings.SilenceThreshold);
        if (reject is not null)
        {
            _log.Info($"Recording discarded: {reject}.");
            return ProcessingOutcome.Discarded;
        }

        var sw = Stopwatch.StartNew();
        var engine = await _transcriber(token).ConfigureAwait(false);
        var raw = await engine.TranscribeAsync(audio.Samples, token).ConfigureAwait(false);
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
        var cleaned = await cleaner.CleanAsync(raw, token).ConfigureAwait(false);
        var text = cleaned.Text;
        _log.Info($"Cleanup by {(cleaned.Cleaned ? "the model" : "the rule-based tidy")} in {sw.ElapsedMilliseconds} ms.");
        if (settings.LogTranscripts) _log.Info($"Cleaned transcript: {text}");
        var finished = _finish(text);
        if (finished != text)
        {
            _log.Info("Corrections and the profanity filter changed the text."); // never the text itself
            if (settings.LogTranscripts) _log.Info($"Final text: {finished}");
            text = finished;
        }

        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text)) return ProcessingOutcome.Discarded;
        await _insert(text, token).ConfigureAwait(false);
        _log.Info($"Inserted {text.Length} chars via {settings.Insert.Method}.");
        _inserted(text);
        insertedWords?.Invoke(text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);
        return ProcessingOutcome.Inserted;
    }
}
