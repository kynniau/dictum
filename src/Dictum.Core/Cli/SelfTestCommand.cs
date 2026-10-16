using Dictum.Core;
using Dictum.Hotkey;
using Dictum.Llm;
using Dictum.Platform;
using Dictum.Settings;
using Dictum.Transcribe;

namespace Dictum.Cli;

/// <summary>
/// --selftest (SPEC §11, §20): the platform and data folders, both models and their backends, recommendations, the Mode
/// preset, microphones, the hotkey (parsed, then a 5-second "press it now" check), key presence. Exit 0/1.
/// </summary>
public static partial class SelfTestCommand
{
    /// <param name="hotkeyCheck">How long to wait for the hotkey; tests pass zero to skip the check.</param>
    public static int Run(CliOptions options, AppPaths paths, ILog log, SecretRedactor redactor, IPlatform platform, TimeSpan? hotkeyCheck = null)
    {
        var ok = true;
        void Line(string label, string value, bool? pass = null)
        {
            var mark = pass switch { true => "  [OK]", false => "  [FAIL]", _ => "" };
            Console.Out.WriteLine($"{label,-22} {value}{mark}");
        }
        bool Check(ModelIntegrity integrity, string path, string label)
        {
            try
            {
                Line(label, integrity.Verify(path), true);
                return true;
            }
            catch (ModelIntegrityException ex)
            {
                Line(label, ex.Message, false); // reported, never loaded
                return false;
            }
        }

        Console.Out.WriteLine($"Dictum {typeof(SelfTestCommand).Assembly.GetName().Version?.ToString(3)} self-test on {platform.Name}");
        DataLines(paths, platform, Line);
        var messages = new List<string>();
        var config = ConfigLoader.Load(paths, redactor, Environment.GetEnvironmentVariable,
            s => { WhisperEngine.ProbeRuntime(s.Whisper.Gpu, log); return WhisperEngine.GpuMachine; }, messages.Add, options.CleanupModel);
        var load = config.File;
        if (config.Problem is { } problem)
        {
            Line("settings.json", problem, false);
            ok = false;
        }
        else
        {
            Line("settings.json", load.FileExisted ? $"loaded, {load.Warnings.Count} warning(s)" : "not found, defaults in use");
        }
        foreach (var w in load.Warnings) Line("  warning", w);
        foreach (var note in messages.Except(load.Warnings)) Line("  note", note); // the cleanup repairs
        var (settings, secrets) = (config.Settings, config.Secrets);
        var integrity = new ModelIntegrity(paths.VerifiedModelsFile, log, () => paths.ModelsDir(settings));
        Line("offline_only", settings.OfflineOnly ? "on: no network calls, downloads and cloud providers refused" : "off");

        if (HotkeyParser.TryParse(settings.Hotkey, out var spec, out var error))
        {
            var kind = spec.IsChord ? "chord" : "single key";
            Line("hotkey", $"\"{settings.Hotkey}\" -> {spec.Display} ({kind}, vk {string.Join("/", spec.Trigger.Vks.Select(v => $"0x{v:X2}"))}), suppress_hotkey={settings.SuppressHotkey.ToString().ToLowerInvariant()}", true);
            HotkeyCheck(platform, spec, hotkeyCheck ?? TimeSpan.FromSeconds(5), log, Line);
        }
        else
        {
            Line("hotkey", error, false);
            ok = false;
        }

        // Speech model
        var modelPath = SettingsFile.EffectiveModelPath(settings, paths, Environment.GetEnvironmentVariable, options.Model);
        var modelExists = File.Exists(modelPath);
        Line("speech model", modelExists ? $"{modelPath} ({new FileInfo(modelPath).Length:N0} bytes)" : $"{modelPath} missing", modelExists);
        ok &= modelExists;
        if (modelExists && Check(integrity, modelPath, "speech model check"))
        {
            try
            {
                using var engine = WhisperEngine.Load(modelPath, settings.Whisper.Language, settings.Whisper.Threads, settings.Whisper.Gpu, log);
                Line("whisper runtime", $"{WhisperEngine.LoadedRuntime} (whisper.gpu = {settings.Whisper.Gpu}; loaded in {engine.LoadTime.TotalSeconds:0.0} s)", true);
            }
            catch (Exception ex)
            {
                Line("whisper runtime", $"model load failed: {ex.GetType().Name}: {ex.Message}", false);
                ok = false;
            }
        }
        else
        {
            ok = false; // missing, or it failed its size/SHA-256 check
            Line("whisper runtime", $"{WhisperEngine.ProbeRuntime(settings.Whisper.Gpu, log)} (probed without a model)");
        }
        var (recommended, why) = ModelCatalog.Recommend(WhisperEngine.GpuMachine);
        Line("recommended speech", $"{recommended} ({why}; Vulkan devices reported: {WhisperEngine.DeviceSummary})");
        ok &= PersonalLines(settings, paths, log, Line);

        // Cleanup model
        LocalLlmEngine? local = null;
        if (settings.Llm.Provider == LlmSettings.Local)
        {
            var cleanupPath = SettingsFile.CleanupModelPath(settings, paths);
            if (File.Exists(cleanupPath))
            {
                Line("cleanup model", $"{cleanupPath} ({new FileInfo(cleanupPath).Length:N0} bytes)", true);
                if (!Check(integrity, cleanupPath, "cleanup model check")) ok = false;
                else try
                {
                    local = LocalLlmEngine.Load(cleanupPath, settings.Llm.Gpu, log);
                    Line("cleanup backend", $"{LocalLlmEngine.Backend} (llm.gpu = {settings.Llm.Gpu}; loaded in {local.LoadTime.TotalSeconds:0.0} s; {LocalLlmEngine.LoadedLibrary})", true);
                }
                catch (Exception ex)
                {
                    Line("cleanup backend", $"load failed: {ex.GetType().Name}: {ex.Message} (the rule-based tidy will be used)", false);
                    ok = false;
                }
            }
            else
            {
                Line("cleanup model", $"{cleanupPath} missing: the rule-based tidy will be used (Dictum.exe --download-cleanup-model {settings.Llm.Model})");
            }
        }
        using (local)
        using (var http = OfflineGuardHandler.CreateClient(() => settings.OfflineOnly))
        {
            Line("cleanup", TranscriptCleaner.Create(settings.Llm, settings.OfflineOnly, secrets, http, log, () => local).Status);
        }
        var (recommendedCleanup, cleanupWhy) = CleanupCatalog.Recommend(WhisperEngine.GpuMachine);
        Line("recommended cleanup", $"{recommendedCleanup} ({cleanupWhy})");

        try
        {
            var devices = platform.ListCaptureDevices();
            Line("microphones", $"{devices.Count} active capture device(s)", devices.Count > 0);
            foreach (var d in devices) Line(d.IsDefault ? "  * default" : "  -", d.Name);
            ok &= devices.Count > 0;
        }
        catch (Exception ex)
        {
            Line("microphones", $"enumeration failed: {ex.GetType().Name}: {ex.Message}", false);
            ok = false;
        }

        KeyLines(secrets, Line);
        Console.Out.WriteLine(ok ? "result: PASS" : "result: FAIL");
        Console.Out.Flush();
        return ok ? 0 : 1;
    }
}
