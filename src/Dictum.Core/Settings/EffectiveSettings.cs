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
    /// <param name="anthropicKeyPresent">ANTHROPIC_API_KEY found in the environment or .env.</param>
    /// <param name="gpuRuntime">Whether a GPU runtime loads; asked only when a recommendation is needed.</param>
    /// <param name="warn">Receives one line per repair, for the log.</param>
    public static AppSettings Cleanup(AppSettings settings, bool anthropicKeyPresent, Func<bool> gpuRuntime, Action<string> warn)
    {
        var llm = settings.Llm;
        if (llm.Provider == LlmSettings.Anthropic && !anthropicKeyPresent)
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
}
