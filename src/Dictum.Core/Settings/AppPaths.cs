namespace Dictum.Settings;

/// <summary>
/// Where Dictum keeps things (SPEC §9, renamed in §21): settings, secrets and the personal text files in the config folder
/// (%APPDATA%\Dictum on Windows, or --config &lt;dir&gt;); models, logs, the verified-model record and calibration recordings
/// in the local folder (%LOCALAPPDATA%\Dictum). The platform resolves the two roots (IPlatform.ResolvePaths); every file
/// the app writes is listed by <see cref="Storage.PathRegistry"/>, which Settings → Storage and "Remove Dictum completely" use.
/// </summary>
public sealed record AppPaths(string ConfigDir, string LocalDir, string ExeDir)
{
    /// <summary>The folder name under the user's app-data roots.</summary>
    public const string AppFolder = "Dictum";

    /// <summary>The earlier build's folder name; the one-time migration moves its files (SPEC §21).</summary>
    public const string LegacyAppFolder = "Dictate";

    /// <summary>True when --config chose the config folder (the migration then leaves the earlier config files alone).</summary>
    public bool ConfigOverridden { get; init; }

    public string SettingsFile => Path.Combine(ConfigDir, "settings.json");

    /// <summary>Consent to cloud clean-up, written only by the in-app dialog (SPEC §22).</summary>
    public string ConsentFile => Path.Combine(ConfigDir, "consent.json");
    public string EnvFile => Path.Combine(ConfigDir, ".env");
    public string ExeEnvFile => Path.Combine(ExeDir, ".env");
    public string DefaultModelsDir => Path.Combine(LocalDir, "models");
    public string LogsDir => Path.Combine(LocalDir, "logs");
    public string VerifiedModelsFile => Path.Combine(LocalDir, "verified-models.json");

    // Batch 2 (SPEC §16): plain text files the user can read, edit and delete; watched like settings.json.
    public string DictionaryFile => Path.Combine(ConfigDir, "dictionary.txt");
    public string CorrectionsFile => Path.Combine(ConfigDir, "corrections.txt");
    public string SnippetsFile => Path.Combine(ConfigDir, "snippets.txt");
    public string ProfanityOverrideFile => Path.Combine(ConfigDir, "profanity.txt");

    /// <summary>The word list shipped beside the executable.</summary>
    public string ProfanityShippedFile => Path.Combine(ExeDir, "profanity.txt");

    /// <summary>The ten calibration sentences shipped beside the executable.</summary>
    public string CalibrationScriptFile => Path.Combine(ExeDir, "calibration-sentences.txt");

    /// <summary>The user's calibration recordings, kept so the test can be re-run when a model is added.</summary>
    public string CalibrationDir => Path.Combine(LocalDir, "calibration");

    public string ModelsDir(AppSettings settings) => settings.Whisper.ModelsDir ?? DefaultModelsDir;

    /// <summary>
    /// The app's folders under the platform's two app-data roots (Windows: %APPDATA% and %LOCALAPPDATA%), or the
    /// <paramref name="configOverride"/> folder for settings.
    /// </summary>
    public static AppPaths ForRoots(string roamingRoot, string localRoot, string exeDir, string? configOverride) =>
        string.IsNullOrWhiteSpace(configOverride)
            ? new AppPaths(Path.Combine(roamingRoot, AppFolder), Path.Combine(localRoot, AppFolder), exeDir)
            : new AppPaths(Path.GetFullPath(configOverride), Path.Combine(localRoot, AppFolder), exeDir) { ConfigOverridden = true };
}
