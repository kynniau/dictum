using System.Diagnostics;
using Dictum.App;
using Dictum.Audio;
using Dictum.Core;
using Dictum.Llm;
using Dictum.Personal;
using Dictum.Settings;
using Dictum.Transcribe;

namespace Dictum.Cli;

/// <summary>--transcribe (SPEC §11): results to stdout, diagnostics and timings to stderr.</summary>
public static class CliCommands
{
    /// <summary>
    /// --print-models-dir: the folder the app downloads models to and looks in first (models.folder, else its earlier name
    /// whisper.models_dir, else %LOCALAPPDATA%\Dictum\models; never the folder of the models that come with Dictum), so
    /// install.ps1 copies -ModelsFrom files where the app looks. A settings.json that cannot be read prints nothing and exits 1:
    /// the folder it names is unknown, and guessing would copy models where the app does not look.
    /// </summary>
    public static int PrintModelsDir(AppPaths paths, TextWriter output, TextWriter error)
    {
        var load = SettingsLoader.LoadFile(paths.SettingsFile);
        if (!load.Usable)
        {
            error.WriteLine($"error: {paths.SettingsFile} could not be read ({load.Warnings.FirstOrDefault()}); fix it and run this again.");
            return 1;
        }
        output.WriteLine(paths.ModelsDir(load.Settings));
        return 0;
    }

    /// <summary>
    /// PROPOSED 23/10/2026 (legal block, lens on PR #76): what --transcribe and --bench-cleanup print, on stderr, before they exit 1
    /// while this account has not accepted the Terms of Use in the app (<see cref="RefusedWithoutTerms"/>).
    /// </summary>
    public const string TermsNotAccepted = "Start Dictum once and press Continue to accept its Terms of Use.";

    /// <summary>
    /// The two command-line modes a person uses Dictum with (--transcribe, --bench-cleanup) run only once settings.json records
    /// this account's acceptance of the Terms of Use version in force (<see cref="AppRules.AsksTerms"/>, the tray app's rule);
    /// otherwise they print <see cref="TermsNotAccepted"/> and exit 1. The administrator and CI modes (--selftest,
    /// --download-model, --download-cleanup-model, --print-models-dir) are not gated (owner's decision, lens on PR #76). Called
    /// after the unreadable-settings check, whose own message is the more useful one for a file that cannot be read.
    /// <paramref name="error"/> is stderr unless a test passes its own writer (as <see cref="PrintModelsDir"/> takes one).
    /// </summary>
    internal static bool RefusedWithoutTerms(AppPaths paths, ILog log, string mode, TextWriter? error = null)
    {
        if (!AppRules.AsksTerms(SettingsLoader.LoadFile(paths.SettingsFile), AppRules.Account)) return false;
        (error ?? Console.Error).WriteLine(TermsNotAccepted);
        log.Info($"{mode} refused: settings.json records no acceptance of the Terms of Use version {Help.TermsText.Version} by this account.");
        return true;
    }

