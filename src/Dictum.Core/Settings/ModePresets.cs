namespace Dictum.Settings;

public enum ModePreset { Light, Full, Custom }

/// <summary>The settings one preset writes: the speech model, the cleanup provider and (for a local provider) its model.</summary>
public sealed record PresetValues(string SpeechModel, string Provider, string? CleanupModel);

/// <summary>
/// Tray → Mode (SPEC §16.1). A preset is a pure function of the hardware; selecting one writes concrete values into
/// settings.json, and "Custom" is whatever matches neither. Only the keys a preset writes are compared, so a beam size
/// chosen by calibration or a profanity setting does not turn a preset into Custom.
/// </summary>
public static class ModePresets
{
    public const string LightSpeech = "base.en";
    public const string FullSpeechGpu = "large-v3-turbo";
    public const string FullSpeechCpu = "small.en";
    public const string FullCleanup = "qwen2.5-1.5b";

    /// <summary>
    /// | Preset | GPU runtime loaded | CPU only |
    /// | Light | base.en + rule-based tidy | base.en + rule-based tidy |
    /// | Full | large-v3-turbo + qwen2.5-1.5b | small.en + qwen2.5-1.5b |
    /// </summary>
    public static PresetValues For(ModePreset preset, bool gpuRuntime) => preset switch
    {
        ModePreset.Light => new(LightSpeech, LlmSettings.NoProvider, null),
        ModePreset.Full => new(gpuRuntime ? FullSpeechGpu : FullSpeechCpu, LlmSettings.Local, FullCleanup),
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Custom has no values of its own."),
    };

    /// <summary>The preset these settings match on this hardware, or Custom.</summary>
    public static ModePreset Match(AppSettings settings, bool gpuRuntime)
    {
        foreach (var preset in new[] { ModePreset.Light, ModePreset.Full })
        {
            if (Matches(settings, For(preset, gpuRuntime))) return preset;
        }
        return ModePreset.Custom;
    }

    private static bool Matches(AppSettings settings, PresetValues values)
    {
        if (!SameName(ModelCatalog.DisplayName(settings.Whisper.Model), values.SpeechModel)) return false;
        if (settings.Llm.Provider != values.Provider) return false;
        // llm.model is a catalogue name or a path to that catalogue file.
        return values.CleanupModel is null || SameName(CleanupCatalog.FindByFile(settings.Llm.Model)?.Name ?? settings.Llm.Model, values.CleanupModel);
    }

    private static bool SameName(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The settings.json keys and values a preset writes.</summary>
    public static (string Key, string Value)[] SettingsValues(PresetValues values) => values.CleanupModel is null
        ? [("whisper.model", values.SpeechModel), ("llm.provider", values.Provider)]
        : [("whisper.model", values.SpeechModel), ("llm.provider", values.Provider), ("llm.model", values.CleanupModel)];

    /// <summary>Tray label, e.g. "Full (large-v3-turbo + qwen2.5-1.5b)".</summary>
    public static string Label(ModePreset preset, bool gpuRuntime)
    {
        if (preset == ModePreset.Custom) return "Custom";
        var v = For(preset, gpuRuntime);
        return $"{preset} ({v.SpeechModel} + {(v.CleanupModel ?? "rule-based tidy")})";
    }
}
