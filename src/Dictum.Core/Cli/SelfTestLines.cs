using Dictum.Core;
using Dictum.Hotkey;
using Dictum.Personal;
using Dictum.Platform;
using Dictum.Settings;
using Dictum.Transcribe;

namespace Dictum.Cli;

public static partial class SelfTestCommand
{
    /// <summary>Where Dictum keeps its files, as they are on disk (a packaged build's are redirected by Windows, SPEC §18.2).</summary>
    private static void DataLines(AppPaths paths, IPlatform platform, Action<string, string, bool?> line)
    {
        var package = platform.Package;
        line("build", package.IsPackaged ? $"packaged ({package.FamilyName})" : "unpackaged (direct download)", null);
        if (package.OtherInstall() is { } other) line("  warning", other, null);
        line("config folder", Real(paths.ConfigDir, package), null);
        line("local folder", Real(paths.LocalDir, package), null);
        line("logs", Real(paths.LogsDir, package), null);
        line("calibration", Real(paths.CalibrationDir, package), null);
        line("program folder", paths.ExeDir, null);
    }

    private static string Real(string path, IPackageInfo package) =>
        package.RealPath(path) is var real && !string.Equals(real, path, StringComparison.OrdinalIgnoreCase) ? $"{real} (the app sees {path})" : path;

    /// <summary>
    /// SPEC §20: a few seconds to press the hotkey, reporting whether the system delivered it and, if not, which keys did
    /// arrive (a Mac keyboard over Remote Desktop has no right Ctrl). Not a failure when nothing is pressed: the check
    /// may run unattended.
    /// </summary>
    private static void HotkeyCheck(IPlatform platform, HotkeySpec spec, TimeSpan window, ILog log, Action<string, string, bool?> line)
    {
        if (window <= TimeSpan.Zero) return;
        var matcher = new HotkeyMatcher(spec, suppress: false, platform.IsKeyDown);
        var received = new List<string>();
        using var seen = new ManualResetEventSlim();
        using var monitor = platform.CreateKeyMonitor(e =>
        {
            if (e.Injected) return;
            if (e.Down) lock (received) received.Add(HotkeyParser.NameOf(e.Vk));
            if (matcher.OnKey(e.Vk, e.Down, enabled: true).Signal == HotkeySignal.Down) seen.Set();
        }, log);
        if (!monitor.Start())
        {
            line("hotkey check", "could not watch the keyboard (see the log)", false);
            return;
        }
        Console.Out.WriteLine($"Press your hotkey ({spec.Display}) now: watching for {window.TotalSeconds:0} s...");
        Console.Out.Flush();
        if (seen.Wait(window))
        {
            line("hotkey check", $"{spec.Display} received", true);
            return;
        }
        string keys;
        lock (received) keys = string.Join(", ", received.Distinct(StringComparer.Ordinal));
        line("hotkey check", keys.Length == 0
            ? $"no key pressed in {window.TotalSeconds:0} s (not a failure; run it again and press {spec.Display})"
            : $"{spec.Display} was not received; these keys were: {keys}", null);
    }

    /// <summary>Batch 2 (SPEC §16): the active Mode preset, decoding, the personal files (counts only) and the shipped data files.</summary>
    private static bool PersonalLines(AppSettings settings, AppPaths paths, ILog log, Action<string, string, bool?> line)
    {
        var gpu = WhisperEngine.GpuMachine;
        var preset = ModePresets.Match(settings, gpu);
        var hardware = gpu ? "discrete GPU" : "CPU or integrated graphics only";
        line("mode preset", preset == ModePreset.Custom
            ? $"Custom ({hardware}: {ModePresets.Label(ModePreset.Light, gpu)} and {ModePresets.Label(ModePreset.Full, gpu)} do not match settings.json)"
            : $"{ModePresets.Label(preset, gpu)} ({hardware})", null);
        var personal = CliCommands.LoadPersonal(paths, log);
        line("whisper decoding", $"{new DecodeOptions(null, settings.Whisper.BeamSize).Describe()} (whisper.beam_size = {settings.Whisper.BeamSize}); " +
             $"dictionary prompt: {(personal.Dictionary.WhisperPrompt is null ? "none" : $"{personal.Dictionary.WhisperPromptTerms} of {personal.Dictionary.Terms.Count} term(s)")}", null);
        line("personal files", personal.Summary(settings.Profanity), null);
        line("grammar assist", settings.Llm.GrammarAssist ? "on" : "off", null);
        line("fix_hotkey", settings.FixHotkey ?? "not set", null);
        var ok = true;
        foreach (var (label, file) in new[] { ("profanity list", paths.ProfanityShippedFile), ("calibration script", paths.CalibrationScriptFile) })
        {
            var present = File.Exists(file);
            line(label, present ? $"{file} ({TextList.Read(file).Count} entries)" : $"{file} missing (the published folder is incomplete)", present);
            ok &= present;
        }
        return ok;
    }

    /// <summary>
    /// SPEC §11: every key Dictum reads (<see cref="SecretStore.KeyNames"/>), present and from where, or absent, then how many are
    /// stored in the credential store when there are any (API keys, decision D2, 30/10/2026); never a value, nor any part of one.
    /// PROPOSED 30/10/2026: the stored-key wording (.demo-preview/approve-api-keys.md rows 1 to 4).
    /// </summary>
    internal static void KeyLines(SecretStore secrets, Action<string, string, bool?> line)
    {
        foreach (var name in SecretStore.KeyNames)
        {
            var count = secrets.StoredCount(name);
            line(name, secrets.Describe(name) + (count == 0 ? "" : count == 1 ? "; 1 stored key" : $"; {count} stored keys"), null);
        }
    }

    /// <summary>
    /// Where keys added in Dictum are kept: the system's credential store by name, or that this platform has none yet.
    /// PROPOSED 30/10/2026 (.demo-preview/approve-api-keys.md row 5).
    /// </summary>
    internal static void KeyStoreLine(ISecretVault vault, Action<string, string, bool?> line) =>
        line("key store", vault.Available ? vault.StoreName ?? "available" : "none on this platform", null);
}
