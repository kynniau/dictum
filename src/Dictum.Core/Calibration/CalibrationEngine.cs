using System.Diagnostics;
using Dictum.Core;
using Dictum.Transcribe;

namespace Dictum.Calibration;

/// <summary>An installed speech model to test: its catalogue name and file.</summary>
public sealed record SpeechCandidate(string Name, string Path);

/// <summary>
/// Calibration's measurement (SPEC §16.5 step 2): every candidate model transcribes every clip, once with greedy and
/// once with beam-search decoding, each clip timed; the WER of each transcript is computed against the script.
/// Models are loaded one at a time and released before the next. It never trains or changes a model and never
/// touches the network. Transcripts are logged only when <c>logText</c> is true (log_transcripts).
/// </summary>
public sealed class CalibrationEngine(Func<string, DecodeOptions, ISpeechEngine> load, ILog log, Func<bool>? warmUp = null, bool logText = false)
{
    /// <summary>Greedy, then beam search with Whisper.net's default beam size.</summary>
    public static readonly int[] BeamSizes = [0, DecodeOptions.CalibrationBeamSize];

    /// <param name="prompt">The dictionary prompt in use, so the test hears the way dictation does.</param>
    /// <param name="progress">One line per step, for the window ("small.en, beam search (5): clip 3 of 10").</param>
    /// <param name="finished">Each combination as soon as it is measured: cancelling the test keeps those.</param>
    public async Task<IReadOnlyList<ComboResult>> RunAsync(IReadOnlyList<CalibrationClip> clips, IReadOnlyList<SpeechCandidate> models, string? prompt,
        IProgress<string>? progress, CancellationToken cancellationToken, IProgress<ComboResult>? finished = null)
    {
        var results = new List<ComboResult>();
        foreach (var model in models)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Loading {model.Name}…");
            ISpeechEngine engine;
            try
            {
                engine = await Task.Run(() => load(model.Path, new DecodeOptions(prompt, BeamSizes[0])), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.Warn($"Calibration: {model.Name} could not be loaded ({ex.GetType().Name}: {ex.Message}); skipped.");
                progress?.Report($"{model.Name} could not be loaded: {ex.Message}");
                continue;
            }
            using (engine)
            {
                foreach (var beam in BeamSizes)
                {
                    var decode = new DecodeOptions(prompt, beam);
                    if (engine.Decode != decode) await Task.Run(() => engine.Reconfigure(decode), cancellationToken).ConfigureAwait(false);
                    if (warmUp?.Invoke() == true)
                    {
                        // GPU: the first run of a decoder compiles shaders; it must not count as that combination's speed.
                        await engine.TranscribeAsync(new float[16000], cancellationToken).ConfigureAwait(false);
                    }
                    var combo = await RunComboAsync(engine, model.Name, beam, clips, progress, cancellationToken).ConfigureAwait(false);
                    results.Add(combo);
                    finished?.Report(combo); // so a cancelled test keeps every combination that finished
                }
            }
        }
        return results;
    }

    private async Task<ComboResult> RunComboAsync(ITranscriber engine, string model, int beam, IReadOnlyList<CalibrationClip> clips,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var outcomes = new List<ClipOutcome>();
        var label = beam > 0 ? $"beam search ({beam})" : "greedy";
        foreach (var clip in clips)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"{model}, {label}: clip {outcomes.Count + 1} of {clips.Count}");
            var sw = Stopwatch.StartNew();
            var heard = await engine.TranscribeAsync(clip.Samples, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            var wer = WordErrorRate.Compute(clip.Script, heard);
            outcomes.Add(new ClipOutcome(clip.Index, heard, sw.Elapsed.TotalSeconds, clip.Seconds, wer));
            if (logText) log.Info($"Calibration {model} {label} clip {clip.Index}: \"{heard}\"");
        }
        var combo = new ComboResult(model, beam, outcomes);
        log.Info($"Calibration: {model} {label}: WER {combo.Wer:P1}, {combo.AverageSeconds:0.00} s per clip, {combo.SecondsPer10s:0.00} s per 10 s of audio" +
                 $"{(CalibrationRanking.WithinCeiling(combo) ? "" : " (over the latency ceiling)")}.");
        return combo;
    }
}
