using Dictum.Llm;

namespace Dictum.Settings;

/// <summary>
/// Repairs cleanup settings that cannot work, at every load (tray, --transcribe, --selftest). A settings.json written
/// by the first build says <c>"provider": "anthropic", "model": "claude-haiku-5-5"</c> because that was the default
/// then; without a key that would only ever give the rule-based tidy. A cloud provider the user has not agreed to in the
/// consent dialog (SPEC §22) is treated the same way: nothing is sent, the local model is used. The file itself is not
/// rewritten.
/// </summary>
public static class EffectiveSettings
{
    // APPROVED 07/11/2026 (owner, round 1 row 21): do not reword.
    public const string APPROVED_ParagraphPauseClamped = "cleanup.paragraph_pause_seconds {0} is outside 0 (off) or 0.5–30 seconds: using {1} seconds.";

    public static CleanupSettings ParagraphPause(CleanupSettings settings, Action<string> warn)
    {
        var seconds = settings.ParagraphPauseSeconds;
        var clamped = seconds <= 0 ? 0 : Math.Clamp(seconds, 0.5, 30);
        if (seconds == clamped) return settings;
        warn(string.Format(System.Globalization.CultureInfo.InvariantCulture, APPROVED_ParagraphPauseClamped, seconds, clamped));
        return settings with { ParagraphPauseSeconds = clamped };
    }

    /// <summary>voice.threshold outside 0.3–0.9 is clamped into it, with one line for the log (row V17).</summary>
    public static VoiceSettings VoiceThreshold(VoiceSettings settings, Action<string> warn)
    {
        var threshold = settings.Threshold;
        var clamped = Math.Clamp(threshold, VoiceSettings.MinThreshold, VoiceSettings.MaxThreshold);
        if (threshold == clamped) return settings;
        warn(string.Format(System.Globalization.CultureInfo.InvariantCulture, Meetings.Voice.VoiceText.APPROVED_ThresholdClamped, threshold, clamped));
        return settings with { Threshold = clamped };
    }

    /// <param name="anthropicKeyPresent">ANTHROPIC_API_KEY found in the environment or .env; its absence repairs Anthropic only when no consent is on file.</param>
    /// <param name="gpuRuntime">Whether a GPU runtime loads; asked only when a recommendation is needed.</param>
    /// <param name="warn">Receives one line per repair, for the log.</param>
    public static AppSettings Cleanup(AppSettings settings, bool anthropicKeyPresent, Func<bool> gpuRuntime, Action<string> warn)
    {
        var llm = settings.Llm;
        // Only without a consent on file (the first build's settings.json): Anthropic agreed to in Settings > Clean-up stays
        // Anthropic without its key, so the cleaner records the key it waits for (TranscriptCleaner.MissingKey) and the pages say
        // "waiting" in the approved words, as for the OpenAI and Google Gemini presets. Switching it to the local model here left
        // the card in its off state while the page said cloud clean-up was on (22/10/2026).
        if (llm.Provider == LlmSettings.Anthropic && !anthropicKeyPresent && !CloudPolicy.HasConsent(llm))
        {
            warn("llm.provider is \"anthropic\" but ANTHROPIC_API_KEY is not set (environment or .env): using the local cleanup model instead.");
            llm = llm with { Provider = LlmSettings.Local };
        }
        if (CloudPolicy.CloudHost(llm) is { } host && !CloudPolicy.HasConsent(llm))
        {
            warn($"llm.provider \"{llm.Provider}\" would send dictations to {host}, which has not been agreed to in Settings > Clean-up (consent.json): using the local cleanup model instead.");
            llm = llm with { Provider = LlmSettings.Local };
        }
        if (llm.Provider == LlmSettings.Local && !CleanupCatalog.IsValidSetting(llm.Model))
        {
            var (model, why) = CleanupCatalog.Recommend(gpuRuntime());
            warn($"llm.model \"{llm.Model}\" is not a cleanup model name or a .gguf path: using the recommendation {model} ({why}).");
            llm = llm with { Model = model };
        }
        return ReferenceEquals(llm, settings.Llm) ? settings : settings with { Llm = llm };
    }

    /// <summary>
    /// hands_free_max_seconds from 5 to 29 (accepted before hands-free typed each part as you pause, 16/10/2026) is raised to
    /// <see cref="AppSettings.HandsFreeMinimumSeconds"/>, with one line for the log; the file itself is not rewritten (the
    /// Settings field writes the new value when it is next changed).
    /// </summary>
    public static AppSettings HandsFree(AppSettings settings, Action<string> warn)
    {
        if (settings.HandsFreeMaxSeconds >= AppSettings.HandsFreeMinimumSeconds) return settings;
        warn($"hands_free_max_seconds {settings.HandsFreeMaxSeconds} is under the {AppSettings.HandsFreeMinimumSeconds} s minimum now that hands-free types as you pause: using {AppSettings.HandsFreeMinimumSeconds} s.");
        return settings with { HandsFreeMaxSeconds = AppSettings.HandsFreeMinimumSeconds };
    }
}
