using System.Globalization;
using Dictum.Audio;
using Dictum.Calibration;
using Dictum.Core;
using Dictum.Personal;
using Dictum.Transcribe;

namespace Dictum.Tests;

/// <summary>
/// SPEC §16.5 integration: the calibration engine over the ten shipped sentences, synthesised with System.Speech, with
/// tiny.en and small.en (whisper's real models, greedy and beam search). Gated on DICTUM_MODEL_PATH; both models are
/// looked for in that file's folder. Prints the WER table and the missed-word suggestions.
/// </summary>
[Collection("Process-wide state")] // loads native models: never beside another model-loading class
public class CalibrationIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Calibration_over_synthesised_clips_ranks_tiny_and_small()
    {
        var modelPath = TestEnv.Get("MODEL_PATH");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(modelPath), "DICTUM_MODEL_PATH is not set.");
        var dir = Path.GetDirectoryName(Path.GetFullPath(modelPath!))!;
        var tiny = Path.Combine(dir, "ggml-tiny.en.bin");
        var small = Path.Combine(dir, "ggml-small.en.bin");
        Assert.SkipUnless(File.Exists(tiny) && File.Exists(small), $"ggml-tiny.en.bin and ggml-small.en.bin are needed in {dir}.");

        var script = TextList.Read(Path.Combine(AppContext.BaseDirectory, "calibration-sentences.txt"));
        Assert.Equal(10, script.Count);
        var clips = new List<CalibrationClip>();
        foreach (var (sentence, i) in script.Select((s, i) => (s, i + 1)))
        {
            var wav = WhisperIntegrationTests.SynthesiseWav(sentence);
            try
            {
                clips.Add(new CalibrationClip(i, sentence, AudioFiles.ReadWav(wav).Samples));
            }
            finally
            {
                File.Delete(wav);
            }
        }

        // DICTUM_CALIBRATION_GPU=cpu forces the CPU runtime (the runtime is chosen once per process: run this class alone).
        var gpu = TestEnv.Get("CALIBRATION_GPU") == "cpu" ? "cpu" : "auto";
        var log = new RecordingLog();
        var engine = new CalibrationEngine((path, decode) => WhisperEngine.Load(path, 0, gpu, NullLog.Instance, decode), log, () => WhisperEngine.VulkanActive);
        var results = await engine.RunAsync(clips, [new("tiny.en", tiny), new("small.en", small)], prompt: null, null, TestContext.Current.CancellationToken);
        Assert.SkipWhen(gpu == "cpu" && WhisperEngine.VulkanActive, "A GPU runtime was already loaded in this process; run this class on its own.");

        var recommended = CalibrationRanking.Recommend(results);
        output.WriteLine($"runtime {WhisperEngine.LoadedRuntime}; {clips.Count} clips, {clips.Sum(c => c.Seconds):0.0} s of synthesised speech");
        output.WriteLine("| Model | Decoding | WER | Avg s per clip | s per 10 s of audio | Under ceiling | Recommended |");
        output.WriteLine("|---|---|---|---|---|---|---|");
        foreach (var r in results)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"| {r.Model} | {r.Decoding} | {r.Wer:P1} | {r.AverageSeconds:0.00} | {r.SecondsPer10s:0.00} | {(CalibrationRanking.WithinCeiling(r) ? "yes" : "no")} | {(ReferenceEquals(r, recommended) ? "yes" : "")} |"));
        }
        foreach (var r in results)
        {
            foreach (var clip in r.Clips.Where(c => c.Wer.Edits > 0)) output.WriteLine($"{r.Model} {r.Decoding} clip {clip.Index}: \"{clip.Heard}\"");
        }
        if (recommended is not null)
        {
            foreach (var s in MissedWords.Find(recommended, script, PersonalDictionary.Empty, Corrections.Empty))
                output.WriteLine($"suggestion: {s.DictionaryText}{(s.CorrectionText is { } c ? "; " + c : "")}");
        }

        Assert.Equal(4, results.Count);
        Assert.All(results, r => Assert.Equal(10, r.Clips.Count));
        var tinyGreedy = results.Single(r => r.Model == "tiny.en" && r.BeamSize == 0);
        var smallGreedy = results.Single(r => r.Model == "small.en" && r.BeamSize == 0);
        Assert.True(tinyGreedy.Wer >= smallGreedy.Wer, $"tiny.en greedy WER {tinyGreedy.Wer:P1} < small.en greedy WER {smallGreedy.Wer:P1}");
        Assert.DoesNotContain(log.Lines, l => l.Message.Contains("Siobhan", StringComparison.OrdinalIgnoreCase)); // no transcript text without log_transcripts
    }
}
