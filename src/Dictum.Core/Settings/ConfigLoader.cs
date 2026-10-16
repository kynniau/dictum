using Dictum.Core;

namespace Dictum.Settings;

/// <param name="Settings">The settings to run with (repaired, with any --cleanup-model applied).</param>
/// <param name="Secrets">Keys from the environment and the two .env files; already registered with the log redactor.</param>
/// <param name="File">What the settings.json reader found.</param>
/// <param name="Problem">Why settings.json could not be used, to show the user; null when it was fine.</param>
public sealed record ConfigLoadResult(AppSettings Settings, SecretStore Secrets, SettingsLoadResult File, string? Problem)
{
    /// <summary>A reload found a broken file and kept the settings already in use.</summary>
    public bool KeptPrevious { get; init; }

    /// <summary>Startup found a broken file: defaults, with offline_only forced on.</summary>
    public bool ForcedOffline { get; init; }
}

/// <summary>
/// The one configuration loader, shared by the tray app (startup and every reload), --transcribe, --selftest,
/// --bench-cleanup and the download commands: settings.json, the secrets (registered with the log redactor so a key
/// never reaches a log line), the cleanup repair, and the --cleanup-model override.
/// </summary>
public static class ConfigLoader
{
    /// <param name="environment">Environment variable lookup (tests pass their own).</param>
    /// <param name="gpuRuntime">Probes the GPU for the settings being loaded; asked only when the cleanup repair needs a recommendation.</param>
    /// <param name="warn">Receives every settings warning and repair, for the log.</param>
    /// <param name="previous">The settings in use, on a reload; null at startup and in the CLI.</param>
    public static ConfigLoadResult Load(AppPaths paths, SecretRedactor redactor, Func<string, string?> environment, Func<AppSettings, bool> gpuRuntime,
        Action<string> warn, string? cleanupOverride = null, AppSettings? previous = null)
    {
        var file = SettingsLoader.LoadFile(paths.SettingsFile);
        foreach (var warning in file.Warnings) warn(warning);
        var secrets = SecretStore.Load(paths, environment);
        foreach (var name in SecretStore.KeyNames) redactor.Register(secrets.Get(name));

        // A file that cannot be read fails closed: its values are unknown, so neither a stray comma nor a locked file may
        // turn a suppressed hotkey back on or drop offline_only.
        if (!file.Usable && previous is not null)
        {
            return new ConfigLoadResult(previous, secrets, file, $"{BrokenOnReload} ({FirstWarning(file)})") { KeptPrevious = true };
        }
        // Consent to cloud clean-up comes only from consent.json, which only the in-app dialog writes (SPEC §22).
        var loaded = CloudConsentFile.Apply(file.Usable ? file.Settings : new AppSettings { OfflineOnly = true }, CloudConsentFile.Read(paths.ConsentFile));
        var repaired = EffectiveSettings.HandsFree(EffectiveSettings.Cleanup(loaded, secrets.Get(SecretStore.AnthropicKey) is not null, () => gpuRuntime(loaded), warn), warn);
        var settings = SettingsFile.WithCleanupOverride(repaired, cleanupOverride);
        if ((settings.Models.Folder ?? settings.Whisper.ModelsDir) is { } chosen && paths.IsBundled(chosen))
            warn(BundledFolderIgnored(settings.Models.Folder is null ? "whisper.models_dir" : "models.folder", chosen, paths.DefaultModelsDir));
        return file.Usable
            ? new ConfigLoadResult(settings, secrets, file, null)
            : new ConfigLoadResult(settings, secrets, file, BrokenAtStartup) { ForcedOffline = true };
    }

    /// <summary>
    /// The log line when models.folder (or whisper.models_dir) names the folder of the models that come with Dictum, or one
    /// inside it, by any name: <see cref="AppPaths.ChosenModelsDir"/> ignores it (lens, 17/10/2026).
    /// </summary>
    internal static string BundledFolderIgnored(string key, string folder, string defaultFolder) =>
        $"Setting \"{key}\" ({folder}) is the folder of the models that come with Dictum, or inside it, which Dictum never changes, so it is ignored: models download to {defaultFolder}.";

    public const string BrokenAtStartup = "settings.json could not be read; running offline with defaults until it is fixed";

    public const string BrokenOnReload = "settings.json has an error; keeping the previous settings until it is fixed";

    private static string FirstWarning(SettingsLoadResult file) => file.Warnings.Count > 0 ? file.Warnings[0] : "unreadable";
}