    public static async Task<int> TranscribeAsync(CliOptions options, AppPaths paths, ILog log, SecretRedactor redactor)
    {
        var wav = Path.GetFullPath(options.WavPath!);
        if (!File.Exists(wav))
        {
            Console.Error.WriteLine($"error: WAV file not found: {wav}");
            return 1;
        }
        var config = LoadConfig(paths, log, redactor, options.CleanupModel);
        if (Unreadable(config, paths)) return 1;
        if (RefusedWithoutTerms(paths, log, "--transcribe")) return 1;
        var (settings, secrets) = (config.Settings, config.Secrets);
        var modelPath = SettingsFile.EffectiveModelPath(settings, paths, Environment.GetEnvironmentVariable, options.Model);
        if (!File.Exists(modelPath))
        {
            Console.Error.WriteLine($"error: model file not found: {modelPath} (set whisper.model, or {SettingsFile.ModelPathVariable}; Dictum.exe --download-model can fetch one)");
            return 1;
        }
        var integrity = new ModelIntegrity(paths.VerifiedModelsFile, log, () => ModelFolders.LookupOrder(paths, settings));
        try
        {
            integrity.Verify(modelPath);
            if (!options.NoLlm && settings.Llm.Provider == LlmSettings.Local && SettingsFile.CleanupModelPath(settings, paths) is var c && File.Exists(c)) integrity.Verify(c);
        }
        catch (ModelIntegrityException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }

        try
        {
            var personal = LoadPersonal(paths, log);
            Console.Error.WriteLine($"personal files: {personal.Summary(settings.Profanity)}");
            var decode = new DecodeOptions(personal.Dictionary.WhisperPrompt, settings.Whisper.BeamSize, settings.Whisper.Language, settings.Whisper.Translate);
            using var engine = WhisperEngine.Load(modelPath, settings.Whisper.Threads, settings.Whisper.Gpu, log, decode);
            Console.Error.WriteLine($"model: {modelPath}");
            Console.Error.WriteLine($"whisper runtime: {WhisperEngine.LoadedRuntime} (loaded in {engine.LoadTime.TotalSeconds:0.0} s; {decode.Describe()}; " +
                                    $"{(decode.Prompt is null ? "no dictionary prompt" : $"dictionary prompt with {personal.Dictionary.WhisperPromptTerms} term(s)")})");

            var audio = AudioFiles.ReadWav(wav);
            Console.Error.WriteLine($"audio: {audio.Duration.TotalSeconds:0.00} s at 16 kHz mono, peak block RMS {audio.PeakLevel:0.000}");

            var sw = Stopwatch.StartNew();
            var raw = await engine.TranscribeAsync(audio.Samples, CancellationToken.None);
            var transcribeMs = sw.ElapsedMilliseconds;
            Console.Error.WriteLine($"transcribed in {transcribeMs} ms");
            Console.Out.WriteLine($"raw: {raw}");

            if (!options.NoLlm)
            {
                using var http = OfflineGuardHandler.CreateClient(() => settings.OfflineOnly);
                LocalLlmEngine? local = null;
                if (settings.Llm.Provider == LlmSettings.Local)
                {
                    var cleanupPath = SettingsFile.CleanupModelPath(settings, paths);
                    if (File.Exists(cleanupPath))
                    {
                        local = LocalLlmEngine.Load(cleanupPath, settings.Llm.Gpu, log);
                        Console.Error.WriteLine($"cleanup model: {cleanupPath} on {LocalLlmEngine.Backend} (loaded in {local.LoadTime.TotalSeconds:0.0} s)");
                    }
                    else
                    {
                        Console.Error.WriteLine($"cleanup model missing: {cleanupPath} (the rule-based tidy is used)");
                    }
                }
                using (local)
                {
                    var cleaner = Cleaner(settings, secrets, http, log, () => local, personal);
                    Console.Error.WriteLine($"cleanup: {cleaner.Status}{(settings.Llm.GrammarAssist ? "; grammar assist on" : "")}");
                    sw.Restart();
                    var cleaned = await cleaner.CleanAsync(raw, CancellationToken.None);
                    var cleanupMs = sw.ElapsedMilliseconds;
                    if (cleaned.FallbackReason is not null) Console.Error.WriteLine($"cleanup fallback: {cleaned.FallbackReason}");
                    Console.Error.WriteLine($"cleanup by {(cleaned.Cleaned ? "the model" : cleaner.IsPlain ? "the whitespace-only tidy" : "the rule-based tidy")} in {cleanupMs} ms; transcribe + cleanup {transcribeMs + cleanupMs} ms");
                    Console.Out.WriteLine($"cleaned: {cleaned.Text}");
                    // What the tray app would paste: the corrections, then the profanity filter (SPEC §22 order: raw, clean-up, corrections, profanity).
                    Console.Out.WriteLine($"final: {personal.Finish(cleaned.Text, settings.Profanity)}");
                }
            }
            var process = Process.GetCurrentProcess();
            Console.Error.WriteLine($"memory: working set {process.WorkingSet64 / 1048576} MiB, peak {process.PeakWorkingSet64 / 1048576} MiB");
            Console.Out.Flush();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.GetType().Name}: {ex.Message}");
            log.Error($"--transcribe failed: {ex}");
            return 1;
        }
    }

    /// <summary>The clean-up --transcribe runs: the configured provider with the tray app's prompt and checks, or whitespace only when the text is not English (SPEC §23).</summary>
    internal static TranscriptCleaner Cleaner(AppSettings settings, SecretStore secrets, HttpClient http, ILog log, Func<ILocalLlmEngine?> local, PersonalText personal) =>
        TranscriptCleaner.Create(settings.Llm, settings.OfflineOnly, secrets, http, log, local, personal.ForCleanup(settings.Profanity),
            WhisperLanguages.OutputIsEnglish(settings.Whisper.Language, settings.Whisper.Translate));

    /// <summary>The user's dictionary, corrections and profanity list from the config folder (honours --config).</summary>
    internal static PersonalText LoadPersonal(AppPaths paths, ILog log) =>
        PersonalText.Load(paths, w => { log.Warn(w); Console.Error.WriteLine($"note: {w}"); });

    /// <summary>The shared loader (<see cref="ConfigLoader"/>); warnings reach stderr through the log's echo.</summary>
    internal static ConfigLoadResult LoadConfig(AppPaths paths, ILog log, SecretRedactor redactor, string? cleanupOverride = null) =>
        ConfigLoader.Load(paths, redactor, Environment.GetEnvironmentVariable,
            s => { WhisperEngine.ProbeRuntime(s.Whisper.Gpu, log); return WhisperEngine.GpuMachine; }, log.Warn, cleanupOverride);

    /// <summary>
    /// SPEC §22: every CLI command exits 1 on a settings.json it cannot read (its values, offline_only included, are
    /// unknown). True, after saying so on stderr, when the command must stop.
    /// </summary>
    internal static bool Unreadable(ConfigLoadResult config, AppPaths paths)
    {
        if (!config.ForcedOffline) return false;
        Console.Error.WriteLine($"error: {paths.SettingsFile} could not be read ({config.File.Warnings.FirstOrDefault() ?? "unreadable"}); fix it, then run this again.");
        return true;
    }
}
