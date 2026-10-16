using System.Diagnostics;
using Dictum.Audio;
using Dictum.Core;
using Dictum.Llm;
using Dictum.Personal;
using Dictum.Settings;
using Dictum.Transcribe;

namespace Dictum.Cli;

/// <summary>
/// --bench-cleanup &lt;file.txt|file.wav&gt;: runs every downloaded catalogue cleanup model, plus a .gguf named with
/// --cleanup-model, over the same input (a WAV is transcribed once with the configured speech model and the dictionary
/// prompt) and prints each one's output, time and validator verdict side by side, plus the rule-based tidy. Uses the
/// app's prompt, examples, token limit, validator context and llm.gpu; no timeout, so slow models are measured rather
/// than cut off. Never downloads anything. Exit 0, or 1 for unusable input.
/// </summary>
public static class BenchCleanupCommand
{
    private sealed record Row(string Model, string Backend, string Time, string Verdict, string Output);

    public static async Task<int> RunAsync(CliOptions options, AppPaths paths, ILog log, SecretRedactor redactor)
    {
        var input = Path.GetFullPath(options.WavPath!);
        if (!File.Exists(input))
        {
            Console.Error.WriteLine($"error: file not found: {input}");
            return 1;
        }
        var config = CliCommands.LoadConfig(paths, log, redactor);
        if (CliCommands.Unreadable(config, paths)) return 1;
        var settings = config.Settings;
        var personal = CliCommands.LoadPersonal(paths, log);
        var integrity = new ModelIntegrity(paths.VerifiedModelsFile, log, () => ModelFolders.LookupOrder(paths, settings));
        string raw;
        try
        {
            raw = input.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
                ? await TranscribeAsync(AudioFiles.ReadWav(input), settings, paths, integrity, personal, options, log) // a bad WAV fails before any model loads
                : (await File.ReadAllTextAsync(input)).Trim();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) // unusable input (not RIFF audio, missing model…) exits 1, never crashes
        {
            Console.Error.WriteLine($"error: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        Console.Out.WriteLine($"input ({raw.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length} words): {raw}");
        Console.Out.WriteLine();

        var (prompt, context) = Checks(settings, personal, log.Warn);
        DeterministicCleaner.Clean(raw); // first call pays regex start-up; time the second
        var sw = Stopwatch.StartNew();
        var tidy = DeterministicCleaner.Clean(raw);
        var rows = new List<Row> { new("rule-based tidy", "-", $"{sw.Elapsed.TotalMilliseconds:0.0} ms", "-", tidy) };
        foreach (var info in CleanupCatalog.Entries)
        {
            // The app's lookup (ModelFolders): a copy in the chosen, the per-user or the bundled folder, as the tray app finds it.
            if (ModelFolders.Find(info.FileName, info.Bytes, paths, settings, integrity.Passes) is not { } path)
            {
                Console.Error.WriteLine($"{info.Name}: not downloaded, skipped");
                continue;
            }
            rows.Add(await RunOneAsync(info.Name, path, raw, prompt, context, settings, integrity, log));
        }
        if (options.CleanupModel is { } custom && Path.IsPathFullyQualified(custom))
        {
            rows.Add(File.Exists(custom)
                ? await RunOneAsync(CleanupCatalog.DisplayName(custom), custom, raw, prompt, context, settings, integrity, log)
                : new Row(CleanupCatalog.DisplayName(custom), "-", "-", "not run: file not found", ""));
        }

        Console.Out.WriteLine($"{"model",-16} {"backend",-8} {"time",9}  verdict");
        foreach (var r in rows) Console.Out.WriteLine($"{r.Model,-16} {r.Backend,-8} {r.Time,9}  {r.Verdict}");
        foreach (var r in rows)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine($"--- {r.Model}:");
            Console.Out.WriteLine(r.Output);
        }
        return 0;
    }

    /// <summary>
    /// The prompt and the checks the tray app uses (SPEC §16): base prompt, grammar assist when on, the personal dictionary
    /// line, the words the profanity setting keeps and the dictionary terms a reply may not add.
    /// </summary>
    internal static (string Prompt, ValidationContext Context) Checks(AppSettings settings, PersonalText personal, Action<string> warn) =>
        personal.ForCleanup(settings.Profanity).Checks(settings.Llm, warn);

    private static async Task<Row> RunOneAsync(string name, string path, string raw, string prompt, ValidationContext context, AppSettings settings,
        ModelIntegrity integrity, ILog log)
    {
        try
        {
            integrity.Verify(path);
            // simplification: the bench never sets whisper's runtime order, so llm.gpu = auto keeps Vulkan here even where the tray
            // app would use the CPU build (every GPU integrated graphics, LocalLlmEngine.UseVulkan). Ceiling: on such a PC the bench
            // times Vulkan, not what dictation uses. Trigger to retire: a bench figure quoted for an integrated-graphics PC; then set
            // the order first (WhisperEngine.ConfigureRuntime with whisper.gpu) or pass llm.gpu = cpu.
            using var engine = LocalLlmEngine.Load(path, settings.Llm.Gpu, log);
            await engine.WarmUpAsync(prompt); // as the tray app does after loading: evaluates the prompt prefix (and compiles GPU shaders)
            var max = CleanupExamples.LocalMaxTokens(engine.CountTokens(raw));
            var sw = Stopwatch.StartNew();
            var result = await engine.CompleteAsync(prompt, CleanupExamples.Pairs, raw, max, CancellationToken.None);
            var time = $"{sw.ElapsedMilliseconds} ms";
            var (accepted, rejection) = result.Finished ? CleanupValidator.Validate(raw, result.Text, context) : (null, $"reply truncated at {max} tokens");
            return new Row(name, LocalLlmEngine.Backend, time, accepted is not null ? "accepted" : $"rejected: {rejection}", result.Text);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) // one model failing (load, integrity) must not stop the others
        {
            return new Row(name, "-", "-", $"not run: {ex.GetType().Name}: {ex.Message}", "");
        }
    }

    private static async Task<string> TranscribeAsync(CapturedAudio audio, AppSettings settings, AppPaths paths, ModelIntegrity integrity, PersonalText personal,
        CliOptions options, ILog log)
    {
        var modelPath = SettingsFile.EffectiveModelPath(settings, paths, Environment.GetEnvironmentVariable, options.Model);
        if (!File.Exists(modelPath)) throw new InvalidOperationException($"speech model not found: {modelPath}");
        integrity.Verify(modelPath);
        var decode = new DecodeOptions(personal.Dictionary.WhisperPrompt, settings.Whisper.BeamSize, settings.Whisper.Language, settings.Whisper.Translate); // as dictation decodes
        using var engine = WhisperEngine.Load(modelPath, settings.Whisper.Threads, settings.Whisper.Gpu, log, decode);
        var sw = Stopwatch.StartNew();
        var raw = await engine.TranscribeAsync(audio.Samples, CancellationToken.None);
        Console.Error.WriteLine($"transcribed {audio.Duration.TotalSeconds:0.0} s with {Path.GetFileName(modelPath)} on {WhisperEngine.LoadedRuntime} in {sw.ElapsedMilliseconds} ms");
        return raw;
    }
}
