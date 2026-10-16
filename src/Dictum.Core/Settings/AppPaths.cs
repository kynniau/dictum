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

    /// <summary>
    /// The models that come with Dictum (Decision 16, 17/10/2026: base.en ships in every package), beside Dictum.exe and, when
    /// installed from the Store, inside the package. Read-only: Dictum never downloads into, writes or deletes anything here.
    /// </summary>
    public string BundledModelsDir => Path.Combine(ExeDir, "models");

    /// <summary>
    /// Where downloads go and Dictum's own models folder: the folder chosen in Settings > Models (<see cref="ChosenModelsDir"/>),
    /// else the per-user default. Where a model is looked for: <see cref="ModelFolders.LookupOrder"/>.
    /// </summary>
    public string ModelsDir(AppSettings settings) => ChosenModelsDir(settings) ?? DefaultModelsDir;

    /// <summary>
    /// models.folder, else its earlier name whisper.models_dir; null when neither is set. A folder that is the bundled one or
    /// inside it, by any name (<see cref="IsBundled"/>), is never used (null): that folder is never written, so no download can
    /// land there, and the settings load logs that it is ignored (ConfigLoader).
    /// </summary>
    public string? ChosenModelsDir(AppSettings settings) =>
        (settings.Models.Folder ?? settings.Whisper.ModelsDir) is { } chosen && !IsBundled(chosen) ? chosen : null;

    /// <summary>
    /// True for <see cref="BundledModelsDir"/> and any folder or file inside it, also by another name for it: a junction or
    /// symbolic link, an 8.3 short name, a \\?\ prefix (<see cref="Storage.PathRegistry.FinalPath"/>; lens, 17/10/2026).
    /// </summary>
    public bool IsBundled(string path) =>
        Within(path, BundledModelsDir) || Within(Storage.PathRegistry.FinalPath(path), Storage.PathRegistry.FinalPath(BundledModelsDir));

    /// <summary>
    /// The user's profile folder (Windows: %USERPROFILE%), which other users cannot open by default. A chosen models folder
    /// outside it may be shared with them, so "Delete all app data" leaves it alone (<see cref="Storage.DataCategory.SharedModels"/>).
    /// Tests set their own.
    /// </summary>
    public string ProfileDir { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>True when <paramref name="folder"/> is <see cref="ProfileDir"/> or inside it, links followed; false when the profile is unknown.</summary>
    public bool InProfile(string folder) =>
        !string.IsNullOrWhiteSpace(ProfileDir) && Within(Storage.PathRegistry.FinalPath(folder), Storage.PathRegistry.FinalPath(ProfileDir));

    private static bool Within(string path, string folder) => Storage.PathRegistry.SamePath(path, folder) || Storage.PathRegistry.IsInside(path, folder);

    /// <summary>
    /// The app's folders under the platform's two app-data roots (Windows: %APPDATA% and %LOCALAPPDATA%), or the
    /// <paramref name="configOverride"/> folder for settings.
    /// </summary>
    public static AppPaths ForRoots(string roamingRoot, string localRoot, string exeDir, string? configOverride) =>
        string.IsNullOrWhiteSpace(configOverride)
            ? new AppPaths(Path.Combine(roamingRoot, AppFolder), Path.Combine(localRoot, AppFolder), exeDir)
            : new AppPaths(Path.GetFullPath(configOverride), Path.Combine(localRoot, AppFolder), exeDir) { ConfigOverridden = true };
}
